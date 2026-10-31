# Build and run the local test harnesses.
# Requires the same .NET Framework 4.8 / WinRT references as build.ps1.
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) { throw 'csc.exe not found' }

$fw = Split-Path -Parent $csc
$lib = Join-Path $here 'lib\winmd'
$sources = Get-ChildItem (Join-Path $here 'src') -Recurse -Filter '*.cs' |
    Sort-Object FullName |
    ForEach-Object { $_.FullName }

$references = @(
    '/r:System.dll',
    '/r:System.Core.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll',
    '/r:System.Management.dll',
    '/r:System.Web.Extensions.dll',
    "/r:$fw\System.Runtime.dll",
    "/r:$fw\System.Runtime.InteropServices.WindowsRuntime.dll",
    "/r:$fw\System.Runtime.WindowsRuntime.dll",
    "/r:$lib\Windows.WinMD",
    "/r:$lib\Windows.Foundation.UniversalApiContract.winmd",
    "/r:$lib\Windows.Foundation.FoundationContract.winmd"
)

function Build-Harness([string]$main, [string]$source, [string]$output) {
    & $csc /nologo /target:exe /codepage:65001 /warn:4 /warnaserror+ "/main:$main" "/out:$output" `
        $references $sources (Join-Path $here $source)
    if ($LASTEXITCODE -ne 0) { throw "compile failed: $source" }
}

function Run-Harness([string]$path) {
    & $path
    if ($LASTEXITCODE -ne 0) { throw "test failed: $path (exit $LASTEXITCODE)" }
}

Write-Host '构建应用程序...'
& (Join-Path $here 'build.ps1')
# build.ps1 抛出的异常会直接终止本脚本；这里再兜一层退出码，避免"编译失败"被当成"只有完整性设置失败"。
if ($LASTEXITCODE -ne 0) { throw "build.ps1 failed (exit $LASTEXITCODE)" }

# AGENTS.md 规定临时文件只能放 开发\tests\：宿主 exe 生成在这里，跑完即删。
$testDir = Join-Path $here 'tests'
if (-not (Test-Path $testDir)) { New-Item -ItemType Directory -Path $testDir | Out-Null }
$testOutputs = @(
    (Join-Path $testDir 'TestHarness.exe'),
    (Join-Path $testDir 'tray-smoke.exe'),
    (Join-Path $testDir 'history-smoke.exe'),
    (Join-Path $testDir 'panel-scroll-smoke.exe')
)
try {
    Build-Harness 'TestHarness' 'tools\TestHarness.cs' $testOutputs[0]
    Run-Harness $testOutputs[0]

    Build-Harness 'TraySmoke' 'tools\TraySmoke.cs' $testOutputs[1]
    Run-Harness $testOutputs[1]

    Build-Harness 'HistorySmoke' 'tools\HistorySmoke.cs' $testOutputs[2]
    Run-Harness $testOutputs[2]

    Build-Harness 'DeviceBattery.PanelScrollSmoke' 'tools\PanelScrollSmoke.cs' $testOutputs[3]
    Run-Harness $testOutputs[3]

    Write-Host 'All tests passed.'
}
finally {
    foreach ($path in $testOutputs) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}
