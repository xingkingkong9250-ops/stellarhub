-- QL World schema migration v3 → v4 (U2-D §4.C.2): source badge.
-- Add a `source` column to image_embeddings so the gallery can show whether
-- an embedding was produced by the local ONNX backend or a cloud API.
-- Historical rows default to 'local' (M3-D/E only shipped local ONNX).
-- Applied by DbMigrator inside one transaction; ALTER TABLE ADD COLUMN is
-- guarded by PRAGMA user_version (runs exactly once).

ALTER TABLE image_embeddings ADD COLUMN source TEXT NOT NULL DEFAULT 'local';

CREATE INDEX IF NOT EXISTS idx_image_embeddings_source ON image_embeddings(source);
