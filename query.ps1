<#
    查 PartyGames 的对局记录。

    给珊瑚（QQ）用：在群里被问"刚才谁赢了"时，跑一下这个脚本，
    把输出整理成人话回答。也给人用。

    用法：
      .\query.ps1                # 最近 5 局
      .\query.ps1 -Limit 1       # 只看最后一局
      .\query.ps1 -Room ABCD1    # 只看某个房间
#>
param(
    [int]$Limit = 5,
    [string]$Room = ""
)

$ErrorActionPreference = "Stop"

$dataDir = if ($env:PARTYGAMES_DATA) {
    $env:PARTYGAMES_DATA
} else {
    Join-Path $env:LOCALAPPDATA "PartyGames"
}

$path = Join-Path $dataDir "matches.json"

if (-not (Test-Path $path)) {
    Write-Output "还没有对局记录：$path 不存在（游戏还没打完一局，或者服务端没跑过）。"
    return
}

$parsed = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json

# PS 5.1 的 ConvertFrom-Json 把整个数组当成一个对象输出，这里显式摊平，
# 让 5.1 和 7 的行为一致。
$all = @()
foreach ($item in $parsed) { $all += $item }

if ($Room) {
    $all = @($all | Where-Object { $_.roomCode -eq $Room })
}

$recent = @($all | Select-Object -Last $Limit)

if ($recent.Count -eq 0) {
    Write-Output "没有符合条件的对局。"
    return
}

foreach ($match in $recent) {
    $ended = [DateTimeOffset]::FromUnixTimeMilliseconds([int64]$match.endedAtMs).ToLocalTime()
    $minutes = [math]::Round(($match.endedAtMs - $match.startedAtMs) / 60000.0, 1)

    Write-Output ("{0}  {1}  房间 {2}  约 {3} 分钟" -f $ended.ToString("MM-dd HH:mm"), $match.gameName, $match.roomCode, $minutes)
    Write-Output ("  结果：{0}" -f $match.result)

    $sorted = @($match.players | Sort-Object -Property @{ Expression = { $_.score }; Descending = $true })

    foreach ($player in $sorted) {
        $delta = if ($player.delta -ne 0) { " ($($player.delta))" } else { "" }
        $note = if ($player.note) { "  [$($player.note)]" } else { "" }
        Write-Output ("  {0,-10} {1} 分{2}{3}" -f $player.name, $player.score, $delta, $note)
    }

    Write-Output ""
}
