# UI 编号表（唯一真源）

规则：W=窗口；W1 下 A=顶部横区/页签、B=主分区、C=底部区；编号下钻到控件（如 B3-3）。
编号是位置锚点：功能变更必须核对；删除即作废不复用；新增顺延。
每次 UI 变更必须同步本表。

| 编号 | 名称 | 状态 | 说明 |
|---|---|---|---|
| W1 | 主窗口 | 已实现（M1） | 1400×850，最小 1100×700，PerMonitorV2 |
| W1/A1 | 顶栏（高 56） | 已实现（M1） | 控件见 A1-1…A1-8（A1-2/A1-8 M3 接线） |
| W1/A1-1 | 「导入」按钮 | 已实现（M1） | 「＋ 导入图片」，打开 W2 |
| W1/A1-2 | 「AI 分析」按钮 | 已实现（M3） | AI 可用时触发批量分析；不可用时置灰 + ToolTip 说明原因 |
| W1/A1-3 | 视图切换（网格/列表） | 已实现（M1） | ToggleButton 互斥切换 + 独立缩放 |
| W1/A1-4 | 搜索框 | 已实现（M1） | AutomationId 语义名 SearchBox，300ms 防抖 |
| W1/A1-5 | 搜索模式下拉 | 已实现（M1） | 标签 / 文件名·备注 / 语义（M3-E1） |
| W1/A1-6 | 通知铃铛 | 已实现（M2） | 未读红点徽标 + 弹层列表（未读标记/全部已读/清空） |
| W1/A1-7 | 设置齿轮 | 已实现（M2） | 打开 W3 |
| W1/A1-8 | 「分析颜色」按钮 | 已实现（M3-D） | 手动批量回填 dominant_colors；与 AI 档位无关；占用主线程串行执行 |
| W1/A1-9 | Logo 文字块 + AI 按钮品牌渐变 | 已实现（U2-A） | Logo 文字 AccentGradientBrush（Accent→AccentDim）；AI 按钮 hover 切渐变背景 + TextOnAccentBrush 文字 |
| W1/B1 | 左栏（220–300，实配 240） | 已实现（M2） | |
| W1/B1-1 | 素材库树 | 已实现（M1） | 全部 + 10 分类 + 未分类（TagTree）；含子项计数显示（Count 绑定，U1-B 核实） |
| W1/B1-2 | 收藏夹树 | 已实现（M2） | 单击筛选/再点清除；右键建/改名/删/设封面/加入；画廊拖拽加入；含子项计数显示（U1-B 核实） |
| W1/B1-3 | 高频标签 Top50 | 已实现（M1） | 含计数显示（TopTag Count，U1-B 核实） |
| W1/B1-4 | 贴图组树 | 已实现（M4） | PBR 组（目录+基名成组）；单击筛选画廊/再点清除；节点显示成员数（U1-B 核实） |
| W1/B1-5 | 属性筛选折叠组 | 已实现（U3-A） | 默认收起，折叠态持久化；四维单选 chips（各含「全部」，再点已选清除）；与 B1/A1 其它筛选 AND 叠加 |
| W1/B1-5-1 | 属性筛选-颜色 | 已实现（U3-A） | 9 色族（红/橙/黄/绿/青/蓝/紫/品红/灰，HSV 归族）；数据源 images.color_family（schema v5，由 dominant_colors 首色纯计算回填/同窗写入）；未分析图片不匹配任何色族 |
| W1/B1-5-2 | 属性筛选-分辨率 | 已实现（U3-A） | 按长边 MAX(width,height) 分档：<1024 / 1024–2047 / 2048–4095 / ≥4096；NULL 尺寸不匹配任何档 |
| W1/B1-5-3 | 属性筛选-文件大小 | 已实现（U3-A） | <1 MB / 1–5 MB / 5–20 MB / ≥20 MB |
| W1/B1-5-4 | 属性筛选-评分 | 已实现（U3-A） | 未评分(=0) / ≥3★ / ≥4★ / 5★ |
| W1/B1-COL | B1 四分区折叠+计数 | 已实现（U3-C） | 素材库/收藏夹/热门标签/贴图组标题改为 ToggleButton（▾/▸ 箭头）；折叠态持久化（settings.sidebar_*_expanded，默认全展开）；折叠时标题显示子项总数「名称 (N)」，展开为纯名；B1-5 属性筛选组沿用同一 SidebarSectionHeaderStyle |
| W1/B2 | 画廊区 | 已实现（M1） | |
| W1/B2-1 | 计数条 | 已实现（M1） | 显示 N 张 / 共 M + 筛选摘要 |
| W1/B2-2 | 排序下拉 | 已实现（M1） | 必须真实生效并持久化 |
| W1/B2-3 | 图卡（网格/列表） | 已实现（M1） | VirtualizingTilePanel UI 虚拟化 |
| W1/B2-3-SEL | 选中态视觉锚点 | 已实现（U1-A） | 网格 2px Accent 外描边；列表 3px 左竖条+底线（Padding 同步减 3px 抵消 layout shift）；底色由 ListBoxItem AccentBg 提供 |
| W1/B2-3-OL1 | 「最近入库」下拉 | 已实现（U2-B） | 今天/近 7 天/近 30 天/全部；持久化到 settings.gallery_recent_import_days |
| W1/B2-3-BDG | 缩略图来源徽标 | 已实现（U2-D） | 网格右下角/列表右上角；三态「本地/云端/AI」，无 AI 数据时隐藏 |
| W1/B2-3-TAG | 卡片标签摘要行 | 已实现（U2-E） | 网格卡片第三行：前 2 标签 + 「+N」；无标签隐藏 |
| W1/B2-3-CM1 | 图卡右键「找相似图」 | 已实现（M3-E1）/ 冒烟（M4-D） | 按 CLIP embedding 余弦相似度返回 top-50；无 embedding 时置灰；UiSmokeB1.ps1 覆盖 |
| W1/B2-3-ACT | 卡片 hover 操作组 | 已实现（U3-B） | 仅网格态：缩略图底部三按钮浮层（打开原文件↗/资源管理器定位⌖/复制路径⧉），hover 浮出、移出即隐；作用于悬停卡片（参数化命令）而非当前选中；缺失卡被缺失遮罩覆盖天然不可触且 CanExecute 排除；列表态不加 |
| W1/B2-4 | 缩放滑杆（80–512） | 已实现（M1） | 网格/列表各一支，按视图显隐 |
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
| W1/C1-R | 快捷键提示 + AI 档位 | 已实现（U1-C） | 快捷键提示按上下文切换（空库/无选中/单选/多选）；AI 档位固定 |
| W1/C1-PB | 细进度条 | 已实现（U2-A） | 2px IsIndeterminate，AccentGradientBrush 前景；导入/AI 运行/缩略图挂起时显示 |
| W1/C1-VER | 品牌化版本号 | 已实现（U2-F） | 页脚右侧显示「vX.Y.Z · 里程碑标签」，仅 Shell；W3/A5 关于页保持纯版本 |
| W2 | 导入对话框（520×480，可调，最小 460×420） | 已实现（M1） | 来源/选项/进度/日志/取消/完成 |
| W3 | 设置窗口 | 已实现（M3） | 480×640 TabControl：A1 常规 / A2 缩略图 / A3 AI / A4 监控 / A5 关于 / A6 贴图组 |
| W3/A1 | 常规页签 | 已实现（M2） | 主题（Dark/Light 即时切换）/完整性检查开关；库位置随数据根（M3 可视化） |
| W3/A2 | 缩略图页签 | 已实现（M2） | 边长/质量滑杆 + 全部重新生成 |
| W3/A3 | AI 页签 | 已实现（M3 第二阶段） | 三档单选 + 云端配置（base_url/模型/API Key/测试连接）+ 本地 ONNX 区块（状态文本/进度条/下载·取消·手动导入·删除模型四按钮/体积·许可·来源小字行；M3-E2 起下载为 vision+text 模型捆绑包） |
| W3/A4 | 监控目录页签 | 已实现（M2） | 目录列表/添加/移除/自动导入开关，即时生效 |
| W3/A5 | 关于页签 | 已实现（M2） | 版本/统计/旧版 Python 库导入/打开日志文件夹 |
| W3/A6 | 贴图组规则页签 | 已实现（M4） | 尾缀→角色规则行（模式/角色下拉/启用/删除）+ 添加规则/保存并重建/仅重建/恢复默认 + 状态文本 |
| W3/A7 | NAS 连接页签 | 已实现（M5） | 连接列表（N1）/ 详情表单 N2-N5 / 测试连接 + 保存（N6）；M5 仅 WebDAV，SMB/SFTP 预留 |
| W3/A7-N1 | NAS 连接列表 + 新建/删除 | 已实现（M5-C） | ObservableCollection 绑定；新建按钮分配单调递增 id（不复用已删除）；删除按钮清除选中连接（含密码） |
| W3/A7-N2 | 名称 | 已实现（M5-C） | 唯一显示名；绑定 SelectedNasConnection.Name |
| W3/A7-N3 | 协议 + 主机 + 端口 | 已实现（M5-C） | 协议下拉（M5 仅 WebDav）；主机文本（IP/域名，不含 scheme）；端口可空=默认 443(TLS)/80 |
| W3/A7-N4 | 用户名 + 密码 | 已实现（M5-C） | 用户名留空=匿名；PasswordBox 永不回填（安全），DPAPI(CurrentUser) 加密落 config.json |
| W3/A7-N5 | 根路径 + TLS + 自签 | 已实现（M5-C） | 根路径如 /photos/；UseTls 默认勾选；TrustSelfSigned 默认取消（仅受信任内网勾选） |
| W3/A7-N6 | 测试连接 + 保存 | 已实现（M5-C） | 测试：INasConnectorFactory.Create + TestConnectionAsync（先 Update 持久化当前编辑→factory 解密密码→PROPFIND root）；保存：UpdateNasConnection（保留 EncryptedPassword/CreatedAt） |
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
| W5 | 登录窗口 | 已实现（M6-B/U6 编号确认） | NAS 连接下拉 + 账号密码登录 + 本地模式入口 + 首次初始化管理员；M6-B 初编 W4 与下载队列撞号，U6 起统一改 W5 |
| W3/A8 | 用户管理页签 | 已实现（M6-C/U6 编号确认） | 仅管理员可见；账号列表/新建用户/启用禁用/重置密码/删除用户；M6-C 初编 A7 与 NAS 页签撞号，U6 起统一改 A8 |
| W1/B1-SCOPE | 本地库/共享库分段切换 | 已实现（U6-A） | 画廊上方分段控件；仅 NAS 登录用户可切共享段；切换时详情栏（B3）随作用域换模板，搜索词按作用域重新应用 |
| W1/B2-SHARED | 共享库画廊 | 已实现（U6-A） | SharedGalleryList：共享 ListBox 复用 B2-3 网格/列表 DataTemplate；缩略图走 SharedThumbnailCache（弱网同样跳过）；SourceBadge=「共享」 |
| W1/B2-3-SUB | 图卡右键「提交到共享库」 | 已实现（U6-B） | 仅本地项可见；上传原图+缩略图+pending JSON；hash 查重（库内已有/审核中均拒绝）；CanSubmitToShared=false 时置灰 |
| W1/A1-REV | 「审核队列」入口 + 待审角标 | 已实现（U6-C） | 工具栏「🛡 审核队列」按钮，仅管理员可见；右上角红色圆角角标显示 pending 数，0 时隐藏；关闭 W6 后自动刷新 |
| W6 | 审核队列窗口 | 已实现（U6-C） | 720×540，最小 600 宽；行：96×72 待审缩略图 + 提交人/时间/尺寸/大小/标签 + ✓通过/✕拒绝；空态「没有待审核的提交」；通过/拒绝后行即时移除 |
| W1/B3-SHARED | 共享详情模板（只读） | 已实现（U6-D） | 共享作用域 B3：240 高缩略图 + 「保存副本到…」/「复制 NAS 路径」+ 文件名/格式/尺寸/大小/评分/分类/标签/提交者/审核人·时间/NAS 路径；全部只读 |
| W1/B3-SAVE | 「保存副本到…」 | 已实现（U6-D） | SaveFileDialog 选本地目标 → connector 下载原图；成功/失败经通知中心反馈；取消无副作用 |
