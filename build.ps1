<#
    一键构建 Party Games。

    插件工程用 Dalamud.NET.Sdk，需要一套 Dalamud 程序集才能编译。
    查找顺序：
      1. -DalamudLib 参数
      2. %APPDATA%\XIVLauncher\addon\Hooks\dev   （装了 XIVLauncher 就有）
      3. .\_dl\dalamud-distrib\extracted          （本机开发时手动下的）
      4. .\.dalamud-libs                          （自定义）

    用法：
      .\build.ps1
      .\build.ps1 -Configuration Debug
      .\build.ps1 -DalamudLib "D:\dalamud\dev"
#>
param(
    [string]$Configuration = "Release",
    [string]$DalamudLib = ""
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

function Find-Dotnet {
    $candidates = @(
        "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe",
        "$env:ProgramFiles\dotnet\dotnet.exe",
        "${env:ProgramFiles(x86)}\dotnet\dotnet.exe"
    )
    foreach ($path in $candidates) {
        if (Test-Path $path) { return $path }
    }

    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    throw "找不到 dotnet。请先装 .NET 10 SDK：https://dotnet.microsoft.com/download/dotnet/10.0"
}

function Resolve-DalamudLib {
    param([string]$Explicit)

    if ($Explicit) {
        if (-not (Test-Path (Join-Path $Explicit "Dalamud.dll"))) {
            throw "指定的目录里没有 Dalamud.dll：$Explicit"
        }
        return $Explicit
    }

    $candidates = @(
        "$env:APPDATA\XIVLauncher\addon\Hooks\dev",
        (Join-Path $root "_dl\dalamud-distrib\extracted"),
        (Join-Path $root ".dalamud-libs")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path (Join-Path $candidate "Dalamud.dll")) { return $candidate }
    }

    throw @"
找不到 Dalamud 程序集，插件编译不了。三选一：
  1) 用 XIVLauncher 启动一次游戏，Dalamud 会装到 %APPDATA%\XIVLauncher\addon\Hooks\dev
  2) 下载 https://goatcorp.github.io/dalamud-distrib/latest.zip 解压，然后 .\build.ps1 -DalamudLib <解压目录>
  3) 把解压出来的 dll 放到 $root\.dalamud-libs
"@
}

$dotnet = Find-Dotnet
$lib = Resolve-DalamudLib -Explicit $DalamudLib

# MSBuild 拼路径时要带结尾分隔符，否则 "extractedDalamud.dll"。
$libMsBuild = $lib.TrimEnd('\', '/') + '/'

Write-Host "dotnet      : $dotnet" -ForegroundColor DarkGray
Write-Host "Dalamud 库  : $libMsBuild" -ForegroundColor DarkGray
Write-Host "配置        : $Configuration" -ForegroundColor DarkGray
Write-Host ""

# .NET 10 默认生成 .slnx，老版本是 .sln，两种都认。
$solution = Get-ChildItem -Path $root -Filter "PartyGames.sln*" -File | Select-Object -First 1
if (-not $solution) { throw "找不到解决方案文件（PartyGames.sln 或 PartyGames.slnx）" }

& $dotnet build $solution `
    -c $Configuration `
    -p:DalamudLibPath="$libMsBuild"

if ($LASTEXITCODE -ne 0) {
    throw "构建失败（exit $LASTEXITCODE）"
}

$pluginOut = Join-Path $root "src\PartyGames.Plugin\bin\$Configuration"
Write-Host ""
Write-Host "构建成功。" -ForegroundColor Green
Write-Host "  服务端      : src\PartyGames.Server\bin\$Configuration\net10.0\PartyGames.Server.dll"
Write-Host "  插件（开发）: $pluginOut\PartyGames.dll  +  PartyGames.Protocol.dll  +  PartyGames.json"
Write-Host "  插件（打包）: $pluginOut\PartyGames\latest.zip"
