# PgHatch v1.1.1 发行验收

2026-11-08。维护者确认 Issue #3 的四项修复通过真实 Windows GUI 验收，表现正常，并授权合并、创建标签和正式发布。ChatGPT 已独立审查远端代码和验收记录，没有发现明确阻止合并的架构或安全问题。具体 DPI、显示器尺寸和跨屏组合按维护者原始记录确认。

## 修复输入与合并

- 已审查 HEAD 为 `755e69bcbc7c666693dc16efd3eeb68ca0f7d2ee`，基线 main 为 `d7b96e2af65674d1fb84861986c68fb54524b6ab`。
- [修复 PR #4](https://github.com/lly007/PgHatch/pull/4) 使用普通 Merge Commit `e0dedb32ea89c6e7d9871ffd0e5a11afe3c281db`。父提交为上述 main 与已审查 HEAD，合并树与已审查修复树一致。
- 保留四个独立提交 `83948c6102a9c3101d7f54cd53f87565b86b8a2f`、`e8aff27ed82613669c4f3001ec4b8b560d90f4a2`、`3eab3c49b96f8507b220549ce7b402213d0ab862`、`755e69bcbc7c666693dc16efd3eeb68ca0f7d2ee`。详细根因、文件和回归范围见 [Issue #3 修复记录](../../issue-3-validation.md)。
- 版本准备分支为 `release/v1.1.1`。本轮版本准备只调整 Directory.Build.props 和当前版本/发行文档，业务源码、测试及发布脚本以修复合并树为准。

## 已完成的修复阶段验证

| 验证 | 结果 | 证据范围 |
| --- | --- | --- |
| Release 构建 | 0 警告、0 错误 | .NET SDK 10.0.403 |
| 完整 IconChecks | 1253 PASS | 原生窗口、资源、合成 DPI、模态 owner 及句柄生命周期 |
| 业务及安全回归 | 400 PASS、1 SKIP | 临时真实集群、SQL、生命周期、身份和受控故障 |
| 维护者真实 Windows GUI | 通过 | 本轮四项修复，范围按维护者确认 |

上述自动化原始日志位于本地 `artifacts/dev/issue3-final/`。本次版本更新后执行必要的构建、版本、资源、四配置包装及发行一致性验证，正式二进制的结果在后续段落中记录。

## 正式构建与资产

本节当前记录版本准备状态。版本 PR 合并后的明确提交作为唯一正式构建输入，使用 `dotnet build PGTray.sln -c Release --nologo` 和 `pwsh -NoProfile -File scripts/Publish.ps1` 在干净工作区生成新的完整矩阵。

构建输入 SHA、版本 PR、标签对象、四 ZIP 实际字节数和哈希、服务端五资产 digest、x64 实际解压包冒烟与最终 Release 状态在发布完成后由独立文档提交补充，标签指向构建输入。

原始发布证据存放于本地忽略目录 `artifacts/dev/v1.1.1-release-20261108/`，四配置构建使用新的候选目录，正式资产归档至新的 `artifacts/releases/v1.1.1/`。

## 保留的验证限制

- ARM64 为 x64 主机上的交叉构建和静态检查，真实 ARM64 Windows 运行需要独立设备验证。
- 修复阶段有一项真实符号链接创建因缺少系统特权而 SKIP。
- PostgreSQL 18.3 的特定中文 share 路径问题保留此前诊断和实际验证范围。
- 维护者的 GUI 通过确认覆盖本轮四项修复，具体 DPI、显示器及跨屏矩阵以原始验收记录为准。
- 核心工具版本一致性与 PG_VERSION 核验不能覆盖所有第三方扩展兼容性。实际断电、强制关机、网络数据目录及服务器别名等限制按现有支持边界处理。
