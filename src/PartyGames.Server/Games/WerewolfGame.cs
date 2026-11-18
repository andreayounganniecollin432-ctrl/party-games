using PartyGames.Protocol;

namespace PartyGames.Server.Games;

/// <summary>
/// 狼人杀。MVP 版角色：狼人、村民、预言家、女巫。
/// 夜晚分两段（狼人+预言家同时行动，然后女巫单独行动），白天讨论后投票。
/// 死亡不公开身份，只有出局者自己知道。
/// </summary>
public sealed class WerewolfGame : IGameModule
{
    private const int WolfMs = 40_000;
    private const int WitchMs = 25_000;
    private const int DawnMs = 5_000;
    private const int DiscussMs = 90_000;
    private const int VoteMs = 45_000;
    private const int ResultMs = 6_000;
    private const int HunterMs = 25_000;

    public string Id => "werewolf";
    public string DisplayName => "狼人杀";
    public string Blurb => "狼人夜里杀人，白天全体投票放逐。找出所有狼人，或者让狼人活到人数占优。";
    public int MinPlayers => 6;
    public int MaxPlayers => 20;

    public bool SupportsScoring => true;

    /// <summary>超过 8 人时起始分变 2，这是房主说的规则；其余情况 3 分。归零时摇点受罚。</summary>
    public ScoreDefaults Scoring => new(Start: 3, Penalty: 1, BigThreshold: 8, BigStart: 2, PenaltyRoll: true);

    private enum Role { Villager, Wolf, Seer, Witch, Hunter }

    private enum Phase { Wolf, Witch, Dawn, Discuss, Vote, Result, Hunter, End }

    private sealed class Seat
    {
        public string PlayerId = string.Empty;
        public string Name = string.Empty;
        public Role Role;
        public bool Alive = true;

        public string RoleName => Role switch
        {
            Role.Wolf => "狼人",
            Role.Seer => "预言家",
            Role.Witch => "女巫",
            Role.Hunter => "猎人",
            _ => "平民",
        };
    }

    private readonly List<Seat> _seats = new();
    private readonly Dictionary<string, string> _wolfVotes = new();
    private readonly Dictionary<string, string> _votes = new();
    private readonly HashSet<string> _acted = new();
    private readonly List<string> _seerHistory = new();
    private readonly Random _rng = new();

    private Phase _phase = Phase.Wolf;
    private int _night = 1;
    private long _deadline;
    private string? _killTarget;
    private bool _witchSave;
    private string? _witchPoison;
    private bool _witchHasAntidote = true;
    private bool _witchHasPoison = true;
    private bool _finished;
    private string? _banner;
    private string _dawnSummary = string.Empty;

    // 猎人开枪：出局后还能带走一个人，所以单独开一个阶段等他选。
    private string? _hunterShooter;
    private bool _hunterAfterDawn;

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>房主可调：狼人数量、是否启用预言家 / 女巫。</summary>
    public IReadOnlyList<GameField> LobbyFields(Room room)
    {
        var players = Math.Max(room.Players.Count, 1);

        return new[]
        {
            new GameField
            {
                Key = "werewolf.wolves",
                Label = "狼人数",
                Value = WolvesOf(room, players).ToString(),
                Kind = "int",
                Min = 1,
                Max = Math.Max(1, room.MaxPlayers - 1),
            },
            new GameField
            {
                Key = "werewolf.seer",
                Label = "预言家",
                Value = SeerOf(room) ? "1" : "0",
                Kind = "bool",
            },
            new GameField
            {
                Key = "werewolf.witch",
                Label = "女巫",
                Value = WitchOf(room) ? "1" : "0",
                Kind = "bool",
            },
            new GameField
            {
                Key = "werewolf.hunter",
                Label = "猎人",
                Value = HunterOf(room) ? "1" : "0",
                Kind = "bool",
            },
        };
    }

