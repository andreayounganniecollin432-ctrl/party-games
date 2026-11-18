using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PartyGames.Protocol;

Console.OutputEncoding = Encoding.UTF8;

var count = args.Length > 0 && int.TryParse(args[0], out var parsedCount) ? parsedCount : 6;
var url = args.Length > 1 ? args[1] : "ws://127.0.0.1:5180/ws";
var gameId = args.Length > 2 ? args[2] : "undercover";
var settingsMode = args.Length > 3 && args[3] is not ("-" or "") ? args[3] : null;

Console.WriteLine($"== PartyGames bot sim: {count} players, game={gameId} -> {url} ==");

var bots = new List<Bot>();
for (var i = 0; i < count; i++)
{
    var bot = new Bot($"Bot{i + 1:00}", url);

    try
    {
        await bot.ConnectAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL: 连不上 {url}（{ex.Message}）。服务器起来了吗？");
        return 1;
    }

    bots.Add(bot);
    await Task.Delay(80);
}

if (settingsMode is not null) bots[0].SettingsMode = settingsMode;
if (args.Length > 6 && args[6] == "bad") foreach (var bot in bots) bot.BadText = true;

// Wait for every bot to finish the hello handshake.
if (!await WaitUntilAsync(() => bots.All(b => b.PlayerId is not null), TimeSpan.FromSeconds(10)))
{
    Console.WriteLine("FAIL: hello handshake timed out");
    return 1;
}

await bots[0].Send(Envelope.Of(Msg.CreateRoom, new CreateRoomRequest { GameId = gameId, MaxPlayers = count }));

if (!await WaitUntilAsync(() => bots[0].Room?.Code is not null, TimeSpan.FromSeconds(10)))
{
    Console.WriteLine("FAIL: room was not created");
    return 1;
}

var code = bots[0].Room!.Code;
Console.WriteLine($"room code: {code}");

foreach (var bot in bots.Skip(1))
{
    await bot.Send(Envelope.Of(Msg.JoinRoom, new JoinRoomRequest { Code = code }));
    await Task.Delay(120);
}

if (!await WaitUntilAsync(() => bots.All(b => b.Room?.Code == code), TimeSpan.FromSeconds(10)))
{
    Console.WriteLine("FAIL: not everyone joined");
    return 1;
}

Console.WriteLine("all players joined, waiting for the match to run...");

var rounds = args.Length > 4 && int.TryParse(args[4], out var parsedRounds) ? Math.Max(1, parsedRounds) : 1;
RoomSnapshot? final = null;

for (var round = 1; round <= rounds; round++)
{
    if (!await WaitUntilAsync(() => bots.Any(b => b.Room?.Phase == RoomPhase.Finished), TimeSpan.FromSeconds(420)))
    {
        Console.WriteLine($"FAIL: round {round} did not finish (phase={bots[0].Room?.Phase})");
        foreach (var line in bots[0].Log.TakeLast(25)) Console.WriteLine("  " + line);
        return 1;
    }

    final = bots[0].Room;
    Console.WriteLine($"ROUND {round}: {final!.View.ResultBanner}");

    if (round == rounds) break;

    // 回大厅再开一局，验证积分是跨局累计的。
    foreach (var bot in bots) bot.Paused = true;
    await bots[0].Send(Envelope.Of(Msg.Action, new ActionRequest { Kind = "lobby" }));

    if (!await WaitUntilAsync(() => bots[0].Room?.Phase == RoomPhase.Lobby, TimeSpan.FromSeconds(10)))
    {
        Console.WriteLine("FAIL: could not return to the lobby");
        return 1;
    }

    foreach (var bot in bots) bot.Paused = false;

    foreach (var bot in bots)
    {
        await bot.NudgeAsync();
        await Task.Delay(120);
    }

    if (!await WaitUntilAsync(() => bots[0].Room?.Phase == RoomPhase.Playing, TimeSpan.FromSeconds(30)))
    {
        Console.WriteLine("FAIL: next round never started");
        return 1;
    }
}

if (final is null)
{
    Console.WriteLine("FAIL: no match was played");
    return 1;
}

Console.WriteLine();
Console.WriteLine($"RESULT: {final.View.ResultBanner}");
foreach (var card in final.View.Players)
{
    Console.WriteLine($"  {card.Name,-8} {card.Badge,-10} {card.Sub}");
}

