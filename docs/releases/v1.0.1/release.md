# PgHatch v1.0.1 — PostgreSQL, the App Way

A portable PostgreSQL instance manager for Windows.

这是针对主窗口恢复布局的小型维护版本，修复已由维护者人工验收。不修改数据库、配置格式或实例生命周期逻辑，不增加新功能。

## 修复

- 修复主窗口最小化后恢复时，实例列表与详情面板整体下移、内容区高度压缩及额外详情滚动条。
- 最小化期间停止标题区和 Logo 测量，在外层布局完成后合并执行标题高度测量，避免嵌套布局时序交错保留过期行高。
- 增加三语言回归检查：连续五次最小化恢复、托盘恢复、重复启动唤醒、窗口宽度变化、最大化恢复及正常内容滚动。
- 保留现有工具栏、Logo、左右面板比例、关闭驻留托盘及 Ctrl+Q 退出行为。历史 v1.0.0 / v0.2.0 发行文件保持不变。

## 下载与运行要求

所有模式功能相同，解压到可写目录后运行 PgHatch.exe；首次使用建议选择 Standalone。升级前使用 Ctrl+Q 退出旧管理器，保留原配置和数据目录，不要用包内文件覆盖 PGDATA。

| 架构 | 下载 | 运行要求 |
| --- | --- | --- |
| Windows x64 | [Standalone](https://github.com/lly007/PgHatch/releases/download/v1.0.1/PgHatch-win-x64-standalone.zip) | 自包含 .NET 10 Desktop Runtime，无需预装 |
| Windows x64 | [Framework-dependent](https://github.com/lly007/PgHatch/releases/download/v1.0.1/PgHatch-win-x64-framework.zip) | 需要 x64 .NET 10 Desktop Runtime |
| Windows ARM64 | [Standalone](https://github.com/lly007/PgHatch/releases/download/v1.0.1/PgHatch-win-arm64-standalone.zip) | 自包含 .NET 10 Desktop Runtime，无需预装 |
| Windows ARM64 | [Framework-dependent](https://github.com/lly007/PgHatch/releases/download/v1.0.1/PgHatch-win-arm64-framework.zip) | 需要 ARM64 .NET 10 Desktop Runtime |

Framework-dependent 仅安装普通 .NET Runtime 不足以满足 WinForms 要求。PgHatch 不捆绑 PostgreSQL；下载和注册不会自动创建或启动数据库。

四个 ZIP 的 SHA-256 见随 Release 提供的 [SHA256SUMS.txt](https://github.com/lly007/PgHatch/releases/download/v1.0.1/SHA256SUMS.txt)，可用 PowerShell 的 `Get-FileHash -Algorithm SHA256` 核对。发行检查保留 XML Catalog、品牌/功能图标、三语言资源、MIT 与第三方许可证，执行 ZIP 解压及全文件哈希回验。

## 验证与边界

- Release 构建通过，0 警告、0 错误；428 项界面与资源检查通过，覆盖三种语言和新增窗口恢复用例。完整功能验收沿用 V1.0 记录，不重复运行 PostgreSQL 生命周期测试。
- 本机 Windows x64 的实际 GUI 验证覆盖 150% DPI；100%、125%、150%、200% 的控件几何缩放模拟不等于四档系统 DPI 实测。其他系统缩放和跨屏切换仍需进一步实机验证。
- **ARM64 仅完成交叉构建和静态检查，尚未在真实 Windows ARM64 设备验收。** PgHatch 与外部 PostgreSQL 程序无需相同架构；ARM64 上运行 Catalog 中的 x64 PostgreSQL 依赖 Windows x64 兼容能力，尚未单独实测。

[用户指南](../../user-guide.md) · [CHANGELOG](../../../CHANGELOG.md) · [V1.0 基础功能验收](../v1.0.0/validation.md)
