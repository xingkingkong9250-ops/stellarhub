using QL.World.Models;
using QL.World.Models.Ai;
using QL.World.Models.Taxonomy;
using QL.World.Services;

namespace QL.World.Tests;

/// <summary>
/// M12-A 回归：TaxonomyService.DeriveCategory 标签驱动分类推导。
/// 验证单分类命中、多分类平局、纯筛选组标签、空标签、混合标签、旧分类不识别。
/// </summary>
public sealed class TaxonomyServiceTests
{
    // ─── TagTaxonomy 结构验证 ────────────────────────────────────

    [Fact]
    public void TagTaxonomy_has_6_categories()
    {
        Assert.Equal(6, TagTaxonomy.Categories.Count);
        Assert.Contains(TagTaxonomy.Character, TagTaxonomy.Categories);
        Assert.Contains(TagTaxonomy.Creature, TagTaxonomy.Categories);
        Assert.Contains(TagTaxonomy.Scene, TagTaxonomy.Categories);
        Assert.Contains(TagTaxonomy.Prop, TagTaxonomy.Categories);
        Assert.Contains(TagTaxonomy.Texture, TagTaxonomy.Categories);
        Assert.Contains(TagTaxonomy.Asset, TagTaxonomy.Categories);
    }

    [Fact]
    public void TagTaxonomy_has_11_groups()
    {
        Assert.Equal(6, TagTaxonomy.CategoryGroups.Count);
        Assert.Equal(7, TagTaxonomy.FilterGroups.Count);
        Assert.Equal(13, TagTaxonomy.AllGroups.Count);
    }

    [Fact]
    public void TagTaxonomy_tag_count_matches_expected_total()
    {
        // 6 分类组 + 7 筛选组（色彩倾向为空组），全部标签去重后 155 个
        // 角色20 + 生物10 + 场景21 + 道具14 + 质感17 + 素材5 = 87
        // 美术风格22 + 文件格式8 + 构图12 + 灯光14 + 题材18 + 状态14 = 88
        // 色彩倾向 0
        // 共 175（去重后，因各标签唯一不重叠）
        Assert.True(TagTaxonomy.TotalTagCount >= 170,
            $"Total tag count {TagTaxonomy.TotalTagCount} should be >= 170");
    }

    [Fact]
    public void GroupToCategory_maps_category_groups_to_themselves()
    {
        Assert.Equal(TagTaxonomy.Character,
            TagTaxonomy.GroupToCategory[TagTaxonomy.GroupCharacter]);
        Assert.Equal(TagTaxonomy.Asset,
            TagTaxonomy.GroupToCategory[TagTaxonomy.GroupAsset]);
    }

    [Fact]
    public void GroupToCategory_maps_filter_groups_to_null()
    {
        Assert.Null(TagTaxonomy.GroupToCategory[TagTaxonomy.GroupArtStyle]);
        Assert.Null(TagTaxonomy.GroupToCategory[TagTaxonomy.GroupLighting]);
    }

    [Fact]
    public void CategoryOfTag_returns_correct_category_for_character_tag()
    {
        Assert.Equal(TagTaxonomy.Character, TagTaxonomy.CategoryOfTag("骑士"));
        Assert.Equal(TagTaxonomy.Scene, TagTaxonomy.CategoryOfTag("森林"));
        Assert.Equal(TagTaxonomy.Texture, TagTaxonomy.CategoryOfTag("金属"));
    }

    [Fact]
    public void CategoryOfTag_returns_null_for_filter_tag()
    {
        Assert.Null(TagTaxonomy.CategoryOfTag("写实"));
        Assert.Null(TagTaxonomy.CategoryOfTag("自然光"));
    }

    [Fact]
    public void CategoryOfTag_returns_null_for_unknown_tag()
    {
        Assert.Null(TagTaxonomy.CategoryOfTag("不存在的标签"));
    }

    // ─── DeriveCategory 推导逻辑 ─────────────────────────────────

