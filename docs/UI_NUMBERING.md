# UI 编号表（唯一真源）

规则：W=窗口；W1 下 A=顶部横区/页签、B=主分区、C=底部区；编号下钻到控件（如 B3-3）。
编号是位置锚点：功能变更必须核对；删除即作废不复用；新增顺延。
每次 UI 变更必须同步本表。

| 编号 | 名称 | 状态 | 说明 |
|---|---|---|---|
| W1 | 主窗口 | 已实现（M1） | 1400×850，最小 1100×700，PerMonitorV2 |
| W1/A1 | 顶栏（菜单行 40 + 工具条行 44 = 84；菜单行兼任标题栏） | 已实现（M1，v0.6.2 改版） | v0.6.2 用 WindowChrome 合并系统标题栏与 Row 0 菜单行；Row 0 = 标题栏（可拖动 + 自定义 caption 三按钮，因 Windows 11 上 AeroCaptionButtons 不显示）；Row 1 工具条行（A1-1…A1-8 控件）；解决窄窗口下右侧控件被遮挡问题 |
| W1/A1-1 | 「导入」按钮 | 已实现（M1） | 「＋ 导入图片」，打开 W2 |
| W1/A1-2 | 「AI 分析」按钮 | 已实现（M3，v0.9.0 语义调整） | 仅分析**无任何标签且无分类**的图（人工/AI 标签均算有标签，跳过不刷新）；AI 不可用时置灰 + ToolTip 说明原因；启动软件不自动分析 |
| W1/A1-3 | 视图切换（网格/列表/浏览） | 已实现（M1+B2-3-BRW，v0.12.3 迁移位置） | 三态 ToggleButton 互斥切换；v0.12.3 起从顶部 Row 1 工具条右侧整组迁移至 B2 画廊工具条左侧（W1/B2-1 区），更贴近画廊与缩放/排序同栏；网格/列表各一支缩放滑杆，浏览态独立 160–512 卡片宽度滑杆；v0.12.3 起默认视图为 Browse（瀑布流） |
| W1/A1-4 | 搜索框 | 已实现（M1） | AutomationId 语义名 SearchBox，300ms 防抖 |
| W1/A1-5 | 搜索模式下拉 | 已实现（M1） | 标签 / 文件名·备注 / 语义（M3-E1） |
| W1/A1-6 | 通知铃铛 | 已实现（M2） | 未读红点徽标 + 弹层列表（未读标记/全部已读/清空）；v0.7.3 修复弹层绑定路径（VM 转发 Items，此前列表恒空仅徽标正常） |
| W1/A1-7 | 设置齿轮 | 已实现（M2） | 打开 W3 |
| W1/A1-8 | 「分析颜色」按钮 | 已实现（M3-D） | 手动批量回填 dominant_colors；与 AI 档位无关；占用主线程串行执行 |
| W1/A1-9 | 品牌 Logo 图块 + 名称 + AI 按钮品牌渐变 | 已实现（U2-A，v0.7.0 焕新） | v0.7.0 起 Logo 为 22×22 圆角品牌图「星光相框猫」（Assets/brand-logo.jpg，ImageBrush UniformToFill + BorderSubtle 描边），替换原 20×20 渐变占位色块；名称「千星汇图」；AI 按钮 hover 切 AccentGradientBrush（青蓝 #0E9FC0→#3B82F6）+ TextOnAccentBrush 文字。全部窗口图标统一 Assets/app.ico（Generic.xaml 隐式 Window 样式 + csproj ApplicationIcon） |
| W1/A1-10 | 「🔄 刷新」按钮 | 已实现（v0.8.0） | 顶栏工具条（扫描 NAS 之后）；主动刷新：逐个重扫全部监控目录（ImportService 按 hash 幂等去重）→ 重载画廊 → 状态栏汇报，有新增联动主色调回填；无监控目录退化为纯画廊重载；F5 快捷键同命令；<1500px 收窄为 🔄 图标 |
| W1/A1-M | 文本菜单栏（VS Code 风格 Row 0，兼任标题栏） | 已实现（v0.6.2） | Menu + MenuItem；IsMainMenu=True；CaptionHeight=40 让 Row 0 兼任可拖动标题栏；自定义 CaptionButton/CaptionCloseButton Style 替代 AeroCaptionButtons（Windows 11 不显示系统按钮）；Menu + 右侧系统区均加 IsHitTestVisibleInChrome=True；子项见 A1-M-FILE / A1-M-VIEW / A1-M-TOOLS / A1-M-HELP |
| W1/A1-M-FILE | 「文件(_F)」菜单 | 已实现（v0.6.2） | 导入图片（Ctrl+I）/ 扫描 NAS 库 / 下载队列 |
| W1/A1-M-VIEW | 「视图(_V)」菜单 | 已实现（v0.6.2） | 三态视图（IsCheckable 互斥：网格/列表/浏览）+ 分隔 + 本地库/共享库（仅 IsSharedLibraryAvailable 可见） |
| W1/A1-M-TOOLS | 「工具(_T)」菜单 | 已实现（v0.6.2） | AI 分析 / 分析颜色 / 分隔 + 审核队列（仅 IsReviewAvailable 可见）/ 分隔 / 设置 |
| W1/A1-M-HELP | 「帮助(_H)」菜单 | 已实现（v0.6.2） | 关于（→设置）/ 打开日志文件夹 / 分隔 / 快捷键提示（IsEnabled=False，引导看 C1-R） |
| W1/A1-T | 工具条行（Row 1，高 42） | 已实现（v0.6.2，v0.12.3 调整） | 三列 Grid：左 Auto=导入/扫描/AI/颜色/下载/审核；中 *=搜索框+搜索模式+最近入库（CompactHidePanel）；右 Auto=本地/共享库切换（视图切换三按钮 v0.12.3 起迁移至 W1/B2-1 区） |
| W1/B1 | 左栏（220–300，实配 240） | 已实现（M2） | |
| W1/B1-1 | 素材库树 | 已实现（M1，M12-F 重构，v0.13.3 二级改气泡） | 全部 + 6 新分类（角色/生物/场景/道具/质感/素材，M12 语义：标签命中分类组词表→推导 primary_category）+ 未分类；分类行可展开（▸/▾ 箭头），展开后该分类组标签以**圆角气泡 chips + WrapPanel 自动换行**呈现（v0.13.3 起替代整行纵列，节省纵向空间；TreeTagChipStyle 圆角 8、选中 Accent 填充），点击二级标签按单标签筛选（Filter.Tag，与 B1-3 同语义）；含子项计数显示 |
| W1/B1-2 | 收藏夹树 | 已实现（M2） | 单击筛选/再点清除；右键建/改名/删/设封面/加入；画廊拖拽加入；含子项计数显示（U1-B 核实） |
| W1/B1-3 | 高频标签 Top50 | 已实现（M1） | 含计数显示（TopTag Count，U1-B 核实） |
| W1/B1-4 | 贴图组树 | 已实现（M4） | PBR 组（目录+基名成组）；单击筛选画廊/再点清除；节点显示成员数（U1-B 核实） |
| W1/B1-5 | 属性筛选折叠组 | **已作废（v0.13.5）** | 原分区整体取消：颜色 v0.13.4 并入 B1-6-3 色板；分辨率/文件大小/评分 v0.13.5 并入 B1-6-8/9/10。B1-5 及 B1-5-1~4 编号全部作废永不复用；设置项 sidebar_attribute_filter_expanded 同步删除 |
| W1/B1-5-1 | 属性筛选-颜色 | **已作废（v0.13.4）** | 原 9 色族单选 chips（红/橙/黄/绿/青/蓝/紫/品红/灰）与 B1-6-3 色彩倾向色板功能重叠（均筛 images.color_family），已合并进 B1-6-3；编号作废永不复用，字段 LibraryFilter.ColorFamily 与单选 SQL 子句同步删除 |
| W1/B1-5-2 | 属性筛选-分辨率 | **已作废（v0.13.5）** | 迁入 B1-6-8；编号作废永不复用 |
| W1/B1-5-3 | 属性筛选-文件大小 | **已作废（v0.13.5）** | 迁入 B1-6-9；编号作废永不复用 |
| W1/B1-5-4 | 属性筛选-评分 | **已作废（v0.13.5）** | 迁入 B1-6-10；编号作废永不复用 |
| W1/B1-COL | B1 四分区折叠+计数 | 已实现（U3-C） | 素材库/收藏夹/热门标签/贴图组标题改为 ToggleButton（▾/▸ 箭头）；折叠态持久化（settings.sidebar_*_expanded，默认全展开）；折叠时标题显示子项总数「名称 (N)」，展开为纯名；B1-6 筛选标签组沿用同一 SidebarSectionHeaderStyle（原 B1-5 已作废） |
| W1/B1-6 | 筛选标签组折叠区 | 已实现（M12-F，v0.13.5 扩为 10 项） | 默认收起，折叠态持久化（settings.sidebar_filter_groups_expanded）；10 个子项 B1-6-1~10（7 标签/格式/色板组 + 3 原属性桶）；组内 OR、组间 AND，与 B1/A1 其它筛选叠加；「清除全部筛选」一并重置 |
| W1/B1-6-1 | 筛选标签组-美术风格 | 已实现（M12-F） | 22 标签多选 chips（写实/卡通/日系二次元…）；写入 LibraryFilter.ArtStyleTags（EXISTS image_tags，组内 OR） |
| W1/B1-6-2 | 筛选标签组-文件格式 | 已实现（M12-F） | JPG/PNG/WebP/BMP/TIFF/GIF/PSD/TGA 多选 chips；写入 FileFormats（与 images.format 不区分大小写比对，不走 image_tags） |
| W1/B1-6-3 | 筛选标签组-色彩倾向（色板） | 已实现（M12-F，v0.13.4 合并色族维度） | 19 色多选色板（8 饱和 + 7 中间调 + 4 中性；中性色映射灰族）；写入 ColorPaletteHexes，逐个经 ColorFamily.HexToFamily 映射色族后与 i.color_family 匹配（OR）。v0.13.4 起为**唯一的色彩筛选入口**：原 B1-5-1 九色族单选 chips 已并入（色板覆盖全部色族且支持多选）。注：计划原列 B1-5-5 色板，落地时归入 B1-6-3 避免与色彩倾向组重复，B1-5-5 编号作废不复用 |
| W1/B1-6-4 | 筛选标签组-画面构图 | 已实现（M12-F） | 12 标签多选 chips（特写/全身/对称构图…）；CompositionTags |
| W1/B1-6-5 | 筛选标签组-灯光氛围 | 已实现（M12-F） | 14 标签多选 chips（自然光/体积光/丁达尔…）；LightingTags |
| W1/B1-6-6 | 筛选标签组-题材气质 | 已实现（M12-F） | 18 标签多选 chips（新中式/赛博朋克气质…）；ThemeTags |
| W1/B1-6-7 | 筛选标签组-状态形容 | 已实现（M12-F） | 14 标签多选 chips（完好/破损/锈蚀…；「锈蚀」同时属质感分类组，分类推导优先）；ConditionTags |
| W1/B1-6-8 | 筛选标签组-分辨率（长边） | 已实现（v0.13.5，原 B1-5-2） | 4 桶**单选** chips（<1024 / 1024–2047 / 2048–4095 / ≥4096，无「全部」，点已选项清除）；写 ResolutionBucket，按 MAX(width,height) 过滤，NULL 尺寸不匹配任何档 |
| W1/B1-6-9 | 筛选标签组-文件大小 | 已实现（v0.13.5，原 B1-5-3） | 4 桶单选 chips（<1 MB / 1–5 MB / 5–20 MB / ≥20 MB）；FileSizeBucket |
| W1/B1-6-10 | 筛选标签组-评分 | 已实现（v0.13.5，原 B1-5-4） | 4 桶单选 chips（未评分 / ≥3★ / ≥4★ / 5★）；RatingBucket |
| W1/B2 | 画廊区 | 已实现（M1） | |
| W1/B2-1 | 视图切换+筛选摘要 | 已实现（M1，v0.12.3 重定位） | v0.12.3 起：W1/A1-3 视图切换三按钮（网格/列表/浏览）+ 分隔 + 筛选摘要（FilterSummary，无筛选时隐藏）。原「显示 N 张 / 共 M」计数移除，计数职责完全交给 W1/C1-C 状态栏居中区（早已存在） |
| W1/B2-2 | 排序下拉 | 已实现（M1） | 必须真实生效并持久化 |
| W1/B2-3 | 图卡（网格/列表） | 已实现（M1） | VirtualizingTilePanel UI 虚拟化 |
| W1/B2-3-SEL | 选中态视觉锚点 | 已实现（U1-A） | 网格 2px Accent 外描边；列表 3px 左竖条+底线（Padding 同步减 3px 抵消 layout shift）；底色由 ListBoxItem AccentBg 提供 |
| W1/B2-3-OL1 | 「最近入库」下拉 | 已实现（U2-B） | 今天/近 7 天/近 30 天/全部；持久化到 settings.gallery_recent_import_days |
| W1/B2-3-BDG | 缩略图来源徽标 | 已实现（U2-D） | 网格右下角/列表右上角；三态「本地/云端/AI」，无 AI 数据时隐藏 |
| W1/B2-3-TAG | 卡片标签摘要行 | 已实现（U2-E） | 网格卡片第三行：前 2 标签 + 「+N」；无标签隐藏 |
| W1/B2-3-CM1 | 图卡右键「找相似图」 | 已实现（M3-E1）/ 冒烟（M4-D） | 按 CLIP embedding 余弦相似度返回 top-50；无 embedding 时置灰；UiSmokeB1.ps1 覆盖 |
| W1/B2-3-ACT | 卡片 hover 操作组 | 已实现（U3-B） | 仅网格态：缩略图底部三按钮浮层（打开原文件↗/资源管理器定位⌖/复制路径⧉），hover 浮出、移出即隐；作用于悬停卡片（参数化命令）而非当前选中；缺失卡被缺失遮罩覆盖天然不可触且 CanExecute 排除；列表态不加 |
| W1/B2-4 | 缩放滑杆（80–512） | 已实现（M1+B2-3-BRW） | 网格/列表各一支；浏览态 160–512 卡片宽度滑杆，按视图显隐 |
| W1/B2-3-BRW | 浏览（瀑布流）视图 | 已实现（B2-3-BRW） | 三态视图之一；VirtualizingMasonryPanel 等宽不等高瀑布流（短列优先放置），GalleryItemViewModel.AspectRatio 决定每张卡片高度；最小文本：仅来源徽标 + 评分 chip + hover 操作浮层，无文件名/标签/尺寸文本 |
| W1/B3 | 详情栏（280–400，实配 300） | 已实现（M1） | 含多选批量编辑态 |
| W1/B3-1 | 大图预览 | 已实现（M1） | 缺失文件覆盖缺失态 |
| W1/B3-2 | 文件信息/缺失态 | 已实现（M1） | 定位/复制路径/打开原文件；重新定位（M2）已接入 |
| W1/B3-3 | 评分（1–5） | 已实现（M1） | Ctrl+1…5 快捷键，立即落库 |
| W1/B3-4 | 主色调 | 已实现（U4-A） | ≤5 个 20×20 色块（dominant_colors JSON 解析），tooltip 显示 hex；未分析/数据损坏整行隐藏；仅展示无交互 |
| W1/B3-5 | 标签 chips + 输入 | 已实现（M1） | AI/manual 视觉区分 |
| W1/B3-6 | 分类下拉 | 已实现（M1） | |
| W1/B3-7 | 备注（自动保存） | 已实现（M1） | 800ms 防抖落库，保存提示 |
| W1/B3-8 | 详情栏「找相似」按钮 | 已实现（M3-E1）/ 冒烟（M4-D） | 同 W1/B2-3-CM1，详情面板内便捷入口；UiSmokeB1.ps1 覆盖 |
| W1/B3-9 | 「相关资产」section | 已实现（U2-C） | 前 6 个语义相似缩略图水平排列；点击切换选中；无结果隐藏 |
| W1/C1 | 状态栏（高 28） | 已实现（M1） | 状态/任务进度/结果数/AI 档位 |
| W1/C1-L | 状态文本 | 已实现（M1） | 「● 状态文本」左对齐 |
| W1/C1-C | 计数（筛选/总数） | 已实现（M1） | 居中显示 N / M |
| W1/C1-R | 快捷键提示 + AI 档位 + 软件署名 | 已实现（U1-C，v0.12.3 扩展） | 快捷键提示按上下文切换（空库/无选中/单选/多选）；AI 档位固定；v0.12.3 起在 AI 档位文本之后追加 ` · {EnglishName} vX.Y.Z` 署名（绑 ShellViewModel.AppVersionText） |
| W1/C1-PB | 细进度条 | 已实现（U2-A） | 2px IsIndeterminate，AccentGradientBrush 前景；导入/AI 运行/缩略图挂起时显示 |
| W1/C1-VER | 品牌化版本号 | 已实现（U2-F） | 顶部 Row 0 菜单行右侧显示「vX.Y.Z · 里程碑标签」（绑 ShellViewModel.VersionText），仅 Shell；W3/A5 关于页保持纯版本（SettingsViewModel.VersionText） |
| W1/C1-NAME | 底部软件署名 | 已实现（v0.12.3） | 底部状态栏 C1-R 区 AI 档位（AiStatusText）之后追加 ` · StellarHub vX.Y.Z`（绑 ShellViewModel.AppVersionText = `{EnglishName} v{Version}`）；与顶部 C1-VER 品牌化展示并存，本编号仅用于底部简洁署名 |
| W2 | 导入对话框（520×480，可调，最小 460×420） | 已实现（M1） | 来源/选项/进度/日志/取消/完成 |
| W2/AI | 「AI 分析（导入完成后自动执行）」三选一 | 已实现（v0.9.0） | 位于文件格式之后：关闭 AI（默认，每次打开重置）/ 云端 AI / 本地 AI；选云端或本地时导入运行结束即对本次新增图按所选后端作用域分析，进度写底部信息提示栏（BatchProgress 事件）；后端按次解析（AiServiceFactory.Create(settings, mode)），不改写 W3/A3 全局设置 |
| W3 | 设置窗口 | 已实现（M3） | 480×640 TabControl：A1 常规 / A2 缩略图 / A3 AI / A4 监控 / A5 关于 / A6 贴图组；v0.7.2 起全部页签内容包 ScrollViewer（TabScrollStyle，矮窗口自适应滚动条）+ 隧道阶段滚轮统一接管（TabScroll_PreviewMouseWheel，防 ListBox/Slider 吞滚轮），A1–A8 编号不变 |
| W3/A1 | 常规页签 | 已实现（M2，M11 加托管卡片） | 主题（Dark/Light 即时切换）/完整性检查开关；库位置随数据根（M3 可视化）；M11 起底部增「素材库托管目录（M11）」卡片（子编号 W3/A1-LIB*） |
| W3/A1-LIB | 素材库托管卡片 | 已实现（M11） | 常规页签底部独立 Border 卡片：只读库目录 + 置灰修改按钮 + 未托管图开关 |
| W3/A1-LIB-PATH | 库目录只读文本框 | 已实现（M11） | 绑 LibraryRootDisplayText；未配置时显「未配置（旧版直存模式）」；库目录仅 W7 首启向导可设 |
| W3/A1-LIB-CHG | 「修改…」按钮 | 已实现（M11） | 恒置灰（IsEnabled=False），M11 不支持迁移库目录，Tooltip 引导退出后手动迁移 |
| W3/A1-LIB-SHOW | 「显示未托管的旧版图片」开关 | 已实现（M11） | 绑 ShowLegacyImages；仅 W7 选过「跳过」（LegacyDataSkipped=true）时可用；翻转经 LegacyVisibilityChanged 事件即时重查画廊；软跳过可逆，不删数据 |
| W3/A2 | 缩略图页签 | 已实现（M2） | 边长/质量滑杆 + 全部重新生成 |
| W3/A3 | AI 页签 | 已实现（M3 第二阶段） | 三档单选 + 云端配置（base_url/模型/API Key/测试连接）+ 本地 ONNX 区块（状态文本/进度条/下载·取消·手动导入·删除模型四按钮/体积·许可·来源小字行；M3-E2 起下载为 vision+text 模型捆绑包） |
| W3/A3-LEARN | 「自适应学习」开关 | 已实现（v0.10.0） | 位于 AI 分析模式之后，默认开：融合词表学习/保留率重排/kNN 投票改进本地打标；关闭仅跳过精排，训练数据快照（ai_tag_suggestions）与编辑日志（tag_feedback）仍持续留存 |
| W3/A4 | 监控目录页签 | 已实现（M2） | 目录列表/添加/移除/自动导入开关，即时生效 |
| W3/A5 | 关于页签 | 已实现（M2，v0.7.0 加品牌 Logo） | 36×36 圆角品牌 Logo + 名称/版本横排；统计/旧版 Python 库导入/打开日志文件夹 |
| W3/A6 | 贴图组规则页签 | 已实现（M4） | 尾缀→角色规则行（模式/角色下拉/启用/删除）+ 添加规则/保存并重建/仅重建/恢复默认 + 状态文本 |
| W3/A7 | NAS 连接页签 | 已实现（M5 WebDav + M7 Smb + v0.2.1 主 NAS/门禁 + M8 ShareLink/聚合/迁移 + M9 Sftp） | 连接列表（N1）/ 详情表单 N2-N5 / 测试连接 + 设为主 NAS + 保存（N6）；M9 起协议下拉三选项齐备（N3-N5 联动）；M8-D 加「🔗 分享链接」入口 + 列表 🔗 图标区分 ShareLink 副库；M8-E 加「设为主 NAS 并迁移数据」+「搬运到此处...」（管理员可见）+ 迁移进度文案 +「显示数据来源徽章」CheckBox；v0.2.1 加管理员门禁（IsNasManagementEnabled：未登录或管理员可见，普通用户隐藏） |
| W3/A7-N1 | NAS 连接列表 + 新建/删除 + 主 NAS 标记 + 分享链接 | 已实现（M5-C + v0.2.1 + M8-D） | ObservableCollection 绑定；新建按钮分配单调递增 id（不复用已删除）；M8-D 加「🔗 分享链接」按钮（Role=ShareLink，匿名访问，无密码框）；删除按钮清除选中连接（含密码）；v0.2.1 列表项「★ 主」徽标（IsPrimary=true 显示），M8-D 加「🔗」图标（IsShareLink=true 显示） |
| W3/A7-N2 | 名称 | 已实现（M5-C） | 唯一显示名；绑定 SelectedNasConnection.Name |
| W3/A7-N3 | 协议 + 主机 + 端口 | 已实现（M5-C + M7-C + M9-C） | 协议下拉 WebDav + Smb + Sftp（M9）；主机文本（IP/域名，不含 scheme）；端口提示按协议显隐（WebDav 443/80 / Smb 445 / Sftp 22）；切换协议若 Port 为 null 或任一已知默认（443/445/22）自动填新协议默认（自定义端口保留） |
| W3/A7-N4 | 用户名 + 密码 | 已实现（M5-C + M9-C） | 用户名标签动态（WebDav/Smb 留空=匿名；Sftp 必填不支持匿名）；PasswordBox 永不回填（安全），DPAPI(CurrentUser) 加密落 config.json |
| W3/A7-N5 | 根路径 + TLS + 自签 | 已实现（M5-C + M7-C + M9-C） | 根路径提示按协议显隐（WebDav `/photos/` / Smb `StellarHubShared/` / Sftp `/volume1/StellarHubShared`）；UseTls 标签随协议切换（WebDav「使用 HTTPS/TLS（推荐）」/ Smb「SMB3 加密协商」），Visibility 绑 IsTlsApplicable（Sftp 走 SSH 隐藏）；TrustSelfSigned 标签绑 NasTrustLabelText（WebDav 信任自签证书 / Sftp 信任未知 SSH 主机密钥），Visibility 绑 IsTrustSelfSignedApplicable（WebDav+Sftp 显，Smb 隐） |
| W3/A7-N6 | 测试连接 + 设为主 NAS（仅切标记）+ 保存 | 已实现（M5-C + v0.2.1 + M8-E 改名） | 测试：INasConnectorFactory.Create + TestConnectionAsync；设为主 NAS（仅切标记，v0.2.1）：SetPrimaryCommand 选中项 IsPrimary=true + 其他全部 false + 逐个 UpdateNasConnection 持久化（选中项已为主时禁用；M8-E 起亦在 IsMigrating 时禁用）；保存：UpdateNasConnection（保留 EncryptedPassword/CreatedAt） |
| W3/A7-N6-MIG | 设为主 NAS 并迁移数据 | 已实现（M8-E） | 管理员可见（IsAdmin）；SetPrimaryWithMigrationCommand 选中副库时可用；SetPrimaryWithMigrationAsync 递归统计源主库 originals+thumbnails+pending 文件数+字节数 → 弹确认框「将迁移约 N 个文件（约 X MB）」→ 调 PrimaryMigrationService.MigrateAsync 4 阶段（元数据/文件树/accounts 最后覆盖/Role 切换）；进度回调实时刷新 MigrationStatusText；失败不切 Role 保留旧主可重试（幂等） |
| W3/A7-N6-XFER | 搬运到此处... | 已实现（M8-E） | 管理员 + 选中副库（非 Primary/非 ShareLink）时可用；TransferToHereCommand 弹引导 MessageBox「请在共享画廊选中 N 张图片 → 右键 → 搬运到副库」（实际搬运流程入口在画廊右键菜单，DataTransferService 已就绪） |
| W3/A7-N6-SOURCE | 显示数据来源徽章 CheckBox | 已实现（M8-E） | IsChecked 绑 ShowSourceBadge（AppSettingsService 持久化，默认 false）；开启后画廊 SourceBadge 由「共享」改显「nas://{id}」便于管理员区分主副库数据 |
| W1-N7 | 扫描 NAS 库 | 已实现（M5-D） | 工具栏「📡 扫描 NAS」按钮；0 连接→提示并打开 W3/A7；1 连接→直扫；>1 连接→代码内建选择器（ComboBox + 扫描/取消）；ScanNasLibraryCommand→RemoteLibraryScanner→ImportService(Source=Remote)→source='remote' + nas_connection_id 落库；进度上 C1 状态栏 |
| W1-N8 | 下载队列入口 | 已实现（M5-G） | 工具栏「📥 下载队列」按钮；ShowDownloadQueueCommand 打开 W4；无远程图片时按钮仍可点（窗口显示空队列引导） |
| W1/B2-3-DL | 图卡右键「下载到本地...」 | 已实现（M5-G） | 远程项（Source=Remote）右键菜单条目；本地项隐藏；弹出 System.Windows.Forms.FolderBrowserDialog 选目录→加入下载队列 |
| W4 | 下载队列窗口 | 已实现（M5-G） | 640×480，最小 520×360，可最小化不阻塞主窗；ResizeMode=CanResize；按 D1-D4 四横区布局 |
| W4/D1 | 队列表头 | 已实现（M5-G） | 列：文件名 / 来源 / 大小 / 进度 / 状态 / 操作；可见列与排序固定，不支持列定制 |
| W4/D2 | 队列行 | 已实现（M5-G） | ObservableCollection<DownloadQueueRowViewModel>；每行 ProgressBar（Mode=OneWay，U3 教训）+ 状态图标 + 操作按钮（重试/取消/打开目录） |
| W4/D3 | 状态聚合条 | 已实现（M5-G） | 底部状态文本：N 个进行中 / M 已完成 / K 失败；总进度环 |
| W4/D4 | 工具栏 | 已实现（M5-G） | 全部重试 / 全部取消 / 清空已完成 / 打开根目录 |
| W3/A7-PRE | ProgressiveImage 三层渐显 | 已实现（M5-F/H） | 详情栏 B3-1 大图预览替换为 ProgressiveImage 控件：PlaceholderLayer（256px thumb，已缓存）→ PreviewLayer（1024px preview，加载完成淡入替代）→ FinalLayer（原图，按需淡入替代）；每层 DoubleAnimation 200ms QuadraticEase；上层淡入完成才折叠下层避免闪烁 |
| W3/A7-WEAK | 弱网优先策略 | 已实现（M5-H） | NetworkConditionMonitor.IsWeakNetwork=true 时：画廊 Attach 与 Prefetch 跳过远程项请求（仅显示已缓存 thumb）；本地项不受影响；恢复后自动恢复请求。无 UI 控件，行为层实现 |
| W5 | 登录窗口（400×440） | 已实现（M6-B/U6 + v0.2.1 简化 + v0.5.0 改版 + v0.7.0 品牌 Logo） | v0.7.0 起内容为 7 项：W5-1…W5-6 + 顶部 W5-7 品牌 Logo（窗高 360→440）；系统自动取主 NAS IsPrimary=true 优先，无主取第一个；首次启动检测主 NAS accounts.json 为空 → 自动创建预置管理员 XingKongKong/zzh18254359250 + 用户名预填；登录按钮 CanLogin 联动 IsBusy + 有可用 NAS；直接关窗（X/Esc）即仅本地模式。v0.5.0 移除「仅本地模式」按钮与「打开 NAS 设置」按钮（后者功能收敛进 W5-6；二者此前无子编号） |
| W5-1 | 用户名输入框 | 已实现（M6-B） | 绑定 LoginViewModel.Username |
| W5-2 | 密码输入框 + 末端显隐开关 | 已实现（v0.5.0） | PasswordBox（与全局 TextBox 同构样式，掩码态）/ TextBox（明文态）按末端眼睛 ToggleButton 互换，双向同步、登录始终读 PasswordBox.Password；勾选明文态眼睛叠加斜杠 + ToolTip 切换；PasswordChanged 走代码后置（PasswordBox 不可绑定） |
| W5-3 | 登录按钮 | 已实现（M6-B） | AccentButtonStyle 通宽，IsDefault；IsEnabled 绑 CanLogin |
| W5-4 | 「记住账户」勾选 | 已实现（v0.5.0） | 绑 RememberAccount；登录成功存 config.json rememberedLoginUsername 并下次回填用户名；取消勾选连带取消 W5-5 |
| W5-5 | 「记录密码」勾选 | 已实现（v0.5.0） | 绑 RememberPassword，勾选连带勾选 W5-4；登录成功密码经 DPAPI(CurrentUser) 加密存 config.json rememberedLoginPassword，启动解密回填密码框（明文不经绑定属性）；未勾选/登录失败不写；ToolTip 说明本机加密 |
| W5-6 | 右上角设置齿轮 | 已实现（v0.5.0） | 角落 30×30 SubtleButtonStyle「⚙」；打开 W3 设置窗口（未登录身份，默认 A1 常规页，可切 A7 配 NAS）；关闭后 RefreshNasState 立即生效 |
| W5-7 | 顶部品牌 Logo + 产品名 | 已实现（v0.7.0） | 64×64 圆角品牌图「星光相框猫」（Assets/brand-logo.jpg，ImageBrush UniformToFill + BorderSubtle 描边 + #2EA4F0 柔光 DropShadow）+ 下方 20px 粗体「千星汇图」（AccentBrush）；窗高 360→440；位置锚点在 W5-1 之上，编号按续编分配 7 |
| W3/A8 | 用户管理页签 | 已实现（M6-C/U6 编号确认） | 仅管理员可见；账号列表/新建用户/启用禁用/重置密码/删除用户；M6-C 初编 A7 与 NAS 页签撞号，U6 起统一改 A8 |
| W1/B1-SCOPE | 本地库/共享库分段切换 | 已实现（U6-A） | 画廊上方分段控件；仅 NAS 登录用户可切共享段；切换时详情栏（B3）随作用域换模板，搜索词按作用域重新应用 |
| W1/B2-SHARED | 共享库画廊 | 已实现（U6-A + M8-C 聚合 + M8-E 来源徽章） | SharedGalleryList：共享 ListBox 复用 B2-3 网格/列表 DataTemplate；缩略图走 SharedThumbnailCache（弱网同样跳过）；M8-C 起用 AggregatedSharedLibraryService 并发加载主+副 library.json 主库优先去重，每条注入 SourceNasId；SourceBadge 默认「共享」，M8-E 起当 ShowSourceBadge=true 改显「nas://{SourceNasId}」便于管理员区分来源 |
| W1/B2-3-SUB | 图卡右键「提交到共享库」 | 已实现（U6-B） | 仅本地项可见；上传原图+缩略图+pending JSON；hash 查重（库内已有/审核中均拒绝）；CanSubmitToShared=false 时置灰 |
| W1/A1-REV | 「审核队列」入口 + 待审角标 | 已实现（U6-C） | 工具栏「🛡 审核队列」按钮，仅管理员可见；右上角红色圆角角标显示 pending 数，0 时隐藏；关闭 W6 后自动刷新 |
| W6 | 审核队列窗口 | 已实现（U6-C） | 720×540，最小 600 宽；行：96×72 待审缩略图 + 提交人/时间/尺寸/大小/标签 + ✓通过/✕拒绝；空态「没有待审核的提交」；通过/拒绝后行即时移除 |
| W1/B3-SHARED | 共享详情模板（只读） | 已实现（U6-D） | 共享作用域 B3：240 高缩略图 + 「保存副本到…」/「复制 NAS 路径」+ 文件名/格式/尺寸/大小/评分/分类/标签/提交者/审核人·时间/NAS 路径；全部只读 |
| W1/B3-SAVE | 「保存副本到…」 | 已实现（U6-D） | SaveFileDialog 选本地目标 → connector 下载原图；成功/失败经通知中心反馈；取消无副作用 |
| W7 | 首次启动向导窗口 | 已实现（M11） | 540×500 模态窗；仅在库目录未配置（IsLibraryRootConfigured=false）时于 DB 初始化后弹出；老用户以初始化前 qlworld.db 文件是否存在判定；中途关窗则 App 干净退出。子编号 W7-1~W7-4 |
| W7-1 | 标题与说明区 | 已实现（M11） | 向导标题 + 托管库用途说明（内容寻址存储/分类硬链接/原文件保留） |
| W7-2 | 库目录选择区 | 已实现（M11） | 只读路径 TextBox +「浏览…」按钮（OpenFolderDialog，选择器以 Func<string?> 注入便于单测）；选后 ValidateSameVolume 同盘校验，跨盘显红字错误并阻断完成 |
| W7-3 | 老数据迁移说明 + 进度区 | 已实现（M11） | 老用户可见：迁移范围/可重跑/跳过可逆说明；执行迁移时显 MigrationStatus（已处理/总数/当前文件）；新安装隐藏 |
| W7-4 | 决策按钮区 | 已实现（M11） | 老用户：「跳过」（置 LegacyDataSkipped=true，画廊软过滤未托管行）+「立即迁移」（跑 LegacyDataMigrator：local 且文件存在→CAS+硬链接+回写；source=remote 跳过+日志；缺文件跳过；幂等续跑）；新安装仅「开始使用」；有效目录选定前全部置灰 |