// 结算之后回大厅，看积分有没有真的扣下去。
foreach (var bot in bots) bot.Paused = true;
await bots[0].Send(Envelope.Of(Msg.Action, new ActionRequest { Kind = "lobby" }));
await Task.Delay(1500);

var lobby = bots[0].Room;
if (lobby is not null)
{
    Console.WriteLine();
    Console.WriteLine($"lobby: {lobby.View.Instruction}");
    Console.WriteLine($"subtitle: {lobby.View.Subtitle}");
    Console.WriteLine($"stats: {string.Join(" / ", lobby.View.Stats.Select(s => $"{s.Game} {s.Wins}胜{s.Losses}负"))}");
    Console.WriteLine($"fields: {string.Join(" / ", lobby.View.Fields.Select(f => $"{f.Label}={f.Value}"))}");
    Console.WriteLine($"buttons: {string.Join(" / ", lobby.View.Actions.Select(a => (a.Enabled ? "" : "(禁用)") + a.Label))}");
}

var board = lobby?.View.Scores ?? new List<ScoreEntry>();
if (board.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("SCOREBOARD (view.scores):");
    foreach (var entry in board)
    {
        var note = string.IsNullOrEmpty(entry.Note) ? string.Empty : $"  [{entry.Note}]";
        Console.WriteLine($"  {entry.Name,-8} {entry.Score,2}  ({entry.Delta:+0;-0;0}){note}");
    }
}
else
{
    var scored = lobby?.View.Players.Where(p => (p.Sub ?? string.Empty).Contains("积分")).ToList() ?? new List<GamePlayerCard>();
    if (scored.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("SCOREBOARD (card sub):");
        foreach (var card in scored.OrderByDescending(c => ScoreOf(c.Sub)))
        {
            Console.WriteLine($"  {card.Name,-8} {card.Sub}");
        }
    }
}

if (args.Length > 5 && args[5] == "reset")
{
    await bots[0].Send(Envelope.Of(Msg.Action, new ActionRequest { Kind = "resetsession" }));
    await Task.Delay(1000);

    var after = bots[0].Room;
    Console.WriteLine();
    Console.WriteLine("AFTER RESET:");
    Console.WriteLine($"  lobby: {after?.View.Instruction}");
    Console.WriteLine($"  buttons: {string.Join(" / ", after?.View.Actions.Select(a => (a.Enabled ? "" : "(禁用)") + a.Label) ?? Array.Empty<string>())}");
    Console.WriteLine($"  scores: {string.Join(", ", after?.View.Scores.Select(s => $"{s.Name} {s.Score}") ?? Array.Empty<string>())}");
}

Console.WriteLine();
Console.WriteLine("events:");
foreach (var line in bots[0].Log.Where(l => !l.Contains("已投票")).TakeLast(30))
{
    Console.WriteLine("  " + line);
}

Console.WriteLine();
Console.WriteLine("OK: full match completed");
return 0;

static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until)
    {
        if (predicate()) return true;
        await Task.Delay(100);
    }

    return predicate();
}

static int ScoreOf(string? sub)
{
    if (string.IsNullOrEmpty(sub)) return 0;

    var match = System.Text.RegularExpressions.Regex.Match(sub, @"积分\s*(-?\d+)");
    return match.Success ? int.Parse(match.Groups[1].Value) : 0;
}

internal sealed class Bot
{
    private static readonly string[] Phrases =
    {
        "挺常见的，平时能遇到",
        "我觉得不算贵",
        "夏天比较常见",
        "家里人更喜欢",
        "有点甜",
        "需要花点时间",
        "年轻人玩得多",
        "我也说不太清楚",
        "经常在街上看到",
        "味道还不错",
    };

    private readonly ClientWebSocket _socket = new();
    private readonly string _url;
    private readonly List<string> _log = new();
    private int _acting;

    public Bot(string name, string url)
    {
        Name = name;
        _url = url;
    }

    public string Name { get; }
    public string? PlayerId { get; private set; }
    public RoomSnapshot? Room { get; private set; }
    public IReadOnlyList<string> Log => _log;

    /// <summary>Set when the test wants the bots to stop clicking buttons.</summary>
    public bool Paused { get; set; }

