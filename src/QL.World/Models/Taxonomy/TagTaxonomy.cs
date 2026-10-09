namespace QL.World.Models.Taxonomy;

/// <summary>
/// M12 (v0.13.0): 标签驱动分类体系——6 个一级分类 + 11 个标签组
/// （6 分类组 + 7 筛选组）。标签决定分类：图片标签命中某分类组的标签时，
/// 由 <see cref="Services.TaxonomyService"/> 推导出 primary_category。
/// 筛选组标签不参与分类推导，仅用于筛选。
/// </summary>
public static class TagTaxonomy
{
    // ─── 6 个一级分类 ──────────────────────────────────────────────

    public const string Character = "角色";
    public const string Creature = "生物";
    public const string Scene = "场景";
    public const string Prop = "道具";
    public const string Texture = "质感";
    public const string Asset = "素材";

    /// <summary>新 6 个一级分类（B1-1 树展示顺序）。</summary>
    public static readonly IReadOnlyList<string> Categories =
    [Character, Creature, Scene, Prop, Texture, Asset];

    // ─── 6 个分类组（标签命中即参与分类推导） ────────────────────

    public const string GroupCharacter = "角色";
    public const string GroupCreature = "生物";
    public const string GroupScene = "场景";
    public const string GroupProp = "道具";
    public const string GroupTexture = "质感";
    public const string GroupAsset = "素材";

    // ─── 7 个筛选组（不参与分类推导） ──────────────────────────────

    public const string GroupArtStyle = "美术风格";
    public const string GroupFileFormat = "文件格式";
    public const string GroupColorTone = "色彩倾向";
    public const string GroupComposition = "画面构图";
    public const string GroupLighting = "灯光氛围";
    public const string GroupTheme = "题材气质";
    public const string GroupCondition = "状态形容";

    /// <summary>6 个分类组 ID（与一级分类同名）。</summary>
    public static readonly IReadOnlyList<string> CategoryGroups =
    [GroupCharacter, GroupCreature, GroupScene, GroupProp, GroupTexture, GroupAsset];

    /// <summary>7 个筛选组 ID。</summary>
    public static readonly IReadOnlyList<string> FilterGroups =
    [GroupArtStyle, GroupFileFormat, GroupColorTone, GroupComposition,
     GroupLighting, GroupTheme, GroupCondition];

    /// <summary>全部 11 个组 ID（分类组在前，筛选组在后）。</summary>
    public static readonly IReadOnlyList<string> AllGroups =
    [.. CategoryGroups, .. FilterGroups];

    // ─── 组 → 标签列表 ───────────────────────────────────────────

    /// <summary>组 ID → 该组包含的全部标签。顺序与用户定义一致。</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> TagsByGroup =
        new Dictionary<string, IReadOnlyList<string>>
        {
            [GroupCharacter] =
            [
                "人类", "少年", "少女", "成年男性", "成年女性", "老人",
                "士兵", "骑士", "佣兵", "法师", "刺客", "贵族", "原住民",
                "机甲人", "改造人", "精灵", "兽人", "恶魔", "天使", "亡灵", "妖怪",
            ],
            [GroupCreature] =
            [
                "野生动物", "奇幻异兽", "巨型生物", "神兽", "虫族",
                "海洋生物", "古生物", "昆虫", "水生巨兽", "植物型生物",
            ],
            [GroupScene] =
            [
                "森林", "雨林", "荒漠", "戈壁", "雪山", "冰川", "沼泽",
                "火山", "洞穴", "地下城", "宫殿", "神庙", "古代城池",
                "现代都市", "实验室", "太空", "空间站", "废墟", "海底",
                "空中浮岛", "末世环境",
            ],
            [GroupProp] =
            [
                "冷兵器", "热武器", "盔甲防具", "载具", "飞行器", "舰船",
                "家具", "器物", "机械零件", "饰品", "食物", "药品",
                "魔法装置", "工业设备",
            ],
            [GroupTexture] =
            [
                "金属", "石材", "木材", "布料", "皮革", "泥土", "冰雪",
                "玻璃", "塑料", "晶体", "毛发", "粘液", "陶瓷",
                "锈蚀", "磨砂", "拉丝", "磨损",
            ],
            [GroupAsset] =
            ["PBR贴图", "纹理包", "笔刷", "HDRI", "Mask图"],

            [GroupArtStyle] =
            [
                "写实", "次世代写实", "照片扫描", "卡通", "Q版",
                "美式卡通", "美式漫画", "日系二次元", "韩系厚涂",
                "赛璐璐", "厚涂", "薄涂", "速写草稿", "水墨国风",
                "岩彩", "敦煌风", "赛博朋克", "蒸汽朋克", "暗黑哥特",
                "LowPoly", "像素风", "扁平化",
            ],
            [GroupFileFormat] =
            ["JPG", "PNG", "WebP", "BMP", "TIFF", "GIF", "PSD", "TGA"],
            [GroupColorTone] =
            [],  // 色彩倾向走色板选择器，不走标签体系
            [GroupComposition] =
            [
                "特写", "半身", "全身", "大远景", "远景", "中景", "近景",
                "横向宽幅", "竖幅", "方形构图", "对称构图", "非对称构图",
            ],
            [GroupLighting] =
            [
                "自然光", "天光", "硬光", "软光", "轮廓光", "体积光",
                "丁达尔", "夜景", "黄昏", "正午", "烛光",
                "魔法自发光", "金属反射", "漫反射",
            ],
            [GroupTheme] =
            [
                "新中式", "欧式", "古风", "科幻", "奇幻", "国风",
                "暗黑", "复古", "末世", "浪漫", "静谧", "狂野",
                "诡谲", "庄重", "活泼", "神秘", "史诗", "市井",
            ],
            [GroupCondition] =
            [
                "完好", "破损", "锈蚀", "风化", "潮湿", "干燥",
                "燃烧", "冰封", "流动", "悬浮", "动态", "静态",
                "陈旧", "崭新",
            ],
        };

