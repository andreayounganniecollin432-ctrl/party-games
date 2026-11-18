using PartyGames.Protocol;

namespace PartyGames.Server.Games;

/// <summary>
/// 谁是卧底. Most players get one word, a minority get a near-synonym. Nobody is
/// told which side they are on, which is what makes the bluffing work.
/// </summary>
public sealed class UndercoverGame : IGameModule
{
    private const int DealMs = 3_500;
    private const int DescribeMs = 75_000;
    private const int VoteMs = 40_000;
    private const int RevealMs = 6_000;

    public string Id => "undercover";
    public string DisplayName => "谁是卧底";
    public string Blurb => "多数人拿到同一个词，少数人拿到近义词。用一句话描述你的词，别被识破。";
    public int MinPlayers => 3;
    public int MaxPlayers => 20;

    public bool SupportsScoring => true;

    public ScoreDefaults Scoring => new(Start: 3, Penalty: 1, BigThreshold: 0, BigStart: 2, PenaltyRoll: true);

    private enum Stage { Deal, Describe, Vote, Reveal, End }

    private sealed class Seat
    {
        public string PlayerId = string.Empty;
        public string Name = string.Empty;
        public string Word = string.Empty;
        public bool IsUndercover;
        public bool Alive = true;
        public string? Description;
        public string Role => IsUndercover ? "卧底" : "平民";
    }

    private readonly List<Seat> _seats = new();
    private readonly Dictionary<string, string> _votes = new();
    private readonly HashSet<string> _rejectAnnounced = new();
    private readonly Random _rng = new();
    private Room? _room;
    private Stage _stage = Stage.Deal;
    private int _round = 1;
    private long _deadline;
    private string? _banner;

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Start(Room room)
    {
        _room = room;
        _seats.Clear();
        _votes.Clear();
        _round = 1;
        _banner = null;

        var players = room.Players.ToList();
        var count = players.Count;
        var undercoverCount = count >= 17 ? 4 : count >= 12 ? 3 : count >= 7 ? 2 : 1;

        var (a, b) = UndercoverWordBank.Pick(_rng);
        var civilianWord = _rng.Next(2) == 0 ? a : b;
        var undercoverWord = civilianWord == a ? b : a;

        var chosen = players.OrderBy(_ => _rng.Next()).Take(undercoverCount)
                            .Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var p in players)
        {
            var isUndercover = chosen.Contains(p.Id);
            _seats.Add(new Seat
            {
                PlayerId = p.Id,
                Name = p.Name,
                IsUndercover = isUndercover,
                Word = isUndercover ? undercoverWord : civilianWord,
            });
        }

