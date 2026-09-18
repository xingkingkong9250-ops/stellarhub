# ClipSpike — M3-D 技术验证结论

## 决策：本地推理默认档 = **fp32**（vision_model.onnx）

实测环境：CPU 推理（Microsoft.ML.OnnxRuntime 1.22.1，win-x64），24 张确定性合成渐变图，
text embeddings 由修复后 tokenizer 生成的 `text-embeddings-clip-vit-b32.json`（PromptTemplateVersion=1）。

### 决策规则与实测

| 判据（int8 默认档须全部满足） | 实测 | 结论 |
|---|---|---|
| 分类一致率 ≥ 95% | 13/24 = 54.2% | FAIL |
| embedding 余弦 ≥ 0.98 | mean 0.9380 / min 0.9181 | FAIL |
| 提速 ≥ 1.3× | 1.62×（44.4ms → 27.4ms） | PASS |
| 体积 ≤ fp32 的 60% | 89MB / 351MB ≈ 25% | PASS |

两项核心质量判据不达标 → 按预定规则选 **fp32**。int8 在合成图上失真明显；
对真实照片一致性会好一些，但按预先承诺的规则执行，不做临场放宽。

### 实测基准数据

- fp32：mean 44.4 ms/图 ≈ 22.5 img/s（单线程 CPU）
- int8：mean 27.4 ms/图 ≈ 36.5 img/s（仅作参考，不采用）

### 模型 I/O（已验证）

- vision（fp32/int8）：`pixel_values` float32 [N,3,224,224]（NCHW，CLIP mean/std 归一化，224×224 crop）→ `image_embeds` [N,512]
- text：`input_ids` int64 [N,77]（**无 attention_mask 输入**，尾部 padding 0）→ `text_embeds` [N,512]

### Tokenizer 教训（重要）

`BytesToUnicode` 第一段范围必须用 `'~' - '!' + 1`（= 94 个可打印 ASCII）。
原实现写成 `94 - '!' + 1` = **62**，导致 95..126 号字节（含 a-z）落入重映射分支：
'a' 被错误映射为 'ģ'（chr 291，vocab id 223），BPE merges 全部失效，逐字符编码
（26 content tokens），embedding 类间余弦全 > 0.98（退化但确定性，sanity 仍能通过）。
修复后 8 content tokens、类别区分度健康（best-other 0.83–0.88）。

经验：**sanity 的 diag>off-diag 检查不充分**，必须同时断言 token 数量级
（"a seamless texture map pattern" ≈ 8，而非 = 字符数）。

### 下载通道

huggingface.co 直连在当前网络 SSL 被阻断，`hf-mirror.com` 镜像 fallback 自动生效（必要功能）。

### 模型清单（SHA-256）

| 文件 | 字节 | SHA-256 |
|---|---:|---|
| vision_model.onnx（默认档） | 351,685,709 | fd6e1402a588279d1723c7534d4bcba5bc0b14b47dfab0e46f8c47b8270d7d40 |
| vision_model_quantized.onnx（不采用） | 89,117,001 | 583fd1110a514667812fee7d684952aaf82a99b959760c8d7dca7e0ab9839299 |
| text_model.onnx（仅 Spike 生成 embeddings 用，不分发） | 254,058,553 | 3f6571f5bad13a97c469c1622e1cfc4d9aef78b79fdbfcff804ca357bfada8cc |

text-embeddings JSON：`tools/spike/models/text-embeddings-clip-vit-b32.json`（287,392 bytes，
camelCase 键，D-C 主工程加载时必须用不区分大小写的命名策略反序列化）。