    public bool ApplyLobbySetting(Room room, string key, string value)
    {
        if (key == "werewolf.wolves")
        {
            if (!int.TryParse(value, out var wolves)) return false;

            // 这里不能按"当前房间里的人数"钳制：设置通常是在只有房主一个人时调的，
            // 那时候 clamp 到 players-1 会把 5 狼直接压成 1 狼。真正的上限留到开局再算。
            room.Options[key] = Math.Clamp(wolves, 1, 19);
            return true;
        }

        if (key is "werewolf.seer" or "werewolf.witch" or "werewolf.hunter")
        {
            room.Options[key] = value is "1" or "true" ? 1 : 0;
            return true;
        }

        return false;
    }

    private static int DefaultWolves(int count) => count switch
    {
        <= 7 => 2,
        <= 10 => 3,
        <= 14 => 4,
        _ => 5,
    };

    private static int WolvesOf(Room room, int players)
    {
        var value = room.Options.GetValueOrDefault("werewolf.wolves", DefaultWolves(players));
        return Math.Clamp(value, 1, Math.Max(1, players - 1));
    }

    private static bool SeerOf(Room room) => room.Options.GetValueOrDefault("werewolf.seer", 1) != 0;

    private static bool WitchOf(Room room) => room.Options.GetValueOrDefault("werewolf.witch", 1) != 0;

    private static bool HunterOf(Room room) => room.Options.GetValueOrDefault("werewolf.hunter", 1) != 0;

    public void Start(Room room)
    {
        _seats.Clear();
        _wolfVotes.Clear();
        _votes.Clear();
        _acted.Clear();
        _seerHistory.Clear();
        _night = 1;
        _finished = false;
        _banner = null;
        _witchHasAntidote = true;
        _witchHasPoison = true;

        var players = room.Players.ToList();
        var count = players.Count;

        var useSeer = SeerOf(room);
        var useWitch = WitchOf(room);
        var useHunter = HunterOf(room);
        var specials = (useSeer ? 1 : 0) + (useWitch ? 1 : 0) + (useHunter ? 1 : 0);

        // 至少要留一个平民，否则好人阵营只剩神职；神职数量也不能超过总人数。
        var wolfCount = Math.Clamp(WolvesOf(room, count), 1, Math.Max(1, count - specials));

        var shuffled = players.OrderBy(_ => _rng.Next()).ToList();

        // 先排好神职顺序，再按顺序发；用下标取，避免上一个分支自增后又命中自己。
        var specialsOrder = new List<Role>();
        if (useSeer) specialsOrder.Add(Role.Seer);
        if (useWitch) specialsOrder.Add(Role.Witch);
        if (useHunter) specialsOrder.Add(Role.Hunter);

        for (var i = 0; i < shuffled.Count; i++)
        {
            Role role;

            if (i < wolfCount)
            {
                role = Role.Wolf;
            }
            else
            {
                var index = i - wolfCount;
                role = index < specialsOrder.Count ? specialsOrder[index] : Role.Villager;
            }

            _seats.Add(new Seat
            {
                PlayerId = shuffled[i].Id,
                Name = shuffled[i].Name,
                Role = role,
            });
        }

        var composition = $"{wolfCount} 狼人";
        if (useSeer) composition += " / 1 预言家";
        if (useWitch) composition += " / 1 女巫";
        if (useHunter) composition += " / 1 猎人";
        composition += $" / {count - wolfCount - specials} 平民";

        room.Log("system", $"游戏开始：{count} 人，{composition}。天黑请闭眼。");
        BeginNight(room);
    }

    public void Tick(Room room, long nowMs)
    {
        if (_finished || nowMs < _deadline) return;

        switch (_phase)
        {
            case Phase.Wolf:
                room.Log("system", "狼人和预言家行动超时。");
                StartWitchPhase(room);
                break;
            case Phase.Witch:
                room.Log("system", "女巫没有行动。");
                StartDawn(room);
                break;
            case Phase.Dawn:
                StartDiscuss(room);
                break;
            case Phase.Discuss:
                StartVote(room);
                break;
            case Phase.Vote:
                ResolveVote(room);
                break;
            case Phase.Result:
                NextNight(room);
                break;
            case Phase.Hunter:
                ResolveHunter(room, null);
                break;
        }
    }

