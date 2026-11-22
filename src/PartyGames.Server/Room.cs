using PartyGames.Protocol;
using PartyGames.Server.Games;

namespace PartyGames.Server;

public sealed class Player
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? World { get; init; }
    public Connection? Conn { get; set; }
    public bool IsReady { get; set; }
    public long JoinedAtMs { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long? OfflineSinceMs { get; set; }

    /// <summary>Seats filled by the server instead of a client, for solo testing.</summary>
    public bool IsBot { get; init; }

    /// <summary>Base64 头像（PNG/JPEG），没传过就是 null。</summary>
    public string? AvatarBase64 { get; set; }

    public bool IsOnline => IsBot || Conn is { IsClosed: false };
}

/// <summary>
/// One room. Owns the seats, the lobby state and the running match. All members are
/// touched under the hub lock, so no internal locking is needed here.
/// </summary>
public sealed class Room
{
    private const int BacklogLimit = 200;
    private const long OfflineGraceMs = 120_000;

    /// <summary>
    /// 空房间保留 48 小时：人走光了也不立刻销毁，房间里的聊天记录还在，
    /// 拿到房间码的朋友第二天回来还能接着聊。
    /// </summary>
    private const long EmptyRoomTtlMs = 48 * 60 * 60 * 1000;

    private readonly List<RoomEvent> _backlog = new();

    public Room(string code, GameDescriptor descriptor, int maxPlayers)
    {
        Code = code;
        GameId = descriptor.Id;
        GameName = descriptor.Name;
        MinPlayers = descriptor.MinPlayers;
        MaxPlayers = Math.Clamp(maxPlayers, descriptor.MinPlayers, descriptor.MaxPlayers);

        var defaults = GameRegistry.Create(descriptor.Id)?.Scoring;
        HasScoring = defaults is not null;
        ScoreEnabled = defaults is not null;
        ScoreStart = defaults?.Start ?? 3;
        ScorePenalty = defaults?.Penalty ?? 1;
        ScoreBigThreshold = defaults?.BigThreshold ?? 0;
        ScoreBigStart = defaults?.BigStart ?? 2;
        PenaltyRollEnabled = defaults?.PenaltyRoll ?? false;
    }

    public string Code { get; }
    public string GameId { get; }
    public string GameName { get; }
    public int MinPlayers { get; }
    public int MaxPlayers { get; }
    public string HostId { get; set; } = string.Empty;
    public string Phase { get; private set; } = RoomPhase.Lobby;
    public List<Player> Players { get; } = new();
    public IGameModule? Game { get; private set; }
    public long LastActivityMs { get; private set; } = Now;

    // -- scoring ----------------------------------------------------------

    /// <summary>False for games like 轮盘 that don't settle points.</summary>
    public bool HasScoring { get; }

    public bool ScoreEnabled { get; private set; }
    public int ScoreStart { get; private set; }
    public int ScorePenalty { get; private set; }
    public int ScoreBigThreshold { get; private set; }
    public int ScoreBigStart { get; private set; }

    /// <summary>Running totals for this room session; kept across matches.</summary>
    public Dictionary<string, int> Scores { get; } = new();

    /// <summary>Points lost in the match that just ended, for display.</summary>
    public Dictionary<string, int> LastDelta { get; } = new();

    /// <summary>True when hitting zero should trigger a 1-2 roll (1 大冒险 / 2 真心话).</summary>
    public bool PenaltyRollEnabled { get; private set; }

    /// <summary>Task handed out to players who hit zero this match.</summary>
    public Dictionary<string, string> LastRoll { get; } = new();

    /// <summary>Set once somebody's pool is empty: the round is over until the host resets.</summary>
    public bool SessionOver { get; private set; }

    /// <summary>Who ran out of points, for the lobby banner.</summary>
    public List<string> SessionZeroed { get; } = new();

    /// <summary>Game-specific lobby options (e.g. how many bullets the roulette cylinder holds).</summary>
    public Dictionary<string, int> Options { get; } = new();

