using QL.World.Services;
using QL.World.Tests.Support;
using QL.World.ViewModels;

namespace QL.World.Tests;

/// <summary>
/// 0.16.0 DUP-D: W8 重复审核 ViewModel——筛选、命令可用性、保留全部、
/// 删除选中联动组 resolved、推荐保留计算。
/// </summary>
public sealed class DuplicateReviewViewModelTests
{
    [Fact]
    public async Task Kind_filter_splits_exact_and_variant_groups()
    {
        using var library = new TestLibrary();
        long exactA = library.InsertImage("a.png");
        long exactB = library.InsertImage("b.png");
        long variantA = library.InsertImage("c.png");
        long variantB = library.InsertImage("d.png");

        SetPhash(library, exactA, 0UL);
        SetPhash(library, exactB, 1UL);
        SetPhash(library, variantA, 0xFFFF_FFFF_0000_0000UL);
        SetPhash(library, variantB, 0x0000_0000_FFFF_FFFFUL);

        InsertEmbedding(library, variantA, "local-model", [1f, 0f, 0f]);
        InsertEmbedding(library, variantB, "local-model", [1f, 0f, 0f]);

        var scanner = new DuplicateScanService(library.Db);
        await scanner.ScanAsync();

        using var vm = CreateViewModel(library, scanner);
        await vm.LoadAsync();
        Assert.Equal(2, vm.Groups.Count);
        Assert.Contains(vm.Groups, group => group.KindText == "严格同图");
        Assert.Contains(vm.Groups, group => group.KindText == "近似变体");

        vm.SelectedKindFilter = DuplicateKindFilter.Exact;
        await vm.LoadAsync();
        DuplicateGroupViewModel exact = Assert.Single(vm.Groups);
        Assert.Equal("exact", exact.Record.Kind);
        Assert.Equal(2, exact.MemberCount);

        vm.SelectedKindFilter = DuplicateKindFilter.Variant;
        await vm.LoadAsync();
        DuplicateGroupViewModel variant = Assert.Single(vm.Groups);
        Assert.Equal("variant", variant.Record.Kind);
        Assert.Equal(2, variant.MemberCount);
    }

    [Fact]
    public async Task Keep_all_dismisses_group_and_status_filter_moves_it_to_processed()
    {
        using var library = new TestLibrary();
        long first = library.InsertImage("a.png");
        long second = library.InsertImage("b.png");
        SetPhash(library, first, 0UL);
        SetPhash(library, second, 0UL);

        var scanner = new DuplicateScanService(library.Db);
        await scanner.ScanAsync();

        var review = new DuplicateReviewService(library.Db);
        using var vm = CreateViewModel(library, scanner, review);
        await vm.LoadAsync();
        DuplicateGroupViewModel group = Assert.Single(vm.Groups);

        Assert.True(vm.KeepAllCommand.CanExecute(group));
        await vm.KeepAllAsync(group);

        Assert.Empty(vm.Groups);
        Assert.Empty(review.ListGroups(DuplicateKindFilter.All, DuplicateStatusFilter.Pending));

        vm.SelectedStatusFilter = DuplicateStatusFilter.Processed;
        await vm.LoadAsync();
        DuplicateGroupViewModel processed = Assert.Single(vm.Groups);
        Assert.Equal("已忽略", processed.StatusText);
    }

    [Fact]
    public async Task Delete_selected_marks_group_resolved_when_one_member_remains()
    {
        using var library = new TestLibrary();
        long first = library.InsertImage("a.png");
        long second = library.InsertImage("b.png");
        long third = library.InsertImage("c.png");
        SetPhash(library, first, 0UL);
        SetPhash(library, second, 0UL);
        SetPhash(library, third, 0UL);

        var scanner = new DuplicateScanService(library.Db);
        await scanner.ScanAsync();
        var review = new DuplicateReviewService(library.Db);
        var query = new ImageQueryService(library.Db);

        using var vm = CreateViewModel(library, scanner, review, query);
        await vm.LoadAsync();
        DuplicateGroupViewModel group = Assert.Single(vm.Groups);
        Assert.Equal(3, group.MemberCount);

        // 未勾选时「删除选中」不可用。
        Assert.False(vm.DeleteSelectedCommand.CanExecute(group));

        group.Members[0].IsSelected = true;
        group.Members[1].IsSelected = true;
        Assert.True(vm.DeleteSelectedCommand.CanExecute(group));

        int removed = await vm.DeleteSelectedAsync(group);

        Assert.Equal(2, removed);
        Assert.Equal(1, CountImages(library));
        Assert.Empty(review.ListGroups(DuplicateKindFilter.All, DuplicateStatusFilter.Pending));

        DuplicateGroupRecord processed = Assert.Single(
            review.ListGroups(DuplicateKindFilter.All, DuplicateStatusFilter.Processed));
        Assert.Equal("resolved", processed.Status);
        Assert.Single(processed.Members);

        // 撤销恢复图片行（候选行需下次扫描重建）。
        vm.Undo();
        Assert.Equal(3, CountImages(library));
    }