    public void HandleAction(Room room, Player player, ActionRequest request)
    {
        var seat = _seats.FirstOrDefault(s => s.PlayerId == player.Id);
        if (seat is null || _finished) return;

        // 猎人出局后仍然要能在自己的开枪阶段操作。
        var hunterActing = _phase == Phase.Hunter && seat.PlayerId == _hunterShooter;
        if (hunterActing)
        {
            if (request.Kind == "shoot") ResolveHunter(room, request.Target);
            return;
        }

        if (!seat.Alive) return;

        switch (_phase)
        {
            case Phase.Wolf:
                HandleNightAction(room, seat, request);
                break;
            case Phase.Witch:
                HandleWitchAction(room, seat, request);
                break;
            case Phase.Discuss:
                if (request.Kind == "say")
                {
                    var text = Clean(request.Text, 100);
                    if (text.Length == 0) return;
                    room.Log("chat", text, seat.Name);
                }
                else if (request.Kind == "pass")
                {
                    _acted.Add(seat.PlayerId);
                    TryAdvanceDiscuss(room);
                }
                break;
            case Phase.Vote:
                HandleVote(room, seat, request);
                break;
        }
    }

    public GameView BuildView(Room room, Player viewer)
    {
        var me = _seats.FirstOrDefault(s => s.PlayerId == viewer.Id);
        var aliveWolves = _seats.Count(s => s is { Alive: true, Role: Role.Wolf });
        var aliveOthers = _seats.Count(s => s.Alive) - aliveWolves;

        var view = new GameView
        {
            Title = $"狼人杀 · {(IsNight ? $"第 {_night} 夜" : $"第 {_night} 天")}",
            Subtitle = PhaseName,
            DeadlineAtMs = _finished ? null : _deadline,
        };

        if (_finished)
        {
            view.ResultBanner = _banner;
            view.Instruction = "所有身份已公开。";
        }
        else
        {
            view.Instruction = Instruction(me);
        }

        if (me is not null)
        {
            view.PrivateNoteLabel = "你的身份";
            view.PrivateNote = PrivateNote(me);
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
            };

            if (!seat.Alive)
            {
                card.Badge = "出局";
                card.Accent = "bad";
            }
            else if (_phase == Phase.Vote && _votes.ContainsKey(seat.PlayerId))
            {
                card.Accent = "warn";
            }

            if (_phase == Phase.Result && seat.Alive)
            {
                var votes = _votes.Values.Count(v => v == seat.PlayerId);
                if (votes > 0) card.Votes = votes;
            }

            if (_finished)
            {
                card.Badge = seat.RoleName;
                card.Accent = seat.Role == Role.Wolf ? "bad" : "good";
            }

            view.Players.Add(card);
        }