    /// <summary>When the current match started, for match history.</summary>
    public long StartedAtMs { get; private set; }

    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public Player? FindPlayer(string? id) =>
        id is null ? null : Players.FirstOrDefault(p => p.Id == id);

    public Player AddPlayer(Connection conn, string? preferredId = null)
    {
        var player = new Player
        {
            Id = preferredId ?? conn.PlayerId ?? Guid.NewGuid().ToString("N")[..10],
            Name = conn.DisplayName ?? "玩家",
            World = conn.World,
            Conn = conn,
        };

        Players.Add(player);
        conn.RoomCode = Code;
        conn.PlayerId = player.Id;
        LastActivityMs = Now;
        return player;
    }

    /// <summary>Give a reconnecting connection its old seat back.</summary>
    public void Rebind(Player player, Connection conn)
    {
        player.Conn = conn;
        player.OfflineSinceMs = null;
        conn.RoomCode = Code;
        conn.PlayerId = player.Id;
        LastActivityMs = Now;

        // 对局中的游戏要撤掉「托管」，把操作权还给他
        Game?.OnPlayerReconnected(this, player);
    }

    public void RemovePlayer(string playerId)
    {
        var player = FindPlayer(playerId);
        if (player is null) return;

        Players.Remove(player);
        if (player.Conn is not null) player.Conn.RoomCode = null;

        Log("system", $"{player.Name} 离开了房间。");
        Game?.OnPlayerLeft(this, player);

        if (HostId == playerId)
        {
            var next = Players.FirstOrDefault(p => p.IsOnline && !p.IsBot) ?? Players.FirstOrDefault(p => p.IsOnline);
            HostId = next?.Id ?? string.Empty;
            if (next is not null) Log("system", $"{next.Name} 成为新房主。");
        }

        if (Players.Count == 0)
        {
            Game = null;
            Phase = RoomPhase.Lobby;
        }

        SyncAll();
    }

    public void HandleDisconnect(Player player)
    {
        if (player.Conn is null) return;

        player.Conn = null;
        player.OfflineSinceMs = Now;
        Log("system", Phase == RoomPhase.Playing
            ? $"{player.Name} 掉线了，先交给托管（重连可收回座位）。"
            : $"{player.Name} 掉线了，座位保留 2 分钟。");
        Game?.OnPlayerDisconnected(this, player);
        SyncAll();
    }

    // -- bots --------------------------------------------------------------

    private readonly Dictionary<string, long> _botTurns = new();

    /// <summary>How many seats the host asked to fill with bots.</summary>
    public int BotTarget { get; private set; }

    public void SetBotCount(int count)
    {
        BotTarget = Math.Clamp(count, 0, Math.Max(0, MaxPlayers - 1));
        UpdateBots();
    }

    /// <summary>Adds or drops bot seats so the room matches <see cref="BotTarget"/>; lobby only.</summary>
    private void UpdateBots()
    {
        if (Phase != RoomPhase.Lobby || Game is not null) return;

        var bots = Players.Where(p => p.IsBot).ToList();
        var humans = Players.Count - bots.Count;
        var want = Math.Min(BotTarget, Math.Max(0, MaxPlayers - humans));

        while (bots.Count > want)
        {
            var bot = bots[^1];
            bots.RemoveAt(bots.Count - 1);
            Players.Remove(bot);
            _botTurns.Remove(bot.Id);
        }

        while (bots.Count < want)
        {
            var bot = new Player
            {
                Id = "bot-" + Guid.NewGuid().ToString("N")[..8],
                Name = Bots.NameFor(bots.Count),
                IsBot = true,
                IsReady = true,
            };

            Players.Add(bot);
            bots.Add(bot);
        }

        if (want > 0) Log("system", $"机器人已就位：{want} 个。");
        SyncAll();
    }

