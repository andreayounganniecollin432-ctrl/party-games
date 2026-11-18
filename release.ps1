param([Parameter(Mandatory = $true)][string]$Version)

$ErrorActionPreference = 'Continue'
$root = "C:\Coral\dalamud-party-games"
$bin = Join-Path $root "src\PartyGames.Plugin\bin\Release"
$pre = Join-Path $root "dist\prebuilt"
$git = "C:\Coral\tools\PortableGit\bin\git.exe"

Write-Host "[1/6] copy build output"
Copy-Item "$bin\PartyGames.dll" "$pre\PartyGames.dll" -Force
Copy-Item "$bin\PartyGames.Protocol.dll" "$pre\PartyGames.Protocol.dll" -Force
Copy-Item "$bin\PartyGames.deps.json" "$pre\PartyGames.deps.json" -Force
if (Test-Path "$bin\audio") {
    if (Test-Path "$pre\audio") { Remove-Item "$pre\audio" -Recurse -Force }
    Copy-Item "$bin\audio" "$pre\audio" -Recurse -Force
}
if (Test-Path "$bin\assets") {
    if (Test-Path "$pre\assets") { Remove-Item "$pre\assets" -Recurse -Force }
    Copy-Item "$bin\assets" "$pre\assets" -Recurse -Force
}

Write-Host "[2/6] bump version to $Version"
$pattern = '"AssemblyVersion": "[^"]+"'
$replacement = '"AssemblyVersion": "' + $Version + '"'

$pj = Join-Path $root "src\PartyGames.Plugin\PartyGames.json"
$j = [System.IO.File]::ReadAllText($pj, [System.Text.Encoding]::UTF8)
$j = [regex]::Replace($j, $pattern, $replacement)
[System.IO.File]::WriteAllText($pj, $j, (New-Object System.Text.UTF8Encoding($false)))
Copy-Item $pj "$pre\PartyGames.json" -Force

$pm = Join-Path $root "docs\pluginmaster.json"
$m = [System.IO.File]::ReadAllText($pm, [System.Text.Encoding]::UTF8)
$m = [regex]::Replace($m, $pattern, $replacement)
$m = [regex]::Replace($m, '"TestingAssemblyVersion": "[^"]+"', '"TestingAssemblyVersion": "' + $Version + '"')
[System.IO.File]::WriteAllText($pm, $m, (New-Object System.Text.UTF8Encoding($false)))

$cs = Join-Path $root "src\PartyGames.Plugin\PartyGames.Plugin.csproj"
$c = [System.IO.File]::ReadAllText($cs, [System.Text.Encoding]::UTF8)
$c = [regex]::Replace($c, '<Version>[^<]+</Version>', '<Version>' + $Version + '</Version>')
[System.IO.File]::WriteAllText($cs, $c, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "[3/6] zip"
$zip = Join-Path $pre "PartyGames.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
$items = @(
    (Join-Path $pre "PartyGames.dll"),
    (Join-Path $pre "PartyGames.Protocol.dll"),
    (Join-Path $pre "PartyGames.deps.json"),
    (Join-Path $pre "PartyGames.json")
)
if (Test-Path "$pre\audio") { $items += (Join-Path $pre "audio") }
if (Test-Path "$pre\assets") { $items += (Join-Path $pre "assets") }
Compress-Archive -Path $items -DestinationPath $zip -Force
Copy-Item $zip (Join-Path $root "docs\PartyGames.zip") -Force
Write-Host ("      zip = " + [math]::Round((Get-Item $zip).Length / 1MB, 2) + " MB")

Write-Host "[4/6] commit"
Push-Location $root
& $git add -A 2>&1 | Out-Null
& $git commit -m ("release: v" + $Version) 2>&1 | Select-Object -Last 1 | ForEach-Object { Write-Host ("      " + $_) }

Write-Host "[5/6] push"
$pushed = $false
for ($i = 1; $i -le 15; $i++) {
    & $git push origin main 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host ("      ok on attempt " + $i)
        $pushed = $true
        break
    }
    Start-Sleep -Seconds 8
}
if (-not $pushed) { Write-Host "      push failed, do it manually" }
Pop-Location

Write-Host ("[6/6] done: v" + $Version)
