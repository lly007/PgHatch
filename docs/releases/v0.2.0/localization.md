# PgHatch 本地化与品牌展示验证记录

本轮保持 **v0.2.0**，未覆盖历史封版 EXE/ZIP，未修改 PostgreSQL 管理控制流、命令参数、实例配置 schema 或已有数据。本轮源码在现有 WinForms 架构上增量修改，无新增依赖。

## 实现内容

- 简体中文、英文、繁体中文共用原生 ResourceManager。三套 resx 各有 **266 个同名、非空资源键**，包含主界面、所有现有对话框、托盘、输入校验、危险操作确认、配置恢复、扫描和应用诊断提示。代码引用无缺失键，桌面源文件无遗留硬编码中文 UI 文字。核心目录中保留的中文备份名称识别正则属于业务匹配规则，未更改。
- “更多 → 语言”提供跟随系统、简体中文、English、繁體中文；保存后提示重启生效。默认系统语言映射、手动选择优先级和英文单复数已测试。
- 语言偏好使用独立 `pghatch.ui.json`。原 PostgreSQL 配置缺少语言字段仍可加载，不需要迁移。偏好文件损坏会提示、保留并阻止覆盖；未知属性保留，外部修改会阻止旧快照保存。
- 现有透明高分辨率 PNG 内嵌至主窗口右上角，通常为 100 个逻辑像素，空间不足时缩小至 80 或隐藏。原 PNG、母版、主 ICO、Tray ICO 均未改动。图片不接收点击、不进入 Tab 顺序，随窗口释放。
- 为长翻译提供欢迎说明换行测量、表头最小宽度/换行和布局对话框滚动支持。保留系统标题栏和 PerMonitorV2。
- PostgreSQL 原始输出和日志不翻译，诊断包装文字使用资源。Windows 原生对话框按钮和文件选择器仍遵循 Windows 自身语言。

## 自动化结果

环境：Windows x64，.NET SDK 10.0.402，.NET Desktop Runtime 10.0.13。

| 验证 | 结果 |
| --- | --- |
| Release 解决方案构建 | 0 警告，0 错误 |
| 既有图标检查 + 新增本地化检查 | **267 PASS**（原有 219 项保留） |
| 配置加载、原子保存、恢复、迁移、注册管理回归 | **75 PASS** |
| 真实 PostgreSQL 18.6 集成回归 | **66 PASS** |
| x64 framework-dependent 单文件静态检查 | **272 PASS** |
| 简体/繁体卫星资源 | 已包含在单文件中；简体中文与英文已实际从单文件启动 |
| 历史产物、品牌母版/ICO及真实 PostgreSQL 配置的保护哈希 | **36 / 36 不变** |

新增检查以集合为单位验证资源完整性、占位符、格式化、源代码资源引用、回退、偏好保存与错误保护；覆盖三语言下八种 Form 的构造、托盘和语言菜单、无实例禁用状态、Logo 边界及欢迎文字高度。离屏控件检查和四档图标栅格检查不等同于 Windows 实际四档 DPI 验收。

真实工具来自 `D:\PostgreSQL\PostgreSQL\bin`。本轮集成集群位于独立的 `D:\PGTray-Test\run-20261021-085244-b16a3a77`。测试后对其完整集群/副本执行只读 `pg_ctl status`，均返回 3（无服务器运行）。未启停、初始化或修改用户真实 PGDATA。测试目录保留供排查。

本表保留当时的构建、回归和保护哈希结论；一次性构建日志、开发预览及单文件验证输出已在工程整理时移除，未作为正式发行包交付。后续源码应按当前发行流程重新构建。

## 实际 GUI 检查

通过 computer-use 在隔离目录运行程序，实际观察了简体中文、英文、繁体中文主窗口；英文工具栏、更多/语言菜单、程序管理和新建表单；简体中文无程序配置导入提示和随后的程序管理跳转。语言由英文选择繁体中文后保存成功，当前窗口维持英文，下次启动显示繁体中文。Logo 在正常和最大化窗口中可见，窄空间隐藏逻辑另有自动化边界检查。

