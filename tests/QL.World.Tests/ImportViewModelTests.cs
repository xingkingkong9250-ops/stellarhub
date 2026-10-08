using System.IO;
using QL.World.Models;
using QL.World.Services;
using QL.World.Tests.Support;
using QL.World.ViewModels;

namespace QL.World.Tests;

/// <summary>
/// W2/AI (v0.9.0) 回归：导入对话框 AI 模式选择与「导入完成后自动分析」
/// 事件——默认关闭不触发；选云端/本地时携带本次新增图 ID 触发。
/// </summary>
public sealed class ImportViewModelTests : IDisposable
{
    private readonly TestLibrary _lib = new();

    public void Dispose() => _lib.Dispose();

    private ImportViewModel CreateVm()
    {
        var importer = new ImportService(_lib.Db, new ImageInspector());
        return new ImportViewModel(importer, () => _lib.DirectoryRoot);
    }

    [Fact]
    public async Task ImportAiMode_defaults_to_disabled_and_event_not_fired()
    {
        string source = Path.Combine(_lib.DirectoryRoot, "source");
        TestImageFactory.CreatePng(source, "a.png");
        var vm = CreateVm();
        vm.SourcePaths.Add(source);

        bool fired = false;
        vm.ImportAnalysisRequested += (_, _) => fired = true;
        await vm.StartCommand.ExecuteAsync();

        Assert.Equal(1, vm.Added);
        Assert.Equal(AiMode.Disabled, vm.ImportAiMode);
        Assert.False(fired);
    }

    [Fact]
    public async Task ImportAnalysisRequested_fires_with_ids_and_mode_when_cloud_selected()
    {
        string source = Path.Combine(_lib.DirectoryRoot, "source");
        TestImageFactory.CreatePng(source, "a.png");
        var vm = CreateVm();
        vm.SourcePaths.Add(source);
        vm.ImportAiMode = AiMode.Cloud;

        IReadOnlyList<long>? raisedIds = null;
        AiMode raisedMode = AiMode.Disabled;
        vm.ImportAnalysisRequested += (ids, mode) =>
        {
            raisedIds = ids;
            raisedMode = mode;
        };
        await vm.StartCommand.ExecuteAsync();

        Assert.Equal(1, vm.Added);
        Assert.NotNull(raisedIds);
        Assert.Single(raisedIds);
        Assert.Equal(AiMode.Cloud, raisedMode);
        Assert.Single(vm.AddedIds);
    }
}
