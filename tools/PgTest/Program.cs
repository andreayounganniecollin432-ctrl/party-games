using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PgTest;

/// <summary>
/// 端到端测试：连本地服务端，跑斗地主抢地主 / 掉线托管 / 直接退出，再跑象棋托管 + 重连。
/// 全部通过返回 0，有失败返回 1。
/// </summary>
internal static class Program
{
    private static readonly List<string> Failures = new();

    private static async Task<int> Main(string[] args)
    {
        var url = args.Length > 0 ? args[0] : "ws://127.0.0.1:5199/ws";

        try
        {
            await TestDouDizhu(url);
            await TestDouDizhuNoCall(url);
            await TestDouDizhuCallOnly(url);
            await TestDouDizhuAutoBid(url);
            await TestXiangqi(url);
            await TestEmptyRoomKeepsHistory(url);
        }
        catch (Exception ex)
        {
            Failures.Add("未捕获异常: " + ex);
        }

        Console.WriteLine();
        if (Failures.Count == 0)
        {
            Console.WriteLine("全部通过 ✔");
            return 0;
        }

        Console.WriteLine($"失败 {Failures.Count} 项：");
        foreach (var f in Failures) Console.WriteLine("  ✘ " + f);
        return 1;
    }

    // ------------------------------------------------------- 斗地主：都不叫 / 只叫不抢

    private static async Task TestDouDizhuNoCall(string url)
    {
        Console.WriteLine("== 斗地主：三家都不叫（两圈后强制地主）==");

        var peers = await StartDouDizhu(url);
        try
        {
            await DriveBidding(peers, 20_000, callWhenAsked: false, robWhenAsked: false);

            var snap = await peers[0].WaitAsync(
                s => Str(s.GetProperty("view"), "subtitle").Contains("1 倍"), "强制地主", 10_000);

            var events = peers[0].EventsSnapshot();
            Check(events.Exists(t => t.Contains("强制地主")), "两圈不叫后强制一家当地主");
            Check(events.Exists(t => t.Contains("被指定为地主")), "日志说明是强制指定的");
            Check(Str(snap.GetProperty("view"), "subtitle").Contains("1 倍"), "没抢 → 1 倍");
        }
        finally
        {
            foreach (var peer in peers) await peer.DisposeAsync();
        }
    }

    private static async Task TestDouDizhuCallOnly(string url)
    {
        Console.WriteLine("== 斗地主：叫了但没人抢 ==");

        var peers = await StartDouDizhu(url);
        try
        {
            await DriveBidding(peers, 20_000, callWhenAsked: true, robWhenAsked: false);

            var snap = await peers[0].WaitAsync(
                s => Str(s.GetProperty("view"), "subtitle").Contains("1 倍"), "叫地主定下来", 10_000);

            var events = peers[0].EventsSnapshot();
            Check(events.Exists(t => t.Contains("叫了地主（1 倍）")), "没人抢 → 叫的人当地主、1 倍");
            Check(Str(snap.GetProperty("view"), "subtitle").Contains("1 倍"), "副标题 1 倍");
        }
        finally
        {
            foreach (var peer in peers) await peer.DisposeAsync();
        }
    }

    /// <summary>有人一开局就掉线时，叫牌不能因为等他那 20 秒而卡住（重发牌也不能把托管弄丢）。</summary>
    private static async Task TestDouDizhuAutoBid(string url)
    {
        Console.WriteLine("== 斗地主：有人掉线，叫牌不卡 ==");

        var peers = await StartDouDizhu(url, new[] { "钟一", "钟二", "钟三" });
        try
        {
            await peers[2].DisposeAsync();
            await Task.Delay(300);

            var alive = new[] { peers[0], peers[1] };
            var started = DateTime.UtcNow;
            await DriveBidding(alive, 20_000, callWhenAsked: false, robWhenAsked: false);

            await alive[0].WaitAsync(
                s => Str(s.GetProperty("view"), "subtitle").Contains("1 倍"), "掉线后仍能定地主", 20_000);

            var cost = (DateTime.UtcNow - started).TotalSeconds;
            Check(cost < 8, $"托管替掉线的人立刻过牌，两圈不叫 {cost:0.0}s 内就定下地主（不是每人干等 20 秒）");
        }
        catch (TimeoutException ex)
        {
            Check(false, ex.Message);
        }
        finally
        {
            foreach (var peer in peers) await peer.DisposeAsync();
        }
    }

