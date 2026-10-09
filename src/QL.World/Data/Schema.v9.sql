-- QL World schema migration v8 → v9 (M11 §图片原始文件统一托管): CAS
-- physical layer + entry-code counter. New rows in `images` carry
-- library_hash / library_path / original_path / entry_code so the
-- gallery can resolve a file purely from the software-managed library
-- directory without depending on the original external path.
--
-- `library_files` is the content-addressed physical layer: one row per
-- unique content_hash (MD5 from Md5Hasher.ComputeStream). The same hash
-- can back multiple `images` rows (different categories / collections)
-- via NTFS hardlinks under library/<category>/. entry_code is unique per
-- physical file and doubles as the human-readable suffix appended to the
-- original file name in both the physical and category hardlink paths.
-- `entry_code_counter` hands out per-day-per-hash-prefix sequence numbers
-- in a transaction-safe way (INSERT ... ON CONFLICT DO UPDATE ...
-- RETURNING counter) — see EntryCodeGenerator.
-- C# backfill in DbMigrator (same transaction) fills library_hash ←
-- file_hash and original_path ← file_path for pre-M11 rows. Physical
-- files are not copied here; the M11-E migration wizard handles that
-- (source=local rows are copied + hardlinked, source=remote rows are
-- skipped + logged per the NAS bypass constraint). library_path and
-- entry_code stay NULL until the wizard runs.
-- `file_path UNIQUE` is intentionally kept: new rows store the category
-- hardlink path, which is naturally unique per (hash, category); the
-- UNIQUE constraint therefore remains satisfiable without a DROP.
-- Applied by DbMigrator inside one transaction; guarded by PRAGMA user_version.

CREATE TABLE IF NOT EXISTS library_files (
    content_hash  TEXT PRIMARY KEY,
    physical_path TEXT NOT NULL,
    file_size     INTEGER NOT NULL,
    width         INTEGER,
    height        INTEGER,
    format        TEXT,
    entry_code    TEXT NOT NULL UNIQUE,
    created_at    TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS entry_code_counter (
    date        TEXT NOT NULL,
    hash_prefix TEXT NOT NULL,
    counter     INTEGER NOT NULL,
    PRIMARY KEY (date, hash_prefix)
);

ALTER TABLE images ADD COLUMN library_hash  TEXT;
ALTER TABLE images ADD COLUMN library_path TEXT;
ALTER TABLE images ADD COLUMN original_path TEXT;
ALTER TABLE images ADD COLUMN entry_code   TEXT;

CREATE INDEX IF NOT EXISTS idx_images_library_hash ON images(library_hash);
