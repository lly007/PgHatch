# PostgreSQL 软件包目录

目录窗口只展示 ZIP 元数据；程序管理中的“下载 PostgreSQL”入口可下载、校验、安全解压并注册所选程序。二者均不创建或启动数据库。PgHatch 的 ARM64 版本同样展示 Windows **x64** PostgreSQL 包；这不表示存在已确认的 Windows ARM64 PostgreSQL 原生包。

## 正式母版与来源

唯一母版是 [catalog/postgresql-catalog.xml](../catalog/postgresql-catalog.xml)，在线地址为：

<https://raw.githubusercontent.com/lly007/PgHatch/main/catalog/postgresql-catalog.xml>

2026-10-25 核对了 [EDB 二进制页面](https://www.enterprisedb.com/download-postgresql-binaries)及官方仓库公开回复。四个条目均为 revision **5**，仅收录稳定版 Windows x64 **binaries.zip**：

| PostgreSQL | EDB 页面文件 ID | 供应商 SHA-256 确认 |
| --- | --- | --- |
| 18.6 | 1260609 | [EDB #746](https://github.com/EnterpriseDB/edb-installers/issues/746#issuecomment-6283764485) |
| 17.11 | 1260616 | [EDB #731](https://github.com/EnterpriseDB/edb-installers/issues/731#issuecomment-6014410619) |
| 16.15 | 1260623 | [EDB #731](https://github.com/EnterpriseDB/edb-installers/issues/731#issuecomment-6014410619) |
| 15.19 | 1260630 | [EDB #749](https://github.com/EnterpriseDB/edb-installers/issues/749#issuecomment-6334213046) |

逐项对 EDB 页面公开的 `sbp.enterprisedb.com/getfile.jsp?fileid=...` 链接发送 HEAD，核实其重定向至 XML 中对应的 `get.enterprisedb.com` ZIP 地址，最终返回 200 / application/zip。没有下载 ZIP，也没有把安装 EXE 的校验值用于 ZIP。15.19 的校验值已与公开回复逐字符核对；14.24 缺少本轮已核实的供应商 ZIP 校验值，暂不收录。完整 SHA-256 和确认链接保存在母版中。

维护时必须重新核对 EDB 当前稳定版本、页面实际链接、完整文件名和打包修订号、官方校验来源，不能按文件名模板猜测新地址或枚举修订号。本文记录核验时的事实，不是自动爬取页面的规则。

## 使用与回退

在“PostgreSQL 程序”窗口打开“PostgreSQL 软件包目录”。进入时按本地 XML 的 `intervalHours` 检查；“刷新在线目录”可显式立即检查。程序启动及其他管理操作不会因此联网。

- `PostgresCatalogService` 接收发行 XML 路径和现有 `ConfigStore`，缓存位于 `ConfigStore.Root/cache/`。当前便携应用的配置根目录与 EXE 目录相同；缓存独立放入其子目录，不覆盖 EXE 旁的发行 XML，不另建全局配置体系。
- 有效在线 XML 原子保存为 `postgresql-catalog-<来源 URL 的 SHA-256>.xml`。不同本地更新源使用不同缓存，改源后不会误用之前来源的缓存。
- 同名 `.checked` 零字节时间戳记录最近检查，用于跨重启限制自动检查频率；它不是有效目录，也不会替代有效缓存。手动刷新可绕过间隔。
- 在线失败或数据无效时，按“有效缓存 → 发行 XML”回退，并在窗口显示来源与原因。缓存损坏不会导致发行 XML 被清空。有效在线数据但缓存写入失败时，可在本次会话展示在线数据并提示缓存错误。
- 本地发行 XML 是来源信任锚。在线 XML 中的 Update 不得改变更新地址或检查间隔；返回对象也保留本地设置。本地 XML 缺失或无效时明确报错，不能从缓存中自行选择更新源。
- XML 上限 1 MiB、HTTPS 请求总时限 15 秒、最多 5 次重定向；禁止 HTTP 降级、凭据 URL、DTD 和外部实体。严格检查 schema 1、字段、稳定版本、修订号、x64 / EDB、精确匹配的 ZIP 地址、64 位十六进制 SHA-256 以及重复条目。
- 软件包地址仅接受 `get.enterprisedb.com` 默认 HTTPS 端口上的匹配 ZIP 路径。未来下载器必须在每次请求及重定向前调用 `PostgresPackage.ValidateDownloadLocation`，并再次核对最终地址，禁止系统自动跟随到其他域名。目录更新 URL 则由用户控制的本地 XML 指定，可以使用其他 HTTPS 目录服务。

SHA-256 用于将未来下载的 ZIP 与目录记录比对，**不构成 XML 的独立可信签名**。在线 XML 的维护权限由 GitHub 仓库控制；校验来源链接便于人工审计。

## 可复用接口与验证

`PostgresCatalog.Parse(byte[])` 严格解析；`PostgresCatalogService.GetAsync(forceRefresh, allowOnline, cancellationToken)` 返回目录、来源和可选告警。`Catalog.Packages` 提供 Version、Revision、Architecture、Provider、DownloadUrl、SHA256、ChecksumSource；列表只读。原有实例和 JSON 配置格式不变。

## 下载与绿色部署

1. 打开“PostgreSQL 程序”→“下载 PostgreSQL”，选择目录提供的版本和本地部署目录。目标须不存在或为空，父目录须已存在；不允许选入 PGDATA、已注册程序树或符号链接 / junction 路径。
2. `PackageDownloader.DownloadAsync` 使用 HttpClient 的 ResponseHeadersRead 和异步文件流，显示大小、百分比、速度和尝试次数；进度约每 200 ms 更新。连接时限 15 秒，响应头及每次无数据读取时限 30 秒，总传输最多 2 小时，网络异常最多尝试三次。手动重试仍可继续已有的安全部分下载。
3. `ConfigStore.Root/cache/downloads/<URL与SHA-256的散列>/` 保存专用 `.part` 与 `download.json` 标识，独占锁防止同一包同时写入。只有 URL、预期 SHA-256、实际长度和强 ETag / Last-Modified 均适用才发出 Range / If-Range；严格核对 206 的 Content-Range、总长度和响应标识。200 安全重写，416 从头重试；没有可用标识时从头下载。取消或网络失败保留部分下载，校验不匹配的包不会部署。
4. 完整 ZIP 必须通过 SHA-256。`PackageDeployment.DeployAsync` 在同盘的独立私有暂存目录解压，验证路径、链接标记、大小、每个文件的实际长度及 CRC、四个必要工具的 x64 PE 结构，并复用 `PostgresTools.InspectBinaryAsync` 执行 `postgres --version`，要求版本与所选目录完全一致。EDB 的 `pgsql/bin` 嵌套布局会完整保留，不假定 bin 在 ZIP 根目录。
5. ZIP 上限 2 GiB、单文件上限 2 GiB、累计解压上限 8 GiB、条目上限 100,000，并拒绝异常高压缩比。提前检查磁盘空间，解压只使用新建文件。拒绝绝对路径、父目录跳转、Windows 设备名、ADS、大小写重复路径、链接、重解析点和加密 ZIP。
6. 验证成功才原子移动至最终目录；已有空目录先移到临时备份，提交失败时恢复。提交前可取消并清理本次暂存文件；提交后等待既有 `InstanceManager.AddBinaryAsync` 完成，不以取消中断配置保存。窗口关闭会等待当前操作结束，不留下失控任务。
7. 注册失败时保留已提交程序，明确显示目录及手动注册入口；绝不为回滚而删除已提交程序。成功后程序列表刷新，显示“PostgreSQL 已准备就绪”，用户可回到主窗口自行新建实例。临时资源清理失败单独报告路径和警告，不掩盖注册成功或原始失败原因。整个流程不调用 initdb / pg_ctl start，不注册 Service、不修改 PATH。

已完成的 ZIP 缓存可供重试复用，故不会在成功后自动删除。需要释放空间时，可在没有传输任务运行时清理配置根目录下的下载缓存；不要删除实例或程序目录。ARM64 Windows 上执行 x64 PostgreSQL 仍依赖系统 x64 兼容能力，必须单独实机验证。

```powershell
# 本地 HTTP 服务和小型 ZIP，验证传输、续传、取消、损坏/恶意 ZIP、真实注册接口与失败恢复：
dotnet run --project tests/PGTray.IntegrationTests -c Release -- --download-tests artifacts/dev/download-tests
# 若本机已有这个精确版本的官方 ZIP，仅从本地读取，不重新下载，也不创建/启动实例：
dotnet run --project tests/PGTray.IntegrationTests -c Release -- --deploy-real 'D:\PostgreSQL\postgresql-18.6-5-windows-x64-binaries.zip' catalog/postgresql-catalog.xml artifacts/dev/real-deployment
```

小型传输测试仅在测试专用 HttpMessageHandler 中映射到 loopback HTTP 服务，生产 HTTPS 和 EDB 地址限制不因此放宽。测试程序模拟 `--version` 的小型工具不能代替真实 PostgreSQL 验证；上述第二条用官方 ZIP 验证真实工具并清理本次创建的隔离部署。

```powershell
dotnet build PGTray.sln -c Release
dotnet run --project tests/PGTray.IntegrationTests -c Release --no-build -- --catalog-tests catalog/postgresql-catalog.xml artifacts/dev/catalog-tests
# XML 提交到 GitHub 后，实际读取 Raw 并与母版逐字节比较，再验证离线缓存回退：
dotnet run --project tests/PGTray.IntegrationTests -c Release --no-build -- --catalog-online catalog/postgresql-catalog.xml artifacts/dev/catalog-online
```

专项测试使用隔离目录和可控 HTTP 传输验证正常读取、离线回退、无效数据、来源固定、缓存原子替换失败、中断、超时、大小限制和检查间隔。第二条在线测试实际通过 HTTPS 读取 GitHub Raw；不以 Mock 结果替代在线发布验证。GUI 的三语言标题和资源检查复用现有图标/本地化测试。真实桌面的缩放、键盘导航、关闭窗口取消请求仍需人工验收。

四种发布模式都会在 EXE 旁复制同一母版，并将其纳入发行校验清单及 ZIP 解压回验。该文件故意保持外部形式，必须随发行包保留。XML 母版已在线发布；后续源码功能使用独立本地候选包验证，不自动重打包或替换现有 v0.2.0 Release。