    /// <summary>开一局三人斗地主，返回三个连着的 peer。</summary>
    private static async Task<Peer[]> StartDouDizhu(string url, string[]? names = null)
    {
        names ??= new[] { "小一", "小二", "小三" };

        var a = new Peer(names[0]);
        var b = new Peer(names[1]);
        var c = new Peer(names[2]);

        await a.ConnectAsync(url);
        await b.ConnectAsync(url);
        await c.ConnectAsync(url);

        await a.SendAsync("create", new { gameId = "doudizhu", maxPlayers = 3 });
        var lobby = await a.WaitAsync(s => Phase(s) == "lobby", "建房");
        var code = Str(lobby, "code");

        await b.SendAsync("join", new { code });
        await c.SendAsync("join", new { code });
        await Task.Delay(300);
        await b.SendAsync("ready", new { ready = true });
        await c.SendAsync("ready", new { ready = true });
        await Task.Delay(300);

        await a.SendAsync("start", null);
        await a.WaitAsync(s => Phase(s) == "playing", "开局");

        return new[] { a, b, c };
    }

    // ---------------------------------------------------------------- 斗地主

    private static async Task TestDouDizhu(string url)
    {
        Console.WriteLine("== 斗地主：抢地主 ==");

        await using var a = new Peer("甲");
        await using var b = new Peer("乙");
        await using var c = new Peer("丙");

        await a.ConnectAsync(url);
        await b.ConnectAsync(url);
        await c.ConnectAsync(url);

        await a.SendAsync("create", new { gameId = "doudizhu", maxPlayers = 3 });
        var lobby = await a.WaitAsync(s => Phase(s) == "lobby", "建房");
        var code = Str(lobby, "code");
        Check(code.Length == 5, $"房间码是 5 位（实际 {code}）");

        await b.SendAsync("join", new { code });
        await c.SendAsync("join", new { code });
        await Task.Delay(300);
        await b.SendAsync("ready", new { ready = true });
        await c.SendAsync("ready", new { ready = true });
        await Task.Delay(300);

        await a.SendAsync("start", null);
        await a.WaitAsync(s => Phase(s) == "playing", "开局");

        // 叫地主：谁被问到就叫；然后两家都抢 → 3 倍
        var driven = await DriveBidding(new[] { a, b, c }, 20_000);
        Check(driven, "叫牌阶段有人接住了按钮");

        var playing = await a.WaitAsync(
            s => Str(s.GetProperty("view"), "subtitle").Contains("3 倍"), "定地主（3 倍）", 8000);

        var joined = a.EventsSnapshot().FindAll(t => t.Contains("抢到地主") || t.Contains("叫了地主"));
        Check(joined.Exists(t => t.Contains("抢到地主（抢 2 次，3 倍）")),
            "两家都抢 → 3 倍: " + string.Join(" | ", joined));

        var subtitle = Str(playing.GetProperty("view"), "subtitle");
        Check(subtitle.Contains("3 倍"), $"副标题显示倍数（{subtitle}）");

        Console.WriteLine("== 斗地主：掉线托管 ==");

        // 地主直接断开连接：座位留着，轮到他应该立刻自动出牌，而不是等 45 秒
        var landlord = PlayerNameWithBadge(playing, "地主");
        Check(landlord.Length > 0, $"有人当上地主（{landlord}）");

        var peers = new[] { a, b, c };
        var victim = peers.First(p => p.Name == landlord);
        var rest = peers.Where(p => p != victim).ToList();
        var observer = rest[0];
        var leaver = rest[1];

        var before = observer.EventsSnapshot().Count;
        await victim.DisposeAsync();

        Check(await WaitEvent(observer, "托管", 6000, before), "其他人看到「托管」标记");
        Check(await WaitEvent(observer, "出牌", 6000, before), "托管后立刻替他出牌（没有干等 45 秒）");

        var spoke = await observer.WaitAsync(
            s => PlayerNameWithBadge(s, "地主") == landlord, "地主仍在场上", 3000);
        Check(PlayerSub(spoke, landlord).Contains("托管"), "地主卡片标出「托管」");

        Console.WriteLine("== 斗地主：直接退出 ==");

        await leaver.SendAsync("action", new { kind = "leave" });
        var finished = await observer.WaitAsync(s => Phase(s) == "finished", "退出后结束本局", 8000);
        var banner = Str(finished.GetProperty("view"), "resultBanner");
        Check(banner.Contains("剩下的人获胜"), $"横幅说明剩下的人赢（{banner}）");
    }

    /// <summary>玩家卡片上带某个徽章的人名（地主 / 农民 / 红 / 黑）。</summary>
    private static string PlayerNameWithBadge(JsonElement snapshot, string badge)
    {
        if (!snapshot.TryGetProperty("view", out var view)) return string.Empty;
        if (!view.TryGetProperty("players", out var players)) return string.Empty;

        foreach (var player in players.EnumerateArray())
        {
            if (Str(player, "badge") == badge) return Str(player, "name");
        }

        return string.Empty;
    }

