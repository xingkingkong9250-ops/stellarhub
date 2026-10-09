using System.IO;
using QL.World.Models;
using QL.World.Services;
using QL.World.Tests.Support;
using QL.World.ViewModels;

namespace QL.World.Tests;

/// <summary>
/// v0.12.0 回归：导入对话框 AI 后端选择与「导入完成后自动分析」
/// 事件——默认关闭不触发；选了后端时携带本次新增图 ID 触发。
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
    public async Task ImportBackendId_defaults_to_null_and_event_not_fired()
    {
        string source = Path.Combine(_lib.DirectoryRoot, "source");
        TestImageFactory.CreatePng(source, "a.png");
        var vm = CreateVm();
        vm.SourcePaths.Add(source);

        bool fired = false;
        vm.ImportAnalysisRequested += (_, _) => fired = true;
        await vm.StartCommand.ExecuteAsync();

        Assert.Equal(1, vm.Added);
        Assert.Null(vm.ImportBackendId);
        Assert.False(fired);
    }

    [Fact]
    public async Task ImportAnalysisRequested_fires_with_ids_and_backend_id_when_selected()
    {
        string source = Path.Combine(_lib.DirectoryRoot, "source");
        TestImageFactory.CreatePng(source, "a.png");
        var vm = CreateVm();
        vm.SourcePaths.Add(source);
        vm.ImportBackendId = 7;

        IReadOnlyList<long>? raisedIds = null;
        int raisedBackendId = 0;
        vm.ImportAnalysisRequested += (ids, backendId) =>
        {
            raisedIds = ids;
            raisedBackendId = backendId;
        };
        await vm.StartCommand.ExecuteAsync();

        Assert.Equal(1, vm.Added);
        Assert.NotNull(raisedIds);
        Assert.Single(raisedIds);
        Assert.Equal(7, raisedBackendId);
        Assert.Single(vm.AddedIds);
    }
}
