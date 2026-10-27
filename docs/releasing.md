# PgHatch 发布与验证

发行工程只发布 `src/PGTray/PGTray.csproj`，不会发布解决方案或混入测试程序。应用版本统一维护在根目录 `Directory.Build.props`，由 MSBuild 求值读取，当前源码候选为 1.0.0-rc.1。

## 一次生成四种候选包

在 Windows 上安装 PowerShell 7.2+ 和 .NET SDK 10.x，从仓库执行：

```powershell
pwsh -NoProfile -File scripts/Publish.ps1
```

可选参数：

```powershell
pwsh -NoProfile -File scripts/Publish.ps1 -Architecture x64 -Mode framework
pwsh -NoProfile -File scripts/Publish.ps1 -Architecture arm64
pwsh -NoProfile -File scripts/Publish.ps1 -OutputRoot 'D:\候选发布 [测试]&'
```

默认矩阵为 `win-x64`、`win-arm64` × `standalone`、`framework`。两种模式均为 Release、`PublishSingleFile=true`；分别显式传 `SelfContained=true/false`。两者均传 `PublishTrimmed=false`、`PublishAot=false`、`PublishReadyToRun=false`；只有 standalone 设置 `IncludeNativeLibrariesForSelfExtract=true`。

脚本会验证当前 SDK 和 TFM、创建独立临时构建目录，并用 `--artifacts-path` 隔离每个项目和矩阵成员的 obj/bin。应用无需新增第三方依赖；静态检查器只使用 .NET 标准库。

四种包都保留 EXE 旁的外部 `postgresql-catalog.xml`，由唯一母版 `catalog/postgresql-catalog.xml` 复制并比对 SHA-256；项目直接构建/发布同样复制该文件，不将其打入单文件 EXE。该 XML 进入包内校验清单及 ZIP 回验。目录机制和维护要求见 [PostgreSQL 目录说明](postgresql-catalog.md)。历史 Release 不因更新目录而自动替换。

发布显式设置 `IncludeSourceRevisionInInformationalVersion=false`，避免 SDK 把提交哈希追加到 EXE ProductVersion。检查器分别核对完整语义版本（含候选后缀）和程序集数字版本；界面与 HTTP User-Agent 使用相同的构建版本。

## 输出与成功标准

每次执行都会新建 `artifacts/candidates/run-<UTC 时间>-<随机 ID>/`，不会重用旧目录或覆盖历史包：

```text
run-.../
  COMPLETE.json
  PgHatch-win-x64-standalone.zip
  PgHatch-win-x64-framework.zip
  PgHatch-win-arm64-standalone.zip
  PgHatch-win-arm64-framework.zip
  RELEASES.json
  SIZES-AND-HASHES.csv
  SHA256SUMS.txt
  history-before.json / history-after.json / history-final.json
  logs/
  packages/PgHatch-win-<arch>-<mode>/
```

子集模式只列所选包；`COMPLETE.json.SelectedBuilds` 标明数量。必须四包齐全才是完整矩阵成功。每次 publish / 检查器都检查退出码；任何成员失败时保留 `INCOMPLETE.txt` 与 `FAILED.json`，不输出成功标记。失败后修复原因并重新执行，创建新的会话，不覆盖失败证据。

临时目录带归属标记，成功后仅清理本次目录，清理前核对绝对路径位于 TEMP 下且标记一致。失败时临时目录路径记录在 FAILED.json，保留用于诊断。不要自行递归删除未经确认的目录。

所有历史 `PgHatch-v*` / `PGTray-v*` 文件在前后核对 SHA-256；输出根目录不能位于历史发行目录内部。没有 `-Force` 覆盖归档操作。

## ZIP 内容

ZIP 根目录可直接解压运行，保留干净 GUI publish 的全部文件，不按“只要 EXE”猜测删除辅助文件。另包含：

- README.md：架构、模式、版本、功能与运行时要求。
- RELEASE.json：构建属性与 EXE SHA-256，明确不将静态检查冒充运行测试。
- VALIDATION.json：PE、bundle、运行时配置与图标资源静态检查。
- SHA256SUMS.txt：包内文件哈希。外层 SHA256SUMS.txt 同时列四 ZIP 和四 EXE；CSV/JSON 记录实际字节大小。
- LICENSE-LUCIDE.txt：完整 Lucide ISC / Feather MIT 版权许可。
- LICENSE：项目自身的 MIT 许可证，按维护者确认选择。
- THIRD-PARTY-NOTICES.md、CHANGELOG.md；standalone 另含实际 Microsoft 运行库的 `licenses/` 许可和版权声明。

