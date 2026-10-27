# 设计文档：数据库图片查重（近重复检测）

- 日期：2026-10-27
- 状态：设计已获用户逐节确认，待实施
- 目标版本：0.16.0（NO.M，自 0.15.1 起）
- 里程碑：DUP-A ~ DUP-E（每阶段独立提交过门禁）

## 1. 背景与目标

库内已有两道去重能力，但都覆盖不了「画面相同、字节不同」的近重复：

| 已有能力 | 覆盖范围 | 缺口 |
|---|---|---|
| ImportService MD5 内容哈希，`(hash, category)` 作用域去重（M11） | 字节级完全相同 | 转码/缩放/调色/加水印/截图裁剪的同一张图 |
| 「找相似图」CLIP 向量余弦（单图手动触发） | 语义相似 | 无全库批量扫描、无重复组概念、阈值不区分「同图」与「同主体」 |

本功能提供**自主查重**：导入时实时提醒 + 全库扫描，输出重复组供人工审核处理。

## 2. 需求决策（已确认）

| 决策点 | 结论 |
|---|---|
| 判定范围 | 严格同图 + 近似变体（连拍/同角色不同渲染变体也归组） |
| 触发时机 | 导入时实时检测（不拦截）+ 全库手动扫描 |
| 处理交互 | 独立重复审核窗口（W8） |
| AI 依赖 | 分层自动降级：无 AI 时 L1（pHash）恒可用 |

## 3. 技术选型（已确认方案三：三层混合管线）

| 层 | 手段 | 判定 | 可用性 |
|---|---|---|---|
| L1 严格同图 | dHash 64-bit 感知哈希，汉明距离 ≤10 | 转码/缩放/调色必命中 | 恒可用（零依赖） |
| L2 近似变体 | CLIP 图像向量余弦 ≥0.92（**同 model_id 内**） | 连拍/变体/裁剪版 | 跟随 AI 档位；无向量跳过 |
| L3 VLM 终审 | 边界带（pHash 10~18 或余弦 0.88~0.92）双图送 VLM 判断是否同图 | 消模糊带误报，组标「已终审/未终审」 | 可选；仅 Cloud 后端支持，不可用跳过 |

淘汰方案：纯 pHash（做不了近似变体分组）；纯 CLIP 向量扫描（未分析图无向量覆盖不全、local/cloud 向量空间不可互比、阈值模糊带大）。

被否因素记录：L1/L2 阈值（≤10 / ≥0.92）为代码常量，v1 不暴露设置 UI；实测后需要调参再议。

## 4. 架构设计

### 4.1 新增组件（3 个，零新第三方依赖）

1. **`PerceptualHashService`**（`Services/`）
   - `ComputePhash(Stream)` → 64-bit dHash：降采样 9×9 灰度，横向相邻差分得 64 位；导入路径对原文件流解码小尺寸（毫秒级），存量补算路径复用 256px Thumb WebP（走现有 `WebpImageDecoder`/ImageSharp）
   - `HammingDistance(ulong, ulong)`：popcount
   - `ToHex` / `FromHex`：16 位 hex 与列值互转
2. **`DuplicateScanService`**（`Services/`）
   - 内存 BK-tree 承载全库 phash 索引（启动懒加载 + 导入增量插入），L1 近邻查询
   - L2 分块余弦扫描：仅比较 `model_id` 相同的向量（local/cloud 向量空间不互比）
   - 聚类：候选对取传递闭包成组（a~b 且 b~c → 同组）
   - 全库扫描后台 Task 运行，IProgress 进度上报，支持取消；结果写 `duplicate_candidates`
3. **`IVlmImageCompare`** 能力接口（`Services/Ai/`）
   - `CompareImagesAsync(string pathA, string pathB, CancellationToken)` → `(bool SameImage, double Confidence)`
   - 仅 `CloudAiService` 实现（VLM 双图对比）；`LocalAiService`/`DisabledAiService` 不实现 → 扫描服务以能力探测（is IVlmImageCompare）决定 L3 是否执行
   - 不改动 `IAiService` 现有三方法（AnalyzeImageAsync / EmbedTextAsync / TestConnectionAsync）

### 4.2 Schema v12（只增不删）

```sql
ALTER TABLE images ADD COLUMN phash TEXT;            -- 16 位 hex，NULL=未计算

CREATE TABLE duplicate_candidates (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    group_id    TEXT NOT NULL,                        -- 组 GUID
    image_id    INTEGER NOT NULL,
    kind        TEXT NOT NULL,                        -- 'exact' | 'variant'
    level       TEXT NOT NULL,                        -- 'L1' | 'L2' | 'L3'
    score       REAL NOT NULL,                        -- 汉明距离取倒数 / 余弦值
    status      TEXT NOT NULL DEFAULT 'pending',      -- 'pending' | 'resolved' | 'dismissed'
    created_at  TEXT NOT NULL,
    updated_at  TEXT NOT NULL,
    FOREIGN KEY (image_id) REFERENCES images(id)
);
CREATE INDEX idx_dupcand_group  ON duplicate_candidates(group_id);
CREATE INDEX idx_dupcand_status ON duplicate_candidates(status);
```

