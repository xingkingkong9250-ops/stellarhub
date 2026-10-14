-- Schema v11: image_tags 加 pinyin 列，支持拼音搜索
-- Created for v0.14.6 智能搜索

ALTER TABLE image_tags ADD COLUMN pinyin TEXT;

CREATE INDEX IF NOT EXISTS idx_image_tags_pinyin ON image_tags(pinyin);
