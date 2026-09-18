namespace ClipSpike;

/// <summary>QL World 10-category + 4-tag-group zero-shot vocabulary.</summary>
public static class Vocab
{
    public sealed record Term(string Name, string Prompt);

    /// <summary>Prompt template version (bump when wording changes).</summary>
    public const int PromptTemplateVersion = 1;

    public static IReadOnlyList<Term> Categories { get; } =
    [
        new("贴图/纹理", "a seamless texture map pattern"),
        new("实拍参考", "a real photograph"),
        new("概念设计", "a concept art illustration"),
        new("雕刻参考", "a 3d digital sculpture render"),
        new("材质参考", "a material surface close-up"),
        new("环境/场景", "an environment scene landscape"),
        new("灯光参考", "a dramatic lighting study"),
        new("配色参考", "a color palette mood board"),
        new("UI/HUD", "a game user interface screen"),
        new("动画参考", "animation sprite frames"),
    ];

    public static IReadOnlyDictionary<string, IReadOnlyList<Term>> TagGroups { get; } =
        new Dictionary<string, IReadOnlyList<Term>>
        {
            ["风格"] =
            [
                new("硬表面", "hard surface mechanical design"),
                new("有机形态", "organic natural forms"),
                new("风格化", "stylized cartoon art"),
                new("写实", "photorealistic render"),
                new("低多边形", "low poly 3d art"),
                new("手绘", "hand painted texture"),
                new("科幻", "science fiction futuristic"),
                new("奇幻", "fantasy medieval art"),
                new("废土", "post apocalyptic wasteland"),
                new("蒸汽朋克", "steampunk brass machinery"),
            ],
            ["题材"] =
            [
                new("角色", "a character figure"),
                new("生物", "a creature monster"),
                new("武器", "a weapon"),
                new("载具", "a vehicle"),
                new("建筑", "architecture building"),
                new("植物", "plants foliage"),
                new("岩石地形", "rocks terrain"),
                new("道具", "a game prop object"),
                new("机械", "machinery gears engine"),
                new("食物", "food items"),
            ],
            ["材质"] =
            [
                new("金属", "polished metal surface"),
                new("木材", "wood grain"),
                new("石材", "stone rock surface"),
                new("布料", "fabric cloth weave"),
                new("玻璃", "transparent glass"),
                new("塑料", "plastic surface"),
                new("混凝土", "rough concrete wall"),
                new("锈蚀", "rusted corroded metal"),
                new("皮革", "leather"),
                new("液体", "liquid water"),
            ],
            ["色调"] =
            [
                new("暖色调", "warm orange color palette"),
                new("冷色调", "cool blue color palette"),
                new("暗调", "dark moody tones"),
                new("明亮", "bright vibrant colors"),
                new("灰暗", "muted desaturated gray"),
                new("鲜艳", "high saturation vivid"),
                new("单色", "monochrome black and white"),
                new("柔和", "soft pastel colors"),
            ],
        };
}
