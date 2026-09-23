-- QL World schema migration v2 → v3 (M4-D): server-side pagination indexes.
-- Composite (sort_key, id) indexes let ORDER BY + LIMIT/OFFSET pages read
-- only the requested range instead of sorting the whole table per query.
-- Applied incrementally by DbMigrator inside one transaction; must be idempotent.

CREATE INDEX IF NOT EXISTS idx_images_created_id  ON images(created_at, id);
CREATE INDEX IF NOT EXISTS idx_images_sortable_id ON images(sortable_name, id);
CREATE INDEX IF NOT EXISTS idx_images_size_id     ON images(file_size, id);

-- (rating, id) supersedes the single-column rating index (leftmost prefix
-- covers the same lookups); drop the redundant one to save write overhead.
DROP INDEX IF EXISTS idx_images_rating;
CREATE INDEX IF NOT EXISTS idx_images_rating_id   ON images(rating, id);