    /// <summary>True when this bot may decide again; also hands back how long it will wait afterwards.</summary>
    public bool TryTakeBotTurn(string botId, long nowMs, out int delay)
    {
        delay = Bots.NextDelay();
        return !_botTurns.TryGetValue(botId, out var next) || nowMs >= next;
    }

    public void SetBotTurn(string botId, long nextAtMs) => _botTurns[botId] = nextAtMs;

    public void SetReady(string playerId, bool ready)
    {
        var player = FindPlayer(playerId);
        if (player is null || Phase != RoomPhase.Lobby) return;

        player.IsReady = ready;
        LastActivityMs = Now;
        SyncAll();
    }

    public bool CanStart()
    {
        if (Phase != RoomPhase.Lobby || Game is not null || SessionOver) return false;

        var online = Players.Where(p => p.IsOnline).ToList();
        if (online.Count < MinPlayers) return false;

        return online.Where(p => p.Id != HostId).All(p => p.IsReady);
    }

    public bool TryStartGame(Player by, out string? error)
    {
        error = null;

        if (by.Id != HostId)
        {
            error = "只有房主可以开始游戏。";
            return false;
        }

        if (Game is not null || Phase != RoomPhase.Lobby)
        {
            error = "游戏已经开始了。";
            return false;
        }

        if (SessionOver)
        {
            error = "本轮已经结束（有人积分归零），先点「清零重开」。";
            return false;
        }

        if (!CanStart())
        {
            error = $"至少需要 {MinPlayers} 人在线，并且其他人都点了准备。";
            return false;
        }

        var game = GameRegistry.Create(GameId);
        if (game is null)
        {
            error = "未知的游戏类型。";
            return false;
        }

        Game = game;
        Phase = RoomPhase.Playing;
        LastActivityMs = Now;
        StartedAtMs = Now;
        EnsureScores();
        game.Start(this);
        return true;
    }

    /// <summary>
    /// Seeds a starting pool for anyone who has not played a match yet. The "big match"
    /// rule is evaluated here, with the actual participant count.
    /// </summary>
    private void EnsureScores()
    {
        LastDelta.Clear();
        LastRoll.Clear();
        if (!HasScoring || !ScoreEnabled) return;

        var start = ScoreBigThreshold > 0 && Players.Count > ScoreBigThreshold
            ? ScoreBigStart
            : ScoreStart;

        foreach (var player in Players)
        {
            if (!Scores.ContainsKey(player.Id)) Scores[player.Id] = start;
        }
    }

    /// <summary>Applies a finished match to the scoreboard. Safe to call for scoreless games.</summary>
    public void Finish(GameOutcome outcome)
    {
        Phase = RoomPhase.Finished;
        LastActivityMs = Now;
        LastDelta.Clear();

        if (HasScoring && ScoreEnabled && outcome.LosingPlayerIds.Count > 0)
        {
            ApplyPenalties(outcome);
        }

        RecordMatch(outcome);
    }

    private void ApplyPenalties(GameOutcome outcome)
    {
        var zeroRolls = new List<string>();
        var zeroed = new List<string>();

        foreach (var id in outcome.LosingPlayerIds.Distinct())
        {
            if (!Scores.TryGetValue(id, out var score)) continue;

            var next = Math.Max(0, score - ScorePenalty);
            if (next == score) continue;

            LastDelta[id] = next - score;
            Scores[id] = next;

            if (next != 0 || score <= 0) continue;

            zeroed.Add(id);
            if (!PenaltyRollEnabled) continue;

            // 摇点：只有 1 和 2，1 = 大冒险，2 = 真心话；再从惩罚库里抽一条具体内容。
            var roll = Random.Shared.Next(1, 3);
            var category = roll == 1 ? "大冒险" : "真心话";
            var entry = Punishments.Pick(category, Random.Shared);
            var task = $"{category}：{entry}";
            LastRoll[id] = task;

            var name = FindPlayer(id)?.Name ?? "有人";
            zeroRolls.Add($"{name} 积分归零，摇到 {roll} 点 —— {task}");
        }

        if (LastDelta.Count > 0 && !string.IsNullOrEmpty(outcome.LosingSide))
        {
            Log("result", $"积分结算：{outcome.LosingSide} 每人 -{ScorePenalty} 分。");
        }

        foreach (var line in zeroRolls) Log("result", line);

        // 有人见底 = 本轮到此为止，接下来只差房主确认重开。
        if (zeroed.Count > 0) EndSession(zeroed);
    }

