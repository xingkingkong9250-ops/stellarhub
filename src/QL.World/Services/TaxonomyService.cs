using QL.World.Models;
using QL.World.Models.Ai;
using QL.World.Models.Taxonomy;

namespace QL.World.Services;

/// <summary>
/// M12 (v0.13.0): 标签驱动分类推导。根据图片的标签集合，推导出 primary_category。
/// 推导规则：命中标签数最多的分类组 → 平局取 confidence 之和最高 →
/// 全部为筛选组标签时返回 null（未分类）。
/// </summary>
public static class TaxonomyService
{
    /// <summary>
    /// 从标签集合推导一级分类。无 confidence 时按命中标签数计；
    /// 有 confidence 时加权（标签数优先，平局取 confidence 和）。
    /// </summary>
    /// <param name="tags">图片的全部标签（不区分来源）。</param>
    /// <returns>推导出的分类名；无法推导返回 null。</returns>
    public static string? DeriveCategory(IReadOnlyList<string> tags)
    {
        if (tags.Count == 0)
            return null;

        // 组 → 命中标签数
        var hitCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string tag in tags)
        {
            string? group = TagTaxonomy.GroupOf(tag);
            if (group is null)
                continue;
            // 只统计分类组（筛选组 GroupToCategory 为 null）
            if (TagTaxonomy.GroupToCategory[group] is null)
                continue;
            hitCounts.TryGetValue(group, out int count);
            hitCounts[group] = count + 1;
        }

        if (hitCounts.Count == 0)
            return null;

        // 命中标签数最多 → 取对应分类
        int maxCount = hitCounts.Values.Max();
        var bestGroups = hitCounts
            .Where(kv => kv.Value == maxCount)
            .Select(kv => kv.Key)
            .ToList();

        // 唯一最优 → 直接返回该分类
        if (bestGroups.Count == 1)
            return TagTaxonomy.GroupToCategory[bestGroups[0]];

        // 平局 → 取 TagTaxonomy.Categories 中靠前的分类组
        foreach (string category in TagTaxonomy.Categories)
        {
            if (bestGroups.Contains(category))
                return category;
        }

        return null;
    }

    /// <summary>
    /// 从带置信度的 AI 标签集合推导一级分类。
    /// 推导规则：命中标签数最多的分类组 → 平局取 confidence 之和最高。
    /// </summary>
    public static string? DeriveCategory(IReadOnlyList<AiTag> tags)
    {
        if (tags.Count == 0)
            return null;

        // 组 → (命中标签数, confidence 之和)
        var hits = new Dictionary<string, (int Count, double Sum)>(StringComparer.Ordinal);
        foreach (AiTag tag in tags)
        {
            string? group = TagTaxonomy.GroupOf(tag.Tag);
            if (group is null)
                continue;
            if (TagTaxonomy.GroupToCategory[group] is null)
                continue;

            if (hits.TryGetValue(group, out var existing))
                hits[group] = (existing.Count + 1, existing.Sum + tag.Confidence);
            else
                hits[group] = (1, tag.Confidence);
        }

        if (hits.Count == 0)
            return null;

        // 先按命中标签数降序，再按 confidence 之和降序
        var ranked = hits
            .OrderByDescending(kv => kv.Value.Count)
            .ThenByDescending(kv => kv.Value.Sum)
            .ToList();

        var best = ranked[0];
        var tied = ranked
            .Where(kv => kv.Value.Count == best.Value.Count
                      && Math.Abs(kv.Value.Sum - best.Value.Sum) < 1e-9)
            .Select(kv => kv.Key)
            .ToList();

        if (tied.Count == 1)
            return TagTaxonomy.GroupToCategory[tied[0]];

        // 平局 → 取 Categories 中靠前的分类组
        foreach (string category in TagTaxonomy.Categories)
        {
            if (tied.Contains(category))
                return category;
        }

        return null;
    }
}
