-- QL World schema migration v1 → v2 (M4-A): PBR texture grouping.
-- Applied incrementally by DbMigrator inside one transaction; must be idempotent.

CREATE TABLE IF NOT EXISTS pbr_groups (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    name       TEXT    NOT NULL,   -- base name for display; duplicates allowed (per-directory groups)
    created_at TEXT    NOT NULL,
    updated_at TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS pbr_group_images (
    group_id INTEGER NOT NULL REFERENCES pbr_groups(id) ON DELETE CASCADE,
    image_id INTEGER NOT NULL REFERENCES images(id)      ON DELETE CASCADE,
    role     TEXT    NOT NULL,   -- basecolor|normal|roughness|metallic|ao|height|other
    PRIMARY KEY (group_id, image_id)
);

-- One image belongs to at most one group; also serves point lookups.
CREATE UNIQUE INDEX IF NOT EXISTS idx_pbr_group_images_image ON pbr_group_images(image_id);
