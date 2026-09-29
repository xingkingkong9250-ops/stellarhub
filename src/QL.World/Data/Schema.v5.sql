-- QL World schema migration v4 → v5 (U3-A §4.B.2): attribute filter color family.
-- Add images.color_family (one of 9 hue families derived from the first
-- dominant color) so the B1-5 color filter is a plain indexed SQL lookup.
-- Backfill from existing dominant_colors JSON runs in C# inside the same
-- migration transaction (DbMigrator) — pure HSV computation, no re-analysis.
-- Applied by DbMigrator inside one transaction; guarded by PRAGMA user_version.

ALTER TABLE images ADD COLUMN color_family TEXT NULL;

CREATE INDEX IF NOT EXISTS idx_images_color_family ON images(color_family);