英文长表头与欢迎说明、表单底部按钮的布局问题已做相应调整并补充测量检查。GUI 试运行未提交新建表单，也未通过 GUI 启停数据库。试运行结束后关闭了本轮隔离测试应用。

尚不能将这些观察视为三语言、四档 DPI 下的完整 GUI 验收；尤其最后一次换行高度调整有自动化测量验证，仍需在实际 DPI 下人工确认。

## 待人工验收

1. 分别选择三种语言并正常退出、重启，检查菜单勾选和所有对话框；再选择跟随系统，重启检查。现有实例记录应保持一致。
2. 在 100%、125%、150%、200% Windows 缩放下测试常用及最小窗口宽度、最大化和跨屏拖动。检查 Logo、表头、欢迎说明、英文按钮、扫描表格、设置与恢复对话框无裁切。空间不足时确认可滚动并访问确定/取消。
3. 用专用测试集群逐项检查三语言下的选择、启动、停止、重启、日志、程序取消注册确认及错误提示；确认原始 PostgreSQL 输出没有被翻译。
4. 检查关闭窗口隐藏、托盘恢复、重复启动恢复窗口及两种退出操作。避免在真实实例上选择“停止全部并退出”。本轮没有完成托盘全流程人工复测。
5. 在隔离目录检查偏好缺失/损坏、损坏实例配置时仍可选择语言。不要损坏真实用户配置。ARM64 和其他 Windows 显示语言环境的实际 GUI 也仍需分别验收。

## 修改文件

| 文件 | 变更范围 |
| --- | --- |
| `src/PGTray.Core/L10n.cs` | ResourceManager、格式化、单复数和状态文字 |
| `src/PGTray.Core/Resources/Strings.resx` | 默认英文资源 |
| `src/PGTray.Core/Resources/Strings.zh-CN.resx` | 简体中文资源 |
| `src/PGTray.Core/Resources/Strings.zh-TW.resx` | 繁体中文资源 |
| `src/PGTray.Core/ConfigStore.cs` | 对外配置错误文字资源化 |
| `src/PGTray.Core/InstanceDiscovery.cs` | 对外扫描提示资源化 |
| `src/PGTray.Core/InstanceManager.cs` | 对外校验、状态诊断文字资源化 |
| `src/PGTray.Core/Models.cs` | 诊断包装文字资源化，原始输出保留 |
| `src/PGTray.Core/PathSafety.cs` | 对外路径保护错误资源化，规则不变 |
| `src/PGTray.Core/PostgresTools.cs` | 对外工具、状态、初始化错误资源化，命令不变 |
| `src/PGTray/UiPreferences.cs` | 独立语言偏好读取和保存 |
| `src/PGTray/Program.cs` | 创建窗口前选择 UI culture，保留原单实例机制 |
| `src/PGTray/MainForm.cs` | 全部文字、语言菜单、Logo、长文字布局 |
| `src/PGTray/Dialogs.cs` | 现有通用/程序/创建/导入/重命名/日志窗口文字及滚动 |
| `src/PGTray/DiscoveryForm.cs` | 发现窗口文字 |
| `src/PGTray/InstanceSettingsForm.cs` | 设置和关联修复窗口文字 |
| `src/PGTray/UiIcons.cs` | 缺失内嵌图标的错误文字 |
| `src/PGTray/PGTray.csproj` | 内嵌现有高分辨率 PNG |
| `tests/PGTray.IconChecks/LocalizationChecks.cs` | 轻量本地化、偏好和布局回归 |
| `tests/PGTray.IconChecks/Program.cs` | 接入新增检查 |
| `tests/PGTray.IntegrationTests/Program.cs` | 诊断断言使用当前语言返回码标签 |
| `README.md` | 使用说明 |
| `docs/localization.md` | 资源及偏好维护说明 |
| `docs/releases/v0.2.0/localization.md` | 本报告 |