    private void EndSession(List<string> zeroed)
    {
        SessionOver = true;
        SessionZeroed.Clear();
        SessionZeroed.AddRange(zeroed);

        var names = string.Join("、", zeroed.Select(id => FindPlayer(id)?.Name ?? "有人"));
        Log("result", $"本轮结束：{names} 的积分用完了。");

        var punished = zeroed.Where(LastRoll.ContainsKey).ToList();
        if (punished.Count > 0)
        {
            Log("result", "惩罚：" + string.Join("，", punished.Select(id => $"{FindPlayer(id)?.Name} 要 {LastRoll[id]}")));
        }
    }

    /// <summary>Host action: clear the scoreboard and start a fresh round.</summary>
    public void ResetSession()
    {
        if (!SessionOver) return;

        SessionOver = false;
        SessionZeroed.Clear();
        Scores.Clear();
        LastDelta.Clear();
        LastRoll.Clear();
        foreach (var player in Players) player.IsReady = player.IsBot;

        // 立刻按当前人数重新发分，大厅里就能看到新的一轮起始分。
        EnsureScores();

        Log("system", "积分已清零，开始新一轮。");
        SyncAll();
    }

    /// <summary>Writes the finished match to the shared history file used by external tools.</summary>
    private void RecordMatch(GameOutcome outcome)
    {
        MatchLog.Record(new MatchRecord
        {
            RoomCode = Code,
            GameId = GameId,
            GameName = GameName,
            StartedAtMs = StartedAtMs,
            EndedAtMs = Now,
            Result = outcome.Banner,
            LosingSide = outcome.LosingSide,
            Players = Players.Select(p => new MatchPlayer
            {
                Name = p.Name,
                Score = Scores.TryGetValue(p.Id, out var score) ? score : 0,
                Delta = LastDelta.TryGetValue(p.Id, out var delta) ? delta : 0,
                Note = LastRoll.TryGetValue(p.Id, out var note) ? note : null,
                Lost = outcome.LosingPlayerIds.Contains(p.Id),
            }).ToList(),
        });
    }

    /// <summary>Host-only lobby tweaks (当前只有积分规则).</summary>
    public void HandleSetting(Player player, string? key, string? text)
    {
        if (player.Id != HostId || key is null) return;

        if (key == "bots")
        {
            if (int.TryParse(text, out var count))
            {
                SetBotCount(count);
                LastActivityMs = Now;
            }

            return;
        }

        // 非 score.* 的键交给游戏自己处理（比如轮盘的子弹数）。
        if (!key.StartsWith("score.", StringComparison.Ordinal))
        {
            var module = GameRegistry.Create(GameId);
            if (module is not null && module.ApplyLobbySetting(this, key, text ?? string.Empty))
            {
                LastActivityMs = Now;
                SyncAll();
            }

            return;
        }

        if (!HasScoring) return;
        if (!int.TryParse(text, out var value)) return;

        switch (key)
        {
            case "score.enabled": ScoreEnabled = value != 0; break;
            case "score.roll": PenaltyRollEnabled = value != 0; break;
            case "score.start": ScoreStart = Math.Clamp(value, 1, 99); break;
            case "score.penalty": ScorePenalty = Math.Clamp(value, 0, 99); break;
            case "score.bigThreshold": ScoreBigThreshold = Math.Clamp(value, 0, 20); break;
            case "score.bigStart": ScoreBigStart = Math.Clamp(value, 1, 99); break;
            default: return;
        }

        LastActivityMs = Now;
        SyncAll();
    }

