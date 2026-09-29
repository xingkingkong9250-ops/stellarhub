-- QL World schema migration v6 → v7 (M5-G §下载队列): persistent download
-- queue for NAS→local file transfers with resume + retry support.
-- user_id is reserved for M6 multi-user; M5 always uses IUserIdentity.CombinedId.

CREATE TABLE IF NOT EXISTS download_queue (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id TEXT NOT NULL,
    asset_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
    local_target TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending',
    progress REAL NOT NULL DEFAULT 0,
    error TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_download_queue_status ON download_queue(status);
