using System.Collections.ObjectModel;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;
using QL.World.Services.Pbr;

namespace QL.World.ViewModels;

/// <summary>
/// W3/A6 贴图组规则：编辑「文件名尾缀 → 贴图角色」映射，保存后全量重建贴图组。
/// 规则持久化到 config.json（pbrRules）；恢复默认 = 清空自定义规则。
/// </summary>
public sealed class PbrRulesViewModel : ObservableObject
{
    private readonly AppSettingsService _settings;
    private readonly PbrGroupingService _grouping;
    private string _statusText = string.Empty;

    public PbrRulesViewModel(AppSettingsService settings, PbrGroupingService grouping)
    {
        _settings = settings;
        _grouping = grouping;

        foreach (PbrRule rule in settings.PbrRules)
            Rules.Add(new PbrRuleRowViewModel(rule.Pattern, rule.Role, rule.Enabled, RemoveRow));

        RoleOptions =
        [
            new RoleOption(PbrRoles.BaseColor, "basecolor（固有色）"),
            new RoleOption(PbrRoles.Normal, "normal（法线）"),
            new RoleOption(PbrRoles.Roughness, "roughness（粗糙度）"),
            new RoleOption(PbrRoles.Metallic, "metallic（金属度）"),
            new RoleOption(PbrRoles.Ao, "ao（环境光遮蔽）"),
            new RoleOption(PbrRoles.Height, "height（高度）"),
            new RoleOption(PbrRoles.Other, "other（其他）"),
        ];

        AddRuleCommand = new RelayCommand(
            () => Rules.Add(new PbrRuleRowViewModel("", PbrRoles.BaseColor, true, RemoveRow)));
        SaveCommand = new RelayCommand(Save);
        RestoreDefaultsCommand = new RelayCommand(RestoreDefaults);
        RescanCommand = new RelayCommand(Rescan);
    }

    public ObservableCollection<PbrRuleRowViewModel> Rules { get; } = [];

    public IReadOnlyList<RoleOption> RoleOptions { get; }

    public RelayCommand AddRuleCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand RestoreDefaultsCommand { get; }
    public RelayCommand RescanCommand { get; }

    /// <summary>最近一次保存/重建/恢复操作的结果反馈。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private void RemoveRow(PbrRuleRowViewModel row) => Rules.Remove(row);

    private void Save()
    {
        List<PbrRule> rules = [.. Rules
            .Where(r => !string.IsNullOrWhiteSpace(r.Pattern))
            .Select(r => new PbrRule(r.Pattern.Trim(), r.Role, r.Enabled))];

        // 空规则表 = 恢复内置默认（SetPbrRules 的约定）。
        _settings.SetPbrRules(rules.Count > 0 ? rules : null);

        PbrRescanReport report = _grouping.RescanAll();
        StatusText = rules.Count > 0
            ? $"已保存 {rules.Count} 条规则；重建 {report.GroupsCreated} 个贴图组（{report.MembersLinked} 张图）。"
            : "规则为空，已恢复内置默认并重建。";
    }

    private void RestoreDefaults()
    {
        _settings.SetPbrRules(null);
        Rules.Clear();
        foreach (PbrRule rule in _settings.PbrRules)
            Rules.Add(new PbrRuleRowViewModel(rule.Pattern, rule.Role, rule.Enabled, RemoveRow));

        PbrRescanReport report = _grouping.RescanAll();
        StatusText = $"已恢复默认规则；重建 {report.GroupsCreated} 个贴图组（{report.MembersLinked} 张图）。";
    }

    private void Rescan()
    {
        PbrRescanReport report = _grouping.RescanAll();
        StatusText = $"重建完成：{report.GroupsCreated} 个贴图组，{report.MembersLinked} 张图。";
    }
}

/// <summary>角色下拉项：值存库（schema role 词汇），显示带中文说明。</summary>
public sealed record RoleOption(string Value, string Display);

/// <summary>W3/A6 规则编辑行。</summary>
public sealed class PbrRuleRowViewModel : ObservableObject
{
    private string _pattern;
    private string _role;
    private bool _enabled;

    public PbrRuleRowViewModel(
        string pattern, string role, bool enabled, Action<PbrRuleRowViewModel> remove)
    {
        _pattern = pattern;
        _role = role;
        _enabled = enabled;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    /// <summary>文件名尾缀（不含扩展名），如 "_Normal"。</summary>
    public string Pattern
    {
        get => _pattern;
        set => SetProperty(ref _pattern, value);
    }

    /// <summary>贴图角色（PbrRoles 词汇）。</summary>
    public string Role
    {
        get => _role;
        set => SetProperty(ref _role, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public RelayCommand RemoveCommand { get; }
}
