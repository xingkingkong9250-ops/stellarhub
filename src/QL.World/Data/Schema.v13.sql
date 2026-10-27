-- Schema v13: 搜索词条日志
-- Created for 0.20.0 词条 chip 搜索框 + 落库
-- 仅记录画廊命中非空的搜索，用于后续打标词表补充和 AI 学习数据。
-- 只增不删；IF NOT EXISTS 保证幂等。

CREATE TABLE IF NOT EXISTS search_log (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    terms_json   TEXT    NOT NULL,       -- JSON 数组，如 ["角色","赛博朋克"]
    result_count INTEGER NOT NULL,        -- 画廊命中数
    user_id      TEXT,                   -- 可选，当前登录用户；local-only 时为 NULL
    created_at   TEXT    NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_search_log_created  ON search_log(created_at);
CREATE INDEX IF NOT EXISTS idx_search_log_user     ON search_log(user_id);