    // ─── 标签 → 组 ID（反向映射，O(1) 查找） ─────────────────────
    // 注意：同一标签可能同时出现在分类组和筛选组（如"锈蚀"在质感组
    // 和状态形容组）。分类组优先——TagToGroup 只记录分类组归属，
    // 这样 DeriveCategory 能正确将"锈蚀"归入质感而非当作纯筛选标签。
    // TagsByGroup 仍保留全部组的完整标签列表，供 UI 在两处分别展示。

    /// <summary>标签 → 所属组 ID（分类组优先）。不在词表中的标签返回 null。</summary>
    public static readonly IReadOnlyDictionary<string, string> TagToGroup =
        BuildTagToGroup();

    private static Dictionary<string, string> BuildTagToGroup()
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        // 分类组优先（如"锈蚀"先被质感组登记）
        foreach (string group in CategoryGroups)
        {
            if (TagsByGroup.TryGetValue(group, out var tags))
                foreach (string tag in tags)
                    dict.TryAdd(tag, group);
        }
        // 筛选组仅补充未登记的标签
        foreach (string group in FilterGroups)
        {
            if (TagsByGroup.TryGetValue(group, out var tags))
                foreach (string tag in tags)
                    dict.TryAdd(tag, group);
        }
        return dict;
    }

    // ─── 组 → 所属一级分类（分类组映射到自身，筛选组映射到 null） ──

    /// <summary>组 ID → 所属一级分类。分类组映射到同名的分类；筛选组映射到 null。</summary>
    public static readonly IReadOnlyDictionary<string, string?> GroupToCategory =
        AllGroups.ToDictionary(
            g => g,
            g => CategoryGroups.Contains(g) ? g : null,
            StringComparer.Ordinal);

    // ─── 查询方法 ─────────────────────────────────────────────────

    /// <summary>判断字符串是否为已知的一级分类。</summary>
    public static bool IsKnownCategory(string? category) =>
        category is not null && Categories.Contains(category);

    /// <summary>判断标签是否在词表中（任意组）。</summary>
    public static bool IsKnownTag(string tag) =>
        TagToGroup.ContainsKey(tag);

    /// <summary>标签 → 所属组 ID。不在词表返回 null。</summary>
    public static string? GroupOf(string tag) =>
        TagToGroup.TryGetValue(tag, out string? group) ? group : null;

    /// <summary>标签 → 所属一级分类。属于分类组返回分类名；属于筛选组或不在词表返回 null。</summary>
    public static string? CategoryOfTag(string tag) =>
        TagToGroup.TryGetValue(tag, out string? group)
            ? GroupToCategory[group]
            : null;

    /// <summary>分类 → 该分类组包含的全部标签（二级分类）。</summary>
    public static IReadOnlyList<string> TagsOfCategory(string category) =>
        TagsByGroup.TryGetValue(category, out var tags) ? tags : [];

    /// <summary>全部标签总数（不含色彩倾向空组）。</summary>
    public static int TotalTagCount => TagToGroup.Count;
}
