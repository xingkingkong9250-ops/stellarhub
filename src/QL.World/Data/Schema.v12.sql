-- Schema v12: 图片感知哈希（phash）与查重候选表
-- Created for 0.16.0 DUP-A（近重复检测基础设施）
-- 只增不删；存量行的 phash 保持 NULL，由全库查重扫描时从缩略图补算。

ALTER TABLE images ADD COLUMN phash TEXT;

CREATE TABLE IF NOT EXISTS duplicate_candidates (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    group_id    TEXT    NOT NULL,
    image_id    INTEGER NOT NULL,
    kind        TEXT    NOT NULL,                      -- 'exact' | 'variant'
    level       TEXT    NOT NULL,                      -- 'L1' | 'L2' | 'L3'
    score       REAL    NOT NULL,
    status      TEXT    NOT NULL DEFAULT 'pending',    -- 'pending' | 'resolved' | 'dismissed'
    created_at  TEXT    NOT NULL,
    updated_at  TEXT    NOT NULL,
    FOREIGN KEY (image_id) REFERENCES images(id)
);

CREATE INDEX IF NOT EXISTS idx_dupcand_group  ON duplicate_candidates(group_id);
CREATE INDEX IF NOT EXISTS idx_dupcand_status ON duplicate_candidates(status);
