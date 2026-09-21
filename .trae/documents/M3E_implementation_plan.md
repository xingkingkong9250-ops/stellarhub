# M3-E 语义搜索实现计划

## Context

M3 第一阶段（v0.3.0）已交付云端 AI 与批量管道，130 测试全绿，git 基线 `fb366fb`。M3-D 已交付本地 ONNX 推理（commit `d4b284f`），169 测试全绿，三档 AI（Disabled/Local/Cloud）均可写 `image_embeddings`（512 维 CLIP 向量，BLOB = little-endian float[]）。

M3-E 在此基础上启用语义搜索：用户可用文本（Cloud 模式）或图（任意模式）从全库找出 top-N 相似图片，千级库 <100ms。本计划按 **M3-E1（本次）** 与 **M3-E2（延后）** 拆分。

**M3-E1 范围**：
- `SemanticSearchService`：内存缓存 `image_embeddings` 表 + 余弦相似度 + top-N
- 图→图相似搜索（任意 AI 模式可用，依赖已存 embedding）
- 文→图语义搜索（**仅 Cloud 模式**：复用 CloudAiService 既有 `/embeddings` 调用）
- `SearchMode.Semantic` 新增 + 顶栏下拉 + 300ms 防抖复用
- 图卡右键菜单「找相似图」+ 详情栏「找相似」按钮
- AiAnalysisService 写入后通过 `EmbeddingsUpdated` 事件失效缓存

**M3-E2 延后范围**（不在本次执行）：`LocalAiService.EmbedTextAsync` 本地 CLIP text_model.onnx + BPE tokenizer。理由：spike README 明确 text_model.onnx "不分发"（额外 ~90MB），且 BPE tokenizer 移植有 BytesToUnicode 等陷阱（见 project_memory L30）。M3-E1 中 LocalAiService.EmbedTextAsync 直接返回 `null` + 日志 "deferred to M3-E2"，UI 自动降级。

---

## 新增文件

### `src/QL.World/Services/SemanticSearchService.cs`（服务 #15）

```csharp
public sealed class SemanticSearchService
{
    public const int TopN = 50;

    public SemanticSearchService(DatabaseService db, IAiService ai);

    /// <summary>库中是否存在任何已分析的 embedding（用于 UI 决定是否显示「语义」档）。</summary>
    public bool HasAnyEmbeddings();

    /// <summary>文本语义搜图。委托 IAiService.EmbedTextAsync 计算文本向量，再走 SearchByEmbedding。
    /// IAiService 不支持或失败时返回 null（由 VM 决定降级提示）。</summary>
    public async Task<IReadOnlyList<long>?> SearchByTextAsync(string text, CancellationToken ct = default);

    /// <summary>以图搜图。从 DB 取指定 image_id 的 embedding，再走 SearchByEmbedding。
    /// 该图无 embedding 或 ID 不存在返回 null。</summary>
    public IReadOnlyList<long>? SearchByImageId(long imageId);

    /// <summary>核心：缓存加载 + 余弦相似度 + TopN 截断。返回降序 image_id 列表。</summary>
    public IReadOnlyList<long>? SearchByEmbedding(float[] query);

    /// <summary>AiAnalysisService.EmbeddingsUpdated 触发；置空缓存。</summary>
    public void Invalidate();
}
```