### 4.3 导入集成（DUP-A/B）

ImportService 在字节哈希去重之后、INSERT 之前：对流解码 9×9 小图计算 phash → 随 images INSERT 落列 → 查 BK-tree。命中 → W2 导入日志行 + 通知中心 `NotificationKind.DuplicateDetected`（「本次导入发现 M 张疑似重复」），**照常入库不拦截**。

### 4.4 全库扫描（DUP-C/E）

W8 触发 → 后台线程：
1. 补算存量缺失 phash（从 Thumb WebP）
2. L1：BK-tree 全量近邻配对（距离 ≤10 → exact）
3. L2：同 model_id 向量余弦 ≥0.92 → variant（未分析图跳过，汇总提示「运行 AI 分析提升覆盖」）
4. L3：边界带（pHash 10~18 或余弦 0.88~0.92）且 Cloud 后端可用 → VLM 终审：确认则该候选对 `level` 改写为 `'L3'`（`kind` 按判定落 exact/variant），否决则该对丢弃；不可用静默跳过。组内存在 `level='L3'` 行即视为「已终审」（W8 徽标按此派生，不加新列）
5. 重新聚类 → 候选组落表（pending）→ 完成通知「全库查重完成：X 组同图 / Y 组变体」

### 4.5 状态流与错误处理

- 候选状态：`pending → resolved / dismissed`，仅 W8 内变更
- AI 不可用：L2 跳过（提示）、L3 跳过，L1 结果照常输出
- phash/向量缺失：对应层自动跳过，不报错
- 扫描可取消；取消不写表
- 重复扫描：新一轮结果先清 `pending` 旧行再写入，`resolved/dismissed` 历史保留

## 5. UI 设计（DUP-D）

### 5.1 W8 重复审核窗口（新窗口，布局参照 W6 审核队列模式）

- **工具栏**：开始全库扫描（后台+进度条+可取消）｜类型筛选（全部/严格同图/近似变体）｜状态筛选（待处理/已处理）
- **左侧组列表**：组卡片 = 缩略图堆叠预览 + 成员数 + 层级徽标（L1 同图 / L2 变体 / L3 已终审）+ 得分
- **右侧组详情**：组内图片并排卡片（大缩略图、文件名、分辨率、大小、入库时间、评分、路径）；「推荐保留」标记打在分辨率×大小最优一张上
- **组动作仅两个**：`保留全部`（组 → dismissed）/ `删除选中`（沿用画廊现有删除+撤销机制；组内仅剩 1 张时组自动 → resolved）
- **顶栏入口**：新增 `W1/A1-11「查重」`按钮打开 W8；AI 不可用**不置灰**（L1 恒可用），与 A1-2 行为不同
- **不加设置项**：阈值与 L3 开关为代码常量

### 5.2 编号变更（UI_NUMBERING.md 同步，铁律：新增顺延）

- 新增 `W8`（重复审核窗口）及下钻编号（工具栏/组列表/组详情/动作区，实施时按实际控件登记）
- 新增 `W1/A1-11`「查重」按钮
- `NotificationKind` 枚举新增 `DuplicateDetected`（非 UI 编号）

## 6. 测试策略

- **PerceptualHashService**：黄金样图（合成纯色/渐变/棋盘）；同图转码、缩放变体汉明距离 ≤10 断言；异图距离超阈值；hex 往返；popcount 正确性
- **DuplicateScanService**：BK-tree 查全性；聚类传递闭包；model_id 隔离（local 不与 cloud 比对）；候选表 CRUD 与重扫清理语义；进度/取消
- **导入集成**：命中不拦截、phash 落列、通知发布、无命中零通知
- **W8 VM**：命令 CanExecute、筛选、删除联动组自动 resolved、推荐保留计算
- 门禁：Release 0 警告 0 错误 + 全量单测绿（现行 790 基线之上新增）

## 7. 里程碑拆分（每阶段独立提交过门禁）

| 阶段 | 内容 |
|---|---|
| DUP-A | PerceptualHashService + schema v12 + 导入 phash 落列 + 单测 |
| DUP-B | 导入时 L1 查重 + W2 日志 + 通知 + 单测 |
| DUP-C | DuplicateScanService 全库扫描（L1+L2）+ 候选落表 + 单测 |
| DUP-D | W8 审核窗口 + A1-11 入口 + UI_NUMBERING/CHANGELOG 同步 + 冒烟 |
| DUP-E | L3 VLM 终审（IVlmImageCompare + CloudAiService 实现 + 边界带判定）+ 单测 |

## 8. 架构红线符合性

- 纯 MVVM：W8 走 ViewModel；Services 不引用 UI ✓
- 零新第三方依赖：dHash 用现有 ImageSharp ✓
- schema 只增不删；状态走 SQLite 事务，无新状态文件 ✓
- 不引入新 P/Invoke ✓

## 9. 开放问题（实施中再定）

- VLM 双图对比的具体提示词与响应解析（DUP-E）
- L2 分块扫描的批大小与并行度（以万级库实测为准）
- 云端 VLM 费用提示文案（是否在 L3 首次启用时提醒）
