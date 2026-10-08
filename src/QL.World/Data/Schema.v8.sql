-- v8 (v0.10.0): AI 自适应学习与训练数据留存
-- 目标：最大化保留「AI 原始建议」与「用户修正行为」两类数据，
-- 支撑应用层自适应（保留率重排 / 词表学习 / 参考样本 kNN），
-- 并为将来有硬件时的模型微调直接提供正负样本与偏好数据集。

-- AI 原始建议快照：每次分析写入，永不删除（用户后续删标签不影响此表），
-- 将来训练时与 image_tags 最终态对齐即得 (image, ai_tags, human_tags) 三元组。
CREATE TABLE IF NOT EXISTS ai_tag_suggestions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    image_id INTEGER NOT NULL,
    tag TEXT NOT NULL,
    confidence REAL,
    is_primary_category INTEGER NOT NULL DEFAULT 0,
    backend TEXT NOT NULL,            -- local / cloud
    model_id TEXT,
    created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_suggestions_tag ON ai_tag_suggestions(tag);
CREATE INDEX IF NOT EXISTS idx_suggestions_image ON ai_tag_suggestions(image_id);

-- 用户标签编辑事件日志：只追加、永不删除；保留率统计与困难负样本来源。
CREATE TABLE IF NOT EXISTS tag_feedback (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    image_id INTEGER NOT NULL,
    tag TEXT NOT NULL,
    action TEXT NOT NULL,             -- added / removed / bulk_removed
    tag_source TEXT NOT NULL,         -- manual / ai（被编辑行的来源）
    created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_feedback_tag ON tag_feedback(tag);

-- 学习词表文本 embedding 缓存（本地 text_model 生成，按标签缓存避免重复推理）。
CREATE TABLE IF NOT EXISTS learned_tag_embeddings (
    tag TEXT PRIMARY KEY,
    dim INTEGER NOT NULL,
    embedding BLOB NOT NULL,
    model_id TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
