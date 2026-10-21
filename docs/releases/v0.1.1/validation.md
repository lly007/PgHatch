# PGTray v0.1.1 验证与交付记录

日期：2026-10-17。延续原 .NET 10 / WinForms 工程，没有更换框架或添加第三方 NuGet 依赖。当前工作目录没有 Git 元数据，因此没有伪造 commit；修改前源码保存在 `artifacts/source-v0.1.0-before-v0.1.1`。

## Review 核对及实际修复

| 核对项 | 修改前证据 | 修复与对应验证 |
| --- | --- | --- |
| 导入配置保护 | InstanceManager.StartAsync 确实传入 `-o "-p PORT -h 127.0.0.1"`，重启也调用它。 | 去掉所有实例的命令行监听覆盖，新建集群依靠初始化配置提供默认值。真实导入设置 127.0.0.2，启动/重启后地址和配置字节保持一致；传入旧登记端口也不覆盖真实配置。 |
| 嵌套初始化 | 原逻辑只检查目标是否为空，并且 manager 会先解析链接，可能抹去原始链接路径。 | 在解析前检查原路径及全部祖先，拒绝集群标识、链接、junction、不明确路径。真实测试普通嵌套、大小写别名、专用 junction，均拒绝且没有创建嵌套目标。 |
| 查询竞争 | 状态查询和生命周期操作共用零等待 SemaphoreSlim，普通轮询可能挡住手动操作。 | 查询不再占生命周期锁；操作取消旧查询、更新 epoch，结果发布前核对 epoch 与登记状态。真实轮询与启动竞争通过，删除后过期结果不恢复状态，同实例并发拒绝，不同实例并行通过。退出等待在途操作并取消只读任务。 |
| 状态语义 | pg_isready 失败统一显示 Starting，自定义地址只试 loopback。 | pg_ctl 进程状态独立；根据 PID 文件及 Windows TCP 所有者确定端点，探测前后核对身份。真实自定义地址、同端口不同地址、备用恢复 Starting + NotReady 均通过。 |
| 中文 PID 路径 | PID 文件按默认 UTF-8 读取，遇系统编码路径会导致身份比较失败。 | 使用 UTF-8 / 系统 ANSI 解码，中文空格路径的真实启停、地址及身份验证通过。 |
| 损坏配置 | 读取失败后常规操作不可用，缺少恢复路径。 | 保留恢复和打开目录入口，阻止正常写入。显式恢复前归档原文，保留有效备份，不用损坏原文覆盖 `.bak`；核心和 GUI 都有实际验证。 |

## 主要文件

- `src/PGTray.Core/InstanceManager.cs`：生命周期/查询协调、退出等待、配置保护、原始配置启动。
- `src/PGTray.Core/PostgresTools.cs`：只读配置读取、进程与连接状态、恢复状态、初始化保护。
- `src/PGTray.Core/PathSafety.cs`（新增）：路径规范、祖先集群标识和链接保护。
- `src/PGTray.Core/LocalEndpoints.cs`（新增）：Windows TCP 监听端点的 PID 所有者查询。
- `src/PGTray.Core/InstanceDiscovery.cs`（新增）：有限范围只读扫描、兼容性/已注册/备份提示。
- `src/PGTray.Core/ConfigStore.cs`、`Models.cs`、`ProcessRunner.cs`：配置恢复和兼容元数据、连接状态、可取消命令。
- `src/PGTray/MainForm.cs`、`Dialogs.cs`：左右布局、内嵌引导、状态和操作入口、统一退出、异步回调安全。
- `src/PGTray/DiscoveryForm.cs`、`InstanceSettingsForm.cs`（新增）：勾选导入和原始配置入口。
- `src/PGTray/PGTray.csproj`：0.1.1；`tests/PGTray.IntegrationTests/Program.cs`：真实回归扩展。
- README、CHANGELOG 和本轮验证资料已更新。schema 1 保持兼容。

## 编译与真实集成测试

环境：本机 Windows x64，.NET SDK 10.0.402，现有 PostgreSQL ZIP 程序 18.6，程序目录 `D:\PostgreSQL\PostgreSQL\bin`。

```powershell
dotnet build PGTray.sln -c Release
dotnet run --project tests/PGTray.IntegrationTests -c Release -- 'D:\PostgreSQL\PostgreSQL' 'D:\PGTray-Test'
```

Release 编译：0 警告、0 错误。最终完整回归：**退出码 0，60 项 PASS**。原始输出见 [integration-results.txt](integration-results.txt)。最终自动化测试根目录为 `D:\PGTray-Test\run-20261017-194931-8e1288cf`，带 `.pgtray-test-owned` 标记。

覆盖上一轮真实 SQL/SCRAM/多实例/端口/版本/权限测试，并新增：原监听与配置字节保留、登记端口不覆盖、嵌套/junction/大小写保护、轮询优先级和删除竞争、真实 pg_ctl 零等待边界及状态复查、同端口不同地址的两台真实服务器、实际 warm standby 不接受连接、扫描只读/重复扫描/明确导入/备份目录/多个程序选择、损坏配置恢复和有效备份保留。