    /// <summary>Stores a player's avatar and relays it to the room; null clears it.</summary>
    public void SetAvatar(Player player, string? imageBase64)
    {
        player.AvatarBase64 = string.IsNullOrEmpty(imageBase64) ? null : imageBase64;
        LastActivityMs = Now;

        Broadcast(Envelope.Of(Msg.Avatar, new AvatarMessage
        {
            PlayerId = player.Id,
            ImageBase64 = player.AvatarBase64,
        }));

        SyncAll();
    }

    /// <summary>Routes a gameplay action to the active match.</summary>
    public void HandleGameAction(Player player, ActionRequest request)
    {
        LastActivityMs = Now;
        Game?.HandleAction(this, player, request);
    }

    public void ReturnToLobby()
    {
        Game = null;
        Phase = RoomPhase.Lobby;
        foreach (var player in Players) player.IsReady = player.IsBot;

        Log("system", "回到大厅，可以再来一局。");
        SyncAll();
    }

    public void Tick(long nowMs)
    {
        // 掉线超过 2 分钟还没回来：座位直接请出去。
        // 对局中这一下会走 OnPlayerLeft —— 斗地主 / 象棋会立刻收场、剩下的人赢，
        // 不会出现"一个人挂在座位上，整局 45 秒一回合地空转"的死循环。
        // 这 2 分钟里游戏自己的托管会替他把该走的棋/牌走掉，其他人不用干等。
        var stale = Players
            .Where(p => !p.IsOnline && p.OfflineSinceMs is { } since && nowMs - since > OfflineGraceMs)
            .ToList();

        foreach (var player in stale) RemovePlayer(player.Id);

        Game?.Tick(this, nowMs);
        Bots.Drive(this, nowMs);
    }

    public bool IsExpired(long nowMs) =>
        Players.Count == 0 && nowMs - LastActivityMs > EmptyRoomTtlMs;

    // -- messaging ---------------------------------------------------------

    public void Log(string kind, string text, string? actor = null)
    {
        var ev = new RoomEvent { Kind = kind, Text = text, Actor = actor, AtMs = Now };

        _backlog.Add(ev);
            var cutoff = DateTimeOffset.UtcNow.AddHours(-48).ToUnixTimeMilliseconds();
            _backlog.RemoveAll(e => e.AtMs < cutoff);
            if (_backlog.Count > 2000) _backlog.RemoveRange(0, _backlog.Count - 2000);

        Broadcast(Envelope.Of(Msg.Event, new EventMessage { Event = ev }));
    }

    public void Broadcast(Envelope envelope)
    {
        foreach (var player in Players)
        {
            if (player.Conn is { IsClosed: false } conn) conn.Send(envelope);
        }
    }

    public void SyncAll()
    {
        foreach (var player in Players)
        {
            if (player.Conn is { IsClosed: false } conn) conn.Send(Envelope.Of(Msg.Room, BuildSnapshot(player)));
        }
    }

    public void Sync(Player player, bool includeHistory = false)
    {
        if (player.Conn is { IsClosed: false } conn)
            conn.Send(Envelope.Of(Msg.Room, BuildSnapshot(player, includeBacklog: includeHistory)));
    }

