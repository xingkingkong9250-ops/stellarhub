# M3-E2 实现计划 — 本地 CLIP 文本编码器

## Context

M3 第二阶段（v0.4.0）已交付本地 CLIP vision 推理与图→图语义搜索；但 `LocalAiService.EmbedTextAsync` 仍是 M3-E1 占位实现：直接返回 `null`，导致 Local 模式下文→图语义搜索不可用（用户切到「语义」搜索档时只能回退到 Tag 模式并看一条"Local 不支持"通知）。本阶段（M3-E2）补齐本地 CLIP 文本编码链路：BPE tokenizer + `text_model.onnx` 推理，使 Local 模式与 Cloud 模式行为对齐。

**已确认决策**（用户答复）：
1. **分发策略**：捆绑单包下载 — `vision_model.onnx`（351 MB）+ `text_model.onnx`（254 MB）作为同一个"本地模型"包下载，单进度条+分别 SHA 校验；用户不能跳过其中之一
2. **Tokenizer 词表**：`vocab.json` + `merges.txt`（合计约 1 MB）作为 EmbeddedResource 打包，首次使用时解压到 `{dataRoot}\models\`，复用 `ClipTextIndex.EnsureDistributed` 模式

**Spike 参考真源**：
- BPE tokenizer 全 C# 参考实现：[ClipTokenizer.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/tools/spike/ClipSpike/ClipTokenizer.cs) — 可几乎原样移植到主工程
- ONNX 调用模式参考：[Program.cs:RunText](file:///c:/AI_Programming/APP/QL_World_dotnet/tools/spike/ClipSpike/Program.cs#L151-L175) — `input_ids` int64 [1,77]，无 attention_mask，输出 [1,512]
- Spike 已验证：text_model.onnx 254,058,553 字节，sha256 `3f6571f5bad13a97c469c1622e1cfc4d9aef78b79fdbfcff804ca357bfada8cc`，仅 Spike 生成 embeddings 用过、此前未分发
- BytesToUnicode 陷阱（已修复，参考实现正确）：必须用 `'~' - '!' + 1`（=94），不是 `94 - '!' + 1`（=62）

---

## E-A：Tokenizer 移植与 EmbeddedResource 分发

**新建**：
- `src/QL.World/Services/Ai/Local/ClipTokenizer.cs` — 从 spike 原样移植（删除 `partial`/`GeneratedRegex` 改为静态预编译正则避免源生成器依赖；保留 `Encode(text) → int[77]`、`TokenStrings(text)`、`BytesToUnicode()`、BPE 缓存）。命名空间 `QL.World.Services.Ai.Local`
- `src/QL.World/Assets/Ml/vocab.json` — EmbeddedResource
- `src/QL.World/Assets/Ml/merges.txt` — EmbeddedResource（UTF-8 无 BOM；构建时确保不被 XML 序列化误改）
- `src/QL.World/Support/TokenizerAssetExtractor.cs` — 静态 `EnsureDistributed(string modelsDirectory) → string[]`：与 `ClipTextIndex.EnsureDistributed` 同模式，从 EmbeddedResource 解压 vocab.json + merges.txt 到 `{dataRoot}\models\`，已存在且大小匹配则跳过

**修改**：
- `QL.World.csproj` — `<EmbeddedResource Include="Assets\Ml\vocab.json" />` + `<EmbeddedResource Include="Assets\Ml\merges.txt" />`
- `src/QL.World/Services/Ai/Local/ClipTextIndex.cs` — 若 `EnsureDistributed` 是公共方法，抽公共逻辑到 `TokenizerAssetExtractor` 或保留原状只复用模式

**资源获取**（一次性，不在 build 流程内）：
- 从 spike 目录 `tools/spike/models/vocab.json` + `merges.txt`（如果存在）复制到 `src/QL.World/Assets/Ml/`
- 若 spike 目录没有，从 `openai/clip-vit-base-patch32` HuggingFace 仓库下载（hf-mirror.com fallback）

---

## E-B：LocalAiService 文本编码集成

**修改 `src/QL.World/Services/Ai/Local/LocalAiService.cs`**：

1. **新增字段**：
   - `InferenceSession? _textSession` + `object _textSessionLock`
   - `ClipTokenizer? _tokenizer` + `bool _tokenizerFailed`
   - `string TextModelFilePath => Path.Combine(_modelsDirectory, TextModelManifest.TextModelFileName)`

2. **IsAvailable 收紧**：`vision_model.onnx` AND `text_model.onnx` 都存在（捆绑下载保证了正常流程下两者同时存在；导入或旧版可能只有 vision，此时图→图仍可用但 EmbedTextAsync 返回 null）

3. **重写 `EmbedTextAsync`**：
   ```
   if (!File.Exists(TextModelFilePath)) return null;
   tokenizer = GetTokenizer(); if null return null;
   ids = tokenizer.Encode(text);
   session = GetTextSession();
   inputs = [NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids.Cast<long>(), [1,77]))];
   results = session.Run(inputs, ["text_embeds"]);
   return ExtractEmbedding(results);
   ```
   全异常捕获 → return null（不抛出，契约一致）

4. **GetTextSession / GetTokenizer**：复用 `GetSession` 的 double-check lock 模式；token 创建失败置 `_tokenizerFailed=true` 不再重试

5. **测试构造函数扩展**：`internal LocalAiService` 加 `Func<string, InferenceSession>? textSessionFactory` 参数（与 vision `sessionFactory` 区分）；旧签名保留向后兼容

6. **TestConnectionAsync**：vision Ready 即 Ready（文本模型不影响 TestConnection；用户感知 Local 模式可用性以 vision 为准）

---

## E-C：模型包捆绑下载

**修改 `src/QL.World/Services/Ai/ModelManifest.cs`**：
- 新增文本模型常量：`TextModelFileName = "text_model.onnx"`、`TextModelSha256 = "3f6571f5..."`、`TextModelSizeBytes = 254_058_553`、`TextModelSourceUrl` + `TextModelMirrorUrl`（`Xenova/clip-vit-base-patch32/onnx/text_model.onnx`）

**修改 `src/QL.World/Services/Ai/ModelDownloadService.cs`**：
- `DownloadAsync` 先下 vision（既有逻辑），成功后接 text（同一 TryDownloadAsync 重用，URL/S(Source)/Mirror/Size/SHA 不同）；任一阶段失败回滚（删除 `.part` 与已完成的另一文件）
- `CheckLocalState` 检查两文件：任一缺失/损坏按整体 Corrupted 报告（vision 优先）
- `ImportManualAsync` 接受单文件（vision 或 text，根据文件大小识别），或拆为两个 import 方法（更直观）；建议保持单方法 + 接受目录导入（用户拖入文件夹含两文件）—— 简化版：维持现状单文件导入仅支持 vision，文本模型只能通过下载获得

**修改 `src/QL.World/ViewModels/SettingsViewModel.cs`**：
- 状态文本扩展：`LocalModelStateText` 现在覆盖组合状态（Ready = 两文件就绪；Downloading = "正在下载（N/2）…"）
- 进度条：vision 阶段 0-50%，text 阶段 50-100%（进度映射）；或保持单段 0-100% 跨两阶段按累计字节数计算（更平滑）
- `LocalModelInfoText` 同时显示两模型 SHA-256 + 体积（PRD 硬性要求展示来源/许可）

**修改 `src/QL.World/Views/SettingsWindow.xaml`**：
- 本地区块说明小字扩展为两行：vision_model.onnx 351 MB（MIT, Xenova/clip-vit-base-patch32）+ text_model.onnx 254 MB（同源）

---

## E-D：测试

**新建 `tests/QL.World.Tests/Ai/Local/ClipTokenizerTests.cs`**：
- `Encode("a seamless texture map pattern")` → 至少 7 个 content token、不超过 9（spike 验证 ≈8）
- `Encode("")` → 全 0 padding + SOT/EOT
- `Encode(超长 77+ token 文本)` → 抛 `ArgumentException`
- `BytesToUnicode()` 关键字节：`'a'(0x61)` 不应映射到 `ģ`(U+0123)；映射到自身 `'a'`
- `TokenStrings("hello")` → 不为逐字符（确认 BPE merges 生效）

**新建 `tests/QL.World.Tests/Ai/Local/TokenizerAssetExtractorTests.cs`**：
- `EnsureDistributed` 首次调用解压两文件，二次调用幂等（size match 跳过）

**修改 `tests/QL.World.Tests/Ai/Local/LocalAiServiceTests.cs`**：
- 替换 M3-E1 的 `EmbedTextAsync_ReturnsNull` 测试为：
  - `EmbedTextAsync_WhenTextModelMissing_ReturnsNull`（vision 存在但 text 缺失）
  - `EmbedTextAsync_WithFakeTextSession_ReturnsExpectedEmbedding`（注入 fake text session，返回 [1,512] 张量）
  - `EmbedTextAsync_WhenTokenizerFails_DoesNotRetry_AndReturnsNull`（注入失败 tokenizer loader）
  - `EmbedTextAsync_DoesNotCreateVisionSession`（隔离验证：调用 EmbedTextAsync 不触发 vision session 创建）

**扩展 `tests/QL.World.Tests/Ai/ModelDownloadServiceTests.cs`**：
- 新增用例：vision 下载成功 + text 下载失败 → 回滚（删除已下 vision）
- 新增用例：vision SHA 失败 → 不继续 text 阶段
- 新增用例：text 阶段 Range 续传成功
- 新增用例：CheckLocalState 报告组合状态（两文件全 Ready / 缺 text / text 损坏）

---

## E-Z：收尾

- `QL.World.csproj` 版本 `0.4.0` → `0.5.0`
- `CHANGELOG.md` 新增 `## [0.5.0] — M3-E2 本地 CLIP 文本编码`（BPE tokenizer 移植 / text_model.onnx 捆绑下载 / EmbedTextAsync 实现 / 测试覆盖）
- `GEMINI.md` 里程碑状态：M3 第二阶段已补齐 Local 文→图 → 状态保持 "M3 第二阶段完成（本地 ONNX + 颜色分析 + 语义搜索 Local/Cloud 双模）"
- `docs/UI_NUMBERING.md`：W3/A3 描述补"含文本模型捆绑下载"
- 门禁：`build.ps1 -Mode Prepare`（0 警告 + 全绿 + R2R；记录 staging 体积从 161.95 MB → 约 416 MB 增量）

