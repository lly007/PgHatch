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

## 正式发行结果

正式发布于 2026-11-15 14:18:32（Asia/Shanghai），Release ID 为 `445852242`。发布后经 authenticated API 和无认证公开 Latest API 核对，Latest 均指向 v1.1.2，Draft=false、Prerelease=false。[v1.1.2 Release](https://github.com/lly007/PgHatch/releases/tag/v1.1.2)

### 版本 PR、构建输入与标签

- 版本 PR #9 通过普通 Merge Commit **`daf42aec2481bc829b08ad340a4bb7c848eb3616`** 合入，该提交为唯一正式构建输入。两个父提交为修复合并 `432dcd3f129a74769b4461df31a86892669614a0` 和版本准备 `05a01cfdcfb16b4b72f4bdbf12915fcbe226f7e8`。[版本 PR #9](https://github.com/lly007/PgHatch/pull/9)
- 正式合并树与版本准备树一致。相对已验收 `8286b309`，业务源码、测试、脚本和 Catalog 树一致，差异限于版本元数据与发行文档。原有四个修复提交均为正式输入的祖先。
- annotated tag `v1.1.2` 的 tag object 为 **`8b246be4ddf1ca5bd186982e2e8207e8c1d9813d`**，准确目标为正式构建提交 `daf42aec2481bc829b08ad340a4bb7c848eb3616`。
- Git HTTPS 传输出现连接重置时，通过 GitHub Git API 获取带签名的合并对象并校验原 SHA，上传本地提交和 annotated tag 时逐项核对相同 blob、tree、commit 和 tag SHA。
- 正式构建开始时 main 与远端一致，暂存区及工作区干净。SDK 为 10.0.404，Release 构建 0 警告、0 错误。

### 四包和资源核验

候选目录为 `artifacts/candidates/run-20261115-060324-e4143f83`，COMPLETE.json 为 COMPLETE，Version=1.1.2，SelectedBuilds=4。四包采用该正式输入，通过现有脚本分别全新构建。正式归档为 `artifacts/releases/v1.1.2/`，包含四个 ZIP 和仅列四 ZIP 的外部 SHA256SUMS.txt。

- 每包通过现有 277 项静态检查，覆盖架构、单文件 bundle、1.1.2.0 程序集、运行时声明、图标、三语言资源和许可。
- 每包另通过 31 项组件和资源核验，直接读取实际发行 EXE，确认 BinaryForm、DiscoveryForm、DownloadForm、InstanceForm 中的路径确认字段，独立诊断窗口及核心诊断属性、路径探测方法、部署入口和准确中文阶段短语均已包含。
- 对每种语言的全部 377 个资源键和完整文案与源码比较。比较按逻辑换行规范化，保留有意的空行。初始核验器将 Windows ResX 的 CR/CRLF 表示差异误报为文案差异，已根据原始差异修正比较方式，并正确识别卫星程序集的文化后缀资源名称。修正后的全部文案核验通过，原始诊断保留于本地证据目录。
- 独立检查四 ZIP CRC、全部包内文件 SHA-256、清单覆盖、版本及架构、Catalog 母版、完整许可证和运行库许可。包内容覆盖检查确认只有发行文件和所需许可。
- 正式版本补充 About 检查 84 PASS，创建预警、技术诊断及三语言检查 103 PASS。已有真实 18.6 路径实验与修复阶段回归继续按其明确范围提供依据。

| 正式资产 | 字节数 | SHA-256，匹配 GitHub 服务端 digest |
| --- | --- | --- |
| PgHatch-win-x64-standalone.zip | 50190508 | `168370A2C6E30F506FA40AC381CCD27D9716596ABC26FE4D64932862E3773727` |
| PgHatch-win-x64-framework.zip | 2875699 | `84BEFEDA98CA353335E6CDEAD118DCC2B586595910688FE057517CDAF791668B` |
| PgHatch-win-arm64-standalone.zip | 48019154 | `3020180076AD0F69AA181F681A916EFF4F42166DE29EBBCA0165ADF038A9E695` |
| PgHatch-win-arm64-framework.zip | 2862844 | `A1B0D1C671A3DFE4401E648E013871FF25B6D7A1C73F0FDB1F3D4754EEFC43D8` |
| SHA256SUMS.txt | 394 | `6B048E49C9EEDA46624C2D08DDA58BB440F0CDBFE50820280916CB7FE9D44045` |

### 最终 x64 解压包冒烟

使用 `scripts/Test-ReleaseGui.ps1` 分别对最终 Standalone、Framework ZIP 做校验解压，测试目录分别为本地证据根目录下的 `smoke-standalone/` 和 `smoke-framework/`。通过 computer-use 技能操作实际窗口。

- 两个实际主窗口均显示 v1.1.2 和空实例状态。通过实际“更多”菜单打开原生 About，核对 v1.1.2、许可文本和 GitHub URL，关闭模态窗口后主窗口恢复。
- 两个实际包均经 Ctrl+Q 正常退出，退出码为 0。进程、模块、宿主日志及最终退出结果保留于各测试目录。
- Framework 实际加载 Microsoft.WindowsDesktop.App 10.0.15。Standalone 在子进程隔离 DOTNET_ROOT 设置下运行，全局 .NET 模块列表为空，本轮 bundle 提取文件数为 0。
- 本组使用全新空配置目录。ARM64 采用交叉构建和静态检查结论，维护者 GUI 验收按其原始设备范围记录。

### 服务端与端到端验证

- 先创建 Draft，上传四 ZIP 和 SHA256SUMS.txt，逐项核对五资产的名称、uploaded 状态、字节数及服务端 `sha256:` digest。
- 发布前通过 GitHub CLI 下载全部五个草稿资产，逐项核对字节数和 SHA-256，全部匹配正式归档。
- 校验通过后正式发布并设置 Latest，随后再次读取 Release、Latest、annotated tag ref/object。
- 发布后通过禁用 curl 配置且未传递 GitHub 身份凭据的公开请求，读取 Latest，并下载 SHA256SUMS.txt 与 x64 Framework ZIP。Latest 状态和两个文件的实际字节及 SHA-256 均匹配。
- 发布前后核对 5 个历史 tag、5 个历史 Release 的内容元数据和资产身份/摘要，以及 40 个历史正式归档文件的字节和 SHA-256，全部一致。下载计数作为实时服务数据处理。

原始证据集中于 `artifacts/dev/v1.1.2-release-20261115/`，包括构建输入、合并对象、构建及发布日志、四包组件报告、package-audit.json、实际 x64 冒烟、草稿及正式 API 快照、服务端摘要、下载资产、公开下载与历史发行核验。发布后文档通过独立提交与正常 PR 合入 main，正式标签继续标识唯一构建输入。
