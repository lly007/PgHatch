# PgHatch v1.1.0 正式发行验收

2026-10-28。维护者已完成人工 GUI 验收、实际点击 About GitHub 链接确认默认浏览器打开项目主页，并批准正式发布。About 补丁 `8f8475cc956fa8e2705214dec7e155d6252fb597` 已通过独立 Code Review。

正式发行已于 2026-10-28T22:01:57+08:00 创建：[v1.1.0](https://github.com/lly007/PgHatch/releases/tag/v1.1.0)，Release ID `426739602`，非草稿、非预发布，已确认是 GitHub 最新版。下方新增正式核验与准确构建输入；历史候选仍按当时事实保留。

本轮只调整发行文档，不修改业务源码；此前 969 PASS / 2 SKIP 完整回归与 About 84 项、完整 UI/资源 527 项检查继续作为各自准确源码范围的证据。保留 ARM64 未实机验证、符号链接权限不足及 PostgreSQL 18.3 中文 share 路径问题，不将测试替身视为真实浏览器验证。

## 最终正式构建、运行与服务器核验

- 发行合并：[PR #2](https://github.com/lly007/PgHatch/pull/2)，普通 Merge Commit / 唯一发行构建输入 **`0f412a1680b3340ad1262880ad3c1feca67d9c16`**；两个父提交分别为 `aed6945b56d462cfe01a60bd8ccc1432d171b7b8` 和文档收尾提交 `3fe3aa66ddf9485655cce36e548377b38fc2505b`。版本准备、About 与审查历史完整保留。
- 开始构建时 main 和工作区干净。与独立审查通过的 `8f8475cc956fa8e2705214dec7e155d6252fb597` 比较，src、tests、scripts、Catalog 及版本属性无差异；本轮新增的封版与发布后提交都只改文档。既有业务回归与 About 检查按原验证范围保留，没有把旧测试描述为本轮重新执行。
- 使用未修改的 `pwsh -NoProfile -File scripts/Publish.ps1` 全新构建，目录 `artifacts/candidates/run-20261028-134924-38e99df6`；COMPLETE.json 为 COMPLETE、Version=1.1.0、SelectedBuilds=4。四包每包 277 项静态检查通过，发布日志无编译警告或错误。正式上传目录 `artifacts/releases/v1.1.0` 首次新建，只包含四 ZIP 和 SHA256SUMS.txt，没有覆盖旧候选或正式包。
- 独立读取四 ZIP，核对 CRC、包内每一文件的 SHA-256、EXE 摘要、版本/架构、许可证与唯一 Catalog 母版。无测试程序、PGDATA、用户配置、凭据、日志或 PDB。Catalog 与母版逐字节相同，SHA-256 为 `63CEFC8DCB3A6D2C9C8EDFBFF3056E9F8054D9BDC9806A64E7F104D49F1A67C8`。
- 直接从四个实际 EXE 的 bundle 读取程序集元数据与方法体，分别核验 AboutForm、MainForm 到 About 的构造绑定、原生 LinkLabel、项目 URL、UseShellExecute 与 Process.Start；全部通过。ARM64 仅静态读取，未执行 ARM64 代码。
- x64 standalone/framework 均使用实际解压目录启动，通过所属 PID/HWND 的窄范围窗口消息打开实际 About：主窗版本 1.1.0、空实例、About 版本/完整许可文本和 GitHub URL 均通过，关闭 About 后主窗恢复，最终正常退出码均为 0。Framework 加载 Microsoft.WindowsDesktop.App 10.0.14；Standalone 的子进程运行时根指向隔离无运行时目录，未加载全局 .NET shared 模块。没有 Computer Use、视觉自动化或用户数据库操作。
- 首次 Framework 探针在模态关闭返回后立即读取 owner.Enabled 发生时序误报；随后只读确认主窗已恢复，并正常退出该测试程序。探针改为最多五秒等待原条件满足，产品源码和条件未放宽；两模式在全新目录重新执行通过，原始日志保留。
- 标签 `v1.1.0` 为新建 annotated tag；本地和 GitHub tag object 均确认指向 **`0f412a1680b3340ad1262880ad3c1feca67d9c16`**。标签和发行输入不随后续文档记录移动，没有修改已有标签或重写历史。
- 发布先上传到草稿，五个资产的名称、字节数与 GitHub 服务端 `sha256:` digest 全部核对一致后，才以 Release ID 转为正式并设置 make_latest。再次核对正式 Release 为非草稿、非预发布，`releases/latest` 指向 v1.1.0。草稿的按 tag 查询曾返回 404，按其 ID 获取成功；没有因此重传或覆盖资产。

| 正式资产 | 字节数 | GitHub 服务端 SHA-256（与本地一致） |
| --- | ---: | --- |
| PgHatch-win-x64-standalone.zip | 50116375 | `87513345E597BC07B13CCD984CB093AB06AB25F0874CA5C5B9B52EAA888C2A9D` |
| PgHatch-win-x64-framework.zip | 2818126 | `95A7056386A4BBD6927876F2CD3D886BBC6CE251E6EB99EBAC4910927B3E4051` |
| PgHatch-win-arm64-standalone.zip | 47970662 | `EED7172A94226D30FECC3B4AE633F725D40EC46CE696EEF2B8BBFC672338D431` |
| PgHatch-win-arm64-framework.zip | 2806183 | `6D058080530B83D8D6758639D0E856218D86230404AD00479E94E0DCDBA64189` |
| SHA256SUMS.txt | 394 | `5C074EE6136166339339D29CE648189E495CBE500A97FA0CFC40D95D8C454776` |

实际启动证据：

- standalone：`E:\Project\PGTray开发\artifacts\dev\v1.1.0-final-20261028\smoke-standalone-52aeb79e`；exit-result.json 为退出码 0，smoke-summary.json 保存实际 About 的版本、许可和 URL 文本。
- framework：`E:\Project\PGTray开发\artifacts\dev\v1.1.0-final-20261028\smoke-framework-d98324c7`；exit-result.json 为退出码 0，smoke-summary.json 保存实际 About 的版本、许可和 URL 文本。

- 本轮开始和结束，114 个历史 ZIP/EXE 的既有 SHA-256 均不变；GitHub 原 v1.0.1/v1.0.0/v0.2.0 的 Release ID、标题、正文、发布时间、草稿/预发布状态及旧资产 ID、名称、字节数、digest 等内容元数据均未变化。下载计数不作为不可变字段。未删除旧 Release、覆盖资产或强推。
- **补充下载验证未完成**：正式发布后，本机通过 GitHub CLI 下载公开 SHA256SUMS.txt 的 CDN 连接返回 EOF，未取得有效内容；没有将此描述为实际下载或端到端下载通过，也没有重传、更改已核验资产或重试。五个服务端计算的 SHA-256、字节数及名称已全部通过，不存在已观测到的摘要不匹配。维护者可另行检查公网下载连通性。
- 正式发布和服务端资产校验已确认；上述下载连通性是仍未完成的补充验证。ARM64 实机、两次符号链接特权 SKIP、18.3 中文 share 路径及原有兼容性限制继续保留。维护者对 GUI 与真实浏览器点击的确认来自本任务明确答复，未将程序化替身或窗口文本当作真实浏览器打开证据。
- 本页与 release.md 的发布后文档记录为独立提交，不改变 tag 指向的构建输入、任何二进制或已发布正文。原始构建/发布/API 元数据、五资产清单和探针日志位于本地忽略目录 `artifacts/dev/v1.1.0-final-20261028/`；补充下载失败只记录主机 EOF，不提交签名下载 URL、凭据或实验输出。

## 以下为正式封版前的历史候选与 About 检查记录

下列“尚未发布”“待重建”和旧 main 状态均描述当时事实。最终正式资产以本页后续正式发行核验及 GitHub v1.1.0 为准。


## About 链接收尾（当前增量）

- 将 MainForm 的 About MessageBox 替换为复用 LayoutForm 的原生模态 AboutForm；完整复用三语言原有版本、产品标语、社区声明和 Lucide/Feather 许可文本。GitHub URL 使用整行 LinkLabel，支持键盘访问；通过 UseShellExecute 调用系统默认浏览器，失败时显示三语言错误及 URL，窗口继续可用。
- 本增量不修改数据库生命周期、配置、安全判断、版本号或依赖。测试通过注入启动调用检查 URL/UseShellExecute 参数及 Win32Exception/InvalidOperationException，不实际打开浏览器；DPI 为原生控件/字体缩放模拟，不冒充真实跨屏验收。未使用 Computer Use。
- `dotnet build PGTray.sln -c Release --nologo`：0 警告、0 错误。
- `PGTray.IconChecks.exe --about-checks`：84 PASS、0 FAIL、0 SKIP；三语言 × 四档缩放、完整说明、URL 可见性、启动参数、打开失败、小工作区与模态 Enter/Escape 关闭。
- 完整 `PGTray.IconChecks.exe <新隔离输出目录>`：527 PASS、0 FAIL、0 SKIP，包含上述 84 项。日志位于本地忽略目录 `artifacts/dev/v1.1.0-about-20261028/`。定向测试发现并修正过链接被标签列挤窄的问题；最终所有相关测试重新执行通过。数据库逻辑未变，本轮未重跑下面的完整数据库回归。
- **下方 d26d591 输入的四个旧 ZIP 不包含本次 About 修改，不能作为含此修改的正式发行包。正式发布前需从最新分支重新生成四配置候选并完成相应验收。** 旧候选和历史发行包均保留，不覆盖。

## Git 与构建输入

- 合并 PR：[PR #1](https://github.com/lly007/PgHatch/pull/1)。合并前工作区/暂存区干净，可靠性分支相对 main 为领先 3、落后 0，无冲突。
- 普通 Merge Commit：`aed6945b56d462cfe01a60bd8ccc1432d171b7b8`。父提交为原 main `f1a23df55d9369299c8b2615748d13f33ae03b4e` 与审查通过的 `dd2214f399485035e58757ea9e05ac5a328597f7`，合并树与后者相同。
- 三个原始可靠性提交 `d6b5b8a56646358c9c335badf181ae7cd4489abd`、`0248de4615e42d03f8c15fdb5643293ed2d773c3`、`dd2214f399485035e58757ea9e05ac5a328597f7` 全部为 main 祖先；没有 squash、rebase 或强推。
- 发行准备分支：`release/v1.1.0`。版本准备/实际构建输入提交：`d26d591845608a956f1df9f2fa6be329e2112aa5`；开始验收、打包时工作区干净。候选生成后以 `3b9b673449fbd899ce6f60134e027eec72e2ac9e` 记录验收，随后追加上方 About 补丁；旧候选仍对应 d26d591，不追溯更新其验证结论。
- 在上述 d26d591 构建输入中，相对合并后的 main，仅修改 `Directory.Build.props` 和 README、CHANGELOG、用户指南、发行/本地化文档、文档索引及本文。`src/`、`tests/`、`scripts/`、Catalog 均无差异；配置 schema 2、稳定程序登记 ID、数据库主版本边界和默认初始化参数不变。main 保留版本 1.0.1，1.1.0 元数据尚未合回 main。

## 本轮实际回归

`dotnet build PGTray.sln -c Release --nologo`：**0 警告、0 错误**。全部入口退出码为 0，最终结果 **969 PASS / 0 FAIL / 2 SKIP**。计数包括受控夹具、故障注入和资源断言，不代表 969 项真实数据库测试。

下表省略 `.exe` 后缀和共同参数；完整执行参数、每项证据类型及结果保存在本地验收脚本与 summary.json。只使用本轮新建的隔离目录及其子集群，未操作用户既有 PGDATA。

| 实际测试入口 | PASS | FAIL | SKIP | 性质 |
| --- | ---: | ---: | ---: | --- |
| `IntegrationTests --config-tests` | 75 | 0 | 0 | 配置夹具、真实工具检查 |
| `IntegrationTests --catalog-tests` | 44 | 0 | 0 | Catalog 确定性夹具 |
| `IntegrationTests --catalog-online` | 1 | 0 | 0 | 真实 HTTPS XML、注入离线回退 |
| `IntegrationTests --download-tests` | 78 | 0 | 0 | 实际 loopback HTTP、受控 ZIP/工具夹具 |
| `IntegrationTests --maintenance-tests` | 15 | 0 | 0 | 受控维护版本及工具夹具 |
| `IntegrationTests --deploy-real` | 2 | 0 | 0 | 完整官方 18.6 ZIP 校验/CRC/真实工具注册，无 initdb |
| `IntegrationTests --reliability-real` | 64 | 0 | 1 | 真实 18.3 → 18.6、SQL、应急恢复，另含失败注入 |
| `IntegrationTests --cross-major-real` | 6 | 0 | 0 | 真实 17.11/18.6 拒绝及数据保留 |
| `IntegrationTests <18.6> <test-parent/lifecycle>` | 75 | 0 | 0 | 真实 18.6 多实例、SQL、生命周期和隔离故障 |
| `IntegrationTests --round3-tests` | 82 | 0 | 0 | 真实 18.6、确定性配置/映射/属性/管道/清理故障 |
| `IntegrationTests --round3-controller` | 15 | 0 | 0 | 真实 PG、隔离控制器、读取与退出观察故障注入 |
| `IntegrationTests --round2-tests` | 43 | 0 | 1 | 真实 SMB 身份及辅助进程，另含故障注入 |
| `IntegrationTests --review-timeout` | 22 | 0 | 0 | 真实 pg_ctl、被动超时控制器、退出协调 |
| `IconChecks <test-parent/resources>` | 443 | 0 | 0 | 三语言资源、PE/图标、原生控件/窗口消息检查；非视觉实机验收 |
| `IconChecks --creation-check` | 4 | 0 | 0 | 真实隔离 initdb 与消息循环心跳、辅助进程清理 |

- `IntegrationTests` 为 `tests/PGTray.IntegrationTests/bin/Release/net10.0/PGTray.IntegrationTests.exe`；`IconChecks` 为 `tests/PGTray.IconChecks/bin/Release/net10.0-windows/PGTray.IconChecks.exe`。
- 本轮隔离父目录：`E:/PgHatch-v1.1.0-20261028-e2aa444e`，各组生成唯一的新子目录；完整日志位于 `artifacts/dev/v1.1.0-acceptance-20261028/`，不纳入 Git。
- 真实 18.3 → 18.6 测试创建原表与数据，实际替换同路径工具、显式修复及重新关联后执行真实 SQL；验证 PGDATA、名称、端口和登记 ID 保留，多关联实例运行时拒绝切换，备用工具不按版本最大值选择。真实 17.11/18.6 测试拒绝跨主版本关联和同路径跨主版本覆盖，恢复兼容程序后原表仍可查询。
- 18.6 完整 ZIP：`D:\PostgreSQL\postgresql-18.6-5-windows-x64-binaries.zip`，SHA-256 `E2246BA91D22345BC3D017586C09EDE52D9DF180B1EEB480F050445F1CAD84E2`，与唯一 Catalog 母版一致。实际安全部署、CRC、程序检查及登记通过，没有在部署组创建或启动数据库。
- 18.3 和 17.11 沿用首轮官方来源的 `artifacts/dev/reliability-20261027/official-18.3/pgsql`、`official-17.11/pgsql`；18.6 来自 `D:\PostgreSQL\PostgreSQL`。真实维护/跨主版本组复制到新隔离程序目录，源文件只读。旧包完整供应商 ZIP SHA-256 未验证的限制沿用原审计，不扩大来源认证结论。
- 真实 initdb 消息循环：同步前缀 3 ms、初始化及登记 4351 ms、139 次心跳，最大间隔 35 ms；该测试未启动服务器。资源组包含既有三语言、布局及会话结束的程序化断言，不能视为系统 DPI、真实关机或人工视觉验收。
- 为避免重复，没有另跑 `--round2-controller`（本轮 round3-controller 包含其原有场景）、`--main-layout-check`（资源入口已包含）及相同版本的独立 `--maintenance-real`（已执行真实 18.3 → 18.6 与完整 ZIP 部署）。中文路径调查矩阵不作为必须重复的正常回归。

## 四配置候选

命令：`pwsh -NoProfile -File scripts/Publish.ps1`，脚本未修改。SDK **10.0.403**，Release、单文件，禁用 trimming/AOT/ReadyToRun；构建及发布日志无编译警告/错误。

候选目录：`E:\Project\PGTray开发\artifacts\candidates\run-20261028-124225-a59fd8aa`（目录名时间为 UTC）。`COMPLETE.json` 为 COMPLETE，Version=1.1.0、SelectedBuilds=4。四包每包 **277 项静态检查通过**，与上方回归数量分开记录；ARM64 仅交叉构建/静态检查。

| 候选 ZIP 完整路径 | 字节数 | SHA-256 |
| --- | ---: | --- |
| `E:\Project\PGTray开发\artifacts\candidates\run-20261028-124225-a59fd8aa\PgHatch-win-x64-standalone.zip` | 50114646 | `AC1F444891E825A5E7E2529751118BFA41662EE2A3633D72AC00F657DDD5BB27` |
| `E:\Project\PGTray开发\artifacts\candidates\run-20261028-124225-a59fd8aa\PgHatch-win-x64-framework.zip` | 2817033 | `A847C217257D11F06771686DF9FECEF7FE0FF4F7A29A3D68A0819905AB4A5F60` |
| `E:\Project\PGTray开发\artifacts\candidates\run-20261028-124225-a59fd8aa\PgHatch-win-arm64-standalone.zip` | 47970223 | `9C545A44F49643F0C5F78E383B78F7F9677388F8ED4215F5882B979FF03C780E` |
| `E:\Project\PGTray开发\artifacts\candidates\run-20261028-124225-a59fd8aa\PgHatch-win-arm64-framework.zip` | 2804537 | `C962E9AD8981B5CECEFD899A674AB61A5CCCAD9265E4D27EEBD410C1735ACD12` |

| EXE 所属配置 | EXE SHA-256 |
| --- | --- |
| win-x64-standalone | `26A81F3335847085C72FC7499D35544925696C90F9D0EFBD87CFBF23F7E3EE2D` |
| win-x64-framework | `D35950830F70ACF5FF87FDE1E6FC52DF269CF1DBAFD32044F72BDDA4A09E8B90` |
| win-arm64-standalone | `EC7BB5FE647879BEE666FCDBB78CF45BDA1DEFF117D67C50976E666D289C6150` |
| win-arm64-framework | `01CB043BD85272355994BD38AC65BFFC839934D1E4E5A8E882C6A7F981FE7829` |

EXE 位于同一候选目录的 `packages/PgHatch-win-<arch>-<mode>/PgHatch.exe`。PE 架构、程序集/产品版本、三语言与图标资源、manifest、bundle、对应运行时声明和许可证通过既有 ReleaseChecks。包内 RELEASE.json 仍如实注明发布脚本自身不验证运行；实际启动结果见下节。

除发布脚本的 ZIP 往返检查外，本轮独立读取四 ZIP 的每个文件并核对 CRC、包内 SHA256SUMS、EXE SHA-256、版本/RID/模式及完整内容清单。framework 每包 10 文件，standalone 每包 13 文件；不含测试程序、用户配置、PGDATA、凭据、日志或 PDB。包含项目 MIT、Lucide/Feather 和 standalone 对应 Microsoft 运行库许可。

四个 Catalog XML 与 `catalog/postgresql-catalog.xml` 逐字节相同，SHA-256 为 `63CEFC8DCB3A6D2C9C8EDFBFF3056E9F8054D9BDC9806A64E7F104D49F1A67C8`。完整包内外清单及独立复核在候选目录和本地 `package-audit.json` 中；没有把整个实验目录提交。

## x64 实际发行包启动

使用未修改的 `scripts/Test-ReleaseGui.ps1` Prepare/Start/Inspect，在两个全新解压目录校验全部哈希后运行真实 EXE。窄范围的本地进程/窗口消息探针只检查本次拥有的 PID/HWND、版本文本及正常 Ctrl+Q 退出路径；没有 Computer Use、桌面输入注入、截图或视觉自动化。

| 模式 | 实际结果 |
| --- | --- |
| x64 standalone | PASS：实际窗口与资源加载，窗口文本含 `0 个实例 / 0 个进程运行中 · v1.1.0`，ProductVersion=1.1.0；子进程 DOTNET_ROOT/DOTNET_ROOT_X64 指向不存在的隔离运行时目录，未加载全局 .NET shared 模块；正常退出码 0 |
| x64 framework | PASS：实际窗口同样显示 v1.1.0 和空实例；加载本机 `Microsoft.WindowsDesktop.App/10.0.14` 对应模块，正常退出码 0 |
| ARM64 两模式 | 未实机运行：无真实 Windows ARM64 设备，只有交叉构建和静态结果 |

启动前实际解压目录无 pghatch.json/pgtray.json；应用 store 使用各自 AppContext.BaseDirectory。运行中记录空实例/空进程，没有读取测试目录之外的用户数据库配置。临时运行时根和跟踪设置只用于子进程，不卸载或修改全局 .NET。Standalone 结果证明本次启动未使用已安装 Desktop Runtime，不等于在所有无 Runtime Windows 10/11 上验收通过。

本地实际启动证据：
- standalone：`E:\Project\PGTray开发\artifacts\dev\v1.1.0-acceptance-20261028\smoke-standalone-35301fcb`，保存 live-process.json、host-trace.txt、exit-result.json；窗口版本文本及结果汇总于 smoke-summary.json。
- framework：`E:\Project\PGTray开发\artifacts\dev\v1.1.0-acceptance-20261028\smoke-framework-0ea350aa`，保存 live-process.json、host-trace.txt、exit-result.json；窗口版本文本及结果汇总于 smoke-summary.json。

## 历史保护、限制和人工待验

- 相对前轮已记录的 **114 个既有 ZIP/EXE**，本轮开始和结束 SHA-256 均未变化；发布脚本自己的历史文件保护亦通过。源 18.6 ZIP 只读。所有新候选使用全新 run 目录，没有覆盖或删除历史候选/正式包。结束时没有残留本轮 PostgreSQL、测试辅助进程或 PgHatch 进程。
- **SKIP 2**：两个测试入口各尝试一次实际符号链接，均因 Windows 特权不足跳过；没有提权或修改系统权限。真实 junction、SMB 映射，以及属性注入的 reparse/访问错误检查通过，不能将注入结果宣称为实际符号链接或关键文件 ACL 测试通过。
- **中文路径已知限制**：上一轮在 ACP936 下固定 PostgreSQL 18.3，已确认中文 share 路径触发 initdb post-bootstrap UTF-8 错误，直接调用与 PgHatch 调用均复现；中文 PGDATA 本身及 PgHatch 独有参数/解码原因已排除，具体供应商二进制内部转换点仍未完全证明。本轮没有重复 14 组调查，没有擅改 locale/编码或加入规避逻辑；详见 [最后定向复核](../../final-reliability-review.md)。使用者遇到该组合应保留失败目录与日志，并使用明确核验的 ASCII 程序/share 路径处理；不能宣称已修复该问题。
- 网络数据库运行、DFS/服务器别名的全面物理等价、跨进程重启前映射历史、任意永不返回的驱动 I/O、ARM64 与第三方扩展不增加支持承诺。有限输出等待的极端分支通过确定性注入验证；Windows 真实无限 I/O 未主动制造。
- 维护者人工清单：主窗口/图标可读性；关闭到托盘、双击恢复、重复启动唤醒及普通退出；在隔离副本上确认旧配置迁移；在新隔离集群确认程序登记、恢复与状态提示的交互；按需要补测真实 DPI/跨屏、Windows 关机及 ARM64。不要用用户既有 PGDATA 做新建或清理测试。

**发行判断：本轮没有自动化或候选制品完整性失败，没有发现新的代码阻断项。正式发布仍等待维护者检查四包和人工 GUI 冒烟，并明确接受或处理上述已知限制。** 本任务只准备候选；没有创建 v1.1.0 标签、创建/发布 GitHub Release 或上传四 ZIP 为正式资产。发行准备分支没有合回 main。
