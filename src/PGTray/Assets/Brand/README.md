# PgHatch 品牌资源

品牌来自用户提供的「破壳而出的蓝色数据库.png」及已确认的派生素材，不使用 PostgreSQL 官方 Slonik Logo。正式图标已接入应用；真实任务栏、通知区域和完整跨屏 DPI 外观仍需人工验收。

## 正式素材

- `Masters/PgHatch.original.png`：原始设计母版，禁止覆盖。SHA-256：`C0DDF319932B563C526139644A12D5023BC568B3B86F5033581DC2D9FEF70FD9`。
- `Masters/PgHatch.transparent.png`：原图大尺寸透明母版，也用于主窗口右上方内嵌 Logo。
- `Masters/PgHatch.small.generated.png`：已采用的小尺寸光学简化稿的原始生成素材，保留追溯。
- `Masters/PgHatch.small.transparent.png`：正式小尺寸母版。SHA-256：`C33E4F3D3C5E09E8E220016BF4F91009110F62F62AEC91DD31B7F3A382C916D2`。
- `PgHatch.ico`、`PgHatch.Tray.ico`：各含 16/20/24/32/40/48/64/128/256px 九帧，32-bit RGBA；小于 256px 使用 BGRA DIB + AND mask，256px 使用 PNG RGBA。
- `Frames/`：与正式 ICO 对应的 RGBA PNG。16–32px 使用简化母版，主图标 40px 以上使用大图母版，托盘各帧使用简化母版。

正式小帧保留数据库与蛋壳造型、颜色及比例，仅调整外围留白：16/20/24/32px 主体分别放大约 9.07%/10.06%/9.97%/10.00%。四张已确认基准位于 [tests/PGTray.IconChecks/Fixtures/Brand](../../../../tests/PGTray.IconChecks/Fixtures/Brand/)，不是运行时资源。已放弃的 A1/A2 素材不参与生成或测试。

## 生成和检查

从工程根目录执行，开发环境需要 Node.js 和 sharp（应用运行不依赖它们，不需要网络访问）：

```text
node scripts/Generate-BrandIcons.cjs
dotnet run --project tests/PGTray.IconChecks -c Release -- artifacts/dev/icon-checks
```

全量生成脚本保留原始母版，输出正式透明派生稿、Frames 和两套 ICO；预览和中间结果写入 `artifacts/dev/brand-icons/`，不写入 docs。`Render-SmallBrandFrame.cjs` 从高分辨率小母版直接渲染四档已确认缩放，不放大现有低分辨率 PNG。每次生成后检查正式素材差异；不要为了预览直接改写封版发行文件。

透明处理只针对蓝色圆角底板外部：大母版在 1254px 画布上使用 8px 内收、330px 圆角，小母版使用 40px 内收、320px 圆角。内部白色蛋壳保持不透明。Lanczos 缩放后仅将 alpha≤2 的外缘噪点归零，不能全图按白色阈值抠除背景。小尺寸必须保留透明圆角和抗锯齿边界。

EXE 由 ApplicationIcon 内嵌主 ICO；ApplicationIcons 为窗口和托盘按 DPI 缓存拥有的 Icon，先销毁控件再释放缓存，不释放系统共享 Icon。主窗口品牌 PNG 同样内嵌，不依赖外部文件路径。

## 来源与许可

大图内部像素保留用户原图；小尺寸光学稿来自已确认的生成派生稿，其后仅进行透明边缘处理和等比例缩放。品牌素材不套用功能图标的 Lucide 许可，也不代表 PostgreSQL 官方产品或商标授权。功能图标的 ISC / Feather MIT 版权声明见[第三方声明](../../../../THIRD-PARTY-NOTICES.md)及 `Assets/Ui/Lucide/LICENSE`。

验证结论见 [v0.2.0 验证记录](../../../../docs/releases/v0.2.0/validation.md)。