        AddActions(view, me, aliveWolves, aliveOthers);
        return view;
    }

    public void OnPlayerDisconnected(Room room, Player player)
    {
        // 掉线玩家不阻塞阶段推进：行动判定只看在线的人。
        if (_phase == Phase.Wolf) TryAdvanceWolf(room);
        else if (_phase == Phase.Witch) TryAdvanceWitch(room);
        else if (_phase == Phase.Discuss) TryAdvanceDiscuss(room);
        else if (_phase == Phase.Vote) TryAdvanceVote(room);
    }

    public void OnPlayerLeft(Room room, Player player)
    {
        var seat = _seats.FirstOrDefault(s => s.PlayerId == player.Id);
        if (seat is null) return;

        if (seat.Alive)
        {
            seat.Alive = false;
            room.Log("system", $"{seat.Name} 离开了游戏，视为出局。");
        }

        CheckWin(room);
        room.SyncAll();
    }

    // -- phases -----------------------------------------------------------

    private bool IsNight => _phase is Phase.Wolf or Phase.Witch;

    private string PhaseName => _phase switch
    {
        Phase.Wolf => "夜晚 · 狼人行动",
        Phase.Witch => "夜晚 · 女巫行动",
        Phase.Dawn => "天亮了",
        Phase.Discuss => "白天 · 发言",
        Phase.Vote => "白天 · 投票",
        Phase.Result => "投票结果",
        Phase.Hunter => "猎人开枪",
        _ => "结束",
    };

    private void BeginNight(Room room)
    {
        _phase = Phase.Wolf;
        _wolfVotes.Clear();
        _votes.Clear();
        _acted.Clear();
        _killTarget = null;
        _witchSave = false;
        _witchPoison = null;
        _dawnSummary = string.Empty;
        _deadline = Now + WolfMs;

        room.Log("system", $"第 {_night} 夜，天黑请闭眼。狼人请睁眼，选择今晚要杀的人。");
        room.SyncAll();
    }

    private void StartWitchPhase(Room room)
    {
        _killTarget = PickWolfVictim();
        _phase = Phase.Witch;
        _acted.Clear();
        _deadline = Now + WitchMs;

        var witch = _seats.FirstOrDefault(s => s is { Alive: true, Role: Role.Witch });
        room.Log("system", "狼人闭眼。女巫请睁眼。");

        if (witch is null || (!_witchHasAntidote && !_witchHasPoison))
        {
            StartDawn(room);
            return;
        }

        room.SyncAll();
    }

    private void StartDawn(Room room)
    {
        _phase = Phase.Dawn;
        _acted.Clear();
        _deadline = Now + DawnMs;

        var dead = new List<Seat>();

        var victim = _seats.FirstOrDefault(s => s.PlayerId == _killTarget && s.Alive);
        if (victim is not null && !_witchSave)
        {
            victim.Alive = false;
            dead.Add(victim);
        }

        var poisoned = _seats.FirstOrDefault(s => s.PlayerId == _witchPoison && s.Alive);
        if (poisoned is not null)
        {
            poisoned.Alive = false;
            dead.Add(poisoned);
        }

        _dawnSummary = dead.Count == 0
            ? "天亮了。昨晚是平安夜，没有人出局。"
            : $"天亮了。昨晚 {string.Join("、", dead.Select(d => d.Name))} 出局了。";

        room.Log("result", _dawnSummary);

        // 猎人被狼刀可以开枪；被女巫毒死不能开枪（标准规则）。
        var hunter = dead.FirstOrDefault(d => d.Role == Role.Hunter && d.PlayerId != _witchPoison);
        if (hunter is not null)
        {
            StartHunterPhase(room, hunter, afterDawn: true);
            return;
        }

        if (CheckWin(room)) return;

        room.SyncAll();
    }

    private void StartHunterPhase(Room room, Seat hunter, bool afterDawn)
    {
        _phase = Phase.Hunter;
        _hunterShooter = hunter.PlayerId;
        _hunterAfterDawn = afterDawn;
        _deadline = Now + HunterMs;

        room.Log("result", $"{hunter.Name} 是猎人，可以开枪带走一个人。");
        room.SyncAll();
    }

    private void ResolveHunter(Room room, string? targetId)
    {
        var shooter = _seats.FirstOrDefault(s => s.PlayerId == _hunterShooter);
        _hunterShooter = null;

        Seat? victim = null;

        if (!string.IsNullOrEmpty(targetId))
        {
            victim = _seats.FirstOrDefault(s => s.PlayerId == targetId && s.Alive);
        }

        if (victim is not null)
        {
            victim.Alive = false;
            room.Log("result", $"{shooter?.Name} 开枪带走了 {victim.Name}。");
        }
        else
        {
            room.Log("result", "猎人没有开枪。");
        }

        if (CheckWin(room)) return;

        if (_hunterAfterDawn) StartDiscuss(room);
        else NextNight(room);
    }

    private void StartDiscuss(Room room)
    {
        _phase = Phase.Discuss;
        _acted.Clear();
        _deadline = Now + DiscussMs;

        room.Log("system", "开始讨论。发言结束后点「过」；所有人都过了会提前进入投票。");
        room.SyncAll();
    }

    private void StartVote(Room room)
    {
        _phase = Phase.Vote;
        _votes.Clear();
        _deadline = Now + VoteMs;

        room.Log("system", "讨论结束，开始投票。平票则今天无人出局。");
        room.SyncAll();
    }

    private void ResolveVote(Room room)
    {
        _phase = Phase.Result;
        _deadline = Now + ResultMs;

        var tally = _seats
            .Where(s => s.Alive)
            .Select(s => new { Seat = s, Count = _votes.Values.Count(v => v == s.PlayerId) })
            .OrderByDescending(x => x.Count)
            .ToList();

        var top = tally.Where(x => x.Count > 0).ToList();

        if (top.Count == 0)
        {
            room.Log("result", "没有人投票，今天无人出局。");
        }
        else if (top.Count > 1 && top[0].Count == top[1].Count)
        {
            room.Log("result", $"平票（各 {top[0].Count} 票），今天无人出局。");
        }
        else
        {
            var outSeat = top[0].Seat;
            outSeat.Alive = false;
            room.Log("result", $"{outSeat.Name} 被投出局（{top[0].Count} 票）。");

            if (outSeat.Role == Role.Hunter)
            {
                StartHunterPhase(room, outSeat, afterDawn: false);
                return;
            }
        }

        if (CheckWin(room)) return;

        room.SyncAll();
    }

    private void NextNight(Room room)
    {
        _night++;
        BeginNight(room);
    }

    // -- actions ----------------------------------------------------------

    private void HandleNightAction(Room room, Seat seat, ActionRequest request)
    {
        if (seat.Role == Role.Wolf && request.Kind == "kill")
        {
            var target = _seats.FirstOrDefault(s => s.PlayerId == request.Target && s.Alive && s.Role != Role.Wolf);
            if (target is null) return;

            _wolfVotes[seat.PlayerId] = target.PlayerId;
            _acted.Add(seat.PlayerId);
            room.Log("system", $"{seat.Name} 已选择目标。");
            TryAdvanceWolf(room);
            return;
        }

        if (seat.Role == Role.Seer && request.Kind == "check")
        {
            var target = _seats.FirstOrDefault(s => s.PlayerId == request.Target && s.Alive && s.PlayerId != seat.PlayerId);
            if (target is null) return;

            _acted.Add(seat.PlayerId);
            var result = target.Role == Role.Wolf ? "狼人" : "好人";
            _seerHistory.Add($"第 {_night} 夜：{target.Name} → {result}");

            // 结果只回给预言家自己。
            room.Sync(room.FindPlayer(seat.PlayerId)!);
            room.Log("system", "预言家已查验。");
            TryAdvanceWolf(room);
        }
    }

    private void HandleWitchAction(Room room, Seat seat, ActionRequest request)
    {
        if (seat.Role != Role.Witch) return;

        switch (request.Kind)
        {
            case "save":
                if (!_witchHasAntidote) return;
                _witchHasAntidote = false;
                _witchSave = true;
                _acted.Add(seat.PlayerId);
                room.Log("system", "女巫用了解药。");
                StartDawn(room);
                return;

            case "poison":
                if (!_witchHasPoison) return;
                var target = _seats.FirstOrDefault(s => s.PlayerId == request.Target && s.Alive);
                if (target is null) return;

                _witchHasPoison = false;
                _witchPoison = target.PlayerId;
                _acted.Add(seat.PlayerId);
                room.Log("system", "女巫用了毒药。");
                StartDawn(room);
                return;

            case "none":
                _acted.Add(seat.PlayerId);
                room.Log("system", "女巫没有用药。");
                StartDawn(room);
                return;
        }
    }

    private void HandleVote(Room room, Seat seat, ActionRequest request)
    {
        if (request.Kind != "vote") return;

        var target = _seats.FirstOrDefault(s => s.PlayerId == request.Target && s.Alive && s.PlayerId != seat.PlayerId);
        if (target is null || _votes.ContainsKey(seat.PlayerId)) return;

        _votes[seat.PlayerId] = target.PlayerId;
        room.Log("system", $"{seat.Name} 已投票。");
        TryAdvanceVote(room);
    }

    // -- advancement ------------------------------------------------------

    private void TryAdvanceWolf(Room room)
    {
        if (_phase != Phase.Wolf) return;

        var actors = _seats
            .Where(s => s.Alive && s.Role is Role.Wolf or Role.Seer)
            .Where(s => room.FindPlayer(s.PlayerId) is { IsOnline: true })
            .ToList();

        var allDone = actors.All(s => s.Role == Role.Seer
            ? _acted.Contains(s.PlayerId)
            : _wolfVotes.ContainsKey(s.PlayerId));

        if (allDone)
        {
            StartWitchPhase(room);
            return;
        }

        room.SyncAll();
    }

    private void TryAdvanceWitch(Room room)
    {
        if (_phase != Phase.Witch) return;

        var witch = _seats.FirstOrDefault(s => s is { Alive: true, Role: Role.Witch });
        if (witch is null || _acted.Contains(witch.PlayerId))
        {
            StartDawn(room);
            return;
        }

        if (room.FindPlayer(witch.PlayerId) is not { IsOnline: true })
        {
            StartDawn(room);
            return;
        }

        room.SyncAll();
    }

    private void TryAdvanceDiscuss(Room room)
    {
        if (_phase != Phase.Discuss) return;

        var alive = _seats.Where(s => s.Alive).ToList();
        var connected = alive.Where(s => room.FindPlayer(s.PlayerId) is { IsOnline: true }).ToList();

        if (connected.Count > 0 && connected.All(s => _acted.Contains(s.PlayerId)))
        {
            StartVote(room);
            return;
        }

        room.SyncAll();
    }

    private void TryAdvanceVote(Room room)
    {
        if (_phase != Phase.Vote) return;

        var alive = _seats.Where(s => s.Alive).ToList();
        var connected = alive.Where(s => room.FindPlayer(s.PlayerId) is { IsOnline: true }).ToList();

        if (connected.Count > 0 && connected.All(s => _votes.ContainsKey(s.PlayerId)))
        {
            ResolveVote(room);
            return;
        }

        room.SyncAll();
    }

    private string? PickWolfVictim()
    {
        if (_wolfVotes.Count == 0) return null;

        var tally = _wolfVotes.Values
            .GroupBy(t => t)
            .Select(g => new { Target = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToList();

        var top = tally.Where(t => t.Count == tally[0].Count).ToList();
        return top[_rng.Next(top.Count)].Target;
    }

    private bool CheckWin(Room room)
    {
        var wolves = _seats.Count(s => s is { Alive: true, Role: Role.Wolf });
        var others = _seats.Count(s => s.Alive) - wolves;

        if (wolves == 0)
        {
            EndMatch(room, "好人阵营胜利", "所有狼人都出局了。");
            return true;
        }

        if (wolves >= others)
        {
            EndMatch(room, "狼人阵营胜利", $"狼人已占多数（{wolves} 狼 vs {others} 好人）。");
            return true;
        }

        return false;
    }

    private void EndMatch(Room room, string banner, string detail)
    {
        _finished = true;
        _phase = Phase.End;
        _banner = banner;
        room.Log("result", detail);

        // 阵营式结算：狼人赢则所有好人扣分，反之所有狼人扣分。
        var goodWon = banner.Contains("好人");
        var losers = _seats.Where(s => (s.Role == Role.Wolf) == goodWon).Select(s => s.PlayerId).ToList();

        room.Finish(new GameOutcome
        {
            Banner = banner,
            LosingSide = goodWon ? "狼人阵营" : "好人阵营",
            LosingPlayerIds = losers,
        });

        room.SyncAll();
    }

    // -- view helpers -----------------------------------------------------

    private string Instruction(Seat? me)
    {
        if (me is null) return string.Empty;

        return _phase switch
        {
            Phase.Wolf => me.Role switch
            {
                Role.Wolf => "选择今晚要杀的人。",
                Role.Seer => "选择要查验的人。",
                _ => "天黑请闭眼，等待天亮。",
            },
            Phase.Witch => me.Role == Role.Witch
                ? "选择救、毒，或者什么都不做。"
                : "天黑请闭眼，等待天亮。",
            Phase.Dawn => _dawnSummary,
            Phase.Hunter => me.PlayerId == _hunterShooter
                ? "你出局了，开枪带走一个人。"
                : "猎人正在决定开枪目标…",
            Phase.Discuss => "轮流发言，讨论谁是狼人。说完点「过」。",
            Phase.Vote => "选出你要放逐的人。平票则无人出局。",
            Phase.Result => "看看投票结果。",
            _ => string.Empty,
        };
    }

    private string PrivateNote(Seat me)
    {
        var text = me.Role switch
        {
            Role.Wolf => $"狼人。队友：{TeamList(me)}",
            Role.Seer => $"预言家。{(_seerHistory.Count == 0 ? "还没有查验记录。" : "查验记录：" + string.Join("；", _seerHistory))}",
            Role.Witch => $"女巫。解药：{(_witchHasAntidote ? "可用" : "已用")}；毒药：{(_witchHasPoison ? "可用" : "已用")}",
            Role.Hunter => "猎人。出局后可以开枪带走一个人（被女巫毒死则不能）。",
            _ => "平民。",
        };

        if (_phase == Phase.Witch && me.Role == Role.Witch)
        {
            var victim = _seats.FirstOrDefault(s => s.PlayerId == _killTarget);
            text += victim is null ? "；今晚没有人被袭击。" : $"；今晚 {victim.Name} 被袭击了。";
        }

        if (!me.Alive) text = $"（已出局，{me.RoleName}）" + text;

        return text;
    }

    private string TeamList(Seat me)
    {
        var mates = _seats.Where(s => s.Role == Role.Wolf && s.PlayerId != me.PlayerId).Select(s => s.Name).ToList();
        return mates.Count == 0 ? "只有你一个" : string.Join("、", mates);
    }

    private void AddActions(GameView view, Seat? me, int aliveWolves, int aliveOthers)
    {
        if (me is null || _finished) return;

        // 猎人的开枪阶段：他自己虽然已出局，仍然要能看到目标按钮。
        if (_phase == Phase.Hunter)
        {
            if (me.PlayerId == _hunterShooter)
            {
                foreach (var target in _seats.Where(s => s.Alive && s.PlayerId != me.PlayerId))
                {
                    view.Actions.Add(new GameAction { Kind = "shoot", Target = target.PlayerId, Label = $"开枪打 {target.Name}", Danger = true });
                }
            }

            return;
        }

        if (!me.Alive) return;

        if (_phase == Phase.Wolf)
        {
            if (me.Role == Role.Wolf)
            {
                foreach (var target in _seats.Where(s => s.Alive && s.Role != Role.Wolf && !_wolfVotes.ContainsKey(me.PlayerId)))
                {
                    view.Actions.Add(new GameAction { Kind = "kill", Target = target.PlayerId, Label = $"杀 {target.Name}", Danger = true });
                }
            }
            else if (me.Role == Role.Seer && !_acted.Contains(me.PlayerId))
            {
                foreach (var target in _seats.Where(s => s.Alive && s.PlayerId != me.PlayerId))
                {
                    view.Actions.Add(new GameAction { Kind = "check", Target = target.PlayerId, Label = $"查验 {target.Name}" });
                }
            }

            return;
        }

        if (_phase == Phase.Witch && me.Role == Role.Witch && !_acted.Contains(me.PlayerId))
        {
            var victim = _seats.FirstOrDefault(s => s.PlayerId == _killTarget && s.Alive);

            if (_witchHasAntidote && victim is not null)
            {
                view.Actions.Add(new GameAction { Kind = "save", Label = $"救 {victim.Name}" });
            }

            if (_witchHasPoison)
            {
                foreach (var target in _seats.Where(s => s.Alive && s.PlayerId != me.PlayerId && s.PlayerId != _killTarget))
                {
                    view.Actions.Add(new GameAction { Kind = "poison", Target = target.PlayerId, Label = $"毒 {target.Name}", Danger = true });
                }
            }

            view.Actions.Add(new GameAction { Kind = "none", Label = "不用药" });
            return;
        }

        if (_phase == Phase.Discuss)
        {
            view.AcceptsText = me.Alive;
            view.TextHint = "说点什么…";
            view.TextActionKind = "say";

            if (!_acted.Contains(me.PlayerId))
            {
                view.Actions.Add(new GameAction { Kind = "pass", Label = "过" });
            }

            return;
        }

        if (_phase == Phase.Vote && !_votes.ContainsKey(me.PlayerId))
        {
            foreach (var target in _seats.Where(s => s.Alive && s.PlayerId != me.PlayerId))
            {
                view.Actions.Add(new GameAction { Kind = "vote", Target = target.PlayerId, Label = $"投 {target.Name}" });
            }
        }
    }

    private static string Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var trimmed = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
