# M3-D 实现计划 — 本地 ONNX 推理 + 颜色分析

## Context

M3 第一阶段（v0.3.0）已交付云端 AI 与批量管道，130 测试全绿，git 基线 `fb366fb`。M3-D 实现 PRD 第 4 节本地模型档：**本地 ONNX 推理（CLIP ViT-B/32）+ 模型下载器 + 颜色分析**，使 AI 三档中「本地模型（离线）」真正可用，并交付不依赖 AI 档位的颜色分析。

**已确认决策**：
- 技术选型遵循 PRD：`Microsoft.ML.OnnxRuntime（CPU）+ CLIP ViT-B-32 ONNX`，图像处理用现有 ImageSharp
- **先做 Spike**：fp32 vs int8 实测（分类一致率/速度/体积）后定默认档
- 模型下载：直连 + hf-mirror 镜像 fallback + 手动选择本地文件；SHA-256 校验；断点续传
- 零样本分类/标签用**预计算 text embeddings JSON**（Spike 产物），规避运行时 BPE tokenizer 与 text 模型分发

**执行顺序：D-Spike → D-A → D-B → D-C**（Spike 结论决定 D-B 清单与 D-C ModelId，先行避免返工；每阶段结束编译+测试+git commit）

---

## D-Spike：fp32 vs int8 实测（独立控制台工程，不动主工程）

新建 `tools/spike/ClipSpike/`（csproj + Program.cs + README.md，仅引用 OnnxRuntime + ImageSharp）：

1. `download`：HuggingFace `Xenova/clip-vit-base-patch32`（vision/text × fp32/quantized），失败切 hf-mirror.com；记录 SHA-256/体积
2. `verify`：断言输出形状 `[1,512]`，记录输入名/dtype
3. `bench`：20–50 张覆盖 10 分类的样本图，1/2/4 线程 mean+p95 图/秒
4. `accuracy`：人工标注一级分类真值，零样本 cosine argmax 对比 fp32/int8 一致率
5. `gen-text-embeddings`：预计算 **10 分类 + 四类标签词表**的 512 维向量 → `text-embeddings-clip-vit-b32.json`（term → float[512]），随应用分发

**决策规则**（写入 README）：int8 默认档 ⟺ 分类一致率 ≥95% 且 embedding 余弦 ≥0.98 且提速 ≥1.3× 且体积 ≤ fp32 的 60%；任一不满足选 fp32。

---

## D-A：颜色分析（无新依赖）

**新建**：
- `src/QL.World/Services/Colors/ColorExtractor.cs` — 纯静态 `Extract(Image) → IReadOnlyList<string>`：降采样 ≤64×64 → K-means（k=4，固定种子确定性）→ 3–5 个 `"#RRGGBB"`
- `src/QL.World/Services/Colors/ColorAnalysisService.cs` — `RunPendingAsync(batchLimit=500, ct)` 后台查询 `dominant_colors IS NULL OR ''` 逐张回填；**仅回填 NULL/空不覆盖**（确定性算法回填即幂等）；文件缺失/解码失败跳过记日志
- 测试：`ColorExtractorTests.cs`（纯色→单 hex、渐变→多簇、确定性、透明/灰度）、`ColorAnalysisServiceTests.cs`（TestLibrary：NULL 回填/非 NULL 不动/缺文件不崩）

**修改**：
- `App.xaml.cs` + `ServiceRegistry` — 14 号成员 `ColorAnalysisService`
- 导入完成 + FileWatcher 自动导入后 fire-and-forget `RunPendingAsync`（挂点：`ImportService.InsertBatch` 已把 dominant_colors 插 NULL，回填天然幂等）
- `TopBarViewModel.ColorCommand` — 手动批量（与 AI 档位无关）；`ShellWindow.xaml` 新按钮 → **UI_NUMBERING 新增 W1/A1-8**

---

## D-B：模型下载器 + 设置 UI