    [Fact]
    public async Task Recommended_keep_prefers_larger_area_then_size()
    {
        using var library = new TestLibrary();
        long small = library.InsertImage("small.png");
        long large = library.InsertImage("large.png");
        SetPhash(library, small, 0UL);
        SetPhash(library, large, 0UL);
        SetSize(library, small, 512, 512, fileSize: 999_999);
        SetSize(library, large, 1024, 1024, fileSize: 10);

        var scanner = new DuplicateScanService(library.Db);
        await scanner.ScanAsync();

        using var vm = CreateViewModel(library, scanner);
        await vm.LoadAsync();
        DuplicateGroupViewModel group = Assert.Single(vm.Groups);

        DuplicateMemberViewModel recommended = Assert.Single(
            group.Members, member => member.IsRecommended);
        Assert.Equal(large, recommended.ImageId);
    }

    [Fact]
    public async Task Scan_command_completes_and_reports_summary()
    {
        using var library = new TestLibrary();

        using var vm = CreateViewModel(library, new DuplicateScanService(library.Db));
        Assert.True(vm.ScanCommand.CanExecute(null));

        await vm.ScanAsync();

        Assert.False(vm.IsScanning);
        Assert.Contains("扫描完成", vm.StatusText);
        Assert.Equal(100, vm.ScanPercent);
        Assert.False(vm.CancelScanCommand.CanExecute(null));
    }

    // ── helpers ───────────────────────────────────────────────────────

    private static DuplicateReviewViewModel CreateViewModel(
        TestLibrary library,
        DuplicateScanService scanner,
        DuplicateReviewService? review = null,
        ImageQueryService? query = null) =>
        new(review ?? new DuplicateReviewService(library.Db), scanner, query);

    private static void SetPhash(TestLibrary library, long imageId, ulong hash) =>
        library.Db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = "UPDATE images SET phash = $phash WHERE id = $id;";
            command.Parameters.AddWithValue("$phash", PerceptualHashService.ToHex(hash));
            command.Parameters.AddWithValue("$id", imageId);
            command.ExecuteNonQuery();
        });

    private static void SetSize(
        TestLibrary library, long imageId, int width, int height, long fileSize) =>
        library.Db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = """
                UPDATE images SET width = $width, height = $height, file_size = $size
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$width", width);
            command.Parameters.AddWithValue("$height", height);
            command.Parameters.AddWithValue("$size", fileSize);
            command.Parameters.AddWithValue("$id", imageId);
            command.ExecuteNonQuery();
        });

    private static void InsertEmbedding(
        TestLibrary library, long imageId, string modelId, float[] vector) =>
        library.Db.Run(conn =>
        {
            byte[] blob = new byte[vector.Length * sizeof(float)];
            Buffer.BlockCopy(vector, 0, blob, 0, blob.Length);

            using var command = conn.CreateCommand();
            command.CommandText = """
                INSERT OR REPLACE INTO image_embeddings
                    (image_id, model_id, dim, embedding, created_at, source)
                VALUES
                    ($id, $model, $dim, $blob, $now, 'local');
                """;
            command.Parameters.AddWithValue("$id", imageId);
            command.Parameters.AddWithValue("$model", modelId);
            command.Parameters.AddWithValue("$dim", vector.Length);
            command.Parameters.AddWithValue("$blob", blob);
            command.Parameters.AddWithValue("$now", "2026-10-27T00:00:00.000Z");
            command.ExecuteNonQuery();
        });

    private static int CountImages(TestLibrary library) =>
        library.Db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM images;";
            return Convert.ToInt32(command.ExecuteScalar());
        });
}
