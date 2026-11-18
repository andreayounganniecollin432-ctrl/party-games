using PartyGames.Protocol;

namespace PartyGames.Server.Games;

/// <summary>
/// 堆雪球：每人滚一个雪球，轮流行动。滚得越大越接近胜利，但也越接近崩掉。
/// 规则是本项目的设计（没有公认玩法），想换规则只需要改这个文件。
/// </summary>
public sealed class SnowballGame : IGameModule
{
    private const int TurnMs = 25_000;
    private const int MaxRounds = 20;
    private const int CollapseAt = 12;
    private const int StartSize = 1;

    public string Id => "snowball";
    public string DisplayName => "堆雪球";
    public string Blurb => "轮流滚自己的雪球：滚雪球 +2、加固 +1、砸人扣对方 3。雪球到 12 会崩，归零出局。";
    public int MinPlayers => 3;
    public int MaxPlayers => 20;

    public bool SupportsScoring => true;

    public ScoreDefaults Scoring => new(Start: 3, Penalty: 1, BigThreshold: 0, BigStart: 2, PenaltyRoll: true);

    private sealed class Seat
    {
        public string PlayerId = string.Empty;
        public string Name = string.Empty;
        public int Size = StartSize;
        public bool Alive = true;
    }

    private readonly List<Seat> _seats = new();
    private int _turn;
    private int _round = 1;
    private long _deadline;
    private bool _finished;
    private string? _banner;

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Start(Room room)
    {
        _seats.Clear();
        _turn = 0;
        _round = 1;
        _finished = false;
        _banner = null;

        foreach (var player in room.Players)
        {
            _seats.Add(new Seat { PlayerId = player.Id, Name = player.Name, Size = StartSize });
        }

        _deadline = Now + TurnMs;
        room.Log("system", $"游戏开始：{_seats.Count} 人。雪球到 {CollapseAt} 会崩，小心。{_seats[0].Name} 先来。");
        room.SyncAll();
    }

    public void Tick(Room room, long nowMs)
    {
        if (_finished || nowMs < _deadline) return;

        var current = CurrentSeat();
        if (current is null) return;

        room.Log("system", $"{current.Name} 超时了，替他滚了一下。");
        ApplyRoll(room, current);
    }

    public void HandleAction(Room room, Player player, ActionRequest request)
    {
        if (_finished) return;

        var current = CurrentSeat();
        if (current is null || current.PlayerId != player.Id) return;

        switch (request.Kind)
        {
            case "roll":
                ApplyRoll(room, current);
                break;
            case "pack":
                ApplyPack(room, current);
                break;
            case "smash":
                ApplySmash(room, current, request.Target);
                break;
        }
    }

    public GameView BuildView(Room room, Player viewer)
    {
        var current = CurrentSeat();
        var mySeat = _seats.FirstOrDefault(s => s.PlayerId == viewer.Id);
        var alive = _seats.Count(s => s.Alive);

        var view = new GameView
        {
            Title = $"堆雪球 · 第 {_round} 轮",
            Subtitle = current is null ? "结束" : $"轮到 {current.Name}",
            DeadlineAtMs = _finished ? null : _deadline,
            Instruction = _finished
                ? "本局结束。"
                : current is not null && current.PlayerId == viewer.Id
                    ? "轮到你了：滚大一点，但别让它崩了。"
                    : $"等 {current?.Name} 出手。",
        };

        if (_finished)
        {
            view.ResultBanner = _banner;
        }

        foreach (var seat in _seats)
        {
            var player = room.FindPlayer(seat.PlayerId);
            var card = new GamePlayerCard
            {
                Id = seat.PlayerId,
                Name = seat.Name,
                IsYou = seat.PlayerId == viewer.Id,
                IsHost = seat.PlayerId == room.HostId,
                IsAlive = seat.Alive,
                IsOffline = player is { IsOnline: false },
                Sub = seat.Alive ? $"雪球 {seat.Size} / {CollapseAt}" : null,
            };

            if (!seat.Alive)
            {
                card.Badge = "出局";
                card.Accent = "bad";
            }
            else if (!_finished && current is not null && seat.PlayerId == current.PlayerId)
            {
                card.Badge = "轮到 TA";
                card.Accent = "warn";
            }
            else if (seat.Size >= CollapseAt - 2)
            {
                card.Accent = "warn";
            }

            view.Players.Add(card);
        }

        if (!_finished && current is not null && current.PlayerId == viewer.Id)
        {
            var canSmash = current.Size > 2;

            view.Actions.Add(new GameAction { Kind = "roll", Label = $"滚雪球 +2（→{current.Size + 2}）" });
            view.Actions.Add(new GameAction
            {
                Kind = "pack",
                Label = $"加固 +1（→{current.Size + 1}）",
                Enabled = current.Size + 1 < CollapseAt,
            });

            if (canSmash)
            {
                foreach (var target in _seats.Where(s => s.Alive && s.PlayerId != current.PlayerId))
                {
                    view.Actions.Add(new GameAction
                    {
                        Kind = "smash",
                        Target = target.PlayerId,
                        Label = $"砸 {target.Name}（耗 2）",
                        Danger = true,
                    });
                }
            }
        }

        if (_finished && viewer.Id == room.HostId)
        {
            view.Actions.Add(new GameAction { Kind = "lobby", Label = "回到大厅" });
        }

        // 让玩家知道自己还剩多少，不至于误判。
        if (mySeat is { Alive: true } && !_finished)
        {
            view.PrivateNoteLabel = "你的雪球";
            view.PrivateNote = $"{mySeat.Size} / {CollapseAt}（{alive} 人还在）";
        }

        return view;
    }