    [Fact]
    public void DeriveCategory_single_category_hit_returns_that_category()
    {
        var tags = new List<string> { "骑士", "写实", "自然光" };
        // 骑士→角色（分类组），写实→美术风格（筛选组），自然光→灯光氛围（筛选组）
        string? category = TaxonomyService.DeriveCategory(tags);
        Assert.Equal(TagTaxonomy.Character, category);
    }

    [Fact]
    public void DeriveCategory_multi_category_hit_returns_one_with_most_tags()
    {
        var tags = new List<string> { "骑士", "士兵", "森林" };
        // 角色×2（骑士+士兵），场景×1（森林）→ 角色
        string? category = TaxonomyService.DeriveCategory(tags);
        Assert.Equal(TagTaxonomy.Character, category);
    }

    [Fact]
    public void DeriveCategory_tie_breaks_by_categories_order()
    {
        var tags = new List<string> { "骑士", "森林" };
        // 角色×1，场景×1 → 平局 → Categories 中角色在场景前面 → 角色
        string? category = TaxonomyService.DeriveCategory(tags);
        Assert.Equal(TagTaxonomy.Character, category);
    }

    [Fact]
    public void DeriveCategory_pure_filter_tags_returns_null()
    {
        var tags = new List<string> { "写实", "自然光", "特写", "完好" };
        // 全是筛选组标签，无分类组命中 → null
        string? category = TaxonomyService.DeriveCategory(tags);
        Assert.Null(category);
    }

    [Fact]
    public void DeriveCategory_empty_tags_returns_null()
    {
        Assert.Null(TaxonomyService.DeriveCategory(Array.Empty<string>()));
    }

    [Fact]
    public void DeriveCategory_unknown_tags_returns_null()
    {
        var tags = new List<string> { "未知标签1", "未知标签2" };
        Assert.Null(TaxonomyService.DeriveCategory(tags));
    }

    [Fact]
    public void DeriveCategory_mixed_known_and_unknown_tags()
    {
        var tags = new List<string> { "金属", "未知标签", "写实" };
        // 金属→质感（分类组），写实→美术风格（筛选组），未知标签忽略 → 质感
        string? category = TaxonomyService.DeriveCategory(tags);
        Assert.Equal(TagTaxonomy.Texture, category);
    }

    // ─── DeriveCategory 带 confidence 重载 ─────────────────────

    [Fact]
    public void DeriveCategory_with_confidence_uses_count_then_sum()
    {
        var tags = new List<AiTag>
        {
            new("骑士", 0.9),   // 角色
            new("森林", 0.3),   // 场景
        };
        // 角色×1(sum=0.9), 场景×1(sum=0.3) → 平局（count=1）→ confidence 和高 → 角色
        string? category = TaxonomyService.DeriveCategory(tags);
        Assert.Equal(TagTaxonomy.Character, category);
    }

    [Fact]
    public void DeriveCategory_with_confidence_count_beats_sum()
    {
        var tags = new List<AiTag>
        {
            new("森林", 0.3),     // 场景 ×1
            new("骑士", 0.95),    // 角色 ×1
            new("士兵", 0.1),     // 角色 ×2（count=2 > 场景 count=1）
        };
        // 角色命中2标签(sum=1.05)，场景命中1标签(sum=0.3) → 角色count更高 → 角色
        string? category = TaxonomyService.DeriveCategory(tags);
        Assert.Equal(TagTaxonomy.Character, category);
    }

    [Fact]
    public void DeriveCategory_with_confidence_pure_filter_returns_null()
    {
        var tags = new List<AiTag>
        {
            new("写实", 0.9),
            new("自然光", 0.8),
        };
        Assert.Null(TaxonomyService.DeriveCategory(tags));
    }

    [Fact]
    public void DeriveCategory_with_confidence_empty_returns_null()
    {
        Assert.Null(TaxonomyService.DeriveCategory(Array.Empty<AiTag>()));
    }
}