**缓存策略**：`Dictionary<long, float[]>?` 懒加载，首次 `SearchByEmbedding` 时全表 SELECT 一次（千级 × 512 × 4B = 2MB 内存可忽略）；余弦复用 ClipTextIndex 的 dot/norm 模式（[ClipTextIndex.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Services/Ai/Local/ClipTextIndex.cs#L142-L148)），避免新依赖；排序用 `Enumerable.OrderByDescending` + `Take(TopN)`。

### `tests/QL.World.Tests/Support/FakeAiService.cs`

抽取 [AiAnalysisServiceTests.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/tests/QL.World.Tests/Ai/AiAnalysisServiceTests.cs#L11-L27) 现有 `FakeAiService` 到独立文件，新增 `Func<string, float[]?>? EmbedTextFunc` 字段供 SemanticSearchServiceTests 与 TopBarViewModelTests 共享。

### `tests/QL.World.Tests/Semantic/SemanticSearchServiceTests.cs`

合成 dim=2 embedding（[1,0]/[0,1]/[0.7,0.7]）直接 SQL 插入 TestLibrary 库；FakeAiService.EmbedTextFunc 返回 [1,0]。断言：
- `SearchByTextAsync` 返回顺序按余弦降序
- TopN=50 截断（插 60 行测截断）
- `Invalidate()` 后新插入的 embedding 可见
- `HasAnyEmbeddings` 反映状态
- 无 embedding 时 `SearchByImageId` 返回 null

### `tests/QL.World.Tests/Semantic/ImageQueryServiceSemanticTests.cs`

- 手动设 `filter.SemanticResultIds=[id2,id3]` → SQL 返回仅这两条
- `Semantic_mode_empty_list` → 返回零行（`AND 1=0` 分支）

---

## 修改文件（按依赖顺序）

| 文件 | 变更 |
|---|---|
| [LibraryEnums.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Models/LibraryEnums.cs#L21-L28) L21-28 | `SearchMode` 加 `Semantic` 成员 |
| [LibraryFilter.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Models/LibraryFilter.cs) | 加 `IReadOnlyList<long>? SemanticResultIds` 与 `long? SimilarToImageId`；`Clone()` 复制两者 |
| [IAiService.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Services/Ai/IAiService.cs) | 加 `Task<float[]?> EmbedTextAsync(string text, CancellationToken ct)`；注释说明 Local 在 M3-E1 返回 null（M3-E2 实现） |
| `DisabledAiService.cs` | `EmbedTextAsync` → `Task.FromResult<float[]?>(null)` |
| [CloudAiService.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Services/Ai/CloudAiService.cs#L157-L179) L157 | 抽 `GetEmbeddingAsync` 重构为接收 raw input string；新增 `EmbedTextAsync` 调它；失败 catch 返回 null（与 AnalyzeImageAsync 错误隔离） |
| `Local/LocalAiService.cs` | `EmbedTextAsync` → `Task.FromResult<float[]?>(null)` + `_log("[AI-LOCAL] text embed deferred to M3-E2")` |
| [AiAnalysisService.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Services/Ai/AiAnalysisService.cs#L189-L206) L160-207 | 加 `public event Action? EmbeddingsUpdated;`，`PersistResult` 末尾 `_db.Run` 后 raise（事件解耦避免循环依赖） |
| [ImageQueryService.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Services/ImageQueryService.cs#L412-L469) L412 `AppendClauses` | 优先分支：`filter.SemanticResultIds is { Count: > 0 }` → 拼 `i.id IN ($sid0,..)`；若 SemanticMode 且 list null/empty → `AND 1=0` |
| [ServiceRegistry.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Services/ServiceRegistry.cs#L13-L29) | 加 `public SemanticSearchService SemanticSearch { get; }` 作 #15，构造参数末尾 |
| [App.xaml.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/App.xaml.cs#L75-L111) L93-112 | 实例化 `semanticSearch`，`aiAnalysis.EmbeddingsUpdated += semanticSearch.Invalidate;` 在 ServiceRegistry 构造前 |
| [TopBarViewModel.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/ViewModels/TopBarViewModel.cs#L111-L166) L111-166 | 构造加 `SemanticSearchService semanticSearch` 参数；SearchModes 改为方法返回（`_ai.SupportsEmbeddings \|\| _semanticSearch.HasAnyEmbeddings()` 时才加「语义」档；当前档被剔除时 setter 回退 Tag）；`CommitSearchAsync` Semantic 分支调 `SearchByTextAsync` → null 则 `NotificationService.Publish("语义搜索不可用：未配置 AI 或无已分析图片")` + 回退 Tag；非空则 `filter.SemanticResultIds = result; filter.SimilarToImageId = null; filter.Text = null` |
| [ShellViewModel.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/ViewModels/ShellViewModel.cs#L50-L67) L50-67 | 加 `FindSimilarCommand = new AsyncRelayCommand(FindSimilarAsync, () => HasSelection() && _services.SemanticSearch.HasAnyEmbeddings())`（无 embedding 时菜单项 IsEnabled=False，ToolTip "暂无已分析图片"）；`FindSimilarAsync` 用 `Gallery.PrimarySelected.Id` 调 `SearchByImageId`，设 `Gallery.Filter.SimilarToImageId/SemanticResultIds/SearchMode=Semantic`，`TopBar.SelectedSearchMode = Semantic`，`RefreshAsync`；`ClearFilters` 清两个新字段 |
| [ShellWindow.xaml](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Views/ShellWindow.xaml#L440-L474) L470 | 在 "重新生成缩略图" 后加 `<MenuItem Header="找相似图" Command="{Binding DataContext.FindSimilarCommand, RelativeSource={RelativeSource AncestorType=ContextMenu}}" />`；详情面板 L542-549 加第 4 按钮 "找相似" 同绑定 |
| [UI_NUMBERING.md](file:///c:/AI_Programming/APP/QL_World_dotnet/docs/UI_NUMBERING.md#L15) L15 | 改 W1/A1-5 为 "标签 / 文件名·备注 / 语义（M3-E1）"；新增 `W1/B2-3-CM1 图卡右键「找相似图」 已实现（M3-E1）` 与 `W1/B3-8 详情栏「找相似」按钮 已实现（M3-E1）` |
| `CHANGELOG.md` | 新增 `## [0.3.1] — M3-E1 语义搜索` 条目 |

---

## 测试策略（GEMINI.md §8：接口假对象，禁网络/GPU/真实模型）

1. **SemanticSearchServiceTests**：合成 dim=2 embedding 直接 SQL 插入 TestLibrary 库；FakeAiService.EmbedTextFunc 返回 [1,0]。断言：`SearchByTextAsync` 顺序按余弦降序、TopN=50 截断（插 60 行）、`Invalidate()` 后新插入可见、`HasAnyEmbeddings` 反映状态。
2. **CloudAiServiceTests**（扩展现有）：加 `EmbedTextAsync` 用 stub `HttpMessageHandler` 返回 `{ "data":[{"embedding":[0.1,0.2]}] }`，断言向量正确；HTTP 500 返回 null。
3. **DisabledAiServiceTests**：`EmbedTextAsync` 返回 null。
4. **LocalAiServiceTests**：`EmbedTextAsync` 返回 null + 不创建 InferenceSession（断言 sessionFactory 不被调用）。
5. **ImageQueryServiceSemanticTests**：`Semantic_filter_limits_to_id_list`（手动设 SemanticResultIds=[id2,id3]，断言只返这两条、顺序按 IN 子句）与 `Semantic_mode_empty_list_returns_zero_rows`。
6. **TopBarViewModelTests**：构造 FakeAiService IsAvailable=true 时 SearchModes 含 3 项；IsAvailable=false 时只 2 项；CommitSearchAsync Semantic 模式调用 `_semanticSearch` 一次。

---

## 验证

1. `dotnet build -c Release` — 0 警告 0 错误
2. `dotnet test` — 全绿（新增 3 个测试文件 + 扩展 CloudAiService/Disabled/Local/TopBar/ImageQuery 测试）
3. 手动 UI（run.bat 启动）：
   - 配置 Cloud AI → 跑批量分析 → image_embeddings 有行
   - 搜索框选「语义」→ 输入 "硬表面金属质感" → 返回 top-50
   - 图卡右键「找相似图」→ gallery 显示相似 top-50
   - 详情栏「找相似」按钮 → 同效果
   - 切 Disabled AI 重启 → 「语义」档从下拉消失（HasAnyEmbeddings 仍 true 时保留），但「找相似」仍可用（用已存 embedding）
   - 全新空库 → 「找相似」置灰 + 提示
4. 性能：1000 张 × 512 维 = 2MB 缓存，单次 `SearchByEmbedding` 应 <100ms（纯内存余弦 + 排序）

## 关键文件（新增 / 重点修改）

- `src/QL.World/Services/SemanticSearchService.cs`（新，服务 #15）
- `src/QL.World/Services/Ai/IAiService.cs`（加 EmbedTextAsync）
- `src/QL.World/Services/Ai/CloudAiService.cs` L157（GetEmbeddingAsync 重构）
- `src/QL.World/Services/Ai/AiAnalysisService.cs` L160-207（EmbeddingsUpdated 事件）
- `src/QL.World/Services/ImageQueryService.cs` L412-469（AppendClauses 语义分支）
- `src/QL.World/ViewModels/TopBarViewModel.cs` L111-166（SearchModes + CommitSearchAsync Semantic）
- `src/QL.World/ViewModels/ShellViewModel.cs` L50-67（FindSimilarCommand）
- `src/QL.World/Views/ShellWindow.xaml` L470（图卡右键菜单）+ L542-549（详情按钮）
- `tests/QL.World.Tests/Support/FakeAiService.cs`（新，抽取共享）
- `tests/QL.World.Tests/Semantic/SemanticSearchServiceTests.cs`（新）
- `tests/QL.World.Tests/Semantic/ImageQueryServiceSemanticTests.cs`（新）
