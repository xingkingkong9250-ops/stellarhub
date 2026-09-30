using System.Windows.Input;

namespace QL.World.Helpers;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool IsExecuting => _isExecuting;

    public bool CanExecute(object? parameter) =>
        !_isExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync();

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
            return;

        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute();
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() =>
        CommandManager.InvalidateRequerySuggested();
}

/// <summary>
/// U6-B: async command with a strongly-typed command parameter.
/// </summary>
public sealed class AsyncRelayCommand<T> : ICommand
{
    private readonly Func<T, Task> _execute;
    private readonly Predicate<T>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<T, Task> execute, Predicate<T>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) =>
        !_isExecuting &&
        (_canExecute?.Invoke(Cast(parameter)) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute(Cast(parameter));
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() =>
        CommandManager.InvalidateRequerySuggested();

    private static T Cast(object? parameter) =>
        parameter is T t ? t : default!;
}
