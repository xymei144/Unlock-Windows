param(
    [string]$Configuration = "Release"
)

# 无 Visual Studio 编译脚本（.NET Framework 4.8 旧式 csproj）
#
# 用法:
#   powershell -ExecutionPolicy Bypass -File scripts\build-wpf.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\build-wpf.ps1 -Configuration Debug
#
# 前置条件:
#   1) 已安装 .NET Framework 4.8 Developer Pack（或让脚本自动用 nuget 还原参考程序集）
#   2) 有 nuget.exe（脚本会尝试从 PATH 找；没有则自动下载到仓库根目录）

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$nuget = "nuget.exe"
if (-not (Get-Command $nuget -ErrorAction SilentlyContinue)) {
    $local = Join-Path $root "nuget.exe"
    if (-not (Test-Path $local)) {
        Write-Host "nuget.exe 未找到，正在下载..." -ForegroundColor Cyan
        Invoke-WebRequest -Uri "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe" -OutFile $local
    }
    $nuget = $local
}

# 还原 packages.config 里的包（32feet.NET 等）
& $nuget restore "UnlockServer.sln"
if ($LASTEXITCODE -ne 0) { throw "nuget restore 失败" }

# 无 VS 环境需要另外两件工具（有 VS 可跳过）:
#   - Microsoft.Net.Compilers.Toolset          新版 C# 编译器
#   - Microsoft.NETFramework.ReferenceAssemblies  .NET 4.8 参考程序集
$compilersPkg = "Microsoft.Net.Compilers.Toolset.4.12.0"
$refAsmPkg    = "Microsoft.NETFramework.ReferenceAssemblies.net48.1.0.3"
if (-not (Test-Path "packages\$compilersPkg")) {
    & $nuget install $compilersPkg -OutputDirectory packages -ExcludeVersion -PreRelease
}
if (-not (Test-Path "packages\$refAsmPkg")) {
    & $nuget install $refAsmPkg -OutputDirectory packages -ExcludeVersion -PreRelease
}

$msbuild = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
if (-not (Test-Path $msbuild)) { throw "未找到 Framework MSBuild: $msbuild" }

$csc = Join-Path $root "packages\$compilersPkg\tasks\net472"
$fao = Join-Path $root "packages\$refAsmPkg\build\.NETFramework\v4.8"

Write-Host "开始编译 UnlockServer.Wpf ($Configuration)..." -ForegroundColor Cyan
& $msbuild "UnlockServer.Wpf\UnlockServer.Wpf.csproj" -t:Rebuild -tv:4.0 `
    -p:Configuration=$Configuration -v:minimal -nologo `
    "-p:CscToolPath=$csc" `
    "-p:FrameworkPathOverride=$fao" `
    -p:LangVersion=latest

if ($LASTEXITCODE -ne 0) { throw "编译失败，exit=$LASTEXITCODE" }

$out = Join-Path $root "UnlockServer.Wpf\bin\$Configuration"
Write-Host ""
Write-Host "编译完成: $out\UnlockServer.exe" -ForegroundColor Green
Write-Host "注意: 若要启用「本机解锁」，还需要单独编译 UnlockServer.Provider (C++/CLI, 需 VS)。"