以下证据有明确边界：

- Connection Unknown 的失败路径使用真实运行的 postmaster，同时注入“端点查询不可用”；它不是实际网络隔离或权限隔离实验。其余数据库启停、SQL、地址、恢复测试使用真实 PostgreSQL。
- PostgreSQL 17 不兼容检查使用独立构造的版本元数据，不是完整真实 17/18 交叉运行。
- 零等待测试使用真实 `pg_ctl -w -t 0`；另有命令执行超时用测试程序延迟子进程验证。未模拟任意长崩溃恢复或操作系统异常挂起。

## 真实数据保护

真实集群 `D:\PostgreSQL\PGData\main` 全程没有启停、初始化、配置改写或删除；没有被导入 GUI 测试配置。只读扫描正确发现主版本 18、原端口 5432 和监听 127.0.0.1；没有自动登记或启动。

原 PG_VERSION、postgresql.conf、postgresql.auto.conf、pg_hba.conf，以及部署目录原 pgtray.json 的前后 SHA256 均一致，见 [real-data-final-check.json](real-data-final-check.json)。开始和结束的 `pg_ctl status` 都为 no server running。

所有写入与生命周期测试均在本轮新建并确认归属的 `D:\PGTray-Test\run-*` 或 `v011-gui-20261017` 中执行。自动化及 GUI 测试的数据库最终均正常停止；数据和日志保留以便复核，没有删除旧开发轮次或真实数据库资源。

## 实际 Windows GUI 验证

使用 computer-use 技能控制原生窗口，最终发布 EXE 的测试副本位于 `D:\PGTray-Test\v011-gui-20261017`。

- 空实例列表有内嵌引导，没有启动弹窗；保留新建/导入/扫描入口。
- 扫描真实部署只展示 main；默认不勾选、不导入，原文件哈希不变。
- 扫描专用目录展示两个 18 集群及不匹配的 17 测试目录；17 行不可选择，junction 被跳过。
- 勾选中文路径专用集群，导入成功后保持 Stopped；没有自动启动。
- 左右布局实际检查并修复按钮被挤出可视区的问题。最终正常尺寸下启停、六项信息、日志/目录/设置/移除入口完整可见，原配置地址读取为 127.0.0.2。较小窗口详情区允许滚动。
- 点击启动，显示 Running 与 Ready，监听地址仍为 127.0.0.2。正常 Ctrl+Q 退出后，pg_ctl 确认同一测试服务器 PID 仍运行；重开 EXE 正确识别 Running / Ready。随后 GUI fast stop 成功，集群配置哈希保持一致。
- 关闭窗口后不再有可见窗口，PGTray 进程仍响应、MainWindowHandle=0；重复运行 EXE 恢复相同窗口。
- 损坏专用测试 JSON 后重开，显示恢复提示，配置目录和退出可用；损坏文件没有被自动覆盖。明确选择从备份恢复后登记实例恢复，原文以 preserved 文件保留且哈希一致，有效 `.bak` 仍可解析。
- 最终管理器测试副本已正常退出，没有留下测试数据库进程。

## 尚需人工验证的步骤

以下尚未执行，不能标记为通过。均应仅使用专用测试集群：

1. 分别在 **100%、125%、150%、200%** 缩放启动主窗口、程序管理、新建、导入、扫描、日志、实例设置、恢复提示；检查文字、按钮、下拉框、列表行、滚动条不重叠或截断。将窗口缩到最小尺寸，再最大化。切换实例、长中文名称和长路径，确认可选择复制路径与使用目录入口。
2. 在不同 DPI 的显示器之间往返拖动上述窗口，检查字体清晰、列表行高、按钮命中区域与窗口工作区限制。当前机器仅检查了当前显示环境，没有调整系统缩放或虚构四档结果。
3. 托盘双击恢复、右键打开/隐藏、普通退出、停止全部并退出逐条点击。后者只登记测试集群，确认 fast stop；再模拟一个停止失败，确认管理器保留并报告实际状态。
4. 真实长恢复或长事务停止期间拖动/调整窗口、切换实例，对另一测试实例操作；确认持续响应，并在当前操作完成后退出。已有异步和竞争回归不能替代所有真实长耗时 GUI 场景。
5. Windows 10/11 x64 无预装 .NET 的干净环境运行最终 EXE；自包含单文件已成功发布并在开发机运行，但开发机安装了 Runtime。
6. 额外安装真实 PostgreSQL 17 ZIP，验证 17/18 多程序注册、实际不兼容拒绝；补充真实 IPv6、IPv6-only 和端点权限不可读的环境检查。

## 发布

`artifacts/PGTray-v0.1.1-win-x64/PGTray.exe` 为自包含单文件；ZIP 附使用说明、CHANGELOG、SHA256 和验证记录。没有 PostgreSQL 二进制、测试数据、开发机实例登记或密码。

当前限制：仅浅色模式；不执行 AutoStart；不支持配置 data_directory 与 PGDATA 分离布局；新建集群拒绝链接和网络共享。没有加入 SQL 编辑、备份恢复、升级、下载或远程管理功能。
