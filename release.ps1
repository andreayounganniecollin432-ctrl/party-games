<#
    一键发版：改版本号 → 编译 → 打包 → 提交 → 推送。

    以前这个脚本只复制 PartyGames.dll / Protocol / deps.json，
    忘了 Glasswork 那四个 dll（还有 System.Drawing.Common），装上去插件起不来。
    现在直接按「bin\Release 根目录里所有 dll（去掉 pdb）+ assets + audio」打包，
    打完还会验一遍关键文件，缺了就报错，不会再把半个包推上去。

    用法：
      .\release.ps1 -Version 0.6.5.2
      .\release.ps1 -Version 0.6.5.2 -NoPush     # 只打包提交，不推
      .\release.ps1 -Version 0.6.5.2 -SkipBuild # 复用上次编译结果
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$NoPush,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$plugin = Join-Path $root 'src\PartyGames.Plugin'
$bin = Join-Path $plugin 'bin\Release'
$pre = Join-Path $root 'dist\prebuilt'
$docs = Join-Path $root 'docs'
$utf8 = New-Object System.Text.UTF8Encoding($false)

$git = 'C:\Coral\tools\PortableGit\bin\git.exe'
if (-not (Test-Path $git)) { $git = 'git' }

function Write-Text($path, $text) {
    [System.IO.File]::WriteAllText($path, $text, $utf8)
}

function Read-Text($path) {
    [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
}

# ---------------------------------------------------------------- 1. 版本号

Write-Host "[1/6] 版本号 -> $Version"

$csproj = Join-Path $plugin 'PartyGames.Plugin.csproj'
$text = Read-Text $csproj
$text = [regex]::Replace($text, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
Write-Text $csproj $text

# 仓库里那份清单（发版时和包内清单、pluginmaster 三处保持一致）
$manifest = Join-Path $plugin 'PartyGames.json'
if (Test-Path $manifest) {
    $text = Read-Text $manifest
    $text = [regex]::Replace($text, '"AssemblyVersion": "[^"]+"', "`"AssemblyVersion`": `"$Version`"")
    Write-Text $manifest $text
}

$master = Join-Path $docs 'pluginmaster.json'
$text = Read-Text $master
$text = [regex]::Replace($text, '"AssemblyVersion": "[^"]+"', "`"AssemblyVersion`": `"$Version`"")
$text = [regex]::Replace($text, '"TestingAssemblyVersion": "[^"]+"', "`"TestingAssemblyVersion`": `"$Version`"")
Write-Text $master $text

# ---------------------------------------------------------------- 2. 编译

if (-not $SkipBuild) {
    Write-Host "[2/6] 编译插件"

    $dotnet = @(
        "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe",
        "$env:ProgramFiles\dotnet\dotnet.exe",
        "${env:ProgramFiles(x86)}\dotnet\dotnet.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $dotnet) {
        $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($cmd) { $dotnet = $cmd.Source } else { throw '找不到 dotnet（.NET 10 SDK）' }
    }

    $lib = $env:DALAMUD_HOME
    if (-not $lib) { $lib = Join-Path $root '_dl\dalamud-distrib\extracted' }
    if (-not (Test-Path (Join-Path $lib 'Dalamud.dll'))) {
        $lib = "$env:APPDATA\XIVLauncher\addon\Hooks\dev"
    }
    if (-not (Test-Path (Join-Path $lib 'Dalamud.dll'))) { throw "找不到 Dalamud 程序集：$lib" }

    $libMsBuild = $lib.TrimEnd('\', '/') + '/'
    & $dotnet build (Join-Path $plugin 'PartyGames.Plugin.csproj') -c Release "-p:DalamudLibPath=$libMsBuild"
    if ($LASTEXITCODE -ne 0) { throw "编译失败（exit $LASTEXITCODE）" }
} else {
    Write-Host "[2/6] 跳过编译"
}

# ---------------------------------------------------------------- 3. 收集文件

Write-Host "[3/6] 收集发版文件"

if (-not (Test-Path $bin)) { throw "没有编译产物：$bin" }

New-Item -ItemType Directory -Force -Path $pre | Out-Null
Get-ChildItem $pre -Force | Remove-Item -Recurse -Force

$required = @(
    'PartyGames.dll',
    'PartyGames.Protocol.dll',
    'PartyGames.deps.json',
    'PartyGames.json',
    'Glasswork.dll',
    'Glasswork.Backend.dll',
    'Glasswork.Dalamud.dll',
    'Glasswork.Glass.dll',
    'System.Drawing.Common.dll',
    'Microsoft.Win32.SystemEvents.dll'
)

foreach ($name in $required) {
    $src = Join-Path $bin $name
    if (-not (Test-Path $src)) { throw "编译产物里缺 $name，包不能发" }
    Copy-Item $src (Join-Path $pre $name) -Force
}

foreach ($folder in @('assets', 'audio')) {
    $src = Join-Path $bin $folder
    if (-not (Test-Path $src)) { throw "编译产物里缺 $folder 目录，包不能发" }
    Copy-Item $src (Join-Path $pre $folder) -Recurse -Force
}

$packed = Read-Text (Join-Path $pre 'PartyGames.json')
if ($packed -notmatch [regex]::Escape("`"AssemblyVersion`": `"$Version`"")) {
    throw "包内清单的 AssemblyVersion 不是 $Version，编译没吃到新版本号？"
}

# ---------------------------------------------------------------- 4. 压缩

Write-Host "[4/6] 压缩"

$zip = Join-Path $pre 'PartyGames.zip'
$items = Get-ChildItem $pre -Exclude 'PartyGames.zip'
Compress-Archive -Path $items.FullName -DestinationPath $zip -Force
Copy-Item $zip (Join-Path $docs 'PartyGames.zip') -Force

$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 2)
Write-Host ("      zip = $sizeMb MB")

# 验一遍包里真的有依赖（老脚本就是在这里漏了 Glasswork）
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $names = $archive.Entries.FullName | ForEach-Object { $_.Replace('/', '\') }
    foreach ($name in $required) {
        if ($names -notcontains $name) { throw "包里缺 $name" }
    }
} finally {
    $archive.Dispose()
}

# ---------------------------------------------------------------- 5. 提交

Write-Host "[5/6] 提交"
Push-Location $root
try {
    & $git add -A | Out-Null
    & $git commit -m "release: v$Version" | Select-Object -Last 2 | ForEach-Object { Write-Host "      $_" }
} finally {
    Pop-Location
}

# ---------------------------------------------------------------- 6. 推送

if ($NoPush) {
    Write-Host "[6/6] 跳过推送（-NoPush）"
    Write-Host "完成：v$Version（本地已提交）"
    return
}

Write-Host "[6/6] 推送"
Push-Location $root
try {
    $pushed = $false
    for ($i = 1; $i -le 15; $i++) {
        & $git push origin main 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) { Write-Host "      ok（第 $i 次）"; $pushed = $true; break }
        Start-Sleep -Seconds 8
    }
    if (-not $pushed) { Write-Host "      推送失败，手动 git push origin main" }
} finally {
    Pop-Location
}

Write-Host "完成：v$Version"
