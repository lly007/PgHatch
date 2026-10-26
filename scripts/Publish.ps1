#Requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('x64','arm64')][string[]]$Architecture = @('x64','arm64'),
    [ValidateSet('standalone','framework')][string[]]$Mode = @('standalone','framework'),
    [string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Publish.ps1 requires Windows and PowerShell 7.2+.' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repoRoot 'src/PGTray/PGTray.csproj'
[xml]$projectXml = Get-Content -Raw -LiteralPath $project
$framework = $projectXml.SelectSingleNode('/Project/PropertyGroup/TargetFramework').InnerText
$version = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($framework -ne 'net10.0-windows') { throw "Unexpected target framework: $framework" }
$sdk = (& dotnet --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $sdk -notmatch '^10\.') { throw '.NET SDK 10.x is required.' }
if (-not $OutputRoot) { $OutputRoot = Join-Path $repoRoot 'artifacts/candidates' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$history = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'artifacts') | Where-Object { $_.Name -match '^(PgHatch|PGTray)-v\d' })
foreach ($item in $history) {
    if ($OutputRoot.Equals($item.FullName, [StringComparison]::OrdinalIgnoreCase) -or $OutputRoot.StartsWith($item.FullName + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'OutputRoot must not be inside a historical release.' }
}
function Get-HistoryHashes {
    @($history | ForEach-Object { if ($_.PSIsContainer) { Get-ChildItem -LiteralPath $_.FullName -File -Recurse } else { $_ } } | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{ Path=$_.FullName; SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}
function Write-Json($Value, [string]$Path) { $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding utf8 }
function Invoke-Dotnet([string[]]$CommandArguments, [string]$Log) {
    Write-Host ('dotnet ' + ($CommandArguments -join ' '))
    # Redirection expands [] as wildcard syntax, even with a quoted path.
    # Out-File -LiteralPath keeps Chinese, spaces and special characters literal.
    & dotnet @CommandArguments 2>&1 | Out-File -LiteralPath $Log -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE). See $Log" }
}
$id = 'run-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$candidate = Join-Path $OutputRoot $id
if (Test-Path -LiteralPath $candidate) { throw 'Candidate directory already exists.' }
$null = [IO.Directory]::CreateDirectory((Join-Path $candidate 'logs'))
$work = Join-Path ([IO.Path]::GetTempPath()) ('PgHatch-publish-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($work)
[IO.File]::WriteAllText((Join-Path $work '.pghatch-publish-owned'), $candidate)
$incomplete = Join-Path $candidate 'INCOMPLETE.txt'
[IO.File]::WriteAllText($incomplete, 'Not a successful release. All selected builds and package validations must finish first.')
$before = Get-HistoryHashes
Write-Json $before (Join-Path $candidate 'history-before.json')
$complete = $false
$summaries = [Collections.Generic.List[object]]::new()
try {
    $checkerOut = Join-Path $work 'checker'
    Invoke-Dotnet @('build',(Join-Path $repoRoot 'tests/PGTray.ReleaseChecks/PGTray.ReleaseChecks.csproj'),'-c','Release','--artifacts-path',(Join-Path $work 'checker-build'),'-o',$checkerOut) (Join-Path $candidate 'logs/checker-build.log')
    $checker = Join-Path $checkerOut 'PGTray.ReleaseChecks.dll'
    $stage = Join-Path $work 'candidate'
    $null = [IO.Directory]::CreateDirectory($stage)
    foreach ($arch in ($Architecture | Select-Object -Unique)) { foreach ($variant in ($Mode | Select-Object -Unique)) {
        $rid = 'win-' + $arch.ToLowerInvariant()
        $variant = $variant.ToLowerInvariant()
        $name = "PgHatch-$rid-$variant"
        $selfContained = $variant -eq 'standalone'
        $publish = Join-Path $stage ('packages/' + $name)
        $build = Join-Path $work ('build/' + $name)
        $command = @('publish',$project,'-c','Release','-r',$rid,'--self-contained',$selfContained.ToString().ToLowerInvariant(),
            '--artifacts-path',$build,'-p:PublishSingleFile=true','-p:PublishTrimmed=false','-p:PublishAot=false','-p:PublishReadyToRun=false',
            ('-p:IncludeNativeLibrariesForSelfExtract=' + $selfContained.ToString().ToLowerInvariant()),'-p:IncludeSourceRevisionInInformationalVersion=false','-p:DebugType=None','-p:DebugSymbols=false','-o',$publish)
        Write-Json $command (Join-Path $candidate "logs/$name-command.json")
        Invoke-Dotnet $command (Join-Path $candidate "logs/$name-publish.log")
        $exe = Join-Path $publish 'PgHatch.exe'
        if (-not (Test-Path -LiteralPath $exe)) { throw "Missing $exe" }
        $catalogSource = Join-Path $repoRoot 'catalog/postgresql-catalog.xml'
        $catalogOutput = Join-Path $publish 'postgresql-catalog.xml'
        # Keep the sole maintained catalog external to the single-file bundle.
        Copy-Item -LiteralPath $catalogSource -Destination $catalogOutput -Force
        if ((Get-FileHash -LiteralPath $catalogSource).Hash -ne (Get-FileHash -LiteralPath $catalogOutput).Hash) { throw 'Published catalog differs from the maintained XML.' }
        # Keep every file produced by the clean GUI publish, not a guessed EXE-only allowlist.
        if (@(Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object { $_.Name -match '^(postgres|pg_ctl|initdb|pg_isready)\.exe$|^(pghatch|pgtray)\.json$|^PG_VERSION$|Tests' }).Count) { throw 'Unexpected database, user configuration or test file in publish output.' }
        $validation = Join-Path $publish 'VALIDATION.json'
        Invoke-Dotnet @($checker,$exe,$rid,$variant,$version,(Join-Path $repoRoot 'src/PGTray/Assets'),$validation) (Join-Path $candidate "logs/$name-static-checks.log")
        $verified = Get-Content -Raw -LiteralPath $validation | ConvertFrom-Json
        $license = Join-Path $publish 'LICENSE-LUCIDE.txt'
        $licenseSource = Join-Path $repoRoot 'src/PGTray/Assets/Ui/Lucide/LICENSE'
        if (-not (Test-Path -LiteralPath $license) -or (Get-FileHash -LiteralPath $license).Hash -ne (Get-FileHash -LiteralPath $licenseSource).Hash) { throw 'Lucide/Feather license missing or changed.' }
        foreach ($doc in @('CHANGELOG.md','THIRD-PARTY-NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $repoRoot $doc) -Destination $publish }
        if ($selfContained) {
            $assetFiles = @(Get-ChildItem -LiteralPath $build -Filter project.assets.json -Recurse -File | Where-Object { $_.Directory.Name -eq 'PGTray' })
            if ($assetFiles.Count -ne 1) { throw 'Could not locate isolated GUI restore assets.' }
            $packageFolders = (Get-Content -Raw -LiteralPath $assetFiles[0].FullName | ConvertFrom-Json).packageFolders.PSObject.Properties.Name
            foreach ($component in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')) {
                $runtimeVersion = @($verified.Frameworks | Where-Object Name -eq $component)[0].Version
                $packageName = "$component.Runtime.$rid".ToLowerInvariant()
                $locations = @($packageFolders | ForEach-Object { Join-Path $_ "$packageName/$runtimeVersion" } | Where-Object { Test-Path -LiteralPath $_ })
                if (-not $locations.Count) { throw "Cannot find runtime license source: $packageName/$runtimeVersion" }
                $notices = @(Get-ChildItem -LiteralPath $locations[0] -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)(\.|$)' })
                if (-not $notices.Count) { throw "No redistribution notices for $component" }
                $destination = Join-Path $publish "licenses/$component"
                $null = [IO.Directory]::CreateDirectory($destination)
                $notices | Copy-Item -Destination $destination
            }
        }
        $runtimeText = if ($selfContained) { "Standalone / 自包含：无需单独安装 .NET Desktop Runtime。.NET 宿主依据实际 bundle 内容决定是否提取文件；本包需提取的原生 bundle 条目数为 $(@($verified.NativeExtractionFiles).Count)。如果需要提取，默认缓存位于 %TEMP%\.net\PgHatch，不写入数据库目录。清单见 VALIDATION.json。" } else { "Framework-dependent / 运行时依赖：需要 $arch 架构的 .NET 10 Desktop Runtime（Microsoft.WindowsDesktop.App）。仅安装普通 .NET Runtime 不足以运行 WinForms。不要把另一架构的运行时当作本版本的依赖。" }
        @"
# PgHatch — PostgreSQL, the App Way.

A portable PostgreSQL instance manager for Windows.

Version: $version · Architecture: $rid · Mode: $variant

$runtimeText

所有版本管理功能相同。解压到当前用户可写目录后运行 PgHatch.exe，无传统安装向导，无需注册 Windows Service、管理员权限或修改全局 PATH。

PgHatch 不内置 PostgreSQL；请在程序中添加自己提供的 PostgreSQL 程序目录。支持创建、导入及管理本地实例。关闭主窗口默认隐藏到系统托盘；双击托盘恢复。退出 PgHatch 不会自动停止数据库，除非明确选择停止操作。

配置 pghatch.json 保存在 EXE 旁，升级时保留原配置和数据目录。本包不含任何预置数据库或用户配置。

Windows x64 使用 x64 发行包；Windows ARM64 优先使用 ARM64 发行包。PgHatch 通过外部进程调用 PostgreSQL，二者无需强制采用相同架构。Windows ARM64 运行 x64 PostgreSQL 的实际兼容性需要单独验证。

ARM64 产物的构建/静态检查不代表真实 ARM64 运行已验证。VALIDATION.json 记录静态结果；RELEASE.json 标明发行属性，不将静态结果冒充 GUI 验证。

请保留随包 README、校验信息及许可文件。LICENSE-LUCIDE.txt 包含 Lucide ISC 和 Feather MIT 版权许可；THIRD-PARTY-NOTICES.md 是资源来源说明。自包含版 licenses/ 中另有 Microsoft 运行库许可。这些文件需要随发行保留，单文件 EXE 不等于可以删除许可。

postgresql-catalog.xml 是外部发行默认目录，请与 EXE 一起保留。进入程序管理中的目录或下载窗口时才按间隔检查在线目录，失败时使用有效缓存或发行 XML。下载窗口支持选择版本、下载、SHA-256 校验、安全解压并注册；目前仅含 Windows x64 PostgreSQL ZIP，不会自动创建或启动实例。ARM64 上运行这些 x64 PostgreSQL 程序依赖系统兼容能力，仍需实机验证。
"@ | Set-Content -LiteralPath (Join-Path $publish 'README.md') -Encoding utf8
        $noticesPath = Join-Path $publish 'THIRD-PARTY-NOTICES.md'
        (Get-Content -Raw -LiteralPath $noticesPath).Replace('](src/PGTray/Assets/Ui/Lucide/LICENSE)','](LICENSE-LUCIDE.txt)') | Set-Content -LiteralPath $noticesPath -Encoding utf8
        Write-Json ([ordered]@{ Product='PgHatch'; Version=$version; Rid=$rid; Mode=$variant; SDK=$sdk; Configuration='Release'; PublishSingleFile=$true; SelfContained=$selfContained; IncludeNativeLibrariesForSelfExtract=$selfContained; PublishTrimmed=$false; PublishAot=$false; PublishReadyToRun=$false; RuntimeExecution='Not verified by publishing'; ExeSHA256=$verified.ExeSHA256 }) (Join-Path $publish 'RELEASE.json')
        $files = @(Get-ChildItem -LiteralPath $publish -Recurse -File | Sort-Object FullName)
        $sums = @($files | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash + '  ' + [IO.Path]::GetRelativePath($publish,$_.FullName).Replace('\','/') })
        $sums | Set-Content -LiteralPath (Join-Path $publish 'SHA256SUMS.txt') -Encoding ascii
        $zip = Join-Path $stage ($name + '.zip')
        [IO.Compression.ZipFile]::CreateFromDirectory($publish,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
        $expanded = Join-Path $work ('roundtrip/' + $name)
        [IO.Compression.ZipFile]::ExtractToDirectory($zip,$expanded)
        $expectedFiles = @(Get-ChildItem -LiteralPath $publish -File -Recurse)
        if (@(Get-ChildItem -LiteralPath $expanded -File -Recurse).Count -ne $expectedFiles.Count) { throw 'ZIP round-trip file count differs.' }
        foreach ($sourceFile in $expectedFiles) {
            $relative = [IO.Path]::GetRelativePath($publish,$sourceFile.FullName)
            $extracted = Join-Path $expanded $relative
            if (-not (Test-Path -LiteralPath $extracted) -or (Get-FileHash -LiteralPath $sourceFile.FullName).Hash -ne (Get-FileHash -LiteralPath $extracted).Hash) { throw "ZIP round-trip mismatch: $relative" }
        }
        $summaries.Add([pscustomobject]@{ Rid=$rid; Mode=$variant; Version=$version; ExePath="packages/$name/PgHatch.exe"; ExeBytes=(Get-Item -LiteralPath $exe).Length; ExeSHA256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash; Zip=($name+'.zip'); ZipBytes=(Get-Item -LiteralPath $zip).Length; ZipSHA256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash; StaticChecks=$verified.Checks.Count; ZipRoundTrip='PASS'; RuntimeExecution='Not verified by publishing' })
    } }
    # Candidate is fresh; never overwrite any previously published files.
    Get-ChildItem -LiteralPath $stage | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $candidate -Recurse }
    Write-Json @($summaries.ToArray()) (Join-Path $candidate 'RELEASES.json')
    $summaries | Select-Object Rid,Mode,Version,ExeBytes,ZipBytes,ExeSHA256,ZipSHA256 | Export-Csv -LiteralPath (Join-Path $candidate 'SIZES-AND-HASHES.csv') -NoTypeInformation -Encoding utf8
    @($summaries | ForEach-Object { $_.ZipSHA256 + '  ' + $_.Zip; $_.ExeSHA256 + '  ' + $_.ExePath }) | Set-Content -LiteralPath (Join-Path $candidate 'SHA256SUMS.txt') -Encoding ascii
    $after = Get-HistoryHashes
    Write-Json $after (Join-Path $candidate 'history-after.json')
    if (@($before).Count -ne @($after).Count -or (Compare-Object ($before | ConvertTo-Json -Compress) ($after | ConvertTo-Json -Compress))) { throw 'Historical release files changed.' }
    Write-Json ([ordered]@{ Status='COMPLETE'; Version=$version; SelectedBuilds=$summaries.Count; RuntimeExecution='See separate GUI verification; ARM64 execution not implied' }) (Join-Path $candidate 'COMPLETE.json')
    Remove-Item -LiteralPath $incomplete
    $complete = $true
    $summaries | Format-Table Rid,Mode,ExeBytes,ZipBytes,StaticChecks
    Write-Host "Candidate release: $candidate"
} catch {
    $successMarker = Join-Path $candidate 'COMPLETE.json'
    if (Test-Path -LiteralPath $successMarker) { Remove-Item -LiteralPath $successMarker }
    Write-Json ([ordered]@{ Status='FAILED'; Error=$_.Exception.Message; WorkDirectory=$work }) (Join-Path $candidate 'FAILED.json')
    throw
} finally {
    $finalHistory = Get-HistoryHashes
    Write-Json $finalHistory (Join-Path $candidate 'history-final.json')
    if (Compare-Object ($before | ConvertTo-Json -Compress) ($finalHistory | ConvertTo-Json -Compress)) {
        $successMarker = Join-Path $candidate 'COMPLETE.json'
        if (Test-Path -LiteralPath $successMarker) { Remove-Item -LiteralPath $successMarker }
        Write-Json ([ordered]@{ Status='FAILED'; Error='Historical release protection check failed'; WorkDirectory=$work }) (Join-Path $candidate 'FAILED.json')
        throw 'Historical release protection check failed.'
    }
    if ($complete) {
        $resolvedWork = [IO.Path]::GetFullPath($work)
        $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolvedWork.StartsWith($tempPrefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.File]::ReadAllText((Join-Path $resolvedWork '.pghatch-publish-owned')) -ne $candidate) { throw 'Refusing unsafe temporary cleanup.' }
        Remove-Item -LiteralPath $resolvedWork -Recurse
    }
}
