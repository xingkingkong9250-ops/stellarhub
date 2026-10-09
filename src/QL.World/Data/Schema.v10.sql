-- QL World schema migration v9 → v10 (M12 §标签驱动分类体系):
-- 新增 legacy_primary_category 列备份旧 10 分类原值（只增不删），
-- 然后由 C# backfill（同事务）执行旧→新 6 分类映射 + 有标签的图重推导。
-- 旧分类映射表：
--   贴图/纹理 → 素材
--   材质参考 → 质感
--   环境/场景 → 场景
--   雕刻参考 → 角色
--   UI/HUD   → 道具
--   实拍参考/概念设计/灯光参考/配色参考/动画参考 → NULL（让 AI 重分析或用户重标）
-- Applied by DbMigrator inside one transaction; guarded by PRAGMA user_version.

ALTER TABLE images ADD COLUMN legacy_primary_category TEXT;

CREATE INDEX IF NOT EXISTS idx_images_legacy_category ON images(legacy_primary_category);