        _stage = Stage.Deal;
        _deadline = Now + DealMs;
        room.Log("system", $"游戏开始：{count} 名玩家，其中 {undercoverCount} 名卧底。");
        room.SyncAll();
    }

    public void Tick(Room room, long nowMs)
    {
        if (_stage == Stage.End || nowMs < _deadline) return;

        switch (_stage)
        {
            case Stage.Deal:
                BeginDescribe(room, nowMs);
                break;
            case Stage.Describe:
                BeginVote(room, nowMs, timedOut: true);
                break;
            case Stage.Vote:
                ResolveVote(room, nowMs);
                break;
            case Stage.Reveal:
                NextRoundOrEnd(room, nowMs);
                break;
        }
    }

    public void HandleAction(Room room, Player player, ActionRequest request)
    {
        var seat = FindSeat(player.Id);
        if (seat is null) return;

        switch (request.Kind)
        {
            case "describe":
                SubmitDescription(room, seat, request.Text);
                break;
            case "vote":
                SubmitVote(room, seat, request.Target);
                break;
        }
    }

    public GameView BuildView(Room room, Player viewer)
    {
        var seat = FindSeat(viewer.Id);
        var view = new GameView
        {
            Title = $"谁是卧底 · 第 {_round} 轮",
            DeadlineAtMs = _stage is Stage.Describe or Stage.Vote or Stage.Reveal or Stage.Deal ? _deadline : null,
        };

        switch (_stage)
        {
            case Stage.Deal:
                view.Subtitle = "发牌中";
                view.Instruction = "记住你的词，有一名（或几名）玩家拿到的词和你不同。";
                break;
            case Stage.Describe:
                view.Subtitle = "描述阶段";
                view.Instruction = "用一句话描述你的词，不要直接说出它。";
                view.AcceptsText = seat is { Alive: true } && string.IsNullOrWhiteSpace(seat.Description);
                view.TextHint = "一句话描述你的词…";
                break;
            case Stage.Vote:
                view.Subtitle = "投票阶段";
                view.Instruction = "选出你认为的卧底。平票则本轮无人出局。";
                break;
            case Stage.Reveal:
                view.Subtitle = "公布结果";
                view.Instruction = "看看这轮投出了谁。";
                break;
            case Stage.End:
                view.Subtitle = "游戏结束";
                view.ResultBanner = _banner;
                view.Instruction = "所有身份已公开。";
                break;
        }

        if (seat is not null)
        {
            view.PrivateNoteLabel = seat.Alive ? "你的词" : "你的词（已出局）";
            view.PrivateNote = seat.Word;
        }

        foreach (var s in _seats)
        {
            var player = room.FindPlayer(s.PlayerId);
            var card = new GamePlayerCard
            {
                Id = s.PlayerId,
                Name = s.Name,
                IsYou = s.PlayerId == viewer.Id,
                IsHost = s.PlayerId == room.HostId,
                IsAlive = s.Alive,
                IsOffline = player is { IsOnline: false },
                Sub = s.Description,
            };

            if (!s.Alive)
            {
                card.Badge = $"出局 · {s.Role}";
                card.Accent = "bad";
            }

            if (_stage == Stage.Reveal && s.Alive)
            {
                var votes = _votes.Values.Count(v => v == s.PlayerId);
                if (votes > 0) card.Votes = votes;
            }

            if (_stage == Stage.End)
            {
                card.Badge = s.Role;
                card.Accent = s.IsUndercover ? "bad" : "good";
                card.Sub = $"「{s.Word}」";
                card.IsAlive = s.Alive;
            }

            view.Players.Add(card);
        }

        if (_stage == Stage.Vote && seat is { Alive: true } && !_votes.ContainsKey(seat.PlayerId))
        {
            foreach (var target in _seats.Where(s => s.Alive && s.PlayerId != seat.PlayerId))
            {
                view.Actions.Add(new GameAction
                {
                    Kind = "vote",
                    Target = target.PlayerId,
                    Label = $"投给 {target.Name}",
                });
            }
        }

        if (_stage == Stage.End && viewer.Id == room.HostId)
        {
            view.Actions.Add(new GameAction { Kind = "lobby", Label = "回到大厅" });
        }

        return view;
    }

    public void OnPlayerDisconnected(Room room, Player player)
    {
        // Free the remaining players from waiting on someone who is gone.
        if (_stage == Stage.Describe && AllConnectedAlive(s => !string.IsNullOrWhiteSpace(s.Description)))
        {
            BeginVote(room, Now, timedOut: false);
        }
        else if (_stage == Stage.Vote && AllConnectedAlive(s => _votes.ContainsKey(s.PlayerId)))
        {
            ResolveVote(room, Now);
        }
    }

    public void OnPlayerLeft(Room room, Player player)
    {
        var seat = FindSeat(player.Id);
        if (seat is null) return;

        if (seat.Alive)
        {
            seat.Alive = false;
            room.Log("system", $"{seat.Name} 离开了游戏，视为出局。");
        }

        OnPlayerDisconnected(room, player);
        room.SyncAll();
    }

    // -- internals ---------------------------------------------------------

    private Seat? FindSeat(string? playerId) =>
        playerId is null ? null : _seats.FirstOrDefault(s => s.PlayerId == playerId);

    private bool AllConnectedAlive(Func<Seat, bool> predicate)
    {
        var connected = _seats
            .Where(s => s.Alive && _room?.FindPlayer(s.PlayerId) is { IsOnline: true })
            .ToList();
        return connected.Count > 0 && connected.All(predicate);
    }

    private void SubmitDescription(Room room, Seat seat, string? text)
    {
        if (_stage != Stage.Describe || !seat.Alive) return;
        if (!string.IsNullOrWhiteSpace(seat.Description)) return;

        var clean = Clean(text, 60);
        if (clean.Length == 0) return;

        // 语义上"和词有没有关系"没法用规则判断，但下面几条能硬性挡住摆烂：
        if (clean.Length < 3)
        {
            Reject(room, seat, "至少 3 个字");
            return;
        }

        if (!clean.Any(char.IsLetter))
        {
            Reject(room, seat, "要写实际内容，别只发符号或数字");
            return;
        }

        if (clean.Contains(seat.Word, StringComparison.Ordinal))
        {
            Reject(room, seat, "不能直接说出自己的词");
            return;
        }

        if (_seats.Any(s => string.Equals(s.Description, clean, StringComparison.Ordinal)))
        {
            Reject(room, seat, "这句话已经有人说过了");
            return;
        }

        seat.Description = clean;
        room.Log("chat", clean, seat.Name);

        if (AllConnectedAlive(s => !string.IsNullOrWhiteSpace(s.Description)))
        {
            BeginVote(room, Now, timedOut: false);
        }
        else
        {
            room.SyncAll();
        }
    }

    /// <summary>每人每轮只提示一次，避免一直发无效内容刷屏。</summary>
    private void Reject(Room room, Seat seat, string reason)
    {
        if (!_rejectAnnounced.Add(seat.PlayerId)) return;
        room.Log("system", $"{seat.Name} 的描述无效：{reason}。");
    }

    private void SubmitVote(Room room, Seat voter, string? targetId)
    {
        if (_stage != Stage.Vote || !voter.Alive) return;
        if (_votes.ContainsKey(voter.PlayerId)) return;
        if (string.IsNullOrEmpty(targetId) || targetId == voter.PlayerId) return;

        var target = FindSeat(targetId);
        if (target is null || !target.Alive) return;

        _votes[voter.PlayerId] = targetId;
        room.Log("system", $"{voter.Name} 已投票。");

        if (AllConnectedAlive(s => _votes.ContainsKey(s.PlayerId)))
        {
            ResolveVote(room, Now);
        }
        else
        {
            room.SyncAll();
        }
    }

    private void BeginDescribe(Room room, long now)
    {
        _stage = Stage.Describe;
        foreach (var s in _seats) s.Description = null;
        _votes.Clear();
        _rejectAnnounced.Clear();
        _deadline = now + DescribeMs;
        room.Log("system", $"第 {_round} 轮 · 描述阶段，每人一句话。");
        room.SyncAll();
    }

    private void BeginVote(Room room, long now, bool timedOut)
    {
        _stage = Stage.Vote;
        _votes.Clear();
        _deadline = now + VoteMs;

        var missing = _seats.Count(s => s.Alive && string.IsNullOrWhiteSpace(s.Description));
        room.Log("system", timedOut && missing > 0
            ? $"描述阶段结束（{missing} 人没来得及说）。开始投票。"
            : "描述阶段结束，开始投票。");
        room.SyncAll();
    }

    private void ResolveVote(Room room, long now)
    {
        _stage = Stage.Reveal;
        _deadline = now + RevealMs;

        var tally = _seats
            .Where(s => s.Alive)
            .Select(s => new { Seat = s, Count = _votes.Values.Count(v => v == s.PlayerId) })
            .OrderByDescending(x => x.Count)
            .ToList();

        var top = tally.Where(x => x.Count > 0).ToList();

        if (top.Count == 0)
        {
            room.Log("system", "没有人投票，本轮无人出局。");
        }
        else if (top.Count > 1 && top[0].Count == top[1].Count)
        {
            room.Log("system", $"平票（各 {top[0].Count} 票），本轮无人出局。");
        }
        else
        {
            var outSeat = top[0].Seat;
            outSeat.Alive = false;
            room.Log("result", $"{outSeat.Name} 被投出局。他的词是「{outSeat.Word}」——他是{outSeat.Role}。");
        }

        room.SyncAll();
    }

    private void NextRoundOrEnd(Room room, long now)
    {
        var alive = _seats.Where(s => s.Alive).ToList();
        var aliveUndercover = alive.Count(s => s.IsUndercover);
        var aliveCivilian = alive.Count - aliveUndercover;

        if (aliveUndercover == 0)
        {
            End(room, "平民胜利", "所有卧底都被找出来了，平民获胜。");
            return;
        }

        if (aliveUndercover >= aliveCivilian)
        {
            End(room, "卧底胜利", $"卧底活到了最后（{aliveUndercover} 名卧底 vs {aliveCivilian} 名平民），卧底获胜。");
            return;
        }

        if (alive.Count <= 2)
        {
            End(room, "卧底胜利", "剩余人数不足以继续投票，卧底获胜。");
            return;
        }

        _round++;
        BeginDescribe(room, now);
    }

    private void End(Room room, string banner, string detail)
    {
        _stage = Stage.End;
        _banner = banner;
        room.Log("result", detail);

        // 阵营式结算：输的那一方全员扣分，出局的人也算在阵营里。
        var civiliansWon = banner.Contains("平民");
        var losers = _seats.Where(s => s.IsUndercover == civiliansWon).Select(s => s.PlayerId).ToList();

        room.Finish(new GameOutcome
        {
            Banner = banner,
            LosingSide = civiliansWon ? "卧底方" : "平民方",
            LosingPlayerIds = losers,
        });

        room.SyncAll();
    }

    private static string Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var trimmed = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
