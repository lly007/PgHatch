#Requires -Version 7.2
# Process/test instrumentation only. Interact with windows manually or with computer-use.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Prepare','Start','Inspect')][string]$Action,
    [Parameter(Mandatory)][string]$TestDirectory,
    [string]$CandidateDirectory,
    [ValidateSet('standalone','framework')][string]$Mode = 'standalone'
)
$ErrorActionPreference = 'Stop'
$TestDirectory = [IO.Path]::GetFullPath($TestDirectory)
$marker = Join-Path $TestDirectory '.pghatch-release-test-owned'
$app = Join-Path $TestDirectory 'app'
$exe = Join-Path $app 'PgHatch.exe'
if ($Action -eq 'Prepare') {
    if (Test-Path -LiteralPath $TestDirectory) { throw 'Use a new isolated test directory.' }
    $null = [IO.Directory]::CreateDirectory($TestDirectory)
    $name = "PgHatch-win-x64-$Mode"
    [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $CandidateDirectory ($name+'.zip')), $app)
    $release = Get-Content -Raw -LiteralPath (Join-Path $app 'RELEASE.json') | ConvertFrom-Json
    if ($release.Rid -ne 'win-x64' -or $release.Mode -ne $Mode -or (Get-FileHash -LiteralPath $exe).Hash -ne $release.ExeSHA256) { throw 'Extracted EXE does not match release metadata.' }
    foreach ($line in Get-Content -LiteralPath (Join-Path $app 'SHA256SUMS.txt')) {
        $name = $line.Substring(66)
        $target = [IO.Path]::GetFullPath((Join-Path $app $name))
        if (-not $target.StartsWith($app + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or (Get-FileHash -LiteralPath $target).Hash -ne $line.Substring(0,64)) { throw 'Extracted package checksum failed.' }
    }
    [IO.File]::WriteAllText($marker,$exe)
    Write-Output "Prepared and checksum-verified: $exe"
    return
}
if (-not (Test-Path -LiteralPath $marker) -or [IO.File]::ReadAllText($marker) -ne $exe) { throw 'Not an owned extracted test package.' }
function Capture-Process($Process) {
    $modules = @($Process.Modules | ForEach-Object FileName)
    [ordered]@{ Id=$Process.Id; Exe=$Process.Path; Title=$Process.MainWindowTitle; NativeModules=$modules;
        GlobalDotnetModules=@($modules | Where-Object {$_ -match '\\dotnet\\shared\\'});
        ExtractedFiles=@(if(Test-Path -LiteralPath (Join-Path $TestDirectory 'extract')) {Get-ChildItem -LiteralPath (Join-Path $TestDirectory 'extract') -File -Recurse | ForEach-Object FullName})
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $TestDirectory 'live-process.json')
}
if ($Action -eq 'Inspect') {
    $processId = [int](Get-Content -LiteralPath (Join-Path $TestDirectory 'pid.txt'))
    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($process) {
        if (-not $process.Path.Equals($exe,[StringComparison]::OrdinalIgnoreCase)) {throw 'PID was reused by another process.'}
        Capture-Process $process
        Write-Output "Running: $processId"
    } else { Write-Output 'Process has exited.' }
    return
}
$release = Get-Content -Raw -LiteralPath (Join-Path $app 'RELEASE.json') | ConvertFrom-Json
$start = [Diagnostics.ProcessStartInfo]::new($exe)
$start.UseShellExecute = $false
$start.WorkingDirectory = $app
$start.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $TestDirectory 'extract'
$start.Environment['DOTNET_HOST_TRACE'] = '1'
$start.Environment['DOTNET_HOST_TRACEFILE'] = Join-Path $TestDirectory 'host-trace.txt'
if ($release.Mode -eq 'standalone') {
    # Child process only; never uninstall a runtime or modify global environment.
    $start.Environment['DOTNET_ROOT'] = Join-Path $TestDirectory 'no-global-runtime'
    $start.Environment['DOTNET_ROOT_X64'] = Join-Path $TestDirectory 'no-global-runtime'
}
$process = [Diagnostics.Process]::Start($start)
$process.Id | Set-Content -LiteralPath (Join-Path $TestDirectory 'pid.txt')
Write-Output "Started extracted $($release.Mode) package, PID $($process.Id). Close with Ctrl+Q after GUI checks."
while (-not $process.WaitForExit(1000)) { try { Capture-Process $process } catch { if (-not $process.HasExited) {throw} } }
[ordered]@{ Exe=$exe; Mode=$release.Mode; ExeSHA256=(Get-FileHash -LiteralPath $exe).Hash; ExitCode=$process.ExitCode;
    ExtractedFiles=@(if(Test-Path -LiteralPath (Join-Path $TestDirectory 'extract')) { Get-ChildItem -LiteralPath (Join-Path $TestDirectory 'extract') -File -Recurse | ForEach-Object FullName })
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $TestDirectory 'exit-result.json')
Write-Output "Exited with code $($process.ExitCode)."
exit $process.ExitCode
