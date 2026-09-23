# M4-D 压测基线（SQL 层，10 万级合成库）

- **日期**：2026-09-22
- **机器**：Windows 本地（Windows 11 / NT 10.0.26200，AMD Ryzen AI 9 HX 370，12C/24T，31 GB RAM，.NET 10.0.400）
- **数据根**：
  - 10 万库：`c:\AI_Programming\APP\QL_World_dotnet\artifacts\stress\root100k`
  - 冒烟库：`c:\AI_Programming\APP\QL_World_dotnet\artifacts\stress\root3`
- **工具**：`tools/StressGen`（console，net10.0-windows，仅引用 QL.World public API；未加入 QL.World.sln）
  - `dotnet run --project tools/StressGen -c Release -- gen --count 100000 --out <root>`
  - `dotnet run --project tools/StressGen -c Release -- bench --root <root> --runs 5`

## 1. 生成统计（gen --count 100000）

| 指标 | 值 |
|---|---|
| images 总数 | 100,000（普通 95,000 + 贴图集 5,000） |
| image_tags 行数 | 285,095（60 词标签池，普通图每图 2–4 个，source='manual', confidence=1.0） |
| collections | 5 × 500 张（共 2,500 关联行） |
| PBR 贴图组（RescanAll） | 扫描 100,000，**组 1,500**，成员 5,000（250 个 set 目录 × 每目录 6 组，组 3–4 张，满足 ≥2 张同目录同基名） |
| 内容 | 64×48 PNG 源图（颜色随 i 微调），WebP 缩略图（边长 64、质量 70，布局 `thumbnails\<hash[..2]>\<hash>.webp`，file_hash 与缩略图同名 hash） |
| **生成总耗时** | **57.4 s（0.96 min，≈1,743 images/s）**，目标 < 15 分钟 ✅ |
| 分阶段耗时 | 文件+入库 50.3 s；标签 1.5 s；收藏夹 0.0 s；PBR rescan 0.4 s；统计 5.1 s |
| 库体积（du） | **96.9 MB / 200,003 文件** = qlworld.db 66.61 MB + 源 PNG 15.2 MB(95,000) + textures 0.8 MB(5,000) + thumbnails 7.9 MB(100,000) |
| 冒烟库 root3 | 3 张图 + 8 标签 + 5×3 收藏，0.09 MB，bench 冒烟通过（无贴图组时 op5 自动跳过） |

## 2. Bench 指标（runs=5，Stopwatch，ms）

数据：100,000 行 images；WAL + synchronous=NORMAL（与主工程一致）；每项连续 5 次全量物化执行。

| # | 操作 | 行数 | 首次 ms | 中位 ms | 最大 ms |
|---|---|---:|---:|---:|---:|
| 1 | `QueryGallery(new LibraryFilter())` 全量（ImportedDesc） | 100,000 | 715.3 | **598.1** | 715.3 |
| 2 | `QueryGallery{ Text="photo_0", SearchMode=FileNameNotes }` | 95,000 | 676.2 | **676.2** | 727.9 |
| 3 | `QueryGallery{ Tag="体素" }`（最热标签，4,942 行命中） | 4,942 | 78.5 | **81.0** | 87.1 |
| 4 | `QueryGallery{ Category="贴图/纹理" }`（Categories.All[0]） | 14,500 | 114.9 | **123.5** | 132.1 |
| 5 | `QueryGallery{ PbrGroupId=1 }` | 3 | 13.2 | **14.1** | 18.7 |
| 6 | `QueryGallery{ CollectionId=1 }` | 500 | 33.2 | **27.1** | 33.2 |
| 7 | `GetCategoryCounts()` | 10 | 8.6 | **7.9** | 8.6 |
| 8 | `PbrGroupingService.GetGroups()` | 1,500 | 2.3 | **2.3** | 2.3 |
| 9 | 裸 SQL `SELECT COUNT(*) FROM images`（对照） | 100,000 | 1.8 | **0.0** | 1.8 |

读法：op9 证明 SQLite 引擎层 COUNT 为 O(1) 级（缓存后 <1ms）；op1 的 ~600ms 几乎全部消耗在**全量物化 10 万行 + tags_csv 相关子查询 + 排序**，而非引擎扫描本身。op2 的 `LIKE '%photo_0%'` 命中全部 95,000 张普通图（贴图集文件名不含该词），是最坏情况文本检索。

## 3. EXPLAIN QUERY PLAN（按 QueryGallery 同语义手写的代表性 SQL）

**op1 全量（ImportedDesc）**
```sql
SELECT id FROM images ORDER BY created_at DESC, id DESC;
```
```text
(3,0)  SCAN images
(10,0) USE TEMP B-TREE FOR ORDER BY
```

**op3 标签过滤**
```sql
SELECT i.id FROM images i
WHERE EXISTS (SELECT 1 FROM image_tags it
              WHERE it.image_id = i.id AND it.tag = $tag);
-- $tag = '体素'
```
```text
(4,0) SCAN i USING COVERING INDEX idx_images_missing
(6,0) SEARCH it EXISTS USING COVERING INDEX sqlite_autoindex_image_tags_1 (image_id=? AND tag=?)
```

**op5 贴图组过滤**
```sql
SELECT i.id FROM images i
WHERE EXISTS (SELECT 1 FROM pbr_group_images pgi
              WHERE pgi.image_id = i.id AND pgi.group_id = $g);
-- $g = 1
```
```text
(4,0) SCAN i USING COVERING INDEX idx_images_missing
(6,0) SEARCH pgi EXISTS USING COVERING INDEX sqlite_autoindex_pbr_group_images_1 (group_id=? AND image_id=?)
```

## 4. 计划解读与结论