    public RoomSnapshot BuildSnapshot(Player viewer, bool includeBacklog = false)
    {
        var view = Phase == RoomPhase.Lobby || Game is null
            ? BuildLobbyView(viewer)
            : Game.BuildView(this, viewer);

        // 结算画面上直接把摇点结果挂在对应玩家身上，不用翻日志。
        if (Phase != RoomPhase.Lobby && LastRoll.Count > 0)
        {
            foreach (var card in view.Players)
            {
                if (!LastRoll.TryGetValue(card.Id, out var task)) continue;

                var shortTask = task.Length > 18 ? task[..18] + "…" : task;
                card.Sub = string.IsNullOrEmpty(card.Sub) ? $"（{shortTask}）" : $"{card.Sub}（{shortTask}）";
            }
        }

        if (HasScoring && ScoreEnabled) view.Scores = BuildScoreEntries(viewer);

        // 谁有头像：只标一个 bool，图片本身随加入时的快照一起发
        foreach (var card in view.Players)
        {
            card.HasAvatar = FindPlayer(card.Id)?.AvatarBase64 is not null;
        }

        return new RoomSnapshot
        {
            Code = Code,
            GameId = GameId,
            GameName = GameName,
            Phase = Phase,
            HostId = HostId,
            ServerTimeMs = Now,
            View = view,
            Backlog = includeBacklog ? _backlog.ToList() : null,
            Avatars = includeBacklog
                ? Players.Where(p => p.AvatarBase64 is not null)
                    .Select(p => new AvatarMessage { PlayerId = p.Id, ImageBase64 = p.AvatarBase64 })
                    .ToList()
                : null,
        };
    }

    private GameView BuildLobbyView(Player viewer)
    {
        var online = Players.Count(p => p.IsOnline);

        var view = new GameView
        {
            Title = $"{GameName} · 房间 {Code}",
            Subtitle = $"{online}/{MaxPlayers} 人在线 · 服务器共 {ServerStats.TotalOnline} 人",
            PrivateNoteLabel = "房间码",
            PrivateNote = Code,
            Instruction = SessionOver
                ? BuildSessionOverText()
                : online < MinPlayers
                    ? $"还差 {MinPlayers - online} 人才能开始（至少 {MinPlayers} 人）。"
                    : "其他人点准备后，房主就可以开始。",
            Players = Players.Select(p => new GamePlayerCard
            {
                Id = p.Id,
                Name = p.Name,
                IsYou = p.Id == viewer.Id,
                IsHost = p.Id == HostId,
                IsReady = p.IsReady,
                IsAlive = p.IsOnline,
                IsOffline = !p.IsOnline,
                Badge = p.IsBot ? "机器人" : p.Id == HostId ? "房主" : p.IsReady ? "已准备" : "未准备",
                Sub = ScoreText(p.Id),
                Accent = p.Id == HostId ? null : p.IsReady ? "good" : null,
            }).ToList(),
        };

        // 只有房主看得到这几个旋钮，其他人在玩家卡片上看到积分就够了。
        if (HasScoring && viewer.Id == HostId) AddScoreFields(view);
        if (viewer.Id == HostId)
        {
            view.Fields.Add(new GameField
            {
                Key = "bots",
                Label = "机器人",
                Value = BotTarget.ToString(),
                Kind = "int",
                Min = 0,
                Max = Math.Max(0, MaxPlayers - 1),
            });
        }

        if (viewer.Id == HostId) AddGameFields(view);

        view.Stats = BuildStatsFor(viewer.Name);

        if (SessionOver)
        {
            if (viewer.Id == HostId)
            {
                view.Actions.Add(new GameAction { Kind = "resetsession", Label = "清零重开" });
            }

            view.Actions.Add(new GameAction { Kind = "leave", Label = "离开房间", Danger = true });
            return view;
        }

        view.Actions.Add(new GameAction
        {
            Kind = viewer.IsReady ? "unready" : "ready",
            Label = viewer.IsReady ? "取消准备" : "准备",
        });

        view.Actions.Add(new GameAction
        {
            Kind = "start",
            Label = "开始游戏",
            Enabled = viewer.Id == HostId && CanStart(),
        });

        view.Actions.Add(new GameAction { Kind = "leave", Label = "离开房间", Danger = true });
        return view;
    }