    private static string PlayerSub(JsonElement snapshot, string name)
    {
        if (!snapshot.TryGetProperty("view", out var view)) return string.Empty;
        if (!view.TryGetProperty("players", out var players)) return string.Empty;

        foreach (var player in players.EnumerateArray())
        {
            if (Str(player, "name") == name) return Str(player, "sub");
        }

        return string.Empty;
    }

    // ------------------------------------------------------------------ 象棋

    private static async Task TestXiangqi(string url)
    {
        Console.WriteLine("== 象棋：掉线托管 + 重连 ==");

        await using var d = new Peer("丁");
        await using var e = new Peer("戊");

        await d.ConnectAsync(url);
        await e.ConnectAsync(url);

        await d.SendAsync("create", new { gameId = "xiangqi", maxPlayers = 2 });
        var lobby = await d.WaitAsync(s => Phase(s) == "lobby", "建象棋房");
        var code = Str(lobby, "code");
        var dingId = d.PlayerId;

        await e.SendAsync("join", new { code });
        await Task.Delay(300);
        await e.SendAsync("ready", new { ready = true });
        await Task.Delay(300);
        await d.SendAsync("start", null);
        await d.WaitAsync(s => Phase(s) == "playing", "象棋开局");

        // 红先：丁是房主也是红方。断掉丁，戊应该马上看到「托管」并看到棋局继续走。
        var before = e.EventsSnapshot().Count;
        await d.DisposeAsync();
        await Task.Delay(400);

        Check(await WaitEvent(e, "自动走了一步", 8000, before), "托管的一方自动走棋（不等 60 秒）");

        try
        {
            await e.WaitAsync(s => PlayerSub(s, "丁").Contains("托管"), "玩家卡片标出托管", 4000);
            Check(true, "玩家卡片标出「托管」");
        }
        catch (TimeoutException ex)
        {
            Check(false, ex.Message);
        }

        // 重连：带 resumeRoom + resumePlayerId 拿回座位，托管应该撤掉
        await using var d2 = new Peer("丁");
        await d2.ConnectAsync(url, code, dingId);
        var back = await d2.WaitAsync(s => Phase(s) == "playing", "重连后回到房间", 8000);
        Check(back.GetProperty("view").TryGetProperty("players", out _), "重连拿到了房间快照");

        Check(await WaitEvent(e, "取消托管", 8000, before), "重连后其他人看到「取消托管」");
    }

    // ------------------------------------------------------- 空房间保留聊天记录

    private static async Task TestEmptyRoomKeepsHistory(string url)
    {
        Console.WriteLine("== 空房间：人走光了，聊天记录还在 ==");

        await using var a = new Peer("老甲");
        await a.ConnectAsync(url);

        await a.SendAsync("create", new { gameId = "lobby", maxPlayers = 8 });
        var lobby = await a.WaitAsync(s => Phase(s) == "lobby", "建聊天房");
        var code = Str(lobby, "code");

        await a.SendAsync("chat", new { text = "这条要留到明天" });
        await Task.Delay(300);

        await a.SendAsync("action", new { kind = "leave" });
        await Task.Delay(500);

        await using var b = new Peer("新来的");
        await b.ConnectAsync(url);
        await b.SendAsync("join", new { code });

        try
        {
            var back = await b.WaitAsync(
                s => s.TryGetProperty("backlog", out var backlog) && backlog.ValueKind == JsonValueKind.Array,
                "加入空房间拿到聊天记录", 5000);

            var lines = back.GetProperty("backlog").EnumerateArray()
                .Select(e => Str(e, "text"))
                .ToList();

            Check(lines.Exists(t => t.Contains("这条要留到明天")),
                "空房间还在，之前的聊天记录也补回来了: " + string.Join(" | ", lines));
        }
        catch (TimeoutException ex)
        {
            Check(false, ex.Message);
        }
    }

    // ---------------------------------------------------------------- 工具

    private static async Task<bool> DriveBidding(Peer[] peers, int timeoutMs,
        bool callWhenAsked = true, bool robWhenAsked = true)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var acted = 0;

        while (DateTime.UtcNow < deadline)
        {
            var moved = false;

            foreach (var peer in peers)
            {
                if (peer.FirstAction() is not { } action) continue;

                var kind = Str(action, "kind");
                var send = kind switch
                {
                    "call" => callWhenAsked ? "call" : "pass",
                    "rob" => robWhenAsked ? "rob" : "pass",
                    _ => null,
                };

                if (send is null) continue;

                await peer.SendAsync("action", new { kind = send });
                acted++;
                moved = true;
                await Task.Delay(200);
            }

            if (moved) continue;

            // 已经没人被问到「叫 / 抢」了：叫牌阶段结束（地主拿到了出牌权）
            if (peers.Any(p => p.AcceptsText())) return acted > 0;
            await Task.Delay(80);
        }

