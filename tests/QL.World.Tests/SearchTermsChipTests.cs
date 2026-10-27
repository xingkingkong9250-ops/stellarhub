using QL.World.Models;
using QL.World.Services;
using QL.World.Tests.Support;

namespace QL.World.Tests;

/// <summary>0.20.0: chip 搜索词条（多 term AND）+ 搜索词条落库。</summary>
public sealed class SearchTermsChipTests
{
    [Fact]
    public void LibraryFilter_clone_copies_search_terms()
    {
        var filter = new LibraryFilter
        {
            Text = "a",
            SearchTerms = ["a", "b"],
        };

        LibraryFilter clone = filter.Clone();

        Assert.Equal(filter.SearchTerms, clone.SearchTerms);
        clone.SearchTerms = ["c"];
        Assert.Equal(["a", "b"], filter.SearchTerms);
    }

    [Fact]
    public void Search_terms_combine_with_and_across_terms()
    {
        using var library = new TestLibrary();
        var query = new ImageQueryService(library.Db);
        var tags = new TagService(library.Db);

        // A: 有 metal + dragon 双标签；B: 只有 metal；C: 只有 dragon
        long a = library.InsertImage(filePath: @"X:\a.png");
        long b = library.InsertImage(filePath: @"X:\b.png");
        long c = library.InsertImage(filePath: @"X:\c.png");
        tags.AddTags([a], ["metal", "dragon"], TagSource.Manual);
        tags.AddTags([b], ["metal"], TagSource.Manual);
        tags.AddTags([c], ["dragon"], TagSource.Manual);

        var both = query.QueryGallery(new LibraryFilter
        {
            SearchTerms = ["metal", "dragon"],
        });
        Assert.Single(both);
        Assert.Equal(a, both[0].Id);

        var single = query.QueryGallery(new LibraryFilter
        {
            SearchTerms = ["metal"],
        });
        Assert.Equal(2, single.Count);

        // 空 term 被忽略
        var blank = query.QueryGallery(new LibraryFilter
        {
            SearchTerms = ["", "  "],
        });
        Assert.Equal(3, blank.Count);
    }

    [Fact]
    public void Search_terms_apply_per_term_mode_and_pinyin()
    {
        using var library = new TestLibrary();
        var query = new ImageQueryService(library.Db);
        var tags = new TagService(library.Db);

        long id = library.InsertImage(filePath: @"X:\role_ref.png", notes: "主角参考");
        tags.AddTags([id], ["角色"], TagSource.Manual);

        // term 1 中文命中标签（同时生成拼音子句），term 2 匹配文件名 —— 两 term AND 命中同一行
        var result = query.QueryGallery(new LibraryFilter
        {
            SearchTerms = ["角色", "role_ref"],
        });
        Assert.Single(result);

        // 文件名 term 不满足 → 不命中
        var miss = query.QueryGallery(new LibraryFilter
        {
            SearchTerms = ["角色", "nothing_match"],
        });
        Assert.Empty(miss);
    }

    [Fact]
    public void Search_log_writes_only_nonzero_hits_and_roundtrips()
    {
        using var library = new TestLibrary();
        var log = new SearchLogService(library.Db);

        log.Insert(["metal", "dragon"], 12, "tester");
        log.Insert([], 5);            // 空词条：不写
        log.Insert(["  "], 5);        // 纯空白：不写
        log.Insert(["zero"], 0);      // 零命中：不写

        var rows = library.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT terms_json, result_count, user_id FROM search_log;";
            using var reader = cmd.ExecuteReader();
            var list = new List<(string Json, int Count, string? User)>();
            while (reader.Read())
                list.Add((reader.GetString(0), reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            return list;
        });

        Assert.Single(rows);
        Assert.Equal("""["metal","dragon"]""", rows[0].Json);
        Assert.Equal(12, rows[0].Count);
        Assert.Equal("tester", rows[0].User);
    }
}