    private string BuildSessionOverText()
    {
        var names = SessionZeroed.Count == 0
            ? "有人"
            : string.Join("、", SessionZeroed.Select(id => FindPlayer(id)?.Name ?? "有人"));

        var text = $"本轮结束：{names} 的积分用完了。";

        var punished = SessionZeroed.Where(LastRoll.ContainsKey).ToList();
        if (punished.Count > 0)
        {
            text += " " + string.Join("，", punished.Select(id => $"{FindPlayer(id)?.Name} 要 {LastRoll[id]}")) + "。";
        }

        return text + "（清点后由房主开新一轮）";
    }

    /// <summary>Game-specific knobs shown to the host, e.g. how many bullets the cylinder holds.</summary>
    private void AddGameFields(GameView view)
    {
        var module = GameRegistry.Create(GameId);
        if (module is null) return;

        view.Fields.AddRange(module.LobbyFields(this));
    }

    /// <summary>This player's record across stored matches, grouped by game.</summary>
    public static List<StatRow> BuildStatsFor(string name)
    {
        if (string.IsNullOrEmpty(name)) return new List<StatRow>();

        return MatchLog.Recent(300)
            .SelectMany(match => match.Players
                .Where(player => player.Name == name)
                .Select(player => new { match.GameName, player.Lost }))
            .GroupBy(entry => entry.GameName)
            .Select(group => new StatRow
            {
                Game = group.Key,
                Wins = group.Count(entry => !entry.Lost),
                Losses = group.Count(entry => entry.Lost),
            })
            .OrderByDescending(row => row.Wins + row.Losses)
            .ToList();
    }

    private List<ScoreEntry> BuildScoreEntries(Player viewer)    {
        return Players
            .Where(p => Scores.ContainsKey(p.Id))
            .OrderByDescending(p => Scores[p.Id])
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new ScoreEntry
            {
                Id = p.Id,
                Name = p.Name,
                IsYou = p.Id == viewer.Id,
                Score = Scores[p.Id],
                Delta = LastDelta.TryGetValue(p.Id, out var delta) ? delta : 0,
                Note = LastRoll.TryGetValue(p.Id, out var note) ? note : null,
            })
            .ToList();
    }

    private string? ScoreText(string playerId)    {
        if (!HasScoring || !ScoreEnabled) return null;
        if (!Scores.TryGetValue(playerId, out var score)) return null;

        if (LastRoll.TryGetValue(playerId, out var task))
        {
            task = task.Length > 16 ? task[..16] + "…" : task;
            return $"积分 {score}（{task}）";
        }

        var delta = LastDelta.TryGetValue(playerId, out var d) ? d : 0;
        return delta == 0 ? $"积分 {score}" : $"积分 {score}（{delta:+0;-0}）";
    }

    private void AddScoreFields(GameView view)
    {
        view.Fields.Add(new GameField
        {
            Key = "score.enabled",
            Label = "积分",
            Value = ScoreEnabled ? "1" : "0",
            Kind = "bool",
        });

        if (!ScoreEnabled) return;

        view.Fields.Add(new GameField
        {
            Key = "score.roll",
            Label = "归零摇点",
            Value = PenaltyRollEnabled ? "1" : "0",
            Kind = "bool",
        });

        view.Fields.Add(new GameField
        {
            Key = "score.start",
            Label = "起始分",
            Value = ScoreStart.ToString(),
            Kind = "int",
            Min = 1,
            Max = 99,
        });

        view.Fields.Add(new GameField
        {
            Key = "score.penalty",
            Label = "输了扣",
            Value = ScorePenalty.ToString(),
            Kind = "int",
            Min = 0,
            Max = 99,
        });

        view.Fields.Add(new GameField
        {
            Key = "score.bigThreshold",
            Label = "大场人数（0=关）",
            Value = ScoreBigThreshold.ToString(),
            Kind = "int",
            Min = 0,
            Max = 20,
        });

        if (ScoreBigThreshold > 0)
        {
            view.Fields.Add(new GameField
            {
                Key = "score.bigStart",
                Label = "大场起始分",
                Value = ScoreBigStart.ToString(),
                Kind = "int",
                Min = 1,
                Max = 99,
            });
        }
    }
}