        return acted > 0;
    }

    private static async Task<bool> WaitEvent(Peer peer, string contains, int timeoutMs, int fromIndex = 0)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var events = peer.EventsSnapshot();
            for (var i = Math.Min(fromIndex, events.Count); i < events.Count; i++)
            {
                if (events[i].Contains(contains, StringComparison.Ordinal)) return true;
            }

            await Task.Delay(60);
        }

        return false;
    }

    private static string Phase(JsonElement snapshot) => Str(snapshot, "phase");

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ✔ " : "  ✘ ") + what);
        if (!ok) Failures.Add(what);
    }

    // ---------------------------------------------------------------- peer

    private sealed class Peer : IAsyncDisposable
    {
        private readonly ClientWebSocket socket = new();
        private readonly CancellationTokenSource cts = new();
        private readonly object gate = new();
        private readonly List<string> events = new();
        private readonly ConcurrentQueue<JsonElement> welcomes = new();
        private Task? loop;

        public Peer(string name) => Name = name;

        public string Name { get; }

        public string PlayerId { get; private set; } = string.Empty;

        public JsonElement? LastRoom { get; private set; }

        public List<string> EventsSnapshot()
        {
            lock (gate) return new List<string>(events);
        }

        public async Task ConnectAsync(string url, string? resumeRoom = null, string? resumePlayerId = null)
        {
            await socket.ConnectAsync(new Uri(url), CancellationToken.None);
            loop = Task.Run(ReceiveLoopAsync);

            await SendAsync("hello", new
            {
                displayName = Name,
                protocolVersion = 1,
                resumeRoom,
                resumePlayerId,
            });

            var welcome = await WaitForType("welcome", 5000);
            PlayerId = Str(welcome, "playerId");
        }

        public async Task SendAsync(string type, object? payload)
        {
            var json = payload is null
                ? $"{{\"t\":\"{type}\"}}"
                : $"{{\"t\":\"{type}\",\"d\":{JsonSerializer.Serialize(payload)}}}";

            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[64 * 1024];

            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    Apply(Encoding.UTF8.GetString(message.ToArray()));
                }
            }
            catch
            {
                // 主动断开时这里会抛，正常
            }
        }

        private void Apply(string text)
        {
            using var doc = JsonDocument.Parse(text);
            var type = Str(doc.RootElement, "t");
            if (!doc.RootElement.TryGetProperty("d", out var data)) return;

            switch (type)
            {
                case "welcome":
                    welcomes.Enqueue(data.Clone());
                    break;

                case "room":
                    LastRoom = data.Clone();
                    break;

                case "event":
                    if (data.TryGetProperty("event", out var ev))
                    {
                        var line = Str(ev, "text");
                        lock (gate) events.Add(line);
                    }

                    break;
            }
        }

        public async Task<JsonElement> WaitForType(string type, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (welcomes.TryDequeue(out var welcome)) return welcome;
                await Task.Delay(40);
            }

            throw new TimeoutException($"{Name}: 等不到 {type}");
        }

        public async Task<JsonElement> WaitAsync(Func<JsonElement, bool> predicate, string what, int timeoutMs = 8000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (LastRoom is { } room && predicate(room)) return room;
                await Task.Delay(40);
            }

            throw new TimeoutException($"{Name}: 等不到「{what}」");
        }

        /// <summary>当前视图里第一个可点的动作（只看 call / rob / pass）。</summary>
        public JsonElement? FirstAction()
        {
            if (LastRoom is not { } room) return null;
            if (!room.TryGetProperty("view", out var view)) return null;
            if (Phase(room) != "playing") return null;
            if (!view.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array) return null;

            foreach (var action in actions.EnumerateArray())
            {
                if (!action.TryGetProperty("enabled", out var enabled) || !enabled.GetBoolean()) continue;
                var kind = Str(action, "kind");
                if (kind is "call" or "rob") return action.Clone();
            }

            return null;
        }

        public bool AcceptsText()
        {
            if (LastRoom is not { } room) return false;
            return room.TryGetProperty("view", out var view) &&
                   view.TryGetProperty("acceptsText", out var accepts) &&
                   accepts.ValueKind == JsonValueKind.True;
        }

        public async ValueTask DisposeAsync()
        {
            try { cts.Cancel(); } catch { }
            try { socket.Abort(); } catch { }
            try { socket.Dispose(); } catch { }
            if (loop is not null)
            {
                try { await Task.WhenAny(loop, Task.Delay(500)); } catch { }
            }
        }
    }
}