    public void OnPlayerDisconnected(Room room, Player player)
    {
        // 掉线的人轮到时按超时处理，没有额外动作。
    }

    public void OnPlayerLeft(Room room, Player player)
    {
        var seat = _seats.FirstOrDefault(s => s.PlayerId == player.Id);
        if (seat is null) return;

        if (seat.Alive)
        {
            seat.Alive = false;
            room.Log("system", $"{seat.Name} 离开了，退出本局。");
        }

        AdvanceTurnIfNeeded(room);
        room.SyncAll();
    }

    // -- actions ----------------------------------------------------------

    private void ApplyRoll(Room room, Seat seat)
    {
        seat.Size += 2;
        room.Log("system", $"{seat.Name} 滚雪球，雪球变成 {seat.Size}。");
        CheckCollapse(room, seat);
        FinishTurn(room);
    }

    private void ApplyPack(Room room, Seat seat)
    {
        seat.Size += 1;
        room.Log("system", $"{seat.Name} 把雪球压紧加固，变成 {seat.Size}。");
        CheckCollapse(room, seat);
        FinishTurn(room);
    }

    private void ApplySmash(Room room, Seat seat, string? targetId)
    {
        var target = _seats.FirstOrDefault(s => s.PlayerId == targetId);
        if (target is null || !target.Alive || target.PlayerId == seat.PlayerId) return;
        if (seat.Size <= 2) return;

        seat.Size -= 2;
        target.Size -= 3;
        room.Log("result", $"{seat.Name} 把雪球砸向 {target.Name}！{target.Name} 的雪球剩 {Math.Max(target.Size, 0)}，自己剩 {seat.Size}。");

        CheckCollapse(room, seat);
        if (target.Size <= 0 && target.Alive)
        {
            target.Size = 0;
            target.Alive = false;
            room.Log("result", $"{target.Name} 的雪球被打没了，出局。");
        }

        FinishTurn(room);
    }

    private static void CheckCollapse(Room room, Seat seat)
    {
        if (!seat.Alive || seat.Size < CollapseAt) return;

        seat.Alive = false;
        seat.Size = 0;
        room.Log("result", $"{seat.Name} 的雪球滚到太大，塌了！出局。");
    }

    private void FinishTurn(Room room)
    {
        if (AdvanceTurnIfNeeded(room)) return;

        _deadline = Now + TurnMs;
        room.SyncAll();
    }

    private Seat? CurrentSeat() =>
        _turn >= 0 && _turn < _seats.Count && _seats[_turn].Alive ? _seats[_turn] : null;

    private bool AdvanceTurnIfNeeded(Room room)
    {
        var alive = _seats.Where(s => s.Alive).ToList();

        if (alive.Count <= 1)
        {
            var sole = alive.Count == 1 ? alive[0] : null;
            EndMatch(room, sole is null ? "全灭" : $"{sole.Name} 的雪球最稳", sole?.PlayerId);
            return true;
        }

        var next = -1;
        for (var step = 1; step <= _seats.Count; step++)
        {
            var index = (_turn + step) % _seats.Count;
            if (_seats[index].Alive) { next = index; break; }
        }

        if (next < 0) return false;

        // 转了一圈就进入下一轮；超过上限按雪球大小结算。
        if (next <= _turn) _round++;

        _turn = next;

        if (_round > MaxRounds)
        {
            var leader = alive.OrderByDescending(s => s.Size).First();
            EndMatch(room, $"{MaxRounds} 轮结束，{leader.Name} 的雪球最大（{leader.Size}）", leader.PlayerId);
            return true;
        }

        return false;
    }

    private void EndMatch(Room room, string banner, string? winnerId)
    {
        _finished = true;
        _banner = banner;
        room.Log("result", banner);

        // 自由混战：只有赢家不扣分。
        var losers = _seats.Where(s => s.PlayerId != winnerId).Select(s => s.PlayerId).ToList();

        room.Finish(new GameOutcome
        {
            Banner = banner,
            LosingSide = "其他人",
            LosingPlayerIds = losers,
        });

        room.SyncAll();
    }
}
