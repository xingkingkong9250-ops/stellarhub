namespace ClipSpike;

/// <summary>
/// M12 (v0.13.0): QL World 6-category + 11-tag-group zero-shot vocabulary.
/// 标签驱动分类：6 分类组（角色/生物/场景/道具/质感/素材）的标签决定
/// primary_category；7 筛选组（美术风格/文件格式/色彩倾向/画面构图/
/// 灯光氛围/题材气质/状态形容）仅用于筛选。色彩倾向走色板不走词表。
/// </summary>
public static class Vocab
{
    public sealed record Term(string Name, string Prompt);

    /// <summary>Prompt template version (bump when wording changes).</summary>
    public const int PromptTemplateVersion = 2;

    // ─── 6 个一级分类 ──────────────────────────────────────────

    public static IReadOnlyList<Term> Categories { get; } =
    [
        new("角色", "a character concept art, person figure"),
        new("生物", "a creature beast monster concept art"),
        new("场景", "an environment scene landscape concept art"),
        new("道具", "a prop object item concept art"),
        new("质感", "a material texture surface close-up"),
        new("素材", "a seamless texture map resource asset"),
    ];

    // ─── 11 个标签组（6 分类组 + 5 筛选组，色彩倾向走色板） ────

    public static IReadOnlyDictionary<string, IReadOnlyList<Term>> TagGroups { get; } =
        new Dictionary<string, IReadOnlyList<Term>>
        {
            // ─── 分类组（标签命中即参与分类推导） ──────────────
            ["角色"] =
            [
                new("人类", "a human person character"),
                new("少年", "a young boy character"),
                new("少女", "a young girl character"),
                new("成年男性", "an adult male man character"),
                new("成年女性", "an adult female woman character"),
                new("老人", "an elderly old person character"),
                new("士兵", "a soldier in military gear"),
                new("骑士", "a knight in armor"),
                new("佣兵", "a mercenary fighter character"),
                new("法师", "a mage wizard casting magic"),
                new("刺客", "an assassin rogue character"),
                new("贵族", "a noble aristocrat character"),
                new("原住民", "an indigenous tribesperson character"),
                new("机甲人", "a mecha android robot character"),
                new("改造人", "a cyborg augmented human character"),
                new("精灵", "an elf character with pointed ears"),
                new("兽人", "an orc beastman character"),
                new("恶魔", "a demon fiend character"),
                new("天使", "an angel character with wings"),
                new("亡灵", "an undead skeleton zombie character"),
                new("妖怪", "a yokai spirit monster character"),
            ],
            ["生物"] =
            [
                new("野生动物", "a wild animal in nature"),
                new("奇幻异兽", "a fantasy mythical creature"),
                new("巨型生物", "a giant enormous creature"),
                new("神兽", "a divine sacred beast"),
                new("虫族", "an insectoid bug creature"),
                new("海洋生物", "a marine sea creature"),
                new("古生物", "a prehistoric dinosaur creature"),
                new("昆虫", "a close-up insect bug"),
                new("水生巨兽", "a giant sea leviathan monster"),
                new("植物型生物", "a plant-like sentient creature"),
            ],
            ["场景"] =
            [
                new("森林", "a forest scene with trees"),
                new("雨林", "a tropical rainforest jungle scene"),
                new("荒漠", "a desert barren wasteland scene"),
                new("戈壁", "a rocky gobi desert scene"),
                new("雪山", "a snowy mountain peak scene"),
                new("冰川", "a glacier ice field scene"),
                new("沼泽", "a swamp marsh wetland scene"),
                new("火山", "a volcano lava eruption scene"),
                new("洞穴", "a cave underground cavern scene"),
                new("地下城", "a dungeon underground city scene"),
                new("宫殿", "a palace grand hall interior"),
                new("神庙", "a temple shrine ancient structure"),
                new("古代城池", "an ancient city walled town"),
                new("现代都市", "a modern city urban streetscape"),
                new("实验室", "a laboratory interior scene"),
                new("太空", "outer space stars nebula scene"),
                new("空间站", "a space station interior"),
                new("废墟", "ruins abandoned destroyed buildings"),
                new("海底", "an underwater deep sea scene"),
                new("空中浮岛", "a floating island in the sky"),
                new("末世环境", "a post-apocalyptic environment scene"),
            ],
            ["道具"] =
            [
                new("冷兵器", "a melee weapon sword axe"),
                new("热武器", "a firearm gun weapon"),
                new("盔甲防具", "armor protective gear equipment"),
                new("载具", "a vehicle transport"),
                new("飞行器", "an aircraft flying vehicle"),
                new("舰船", "a naval ship vessel"),
                new("家具", "furniture chair table interior"),
                new("器物", "a tool utensil object"),
                new("机械零件", "mechanical gears parts components"),
                new("饰品", "an accessory jewelry ornament"),
                new("食物", "food items meal"),
                new("药品", "medicine potion bottle"),
                new("魔法装置", "a magical artifact device"),
                new("工业设备", "industrial machinery equipment"),
            ],
            ["质感"] =
            [
                new("金属", "polished metal surface"),
                new("石材", "stone rock surface texture"),
                new("木材", "wood grain texture surface"),
                new("布料", "fabric cloth weave texture"),
                new("皮革", "leather material texture"),
                new("泥土", "dirt soil earth texture"),
                new("冰雪", "ice snow frozen texture"),
                new("玻璃", "transparent glass material"),
                new("塑料", "plastic surface material"),
                new("晶体", "crystal gem mineral texture"),
                new("毛发", "hair fur texture surface"),
                new("粘液", "slime goo liquid texture"),
                new("陶瓷", "ceramic pottery glaze surface"),
                new("锈蚀", "rusted corroded metal surface"),
                new("磨砂", "matte frosted surface texture"),
                new("拉丝", "brushed metal surface texture"),
                new("磨损", "worn weathered surface texture"),
            ],
            ["素材"] =
            [
                new("PBR贴图", "a PBR texture map material asset"),
                new("纹理包", "a texture pack seamless pattern"),
                new("笔刷", "a digital brush stamp asset"),
                new("HDRI", "an HDRI environment panorama"),
                new("Mask图", "a mask alpha channel texture"),
            ],

            // ─── 筛选组（不参与分类推导） ──────────────────────
            ["美术风格"] =
            [
                new("写实", "photorealistic render"),
                new("次世代写实", "next-gen photorealistic 3d render"),
                new("照片扫描", "photogrammetry scanned 3d model"),
                new("卡通", "cartoon stylized art"),
                new("Q版", "chibi cute super deformed art"),
                new("美式卡通", "american cartoon style"),
                new("美式漫画", "american comic book style"),
                new("日系二次元", "anime japanese art style"),
                new("韩系厚涂", "korean thick painting style"),
                new("赛璐璐", "cel-shaded anime art"),
                new("厚涂", "thick impasto painting style"),
                new("薄涂", "thin wash watercolor painting"),
                new("速写草稿", "quick sketch draft"),
                new("水墨国风", "chinese ink wash painting"),
                new("岩彩", "colored rock mineral pigment art"),
                new("敦煌风", "dunhuang mural fresco art style"),
                new("赛博朋克", "cyberpunk neon dystopian style"),
                new("蒸汽朋克", "steampunk brass victorian style"),
                new("暗黑哥特", "dark gothic horror art style"),
                new("LowPoly", "low poly 3d art"),
                new("像素风", "pixel art retro game style"),
                new("扁平化", "flat design minimalist style"),
            ],
            ["画面构图"] =
            [
                new("特写", "extreme close-up shot composition"),
                new("半身", "half-body portrait composition"),
                new("全身", "full-body character composition"),
                new("大远景", "extreme wide establishing shot"),
                new("远景", "wide angle distant shot"),
                new("中景", "medium shot composition"),
                new("近景", "close shot composition"),
                new("横向宽幅", "panoramic wide aspect composition"),
                new("竖幅", "vertical portrait orientation"),
                new("方形构图", "square 1:1 aspect composition"),
                new("对称构图", "symmetrical balanced composition"),
                new("非对称构图", "asymmetrical dynamic composition"),
            ],
            ["灯光氛围"] =
            [
                new("自然光", "natural daylight outdoor lighting"),
                new("天光", "sky light ambient illumination"),
                new("硬光", "hard direct harsh lighting"),
                new("软光", "soft diffuse lighting"),
                new("轮廓光", "rim light edge lighting"),
                new("体积光", "volumetric god rays lighting"),
                new("丁达尔", "tyndall effect light rays"),
                new("夜景", "night scene dark lighting"),
                new("黄昏", "golden hour dusk lighting"),
                new("正午", "noon bright sunlight"),
                new("烛光", "candlelight warm glow"),
                new("魔法自发光", "magical glowing emission light"),
                new("金属反射", "metallic reflection specular highlight"),
                new("漫反射", "diffuse reflection ambient lighting"),
            ],
            ["题材气质"] =
            [
                new("新中式", "modern chinese style aesthetic"),
                new("欧式", "european western style aesthetic"),
                new("古风", "ancient traditional chinese style"),
                new("科幻", "science fiction futuristic theme"),
                new("奇幻", "fantasy magical medieval theme"),
                new("国风", "chinese national style aesthetic"),
                new("暗黑", "dark horror sinister theme"),
                new("复古", "retro vintage style theme"),
                new("末世", "post-apocalyptic wasteland theme"),
                new("浪漫", "romantic dreamy theme"),
                new("静谧", "serene calm peaceful mood"),
                new("狂野", "wild untamed energy mood"),
                new("诡谲", "eerie uncanny mysterious mood"),
                new("庄重", "solemn dignified grand mood"),
                new("活泼", "lively playful energetic mood"),
                new("神秘", "mysterious enigmatic atmosphere"),
                new("史诗", "epic grand heroic scale mood"),
                new("市井", "everyday street life mundane scene"),
            ],
            ["状态形容"] =
            [
                new("完好", "pristine intact undamaged condition"),
                new("破损", "broken damaged cracked condition"),
                new("锈蚀", "rusted corroded condition"),
                new("风化", "weathered eroded condition"),
                new("潮湿", "wet damp moist condition"),
                new("干燥", "dry arid condition"),
                new("燃烧", "burning on fire flames condition"),
                new("冰封", "frozen ice-covered condition"),
                new("流动", "flowing liquid moving condition"),
                new("悬浮", "floating levitating condition"),
                new("动态", "dynamic motion action pose"),
                new("静态", "static still posed condition"),
                new("陈旧", "old aged worn condition"),
                new("崭新", "brand new fresh pristine condition"),
            ],
        };
}