1. **op1 是全表扫描**（`SCAN images` + `TEMP B-TREE` 排序）。`created_at` 无索引；在"无 LIMIT 全量加载"的现有语义下扫描无法避免，10 万行物化 ~600ms 为基线。**引入 LIMIT 分页后**，若不建 `(created_at, id)` 索引，`ORDER BY created_at DESC, id DESC` 仍会 TEMP B-TREE——M4 后续做 LIMIT 优化时应同步加该索引。
2. **op3/op5 外层均为全索引扫描逐行探测**：SQLite 对 EXISTS 相关子查询选择了扫 `idx_images_missing` 覆盖索引（10 万行）× 内层 PK 索引 seek 的策略；`idx_image_tags_tag(tag)` 索引存在但**未被选中**先物化标签行集。81ms 结果可接受，不算缺索引故障；若 tag 过滤成为高频交互路径，可评估改写为 `IN (SELECT image_id FROM image_tags WHERE tag = ?)`。
3. **无缺失索引导致的性能事故**：image_tags PK、pbr_group_images PK、collection_images 均以覆盖索引 SEARCH 命中；分类过滤走 `idx_images_category`（op4 123ms / 14,500 行）。
4. 缩略图布局与主工程 `ThumbnailService.GetThumbnailPath` 一致（`thumbnails\<hash[..2]>\<hash>.webp`，file_hash 列 = 缩略图文件名），可直接供画廊虚拟化加载冒烟。

## 5. 复现

```powershell
dotnet run --project tools/StressGen -c Release -- gen --count 100000 --out artifacts/stress/root100k
dotnet run --project tools/StressGen -c Release -- bench --root artifacts/stress/root100k --runs 5
```

注意：gen 目标根必须不含 `qlworld.db`（工具会拒绝覆盖）；StressGen 未加入 QL.World.sln，独立 `dotnet run`/`dotnet build` 即可。

## 6. M4-D 优化落地：前后对比（同库同机，库自动迁移 v2→v3）

**优化内容**：① Schema v3 补分页排序复合索引 `(created_at,id)/(sortable_name,id)/(file_size,id)`，`idx_images_rating` 升级为 `(rating,id)`；② `LibraryFilter.Limit/Offset` + `ImageQueryService` 参数化 `LIMIT/OFFSET` + `CountFiltered`；③ `GalleryViewModel` 按 1000 行/页增量加载，`ShellWindow` 滚动近底 500px 触发 `LoadMoreAsync`（endless scroll）。

**优化后 Bench（runs=5，同库 100,000 行）**

| # | 操作 | 行数 | 首次 ms | 中位 ms | 最大 ms |
|---|---|---:|---:|---:|---:|
| 1 | `QueryGallery` 全量（ImportedDesc，未分页语义对照） | 100,000 | 722.2 | **557.3** | 722.2 |
| 2 | `QueryGallery{ Text="photo_0" }` | 95,000 | 580.3 | **569.6** | 580.3 |
| 3 | `QueryGallery{ Tag="体素" }` | 4,942 | 74.8 | **77.6** | 89.0 |
| 4 | `QueryGallery{ Category="贴图/纹理" }` | 14,500 | 118.8 | **119.7** | 121.5 |
| 5 | `QueryGallery{ PbrGroupId=1 }` | 3 | 20.1 | **17.7** | 20.1 |
| 6 | `QueryGallery{ CollectionId=1 }` | 500 | 25.1 | **24.6** | 26.9 |
| 7 | `GetCategoryCounts()` | 10 | 6.8 | **6.3** | 6.8 |
| 8 | `GetGroups()` | 1,500 | 1.8 | **1.6** | 2.2 |
| 9 | 裸 SQL `COUNT(*)`（对照） | 100,000 | 1.4 | **0.0** | 1.4 |
| **10** | **分页首页 `QueryGallery{Limit=1000}`+`CountFiltered`（= RefreshAsync 全链路）** | 1,000+1 | 7.4 | **4.7** | 7.4 |
| **11** | **深翻页 `QueryGallery{Limit=1000, Offset=50000}`+`CountFiltered`** | 1,000+1 | 13.2 | **11.2** | 15.7 |

**前后对比结论**

| 指标 | 优化前（v2 全量加载） | 优化后（v3 分页） | 提升 |
|---|---:|---:|---|
| 画廊首屏（RefreshAsync 全链路，SQL 侧） | 598.1 ms（全量物化 10 万行） | **4.7 ms**（首页 1,000 行 + 计数） | **≈127×** |
| 深翻页（滚到第 5 万行附近，下一页） | 同 598 ms 级（仍全量） | **11.2 ms** | ≈53× |
| op1 排序开销 | `SCAN images` + `TEMP B-TREE`（created_at 无索引） | `SCAN images USING COVERING INDEX idx_images_created_id`（免排序） | TEMP B-TREE 消除 |
| 标签/分类/贴图组/收藏夹过滤 | 81.0 / 123.5 / 14.1 / 27.1 ms | 77.6 / 119.7 / 17.7 / 24.6 ms | 无回归（噪声级差异） |

**优化后 EXPLAIN（新增 op10）**

```sql
SELECT id FROM images ORDER BY created_at DESC, id DESC LIMIT 1000 OFFSET 0;
```
```text
(7,0)  SCAN images USING COVERING INDEX idx_images_created_id
```

LIMIT 分页沿覆盖索引顺序读前 N 行即返回，既无 TEMP B-TREE 也无回表；深翻页 OFFSET=50,000 仅多跳过 5 万索引项，代价 11ms。**PRD L233「服务端分页+排序，按页取数」达标；10 万库画廊首屏 SQL 侧 <10ms，满足 PRD L68 滚动 60fps 设计目标的数据侧要求（缩略图由 VirtualizedThumbnailLoader 按 hash 异步加载，不进主查询）。**
