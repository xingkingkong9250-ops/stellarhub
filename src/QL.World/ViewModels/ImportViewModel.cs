using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;

namespace QL.World.ViewModels;

/// <summary>
/// W2 import dialog: pick sources, options, run the background pipeline
/// with progress/cancellation and a rolling log.
/// </summary>
public sealed class ImportViewModel : ObservableObject
{
    private readonly ImportService _import;
    private readonly Func<string> _getSuggestedFolder;
    private CancellationTokenSource? _cts;

    private string _sourceSummary = "尚未选择";
    private bool _recurse = true;
    private bool _skipHidden = true;
    private bool _isRunning;
    private bool _isFinished;
    private int _scannedTotal;
    private int _processed;
    private int _added;
    private int _skipped;
    private string _currentFile = string.Empty;
    private string _resultSummary = string.Empty;

    public ImportViewModel(ImportService import, Func<string>? getSuggestedFolder = null)
    {
        _import = import;
        _getSuggestedFolder = getSuggestedFolder ?? (() => Environment.GetFolderPath(
            Environment.SpecialFolder.MyPictures));

        foreach (string ext in ImportOptions.SupportedExtensions.Order())
            ExtensionToggles.Add(new ExtensionToggle(ext, enabled: true));

        PickFolderCommand = new RelayCommand(PickFolder, () => !IsRunning);
        PickFilesCommand = new RelayCommand(PickFiles, () => !IsRunning);
        StartCommand = new AsyncRelayCommand(RunAsync,
            () => !IsRunning && SourcePaths.Count > 0);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        FinishCommand = new RelayCommand(() => RequestClose?.Invoke(AddedOnClose),
            () => IsFinished);
    }

    public ObservableCollection<string> SourcePaths { get; } = [];
    public ObservableCollection<ExtensionToggle> ExtensionToggles { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];

    public RelayCommand PickFolderCommand { get; }
    public RelayCommand PickFilesCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand FinishCommand { get; }

    /// <summary>Close returns the number of newly added images.</summary>
    public event Action<int>? RequestClose;

    public int AddedOnClose { get; private set; }

    public string SourceSummary
    {
        get => _sourceSummary;
        private set => SetProperty(ref _sourceSummary, value);
    }

    public bool RecurseSubfolders
    {
        get => _recurse;
        set => SetProperty(ref _recurse, value);
    }

    public bool SkipHidden
    {
        get => _skipHidden;
        set => SetProperty(ref _skipHidden, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanEditOptions));
                CommandManagerInvalidate();
            }
        }
    }

    public bool IsFinished
    {
        get => _isFinished;
        private set
        {
            if (SetProperty(ref _isFinished, value))
                CommandManagerInvalidate();
        }
    }

    public bool CanEditOptions => !IsRunning;

    public int ScannedTotal
    {
        get => _scannedTotal;
        private set => SetProperty(ref _scannedTotal, value);
    }

    public int Processed
    {
        get => _processed;
        private set => SetProperty(ref _processed, value);
    }

    public int Added
    {
        get => _added;
        private set => SetProperty(ref _added, value);
    }

    public int Skipped
    {
        get => _skipped;
        private set => SetProperty(ref _skipped, value);
    }

    public string CurrentFile
    {
        get => _currentFile;
        private set => SetProperty(ref _currentFile, value);
    }

    public double ProgressFraction =>
        ScannedTotal == 0 ? 0 : Math.Min(1.0, (double)Processed / ScannedTotal);

    public string ResultSummary
    {
        get => _resultSummary;
        private set => SetProperty(ref _resultSummary, value);
    }

    private void PickFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要导入的素材文件夹",
            InitialDirectory = SafeDirectory(_getSuggestedFolder()),
        };
        if (dialog.ShowDialog() == true)
        {
            SourcePaths.Clear();
            SourcePaths.Add(dialog.FolderName);
            UpdateSummary();
        }
    }

    private void PickFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要导入的图片文件",
            InitialDirectory = SafeDirectory(_getSuggestedFolder()),
            Multiselect = true,
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.tga|所有文件|*.*",
        };
        if (dialog.ShowDialog() == true)
        {
            SourcePaths.Clear();
            foreach (string name in dialog.FileNames)
                SourcePaths.Add(name);
            UpdateSummary();
        }
    }

    private void UpdateSummary()
    {
        SourceSummary = SourcePaths.Count == 1
            ? SourcePaths[0]
            : $"已选择 {SourcePaths.Count} 个文件/文件夹";
        CommandManagerInvalidate();
    }

    private async Task RunAsync()
    {
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ExtensionToggle toggle in ExtensionToggles.Where(t => t.Enabled))
            enabled.Add(toggle.Extension);

        var options = new ImportOptions
        {
            InputPaths = SourcePaths.ToList(),
            RecurseSubfolders = RecurseSubfolders,
            SkipHidden = SkipHidden,
            EnabledExtensions = enabled,
        };

        LogLines.Clear();
        IsFinished = false;
        IsRunning = true;
        ScannedTotal = 0;
        Processed = 0;
        Added = 0;
        Skipped = 0;
        CurrentFile = string.Empty;
        ResultSummary = "导入中…";

        _cts = new CancellationTokenSource();
        var progress = new Progress<ImportProgress>(p =>
        {
            ScannedTotal = p.ScannedTotal;
            Processed = p.Processed;
            Added = p.Added;
            Skipped = p.Skipped;
            CurrentFile = p.CurrentFile;
            OnPropertyChanged(nameof(ProgressFraction));
        });

        try
        {
            ImportReport report = await _import.ImportAsync(options, progress, _cts.Token);

            foreach (string line in report.LogLines)
                LogLines.Add(line);

            AddedOnClose = report.AddedCount;
            ResultSummary = report.Cancelled
                ? $"已取消：新增 {report.AddedCount} 张，跳过 {report.SkippedCount} 张"
                : $"完成：新增 {report.AddedCount} 张，跳过 {report.SkippedCount} 张";
            if (report.LogLines.Count > 0)
                CurrentFile = string.Empty;
        }
        catch (Exception ex)
        {
            ResultSummary = $"导入失败：{ex.Message}";
            LogLines.Add(ex.ToString());
        }
        finally
        {
            IsRunning = false;
            IsFinished = true;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void Cancel() => _cts?.Cancel();

    private static string SafeDirectory(string path) =>
        Directory.Exists(path)
            ? path
            : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

    private static void CommandManagerInvalidate() =>
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
}

public sealed class ExtensionToggle : ObservableObject
{
    private bool _enabled;

    public ExtensionToggle(string extension, bool enabled)
    {
        Extension = extension;
        _enabled = enabled;
    }

    public string Extension { get; }
    public string Display => Extension;

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }
}
