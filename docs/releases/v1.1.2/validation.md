# PgHatch v1.1.2 发行验收

2026-11-15。维护者已批准最终修复 HEAD `8286b3095dfd216fdeecbacd3e22ccd2f411c27f`，确认真实 Windows GUI 验收通过，并授权修复合并、版本准备、四包构建、正式发布与 Issue #7 收尾。

## 已验收修复输入

- 基线 main 为 `d3a223e3b993d3a582f6b6f1702fcf2c26b302d8`。
- 修复经 PR #8 使用普通 Merge Commit `432dcd3f129a74769b4461df31a86892669614a0` 合入。两个父提交为基线 main 和已验收 HEAD，合并树与已验收树完全一致。[修复 PR #8](https://github.com/lly007/PgHatch/pull/8)
- 四个原提交依次为 `a0ffc333118674c85fb89a59d8750bb5c5a4fee5`、`9670aa831302f262df08ab838d8792ed12d1905a`、`e5476d5db18b3023a134067a055eb9cc803e0fe9`、`8286b3095dfd216fdeecbacd3e22ccd2f411c27f`。
- 版本准备分支为 `codex/release-v1.1.2`，调整 `Directory.Build.props` 及当前版本、发行说明和文档索引。正式构建输入采用版本 PR 的合并提交。

## 修复阶段证据

| 检查 | 结果 | 范围 |
| --- | --- | --- |
| Release 构建 | 0 警告、0 错误 | .NET SDK 10.0.404，.NET 10 目标 |
| 新增注册 WinForms | 63 PASS | 原生控件、受控工具与 HTTP，覆盖注册、修复、扫描导入、下载部署及返回/继续 |
| 初始化分类 | 89 PASS | 中文阶段正反例、保守分类、密码脱敏和目录保留 |
| 创建及本地化 | 103 PASS | 创建前提醒、独立诊断、三套资源与既有模拟 DPI 检查 |
| 下载部署 | 78 PASS | 环回 HTTP 与部署夹具，覆盖校验、取消、目录保护及登记故障 |
| 真实 PostgreSQL 18.6 集成 | 75 PASS | 创建、导入、SQL、启动停止、刷新及 PGDATA 安全 |
| 真实路径矩阵及明确的故障注入 | 30 PASS | 先前六次真实路径初始化、同源哈希与初始化后故障处理 |
| 维护者最终 Windows GUI | 通过 | 已验收 HEAD，设备与 DPI 细节按维护者记录 |

真实 PostgreSQL 实验、夹具检查和维护者 GUI 验收按各自范围记录，原始依据见 Issue #7 验证文档。正式版本补充构建、包资源核验和最终 x64 启退冒烟，发布后由独立文档提交记录实际 SHA、资产摘要和服务端状态。[Issue #7 验证文档](../../issue-7-validation.md)

## 发行检查方法

从合并后的干净 main 执行 `dotnet build PGTray.sln -c Release --nologo` 和 `pwsh -NoProfile -File scripts/Publish.ps1`，四包使用同一个正式提交和全新隔离构建目录。

检查版本、架构、三语言资源、图标、Catalog、许可证、ZIP CRC、包内文件 SHA-256、发行包内容，以及本版预警和技术诊断组件。最终实际解压的 x64 Standalone 与 Framework 各执行启动和正常退出冒烟。annotated tag `v1.1.2` 标识唯一正式构建输入，发布后记录通过独立提交进入 main。

## 平台及日志边界

ARM64 的实际完成范围为 x64 主机交叉构建和静态检查，真实 ARM64 Windows 运行仍需设备验收。特殊代码页、混合 UTF-8/CP936 本地化文字及其他阶段译文存在既有边界。PGDATA 保护、配置 schema 2、默认初始化参数及扩展兼容性约束按本版发行说明和用户指南执行。
