# QL World v1.0.0 更新公告

发布日期: 2026-09-24

## 亮点

- 正式版（v1.0.0）：M0–M4 全部里程碑交付完成
- PBR 贴图自动成组：按目录 + 文件尾缀规则识别 basecolor/normal/roughness
  等角色，同一套贴图在左栏「贴图组」区块一键筛选
- 专业格式全支持：TGA / EXR / HDR / PSD（PSD 显示合成帧）直接导入、
  生成缩略图、参与画廊浏览
- 10 万级素材库实测流畅：服务端分页 + 复合索引优化，画廊首次绘制
  从 598ms 降至 4.7ms（约 127 倍）
- 随包发布完整中文用户手册（docs/USER_GUIDE.md）

## 新功能

- PBR 贴图成组（M4-A/B）：导入时按可编辑尾缀规则自动成组，支持手动
  全库重扫；设置窗口新增「贴图组规则」页签（模式/角色/启用/删除、
  恢复默认、保存并重建）
- 专业格式接入（M4-C）：ImageSharp 识别失败自动回退 Magick.NET 解码，
  白名单扩展 tga/exr/hdr/psd/tif/tiff
- 大库性能（M4-D）：服务端分页（每页 1000 增量加载）+ 画廊 endless
  scroll + schema v3 复合索引
- 用户手册（M4-E）：覆盖安装、导入、浏览、搜索、AI 三档、语义搜索、
  设置、数据安全与常见问题
- 发布工具链（M4-F）：build.ps1 -Mode Finalize 一键产出五件套
  （应用包 / 源码包 / 更新公告 / 使用说明 / manifest.json），
  manifest 最后原子落地，历史版本永不覆盖

## 修复

- build.ps1：函数内原生命令 stdout 污染 PowerShell 返回值管道，
  导致 Finalize 向发布工具传参错乱（实测发现，M4-Z 修复）
- build.ps1：中文系统 PS 5.1 读取无 BOM 的 UTF-8 脚本按 GBK 解析，
  中文标点字节对吞换行导致脚本解析错乱；现以 ASCII 注释 + UTF-8 BOM
  双重规避（M4-Z 修复）

## 已知问题

- 本地 AI 模型包（vision 351 MB + text 254 MB）按需下载，未下载时
  文→图语义搜索不可用（图→图搜索与图像分析不受影响），设置页会
  提示补下完整包
- 605 MB 模型包下载端到端与真实 dist 包首启验收建议在目标机人工执行：
  用系统环境变量 QL_WORLD_DATA_ROOT 指向空目录后首启验证
- 旧版 Python 库导入仅迁移图片记录/标签/收藏夹，embeddings 与
  dominant_colors 需在本版本重新分析生成