---

## 风险与规避

| 风险 | 规避 |
|---|---|
| Staging 体积翻倍（+254 MB text_model + ~1 MB tokenizer 资源） | 用户已确认捆绑下载；CHANGELOG 记录体积增量 |
| BPE 移植引入 BytesToUnicode 回归 | 直接复用 spike 已修复版本；新增 `BytesToUnicode` 字节级断言 |
| `merges.txt` EmbeddedResource 编码问题（BOM/换行） | 构建前验证文件 UTF-8 无 BOM；`ReadLines` 默认按平台换行处理；测试断言 BPE merges 加载条数 ≥ 48800 |
| 双 session 内存峰值（vision 351 + text 254 MB 常驻） | 文本 session 用同一 `SessionOptions`（核数一半 intra-op）；用户感知低（仅在 EmbedTextAsync 触发时才创建文本 session） |
| 旧版用户已下 vision 单文件，升级后 IsAvailable 收紧导致功能失效 | 检测到只有 vision 时仍允许图→图搜索；EmbedTextAsync 返回 null；设置面板提示"补下文本模型以启用 Local 文→图搜索" |

---

## 验证

1. **单元测试**：`dotnet test tests/QL.World.Tests -c Release` 全绿（189 → 约 200+，新增 tokenizer/download/local 文本编码用例）
2. **集成手测**：删除 `{APPDATA}\Qianlan_App\QL_World\models\` 两文件 → 设置点"下载模型" → 单进度条跨两阶段 → Ready → TopBar 切到 Local 模式 → 搜索框切"语义" → 输入"red car" → 返回 top-50 相关图（已分析库）
3. **回退验证**：只有 vision_model.onnx 时，启动应用 → 切 Local → 搜索"语义" → 通知显示"Local 模式未启用文本编码，请下载完整模型包" → 回退 Tag 模式（不崩）
4. **门禁**：`build.ps1 -Mode Prepare` 0 警告 + 全绿 + R2R；CHANGELOG 记录 staging 体积 161.95 → 约 416 MB（+254 MB / +157%）
5. **每子阶段 git commit**：E-A / E-B / E-C / E-D / E-Z 各一次提交
