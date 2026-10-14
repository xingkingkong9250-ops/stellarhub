using System.Text.RegularExpressions;
using TinyPinyin;
using QL.World.Models;

namespace QL.World.Services;

/// <summary>
/// v0.14.6: 智能搜索查询解析器。
/// 单搜索框自动识别意图，不再需要用户手动选择搜索模式。
///
/// 识别规则（按优先级）：
/// 1. 空文本 → 清空搜索
/// 2. 包含文件扩展名 (.png/.jpg/...) → 文件名搜索
/// 3. 完全匹配已知分类名 → 分类搜索
/// 4. 纯 ASCII → 标签+文件名+备注 多字段 OR
/// 5. 含中文 → 标签+文件名+备注+拼音 多字段 OR
/// 6. 混合 → 同规则 4/5
///
/// 输出 <see cref="SearchQuery"/>：包含原始文本、推断模式、拼音版本。
/// SQL 层根据 Mode 决定 WHERE 子句结构。
/// </summary>
public static class SearchQueryParser
{
    private static readonly Regex ExtensionRegex =
        new(@"\.(png|jpe?g|gif|webp|bmp|tga|tiff?|exr|hdr|psd|ai|blend|fbx|obj|gltf?|glb|max|ma|mb|cgfx|shader|mat)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>已知一级分类名集合（用于精确匹配）。</summary>
    private static readonly HashSet<string> KnownCategories = new(Categories.All, StringComparer.Ordinal);

    /// <summary>
    /// 解析原始搜索文本，输出智能搜索查询。
    /// </summary>
    public static SearchQuery Parse(string? rawText)
    {
        string text = (rawText ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(text))
            return SearchQuery.Empty;

        // 1. 包含文件扩展名 → 文件名优先
        if (ExtensionRegex.IsMatch(text))
            return new SearchQuery(text, SearchMode.FileNameNotes, null, SearchIntent.FileName);

        // 2. 完全匹配已知分类名
        if (KnownCategories.Contains(text))
            return new SearchQuery(text, SearchMode.Tag, null, SearchIntent.Category);

        // 3. 含中文字符 → 生成拼音版本供 SQL 同时匹配
        bool hasChinese = text.Any(c => c is >= '\u4e00' and <= '\u9fff');
        string? pinyin = hasChinese ? PinyinHelper.GetPinyin(text) : null;

        // 4. 默认：多字段 OR 搜索（标签 + 文件名 + 备注 + 分类 + 拼音）
        return new SearchQuery(text, SearchMode.Tag, pinyin,
            hasChinese ? SearchIntent.MultiFieldWithPinyin : SearchIntent.MultiField);
    }
}

/// <summary>解析后的搜索查询。</summary>
public sealed record SearchQuery(
    string Text,
    SearchMode Mode,
    string? Pinyin,
    SearchIntent Intent)
{
    public static SearchQuery Empty { get; } =
        new(string.Empty, SearchMode.Tag, null, SearchIntent.None);
}

/// <summary>搜索意图（UI 不需要知道，SQL 层根据 Mode+Intent 决定 WHERE）。</summary>
public enum SearchIntent
{
    None,
    FileName,           // 含扩展名
    Category,           // 完全匹配已知分类
    MultiField,         // 纯 ASCII → tag+filename+notes+category
    MultiFieldWithPinyin, // 含中文 → tag+filename+notes+category+pinyin
}
