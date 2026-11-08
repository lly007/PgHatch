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

正式发布于 2026-11-08T14:23:45+08:00，Release ID 为 `438072446`。[v1.1.1 Release](https://github.com/lly007/PgHatch/releases/tag/v1.1.1) 已确认为 Latest，Draft=false、Prerelease=false。

### Git 与构建输入

- [版本 PR #5](https://github.com/lly007/PgHatch/pull/5) 合并提交及唯一构建输入为 **`0900ef42a2f76580372eed031e5ac293e1a3b583`**。两个父提交分别为修复合并 `e0dedb32ea89c6e7d9871ffd0e5a11afe3c281db` 与版本准备 `1d548c6ff6438a16b870e542744a1b1f3814b422`。
- 四个原始修复提交均为该输入的祖先。相对已审查 HEAD `755e69bcbc7c666693dc16efd3eeb68ca0f7d2ee`，业务源码、测试、脚本及 Catalog 树一致，版本准备差异仅为 Directory.Build.props 与当前版本/发行文档。
- 开始正式构建时 main 与远端一致，工作区和暂存区干净。构建命令为 `dotnet build PGTray.sln -c Release --nologo` 和 `pwsh -NoProfile -File scripts/Publish.ps1`，SDK 10.0.403，Release 构建 0 警告、0 错误。
- annotated tag `v1.1.1` 的 tag object 为 **`b2c79f18b40d3cbdef8e4628e4d2a09e59e62ec9`**，准确指向上述构建提交。发布后文档通过独立提交记录事实，正式 tag 继续标识构建输入。

### 四配置构建与校验

候选目录为 `artifacts/candidates/run-20261108-061459-cd605038`，COMPLETE.json 为 COMPLETE，Version=1.1.1，SelectedBuilds=4。四包由同一输入全新构建，分别使用隔离 obj/bin，发布日志均无编译警告或错误。正式归档首次创建于 `artifacts/releases/v1.1.1/`，只包含四 ZIP 和外部 SHA256SUMS.txt。

- 每包通过现有 277 项静态检查。另直接读取四个实际 EXE 的 bundle、GUI/Core 元数据和内嵌资源，每包补充 25 项结构及资源检查，覆盖 1.1.1.0 程序集、拥有者菜单、逻辑尺寸状态、统一图标绑定、三种多帧窗口 ICO 的准确字节及显式移除模式/受控停止方法。
- 独立读取四 ZIP，核对实际字节数、ZIP CRC、包内每个文件 SHA-256、EXE 哈希、版本/架构、Catalog 母版和完整许可证。包内清单逐项匹配，发行内容检查覆盖 PGDATA、用户配置、凭据文件名、测试文件、日志、PDB 和调试脚本。
- 版本更新后的 About 原生窗口和三语言定向回归为 84 PASS，包含模拟 DPI 与替代 shell/错误检查。已有完整 IconChecks 1253 PASS、业务及安全回归 400 PASS / 1 SKIP 继续按其修复源码范围提供证据。

| 正式资产 | 字节数 | SHA-256，已匹配 GitHub 服务端 digest |
| --- | --- | --- |
| PgHatch-win-x64-standalone.zip | 50158322 | `A6DFCD4B64F6D4B7A67669113680584A5C8AE2BF9AF0891BA229A7AF36C87BDE` |
| PgHatch-win-x64-framework.zip | 2864918 | `60C09C9CE4F9E8321C32C749A27EE20354167A41486849161A01F558281F5980` |
| PgHatch-win-arm64-standalone.zip | 48017908 | `8FE56D222CA348EDAE3E810C964770EA05868C339F084D4026C7AF958562EBE3` |
| PgHatch-win-arm64-framework.zip | 2852361 | `36C747FF4519E2B03D898F73FE9338F178730AC3683732B0B5E732FD6F9F6E75` |
| SHA256SUMS.txt | 394 | `E3B7EBC5422AF9B15B531D1F28A486E57C782C046005E4488FD5F359B1FF826B` |

### 最终实际解压包 x64 冒烟

使用 scripts/Test-ReleaseGui.ps1 对实际 ZIP 校验解压，在全新隔离目录启动两个最终包。窄范围原生窗口探针只向对应 PID/HWND 发送测试窗口消息。

- Standalone 与 Framework 的实际主窗口均显示 1.1.1 和空实例状态，通过实际菜单打开原生模态 About，核对版本、许可文本、GitHub URL 和 owner，关闭后主窗口恢复，最终通过正常 Ctrl+Q 路径退出，退出码均为 0。
- Framework 加载已安装的 Microsoft.WindowsDesktop.App 10.0.14。Standalone 在子进程隔离 DOTNET_ROOT 设置下运行，实际进程全局 .NET 模块列表为空。
- 本轮冒烟只使用新建的空配置测试目录。原生窗口消息结果与维护者真实 GUI 验收分别记录，ARM64 继续采用静态验收范围。
- 实际路径为 `artifacts/dev/v1.1.1-release-20261108/smoke-standalone-b336420a/` 与 `smoke-framework-ef0578ec/`。smoke-summary.json、live-process.json、exit-result.json 和宿主日志保存实际运行证据。

### 服务端发布与下载

- 首先创建 Release 草稿，上传四 ZIP 和 SHA256SUMS.txt，逐项核对五资产的名称、uploaded 状态、实际字节数及服务端 `sha256:` digest，再将 Release 发布并设为 Latest。
- 发布后重新读取 Release、Latest、annotated tag ref/object 和历史 Release 列表，确认发布状态、准确输入及五资产摘要。
- 使用 GitHub CLI 独立下载全部五个正式资产，大小和 SHA-256 全部匹配本地正式文件。另通过公开 browser_download_url 无认证下载 SHA256SUMS.txt，其内容摘要同样匹配。
- 发布前后核对 35 个旧正式归档文件的 SHA-256，历史 tag refs 与基线一致。既有 Release 的标识、正文、目标、发布时间及资产 ID/名称/大小/digest 等内容元数据均通过核对。下载计数按实时服务数据处理。

原始证据位于本地忽略目录 `artifacts/dev/v1.1.1-release-20261108/`，包括 build-input.json、构建/发布日志、package-*.json、package-audit.json、smoke-summary.json、final-assets.json、Release/tag/API 快照、服务端核验结果、下载文件和公开 checksum 下载。正式 tag 对应的原始包保存在独立正式归档目录。

## 保留的验证限制

- ARM64 为 x64 主机上的交叉构建和静态检查，真实 ARM64 Windows 运行需要独立设备验证。
- 修复阶段有一项真实符号链接创建因缺少系统特权而 SKIP。
- PostgreSQL 18.3 的特定中文 share 路径问题保留此前诊断和实际验证范围。
- 维护者的 GUI 通过确认覆盖本轮四项修复，具体 DPI、显示器及跨屏矩阵以原始验收记录为准。
- 核心工具版本一致性与 PG_VERSION 核验不能覆盖所有第三方扩展兼容性。实际断电、强制关机、网络数据目录及服务器别名等限制按现有支持边界处理。
