-- QL World schema migration v5 → v6 (M5-D §远程库扫描): record where each
-- image's bytes live so remote (NAS) imports coexist with the local library.
-- `source` is 'local' (default, preserves every pre-M5 row) or 'remote';
-- `nas_connection_id` ties a remote row to the NAS connection stored in
-- AppSettings.NasConnections (config.json — M5-C). No nas_connections table
-- is created in the DB: the connection catalog stays in config.json, which is
-- the single source of truth M5-C shipped with. nas_connection_id is therefore
-- a plain INTEGER with no FK — deleting a connection in config leaves the row
-- pointing at a now-missing id (UI renders it as "deleted NAS"), which is the
-- same UX a FK ON DELETE SET NULL would yield without the data-duplication
-- hazard of mirroring config into SQLite.
-- Applied by DbMigrator inside one transaction; guarded by PRAGMA user_version.

ALTER TABLE images ADD COLUMN source TEXT NOT NULL DEFAULT 'local';
ALTER TABLE images ADD COLUMN nas_connection_id INTEGER NULL;

CREATE INDEX IF NOT EXISTS idx_images_source ON images(source);
CREATE INDEX IF NOT EXISTS idx_images_nas_connection
    ON images(nas_connection_id) WHERE nas_connection_id IS NOT NULL;