    /// <summary>Send junk text (to exercise the description validator).</summary>
    public bool BadText { get; set; }

    private DateTime _playingSinceUtc;
    private DateTime _lastGenericUtc = DateTime.MinValue;

    /// <summary>Throttled actions still need to happen; nudge ourselves later.</summary>
    private void RetryLater(int delayMs = 1600)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs);
            await ReactAsync();
        });
    }

    /// <summary>Re-evaluate the current snapshot (needed after unpausing, since no new frame arrives).</summary>
    public Task NudgeAsync() => ReactAsync();

    /// <summary>Host bot nudges the scoring settings once, to exercise the settings path.</summary>
    /// <remarks>"tweak" = 起始分+2 扣分+1；"low" = 起始分压到 1，方便触发归零摇点。</remarks>
    public string? SettingsMode { get; set; }

    private bool _tweaked;

    private string? _lastDescribeKey;
    private string? _lastVoteKey;
    private string? _lastGenericKey;
    private DateTime _lastTextUtc = DateTime.MinValue;

    /// <summary>词语接龙用的小词库：按首字查一个能接得上的词。</summary>
    private static readonly Dictionary<string, string> ChainWords = new()
    {
        ["苹"] = "苹果", ["果"] = "果实", ["实"] = "实在", ["在"] = "在意", ["意"] = "意思",
        ["思"] = "思念", ["念"] = "念头", ["头"] = "头发", ["发"] = "发展", ["展"] = "展开",
        ["开"] = "开心", ["心"] = "心情", ["情"] = "情绪", ["绪"] = "绪论", ["论"] = "论文",
        ["文"] = "文化", ["化"] = "化学", ["学"] = "学生", ["生"] = "生活", ["活"] = "活动",
        ["动"] = "动物", ["物"] = "物理", ["理"] = "理想", ["想"] = "想法", ["法"] = "法律",
        ["律"] = "律师", ["师"] = "师傅", ["傅"] = "傅说", ["说"] = "说明", ["明"] = "明天",
        ["天"] = "天空", ["空"] = "空气", ["气"] = "气球", ["球"] = "球场", ["场"] = "场景",
        ["景"] = "景色", ["色"] = "色彩", ["彩"] = "彩虹", ["虹"] = "虹口",
    };

    private static string? NextChainWord(string? hint)
    {
        if (string.IsNullOrEmpty(hint) || !hint.Contains('「')) return "苹果";

        var match = System.Text.RegularExpressions.Regex.Match(hint, "「(.)」");
        if (!match.Success) return "苹果";

        return ChainWords.TryGetValue(match.Groups[1].Value, out var word) ? word : null;
    }

    public async Task ConnectAsync()
    {
        await _socket.ConnectAsync(new Uri(_url), CancellationToken.None);
        _ = Task.Run(ReceiveLoopAsync);

        await Send(Envelope.Of(Msg.Hello, new HelloRequest
        {
            DisplayName = Name,
            World = "Test",
            ProtocolVersion = Proto.Version,
        }));
    }

    public async Task Send(Envelope envelope)
    {
        var bytes = Encoding.UTF8.GetBytes(PgJson.SerializeEnvelope(envelope));
        await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[16384];
        using var stream = new MemoryStream();

        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                stream.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    stream.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
                Handle(json);
            }
        }
        catch
        {
            // Socket closed; the coordinator will time out if this matters.
        }
    }

    private void Handle(string json)
    {
        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(json, PgJson.Options);
        }
        catch (JsonException)
        {
            return;
        }

        if (envelope is null) return;

        switch (envelope.Type)
        {
            case Msg.Welcome:
                PlayerId = PgJson.Read<WelcomeResponse>(envelope.Data)?.PlayerId;
                break;

            case Msg.Room:
                Room = PgJson.Read<RoomSnapshot>(envelope.Data);

                // 回到大厅意味着新的一局，清掉上一局的阶段记忆，否则机器人会以为已经行动过。
                if (Room?.Phase == RoomPhase.Lobby)
                {
                    _lastDescribeKey = null;
                    _lastVoteKey = null;
                    _lastGenericKey = null;
                }

                if (Room?.Phase == RoomPhase.Playing && _playingSinceUtc == default)
                {
                    _playingSinceUtc = DateTime.UtcNow;
                }

                _ = ReactAsync();
                break;

            case Msg.Status:
            {
                var status = PgJson.Read<StatusMessage>(envelope.Data);
                if (status is not null && Environment.GetEnvironmentVariable("PG_VERBOSE") == "1")
                {
                    Console.WriteLine($"  [status] online={status.Online} rooms={status.Rooms} "
                        + "players=[" + string.Join(",", status.OnlinePlayers) + "] "
                        + "stats=" + string.Join("/", status.Stats.Select(s => $"{s.Game} {s.Wins}胜{s.Losses}负")));
                }

                break;
            }

            case Msg.Event:
                var ev = PgJson.Read<EventMessage>(envelope.Data)?.Event;
                if (ev is not null)
                {
                    _log.Add($"{ev.Kind}: {ev.Text}");
                    if (Environment.GetEnvironmentVariable("PG_VERBOSE") == "1")
                    {
                        Console.WriteLine($"  [ev] {ev.Kind}: {ev.Text}");
                    }
                }
                break;

            case Msg.Error:
                var err = PgJson.Read<ErrorResponse>(envelope.Data);
                _log.Add($"ERROR {err?.Code}: {err?.Message}");
                break;

            case Msg.Reject:
                _log.Add("REJECTED by server");
                break;
        }
    }

    private async Task ReactAsync()
    {
        if (Paused) return;
        if (Interlocked.Exchange(ref _acting, 1) == 1) return;

        try
        {
            var snapshot = Room;
            var view = snapshot?.View;
            if (snapshot is null || view is null) return;

            if (snapshot.Phase == RoomPhase.Lobby)
            {
                if (!string.IsNullOrEmpty(SettingsMode) && !_tweaked && view.Fields.Count > 0)
                {
                    _tweaked = true;

                    if (SettingsMode.StartsWith("bullets", StringComparison.Ordinal)
                        && int.TryParse(SettingsMode["bullets".Length..], out var bulletCount))
                    {
                        await Send(Envelope.Of(Msg.Action, new ActionRequest
                        {
                            Kind = "setting",
                            Target = "roulette.bullets",
                            Text = bulletCount.ToString(),
                        }));
                        return;
                    }

                    if (SettingsMode.StartsWith("wolves", StringComparison.Ordinal)
                        && int.TryParse(SettingsMode["wolves".Length..], out var wolfCount))
                    {
                        await Send(Envelope.Of(Msg.Action, new ActionRequest
                        {
                            Kind = "setting",
                            Target = "werewolf.wolves",
                            Text = wolfCount.ToString(),
                        }));
                        return;
                    }

                    if (SettingsMode == "nowitch")
                    {
                        await Send(Envelope.Of(Msg.Action, new ActionRequest
                        {
                            Kind = "setting",
                            Target = "werewolf.witch",
                            Text = "0",
                        }));
                        return;
                    }

                    if (SettingsMode == "low")
                    {
                        await Send(Envelope.Of(Msg.Action, new ActionRequest
                        {
                            Kind = "setting",
                            Target = "score.start",
                            Text = "1",
                        }));
                        return;
                    }

                    foreach (var field in view.Fields.Where(f => f.Kind == "int" && f.Key is "score.start" or "score.penalty"))
                    {
                        if (!int.TryParse(field.Value, out var current)) continue;

                        // 起始分 +2、扣分 +1，这样结算结果和默认值不会撞在一起。
                        var step = field.Key == "score.start" ? 2 : 1;
                        await Send(Envelope.Of(Msg.Action, new ActionRequest
                        {
                            Kind = "setting",
                            Target = field.Key,
                            Text = (current + step).ToString(),
                        }));
                        await Task.Delay(150);
                    }

                    return;
                }

                if (view.Actions.Any(a => a is { Kind: "ready", Enabled: true }))
                {
                    await Task.Delay(Random.Shared.Next(80, 400));
                    await Send(Envelope.Of(Msg.Action, new ActionRequest { Kind = "ready" }));
                    return;
                }

                if (view.Actions.Any(a => a is { Kind: "start", Enabled: true }))
                {
                    await Task.Delay(350);
                    await Send(Envelope.Of(Msg.Action, new ActionRequest { Kind = "start" }));
                }

                return;
            }

            if (view.AcceptsText)
            {
                var kind = string.IsNullOrEmpty(view.TextActionKind) ? "describe" : view.TextActionKind;

                // 描述/发言是一局一次；问答类（问问题、接龙）允许反复说，按时间节流。
                var alreadySpoke = (kind is "describe" or "say") && _lastDescribeKey == view.Title;
                var tooSoon = (DateTime.UtcNow - _lastTextUtc).TotalMilliseconds < 1500;

                if (!alreadySpoke && tooSoon)
                {
                    RetryLater();
                    return;
                }

                if (!alreadySpoke && !tooSoon)
                {
                    if (kind is "describe" or "say") _lastDescribeKey = view.Title;
                    _lastTextUtc = DateTime.UtcNow;
                    var text = BadText
                        ? "。"
                        : kind == "word"
                            ? NextChainWord(view.TextHint)
                            : Phrases[Random.Shared.Next(Phrases.Length)];

                    if (kind == "word" && text is null)
                    {
                        await Send(Envelope.Of(Msg.Action, new ActionRequest { Kind = "giveup" }));
                        return;
                    }

                    await Task.Delay(Random.Shared.Next(150, 500));
                    await Send(Envelope.Of(Msg.Action, new ActionRequest { Kind = kind, Text = text }));

                    // 发言不会触发快照推送，所以不会有新消息把「过」按钮送过来，
                    // 这里主动补一次，否则白天要干等满 90 秒。
                    if (view.Actions.Any(a => a is { Kind: "pass", Enabled: true }))
                    {
                        await Task.Delay(200);
                        await Send(Envelope.Of(Msg.Action, new ActionRequest { Kind = "pass" }));
                    }

                    return;
                }
            }

            var pulls = view.Actions.Where(a => a is { Kind: "pull", Enabled: true }).ToList();
            if (pulls.Count > 0)
            {
                await Task.Delay(Random.Shared.Next(120, 400));
                await Send(Envelope.Of(Msg.Action, new ActionRequest { Kind = "pull" }));
                return;
            }

            var votes = view.Actions.Where(a => a is { Kind: "vote", Enabled: true }).ToList();
            if (votes.Count == 0)
            {
                // 未知游戏的兜底：随便点一个可用按钮。游戏全在服务端，
                // 所以模拟器不需要认识每一种游戏。
                var generic = view.Actions
                    .Where(a => a.Enabled && a.Kind is not ("leave" or "lobby"))
                    .ToList();

                if (generic.Count == 0) return;

                // 有问答/判定这类"正事"就先做，别急着公布答案。
                var preferred = generic.Where(a => a.Kind is not ("reveal" or "giveup")).ToList();
                if (preferred.Count == 0
                    && generic.Any(a => a.Kind == "reveal")
                    && (DateTime.UtcNow - _playingSinceUtc).TotalSeconds < 12)
                {
                    RetryLater();
                    return;
                }

                var pool = preferred.Count > 0 ? preferred : generic;

                var genericKey = view.DeadlineAtMs is { } deadline ? $"{view.Title}|{deadline}" : null;
                if (genericKey is not null && _lastGenericKey == genericKey) return;
                if ((DateTime.UtcNow - _lastGenericUtc).TotalMilliseconds < 1500)
                {
                    RetryLater();
                    return;
                }

                _lastGenericKey = genericKey;
                _lastGenericUtc = DateTime.UtcNow;
                await Task.Delay(Random.Shared.Next(120, 400));

                var genericPick = pool[Random.Shared.Next(pool.Count)];
                await Send(Envelope.Of(Msg.Action, new ActionRequest
                {
                    Kind = genericPick.Kind,
                    Target = genericPick.Target,
                }));
                return;
            }

            if (_lastVoteKey == view.Title) return;

            _lastVoteKey = view.Title;
            await Task.Delay(Random.Shared.Next(150, 450));

            // Humans converge on a suspect; picking by label order makes every bot
            // agree, so the tally produces a decisive result instead of endless ties.
            var pick = votes.OrderBy(v => v.Label, StringComparer.Ordinal).First();
            await Send(Envelope.Of(Msg.Action, new ActionRequest { Kind = "vote", Target = pick.Target }));
        }
        finally
        {
            Interlocked.Exchange(ref _acting, 0);
        }
    }
}
