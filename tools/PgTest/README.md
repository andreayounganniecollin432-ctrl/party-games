# PgTest · 服务端端到端测试

不开游戏、不开插件，直接对着一个本地服务端跑协议，验证那些「只有三个人真的开一局才会暴露」的流程：

| 用例 | 验的是 |
|---|---|
| 斗地主 · 抢地主 | 第一家叫了以后另外两家能抢，抢 2 次 → 3 倍 |
| 斗地主 · 掉线托管 | 地主掉线，其他人看到「托管」，轮到他的回合立刻替他出牌（不等 45 秒） |
| 斗地主 · 直接退出 | 有人点离开，本局立刻收场，剩下的人获胜 |
| 斗地主 · 三家都不叫 | 两圈不叫后强制第一家当地主（1 倍） |
| 斗地主 · 叫了没人抢 | 叫的人当地主（1 倍） |
| 斗地主 · 有人掉线时叫牌 | 重发牌不会把托管弄丢，两圈不叫仍能很快定地主 |
| 象棋 · 托管 + 重连 | 掉线方自动走棋；带 resumeRoom / resumePlayerId 重连后取消托管 |
| 空房间 | 人走光了房间还在，聊天记录也还在 |

## 跑

```powershell
# 1. 起一个测试用的服务端（数据目录指到临时目录，别弄脏正式战绩）
$env:PARTYGAMES_DATA = "C:\Coral\pgtest-data"
dotnet C:\Coral\dalamud-party-games\src\PartyGames.Server\bin\Release\net10.0\PartyGames.Server.dll --urls http://127.0.0.1:5199

# 2. 另一个窗口跑测试
dotnet run --project C:\Coral\dalamud-party-games\tools\PgTest -- ws://127.0.0.1:5199/ws
```

全部通过时最后一行是 `全部通过 ✔`，退出码 0；有失败会逐条列出来，退出码 1。

## 为什么要单独做一个工程

`PartyGames.Server` 本来就只是一个 WebSocket + JSON 协议，用三个 `ClientWebSocket` 就能把整局跑完。
比起进游戏开三个客户端，这样跑一次只要几秒钟，改完服务端立刻能回归。