**新建**：
- `src/QL.World/Services/Ai/ModelManifest.cs` — 常量：`ModelId`（Spike 定，如 `clip-vit-b32-int8`）、文件名、SHA-256、体积、License（MIT）、SourceUrl/MirrorUrl、PromptTemplateVersion
- `src/QL.World/Services/Ai/ModelDownloadService.cs`：
  - `CheckLocalState() → NotDownloaded/Corrupted/Ready`（stat + SHA 流式校验）
  - `DownloadAsync(IProgress<ModelDownloadProgress>, CancellationToken)` — 存 `dataRoot\models\`；`.part` 临时文件 + HTTP Range 断点续传（416/200 重启）；主源连续失败切镜像（清 .part）；完成 SHA 校验失败删 .part
  - `ImportManualAsync(sourceFile)` — 复制进 models\ + SHA 校验；`DeleteModel()`
- `src/QL.World/Models/Ai/ModelDownloadProgress.cs` — record(Phase, Percent, BytesReceived, TotalBytes, Message)
- 测试 `ModelDownloadServiceTests.cs` — HttpMessageHandler stub 模拟 Range/416/断流/镜像切换/校验失败/手动导入

**进度通道**：高频进度走 `IProgress<T>`（ViewModel `Progress<T>` 自动回 UI 线程绑进度条）；完成/失败另发 NotificationService 一条。

**修改**：
- `AppSettingsService` — 新增只读 `DataRoot` 属性（构造已接收未公开）；`LocalModelPath` 语义收窄为手动导入源文件
- `SettingsViewModel` — `LocalModelStateText`、`IsDownloading`、`DownloadPercent`、`DownloadModelCommand`/`CancelDownloadCommand`/`SelectModelFileCommand`/`DeleteModelCommand`（OpenFileDialog 同 legacy import 现法）
- `SettingsWindow.xaml` A3 — 替换「M3-D 提供」占位：状态文本 + ProgressBar + 三按钮 + 体积/许可/来源小字行（PRD 硬性要求展示）

---

## D-C：LocalAiService + 工厂 + 管道集成

**新建**：
- `src/QL.World/Services/Ai/Local/ClipPreprocessor.cs` — 纯静态：Resize 短边 224 → 中心裁剪 224×224 → CLIP mean/std 归一化 → NCHW float
- `src/QL.World/Services/Ai/Local/ClipTextIndex.cs` — 加载 text-embeddings JSON；`RankGroups(imageEmb)`：分类组 argmax+阈值 → PrimaryCategory；四类标签组内 top-k(≤3)+阈值 → Tags；纯预计算向量余弦，无 ONNX
- `src/QL.World/Services/Ai/Local/LocalAiService.cs` — IAiService 实现：
  - 惰性创建 `InferenceSession`（首次推理），`lock` 包裹 `session.Run`；intra-op = 核数一半
  - `IsAvailable` 动态 stat 模型文件（会话中途下载完成即生效）
  - `ModelId = ModelManifest.ModelId`；输出 512 维 embedding
  - `internal` 构造重载注入 `Func<string, InferenceSession>` 供测试 fake
  - 全异常捕获 → ErrorMessage；`TestConnectionAsync` = CheckLocalState==Ready
- `src/QL.World/Assets/Ml/tag-vocabulary.json`（四组词表）EmbeddedResource + `text-embeddings-*.json` 分发方案（EmbeddedResource 首次解压到 models\）
- 测试：`ClipPreprocessorTests`（纯色图张量值精确断言/NCHW/归一化边界）、`ClipTextIndexTests`（余弦排序/top-k/阈值）、`LocalAiServiceTests`（fake session：正常路径/异常→ErrorMessage/文件缺失→IsAvailable=false）

**修改**：
- `AiServiceFactory` — `AiMode.Local => new LocalAiService(settings, log)` **不抛异常**（未下载模型时 IsAvailable=false，TopBar 置灰自动生效）；签名加 `Action<string>? log`
- `AiServiceFactoryTests` L49-54 — 改断言 Local 返回非 null 且 IsAvailable=false
- `QL.World.csproj` — 加 `Microsoft.ML.OnnxRuntime`（仅 CPU 包）
- `AiAnalysisServiceTests` 增补 — Local 型结果含 ModelId 持久化后 dim=512

**已验证结论**：`AiAnalysisService` 无需改动——PersistResult 与后端无关；Local 成功必写 embedding 行，NOT EXISTS 防死循环成立。

---

## 收尾（D-Z）

- `QL.World.csproj` 版本 → `0.4.0`；CHANGELOG 新增 `## [0.4.0] — M3 第二阶段（本地 ONNX + 颜色分析）`
- `docs/UI_NUMBERING.md` — W1/A1-8（颜色按钮）、W3/A3 本地区块
- `GEMINI.md` 里程碑状态；记忆追加 Spike 结论与 OnnxRuntime 经验
- 门禁 `build.ps1 -Mode Prepare`（0 警告 + 全绿 + R2R；记录发布体积增量）
- UIA 冒烟扩展：`tools/smoke/UiSmokeA3.ps1` 增加本地区块控件存在性断言
- 每子阶段结束 git commit

## 风险与规避

| 风险 | 规避 |
|---|---|
| 发布体积 +onnxruntime.dll（约 20–60MB） | 仅 CPU 包；门禁记录体积增量 |
| 大图解码内存 | `using` 严格释放、逐张串行、失败跳过 |
| 国内下载失败 | 镜像 fallback + 断点续传 + 手动导入（PRD 三通道全覆盖） |
| 推理异常崩主程序 | LocalAiService 全捕获 → ErrorMessage；session 创建失败 → IsAvailable=false |
| OnnxRuntime 包源 | 实现 D-C 前先 `dotnet restore` 确认 |

## 验证

1. Spike：README 决策表 + text-embeddings JSON 生成
2. 每阶段：`dotnet test tests/QL.World.Tests -c Release` 全绿
3. 手动：UI 下载模型（断网模拟镜像 fallback）→ 切本地档 → AI 按钮亮起 → 批量分析 → DB 验证 embedding dim=512 + tags + primary_category
4. 颜色：导入新图自动回填 dominant_colors；存量图手动批量
5. 门禁：`build.ps1 -Mode Prepare` 通过