不包含 PostgreSQL 二进制包、真实 PGDATA、用户配置、日志或测试程序。单文件不意味着可以删除发行所需的许可说明。

## 架构和运行时

Standalone 自带 .NET 运行环境；framework 需要对应 **x64 或 ARM64 的 .NET 10 Desktop Runtime**（Microsoft.WindowsDesktop.App），仅安装普通 Microsoft.NETCore.App 不够。Windows ARM64 上也可以有 x64 运行环境，但它不替代原生 ARM64 发行包要求的 ARM64 Desktop Runtime。

本轮 SDK 的单文件 standalone 宿主内置原生运行库，bundle 没有 NativeBinary 待提取条目；x64 实测未产生提取缓存。脚本保留 IncludeNativeLibrariesForSelfExtract=true，并记录真实条目清单；将来的 SDK 如果引入需要提取的条目，以检查结果和实际宿主行为为准，不能假定“必然解压”或“永不解压”。

PgHatch 通过外部进程调用 PostgreSQL，不将数据库代码加载到自身进程。不强制 PgHatch 与 PostgreSQL 架构一致；Windows ARM64 运行 x64 PostgreSQL 仍需另行实测。

静态检查器支持在 x64 主机读取 ARM64 文件，检查原生 Machine、bundle 配置、原生依赖 Machine（如果存在）、应用 IL/R2R 属性、两套品牌 ICO、所有功能图标、许可、原生九帧图标和 manifest。它不执行待检查 EXE，也不加载 ARM64 DLL。未知 bundle 格式会报错，不绕过校验。

## 回归和 GUI 验收

```powershell
dotnet build PGTray.sln -c Release
dotnet run --project tests/PGTray.IconChecks -c Release -- D:\PGTray-Test\icons
dotnet run --project tests/PGTray.IntegrationTests -c Release -- --config-tests D:\PostgreSQL\PostgreSQL artifacts/dev/config-tests
dotnet run --project tests/PGTray.IntegrationTests -c Release -- D:\PostgreSQL\PostgreSQL D:\PGTray-Test
```

集成测试只在自身新建并标记的测试目录初始化/启停，结束确认全部停止；不得将真实 PGDATA 当作测试根目录。

使用 `scripts/Test-ReleaseGui.ps1` 对实际 ZIP 做校验解压，启动时仅为子进程设置 bundle 提取目录和宿主跟踪，记录 PID、运行库模块路径及退出码，不修改全局环境：

```powershell
pwsh -File scripts/Test-ReleaseGui.ps1 -Action Prepare -CandidateDirectory '<run 目录>' -Mode standalone -TestDirectory 'D:\PGTray-Test\全新目录'
pwsh -File scripts/Test-ReleaseGui.ps1 -Action Start -TestDirectory 'D:\PGTray-Test\全新目录'
```

Start 在独立终端等待程序结束；从实际解压目录检查主窗口、功能/品牌图标、程序注册、配置读取、状态刷新、关闭到托盘、双击托盘、重复启动恢复与 Ctrl+Q 退出。framework 另准备新目录，指定 `-Mode framework`，在对应 Desktop Runtime 上运行。不要在源码 bin 目录运行来代替发行包验收。

ARM64 两种包都必须在真实 ARM64 Windows 上补测。x64 交叉构建成功、静态检查通过、真实 ARM64 运行通过是三项不同结论。Windows 图标缓存可能保留旧外观，可使用本轮全新解压路径检查，避免误判。

历史四配置验证见 [v0.2.0 发行矩阵](releases/v0.2.0/release-matrix.md)，当前候选实际结果和人工待验项目见 [V1.0 验证](releases/v1.0.0/validation.md)。COMPLETE.json 仅证明所选构建及静态/压缩包检查成功，不代表正式发布获准或全部实机验收完成。

配置回归支持可选的第三个输出目录参数，便于将全部测试写入保留在仓库内。
