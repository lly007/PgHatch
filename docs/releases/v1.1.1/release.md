# PgHatch v1.1.1

PostgreSQL, the App Way.

PgHatch v1.1.1 是 Issue #3 对应的维护版本。维护者已完成本轮真实 Windows GUI 验收并授权正式发布。具体 DPI 和显示器组合按维护者原始记录确认。

## 本轮修复

- 附近程序候选菜单由窗体持有、复用及统一释放，修复点击与关闭期间的 ContextMenuStrip 已释放异常。
- 运行实例移除提供明确的停止并移除、保持运行仅解除登记和取消选择。停止与登记保存共享生命周期互斥，核对身份、控制器及实际退出，失败或不确定时保留登记。PGDATA 和日志由用户管理。
- 主窗口采用统一 96 DPI 逻辑设计树，分别管理窗口工作区约束和内容布局。保留用户逻辑尺寸，小工作区提供换行、次要 Logo 隐藏及内容滚动。
- 子窗口统一品牌、齿轮、数据库及文档多帧 ICO，复用现有 Lucide 素材和独立窗体资源生命周期。

## 下载与兼容性

[v1.1.1 Release](https://github.com/lly007/PgHatch/releases/tag/v1.1.1) 提供 Windows x64、ARM64 的 standalone 和 framework 四种 ZIP，以及 SHA256SUMS.txt。Standalone 自带 .NET，Framework 需要对应架构的 .NET 10 Desktop Runtime。实际字节数、SHA-256、构建输入和最终发布状态见[发行验收](validation.md)。

升级前退出旧管理器，保留配置、PGDATA、日志及许可文件。配置 schema 2、现有初始化默认值和主版本兼容规则继续适用。PostgreSQL 同主版本维护切换仍需显式操作和扩展兼容核对。

## 验证范围

修复阶段自动化为 IconChecks 1253 PASS、业务及安全回归 400 PASS / 1 SKIP，Release 构建为 0 警告、0 错误。维护者随后确认本轮真实 Windows GUI 验收通过。正式版本从合并后的干净 main 全新构建，版本、资源、压缩包和实际 x64 冒烟结果在验收记录中单列。

ARM64 Windows 实机、符号链接特权、PostgreSQL 18.3 特定中文 share 路径等既有验证限制继续列入维护范围。真实 GUI 确认的范围以维护者描述为准。Windows 强制关机、断电、扩展兼容和网络数据目录的现有限制继续适用。
