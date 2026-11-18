using PartyGames.Protocol;

namespace PartyGames.Server.Games;

/// <summary>
/// Russian roulette. A six-chamber cylinder with one round; players pull in turn.
/// The cylinder is re-spun after every death, so the odds never get stale.
/// </summary>
public sealed class RouletteGame : IGameModule
{
    private const int Chambers = 6;
    private const int TurnMs = 30_000;

    public string Id => "roulette";
    public string DisplayName => "俄罗斯轮盘";
    public string Blurb => "轮流扣扳机，六格弹巢里装 1-5 发。运气差的人先出局，活到最后的赢。";
    public int MinPlayers => 3;
    public int MaxPlayers => 20;

    public bool SupportsScoring => true;

    public ScoreDefaults Scoring => new(Start: 3, Penalty: 1, BigThreshold: 0, BigStart: 2, PenaltyRoll: true);

    private sealed class Seat
    {
        public string PlayerId = string.Empty;
        public string Name = string.Empty;
        public bool Alive = true;
    }

    private readonly List<Seat> _seats = new();
    private readonly Random _rng = new();
    private int _turn;
    private bool[] _cylinder = Array.Empty<bool>();
    private int _chamberIndex;
    private int _pullsSinceSpin;
    private int _bullets = 1;
    private long _deadline;
    private string? _banner;
    private bool _finished;

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>House option: how many of the six chambers are loaded (1-5).</summary>
    private static int BulletsOf(Room room) =>
        Math.Clamp(room.Options.GetValueOrDefault("roulette.bullets", 1), 1, Chambers - 1);

    public IReadOnlyList<GameField> LobbyFields(Room room) => new[]
    {
        new GameField
        {
            Key = "roulette.bullets",
            Label = "子弹数",
            Value = BulletsOf(room).ToString(),
            Kind = "int",
            Min = 1,
            Max = Chambers - 1,
        },
    };

    public bool ApplyLobbySetting(Room room, string key, string value)
    {
        if (key != "roulette.bullets" || !int.TryParse(value, out var bullets)) return false;

        room.Options[key] = Math.Clamp(bullets, 1, Chambers - 1);
        return true;
    }

    public void Start(Room room)
    {
        _seats.Clear();
        _turn = 0;
        _finished = false;
        _banner = null;

        foreach (var player in room.Players)
        {
            _seats.Add(new Seat { PlayerId = player.Id, Name = player.Name });
        }

        _bullets = BulletsOf(room);
        Spin();
        _deadline = Now + TurnMs;

        room.Log("system", $"游戏开始：{_seats.Count} 人，六格弹巢装 {_bullets} 发。{_seats[0].Name} 先来。");
        room.SyncAll();
    }

    public void Tick(Room room, long nowMs)
    {
        if (_finished || nowMs < _deadline) return;

        var current = CurrentSeat();
        if (current is null) return;

        room.Log("system", $"{current.Name} 超时了，替他扣一下。");
        Pull(room, current);
    }

    public void HandleAction(Room room, Player player, ActionRequest request)
    {
        if (_finished || request.Kind != "pull") return;

        var current = CurrentSeat();
        if (current is null || current.PlayerId != player.Id) return;

        Pull(room, current);
    }

    public GameView BuildView(Room room, Player viewer)
    {
        var current = CurrentSeat();
        var view = new GameView
        {
            Title = "俄罗斯轮盘",
            Subtitle = current is null ? "结束" : $"轮到 {current.Name}",
            DeadlineAtMs = _finished ? null : _deadline,
        };

        if (_finished)
        {
            view.ResultBanner = _banner;
            view.Instruction = "本局结束。";
        }
        else
        {
            view.Instruction = current is not null && current.PlayerId == viewer.Id
                ? "轮到你了。扣下去，看是空枪还是……"
                : $"等 {current?.Name} 扣扳机。";
        }

        var aliveCount = _seats.Count(s => s.Alive);

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

            view.Players.Add(card);
        }

        if (!_finished && current is not null && current.PlayerId == viewer.Id)
        {
            view.Actions.Add(new GameAction { Kind = "pull", Label = "扣扳机", Danger = true });
        }

        if (_finished && viewer.Id == room.HostId)
        {
            view.Actions.Add(new GameAction { Kind = "lobby", Label = "回到大厅" });
        }

        return view;
    }

    public void OnPlayerDisconnected(Room room, Player player)
    {
        // A disconnected player just times out on their turn; nothing else to do.
    }

    public void OnPlayerLeft(Room room, Player player)
    {
        var seat = _seats.FirstOrDefault(s => s.PlayerId == player.Id);
        if (seat is null) return;

        if (seat.Alive)
        {
            seat.Alive = false;
            room.Log("system", $"{seat.Name} 离开了，视为出局。");
        }

        AdvanceTurnIfNeeded(room);
        room.SyncAll();
    }

    private Seat? CurrentSeat() =>
        _turn >= 0 && _turn < _seats.Count && _seats[_turn].Alive ? _seats[_turn] : null;

    private void Spin()
    {
        // 六格弹巢里随机塞 N 发实弹，然后打乱顺序。
        var chambers = new bool[Chambers];
        for (var i = 0; i < _bullets && i < chambers.Length; i++) chambers[i] = true;

        for (var i = chambers.Length - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (chambers[i], chambers[j]) = (chambers[j], chambers[i]);
        }

        _cylinder = chambers;
        _chamberIndex = 0;
        _pullsSinceSpin = 0;
    }

    private void Pull(Room room, Seat seat)
    {
        var fired = _cylinder[_chamberIndex];
        _chamberIndex++;
        _pullsSinceSpin++;

        if (fired)
        {
            seat.Alive = false;
            room.Log("result", $"砰！{seat.Name} 中弹出局。");
            Spin();
        }
        else
        {
            var left = _cylinder.Skip(_chamberIndex).Count(loaded => loaded);
            room.Log("system", $"咔哒——{seat.Name} 扣下扳机，空枪。（弹巢里还有 {left} 发）");

            // 方案 C：一圈人轮完就重新装填。这样不会出现"最后一格必中"的死板感——
            // 子弹可能刚好在没轮到的那格里，于是这一圈平安过去。
            var alive = _seats.Count(s => s.Alive);
            if (_pullsSinceSpin >= alive)
            {
                Spin();
                room.Log("system", "一圈扣完，重新装填。");
            }
            else if (_chamberIndex >= Chambers)
            {
                Spin();
            }
        }

        if (AdvanceTurnIfNeeded(room)) return;

        _deadline = Now + TurnMs;
        room.SyncAll();
    }

    /// <summary>Moves the turn forward and ends the game if only one player is left.</summary>
    private bool AdvanceTurnIfNeeded(Room room)
    {
        var alive = _seats.Where(s => s.Alive).ToList();

        if (alive.Count <= 1)
        {
            _finished = true;
            _banner = alive.Count == 1 ? $"{alive[0].Name} 活到了最后" : "全灭";
            room.Log("result", _banner);

            // 自由混战：只有活到最后的那个不扣分，其余全算输。
            var winnerId = alive.Count == 1 ? alive[0].PlayerId : null;
            var losers = _seats.Where(s => s.PlayerId != winnerId).Select(s => s.PlayerId).ToList();

            room.Finish(new GameOutcome
            {
                Banner = _banner!,
                LosingSide = "其他人",
                LosingPlayerIds = losers,
            });

            room.SyncAll();
            return true;
        }

        for (var step = 1; step <= _seats.Count; step++)
        {
            var index = (_turn + step) % _seats.Count;
            if (!_seats[index].Alive) continue;

            _turn = index;
            return false;
        }

        return false;
    }
}
