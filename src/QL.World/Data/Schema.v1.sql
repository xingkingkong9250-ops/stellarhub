-- QL World schema, user_version = 1
-- Created wholesale by DbMigrator on a fresh library.

CREATE TABLE IF NOT EXISTS images (
    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    file_path          TEXT    NOT NULL UNIQUE,
    file_name          TEXT    NOT NULL,
    sortable_name      TEXT    NOT NULL,
    file_hash          TEXT    NOT NULL,
    file_size          INTEGER NOT NULL,
    width              INTEGER,
    height             INTEGER,
    format             TEXT,
    rating             INTEGER NOT NULL DEFAULT 0,
    notes              TEXT    NOT NULL DEFAULT '',
    primary_category   TEXT,
    dominant_colors    TEXT,
    is_missing         INTEGER NOT NULL DEFAULT 0,
    created_at         TEXT    NOT NULL,
    updated_at         TEXT    NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_images_hash     ON images(file_hash);
CREATE INDEX IF NOT EXISTS idx_images_category ON images(primary_category);
CREATE INDEX IF NOT EXISTS idx_images_rating   ON images(rating);
CREATE INDEX IF NOT EXISTS idx_images_missing  ON images(is_missing);

CREATE TABLE IF NOT EXISTS image_tags (
    image_id   INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
    tag        TEXT    NOT NULL,
    source     TEXT    NOT NULL DEFAULT 'manual',   -- 'manual' | 'ai'
    confidence REAL,
    created_at TEXT    NOT NULL,
    PRIMARY KEY (image_id, tag)
);

CREATE INDEX IF NOT EXISTS idx_image_tags_tag ON image_tags(tag);

CREATE TABLE IF NOT EXISTS collections (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    name           TEXT    NOT NULL UNIQUE,
    description    TEXT    NOT NULL DEFAULT '',
    cover_image_id INTEGER REFERENCES images(id) ON DELETE SET NULL,
    created_at     TEXT    NOT NULL,
    updated_at     TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS collection_images (
    collection_id INTEGER NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
    image_id      INTEGER NOT NULL REFERENCES images(id)      ON DELETE CASCADE,
    sort_order    INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (collection_id, image_id)
);

CREATE INDEX IF NOT EXISTS idx_collection_images_image ON collection_images(image_id);

CREATE TABLE IF NOT EXISTS image_embeddings (
    image_id   INTEGER PRIMARY KEY REFERENCES images(id) ON DELETE CASCADE,
    model_id   TEXT    NOT NULL,
    dim        INTEGER NOT NULL,
    embedding  BLOB    NOT NULL,   -- little-endian floats, self-describing header
    created_at TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
