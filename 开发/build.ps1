# 重新编译「VoltPick.exe」
# 不需要安装 SDK：使用 Windows 自带的 .NET Framework 4.8 编译器 csc.exe。
# 源码在 开发/ 里，成品 exe 输出到上一层目录，保证交付目录只有 exe 和 开发/。
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$output = Join-Path (Split-Path -Parent $here) 'VoltPick.exe'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw '找不到 .NET Framework 编译器 csc.exe'
}

$fw = Split-Path -Parent $csc
$lib = Join-Path $here 'lib\winmd'
$winmd = @(
    (Join-Path $lib 'Windows.WinMD'),
    (Join-Path $lib 'Windows.Foundation.UniversalApiContract.winmd'),
    (Join-Path $lib 'Windows.Foundation.FoundationContract.winmd')
)
foreach ($item in $winmd) {
    if (-not (Test-Path $item)) {
        throw "缺少 WinRT 引用文件：$item"
    }
}

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
    "/r:$fw\System.Runtime.WindowsRuntime.dll"
) + ($winmd | ForEach-Object { "/r:$_" })

# /warn:4 + /warnaserror+：AGENTS.md 要求零警告，这里把警告直接升级成错误，避免"看着通过"。
& $csc /nologo /target:winexe /codepage:65001 /optimize+ /warn:4 /warnaserror+ `
    "/win32icon:$here\assets\app.ico" `
    "/out:$output" `
    $references `
    $sources

if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }

# 托盘图标要求进程至少是 Medium 完整性；从某些受限目录复制/编译出的 exe 可能带 Low 标签。
# icacls 是原生命令：失败只写 stderr、不抛异常，$ErrorActionPreference 也管不到它，只能看退出码。
# 设置完整性级别需要 WRITE_OWNER，普通用户对刚生成的 exe 通常只有 Modify，因此这一步失败不算构建失败。
$previousPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$null = & icacls "$output" /setintegritylevel Medium 2>&1
$icaclsExit = $LASTEXITCODE
$ErrorActionPreference = $previousPreference
# 清掉 icacls 的退出码：$LASTEXITCODE 会漏回调用方，test.ps1 会把它误当成"构建失败"。
$global:LASTEXITCODE = 0
if ($icaclsExit -ne 0) {
    Write-Warning (("未把 exe 完整性级别设为 Medium（icacls 退出码 {0}）；" -f $icaclsExit) +
        '若程序提示以受限权限运行，用管理员身份重跑 build.ps1 即可。')
}
Write-Host "已生成：$output"
