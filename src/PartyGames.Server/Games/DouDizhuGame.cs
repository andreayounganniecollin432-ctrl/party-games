using PartyGames.Protocol;

namespace PartyGames.Server.Games;

/// <summary>
/// 斗地主：3 人、54 张牌、叫地主、出牌压制。
/// 出牌用文字输入（如 "334455" 或 "3 4 5 6 7"），和词语接龙一样走文本框，
/// 这样插件端不需要任何改动就能玩。
/// 每次出牌都会写一条 result 日志，插件的语音播报会自动念出来。
/// </summary>
public sealed class DouDizhuGame : IGameModule
{
    private const int HandSize = 17;
    private const int TurnMs = 45_000;
    private const int BidMs = 20_000;

    public string Id => "doudizhu";
    public string DisplayName => "斗地主";
    public string Blurb => "一副牌三个人，叫了地主就得一对二。先出完手牌的一方赢，炸弹和王炸都翻倍。";
    public int MinPlayers => 3;
    public int MaxPlayers => 3;

    public bool SupportsScoring => true;

    // 每个农民输 1 分，地主输 2 分（输赢对称由房间统一结算）
    public ScoreDefaults Scoring => new(Start: 5, Penalty: 1, BigThreshold: 0, BigStart: 5, PenaltyRoll: false);

    private sealed class Seat
    {
        public string PlayerId = string.Empty;
        public string Name = string.Empty;
        public List<int> Hand = new();
    }

    private readonly List<Seat> _seats = new();
    private readonly Random _rng = new();
    private List<int> _bottom = new();

    private int _landlord = -1;
    private int _turn;
    private int _bidTurn;
    private int _multi;              // 倍数：炸弹翻倍
    private bool _bidding = true;
    private bool _finished;
    private string? _banner;
    private long _deadline;

    private Combo? _lastCombo;       // 当前需要压过的牌
    private int _lastOwner = -1;

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ---------- 牌面 ----------

    private static string RankName(int rank) => rank switch
    {
        0 => "3", 1 => "4", 2 => "5", 3 => "6", 4 => "7", 5 => "8", 6 => "9",
        7 => "10", 8 => "J", 9 => "Q", 10 => "K", 11 => "A", 12 => "2",
        13 => "小王", _ => "大王",
    };

    private static readonly string[] Suits = { "♠", "♥", "♣", "♦" };

    private static string CardName(int card) => card switch
    {
        52 => "小王",
        53 => "大王",
        _ => RankName(card % 13) + Suits[card / 13],
    };

    private static int RankOf(int card) => card switch
    {
        52 => 13,
        53 => 14,
        _ => card % 13,
    };

    private static string HandText(IEnumerable<int> cards) =>
        string.Join(" ", cards.OrderBy(RankOf).ThenBy(c => c).Select(CardName));

    // ---------- 牌型 ----------

    private sealed class Combo
    {
        public string Type = string.Empty;
        public int Rank = -1;      // 主牌大小
        public int Length;         // 顺子/连对的长度
        public int Count;

        public bool IsBomb => Type is "bomb" or "rocket";
    }

    private static bool TryParse(string? text, out List<int> ranks, out string error)
    {
        ranks = new List<int>();
        error = string.Empty;

        var raw = (text ?? string.Empty)
            .Replace(" ", string.Empty)
            .Replace(",", string.Empty)
            .Replace("，", string.Empty)
            .Replace("\t", string.Empty)
            .Replace("♠", string.Empty).Replace("♥", string.Empty)
            .Replace("♣", string.Empty).Replace("♦", string.Empty)
            .Trim();

        if (raw.Length == 0)
        {
            error = "先选牌，比如 334455";
            return false;
        }

        // 支持 "小王"/"大王"/"10" 等写法：从左到右贪心匹配
        var i = 0;
        while (i < raw.Length)
        {
            if (i + 1 < raw.Length && raw.Substring(i, 2) == "小王") { ranks.Add(13); i += 2; continue; }
            if (i + 1 < raw.Length && raw.Substring(i, 2) == "大王") { ranks.Add(14); i += 2; continue; }

            var ch = raw[i];
            switch (ch)
            {
                case '3': case '4': case '5': case '6': case '7':
                case '8': case '9':
                    ranks.Add(ch - '3');
                    i++;
                    continue;
                case '1' when i + 1 < raw.Length && raw[i + 1] == '0':
                    ranks.Add(7); i += 2; continue;
                case 'J': case 'j': ranks.Add(8); i++; continue;
                case 'Q': case 'q': ranks.Add(9); i++; continue;
                case 'K': case 'k': ranks.Add(10); i++; continue;
                case 'A': case 'a': ranks.Add(11); i++; continue;
                case '2': ranks.Add(12); i++; continue;
                case 'X': case 'x': ranks.Add(13); i++; continue;
                case 'D': case 'd': ranks.Add(14); i++; continue;
                default:
                    error = $"看不懂这张牌：{ch}";
                    return false;
            }
        }

        return true;
    }

    private static Combo? Detect(List<int> ranks)
    {
        if (ranks.Count == 0) return null;

        var groups = ranks.GroupBy(r => r).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).ToList();
        var groupsByRank = ranks.GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count());
        var distinct = groupsByRank.Keys.OrderBy(k => k).ToList();

        // 王炸
        if (ranks.Count == 2 && groupsByRank.ContainsKey(13) && groupsByRank.ContainsKey(14))
            return new Combo { Type = "rocket", Rank = 99, Length = 1, Count = 2 };

        // 炸弹
        if (ranks.Count == 4 && groups.Count == 1)
            return new Combo { Type = "bomb", Rank = groups[0].Key, Length = 1, Count = 4 };

        // 单张 / 对子 / 三张
        if (groups.Count == 1)
        {
            var type = ranks.Count switch { 1 => "single", 2 => "pair", 3 => "triple", _ => "" };
            if (type.Length > 0)
                return new Combo { Type = type, Rank = groups[0].Key, Length = 1, Count = ranks.Count };
        }

        // 三带一 / 三带二
        if (groups.Count == 2)
        {
            var triple = groups.FirstOrDefault(g => g.Count() == 3);
            var kicker = groups.FirstOrDefault(g => g.Count() != 3);
            if (triple != null && kicker != null)
            {
                var kick = kicker.Count();
                if (kick == 1 || kick == 2)
                    return new Combo
                    {
                        Type = kick == 1 ? "triple1" : "triple2",
                        Rank = triple.Key,
                        Length = 1,
                        Count = ranks.Count,
                    };
            }
        }

        // 顺子：至少 5 张，连续，且不含 2 和王
        if (ranks.Count >= 5 && groups.Count == ranks.Count && distinct.Max() <= 11)
        {
            var ok = true;
            for (var i = 1; i < distinct.Count; i++)
                if (distinct[i] != distinct[i - 1] + 1) { ok = false; break; }

            if (ok)
                return new Combo { Type = "straight", Rank = distinct.Max(), Length = distinct.Count, Count = ranks.Count };
        }

        // 连对：至少 3 对，连续，不含 2 和王
        if (ranks.Count >= 6 && ranks.Count % 2 == 0 && groups.All(g => g.Count() == 2) && distinct.Max() <= 11)
        {
            var ok = true;
            for (var i = 1; i < distinct.Count; i++)
                if (distinct[i] != distinct[i - 1] + 1) { ok = false; break; }

            if (ok)
                return new Combo { Type = "pairs", Rank = distinct.Max(), Length = distinct.Count, Count = ranks.Count };
        }

        return null;
    }

    private static bool Beats(Combo candidate, Combo? previous)
    {
        if (previous is null) return true;

        if (candidate.Type == "rocket") return true;
        if (previous.Type == "rocket") return false;

        if (candidate.Type == "bomb" && previous.Type != "bomb") return true;
        if (candidate.Type == "bomb" && previous.Type == "bomb") return candidate.Rank > previous.Rank;
        if (previous.Type == "bomb") return false;

        if (candidate.Type != previous.Type) return false;
        if (candidate.Count != previous.Count) return false;
        if (candidate.Length != previous.Length) return false;

        return candidate.Rank > previous.Rank;
    }

    // ---------- 流程 ----------

    public void Start(Room room)
    {
        _seats.Clear();
        _finished = false;
        _banner = null;
        _multi = 1;
        _landlord = -1;
        _lastCombo = null;
        _lastOwner = -1;
        _bidding = true;

        foreach (var player in room.Players.Take(3))
        {
            _seats.Add(new Seat { PlayerId = player.Id, Name = player.Name });
        }

        if (_seats.Count < 3)
        {
            _finished = true;
            _banner = "斗地主需要正好 3 个人。";
            room.Log("system", _banner);
            room.SyncAll();
            return;
        }

        var deck = Enumerable.Range(0, 54).ToList();
        for (var i = deck.Count - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }

        for (var i = 0; i < 3; i++)
            _seats[i].Hand = deck.Skip(i * HandSize).Take(HandSize).OrderBy(c => c).ToList();

        _bottom = deck.Skip(51).Take(3).ToList();

        _bidTurn = _rng.Next(3);
        _turn = _bidTurn;
        _deadline = Now + BidMs;

        room.Log("system", $"发牌完毕，{_seats[_bidTurn].Name} 先叫地主。");
        room.SyncAll();
    }

    public void Tick(Room room, long nowMs)
    {
        if (_finished || nowMs < _deadline) return;

        if (_bidding)
        {
            room.Log("system", $"{_seats[_bidTurn].Name} 没吭声，默认不叫。");
            NextBid(room, false);
            return;
        }

        var seat = _seats[_turn];
        room.Log("system", $"{seat.Name} 超时，自动过牌。");
        Pass(room, _turn);
    }

    public void HandleAction(Room room, Player player, ActionRequest request)
    {
        if (_finished) return;

        var index = _seats.FindIndex(s => s.PlayerId == player.Id);
        if (index < 0) return;

        if (_bidding)
        {
            if (index != _bidTurn) return;

            if (request.Kind == "call") { BecomeLandlord(room, index); return; }
            if (request.Kind == "pass") { NextBid(room, false); return; }
            return;
        }

        if (index != _turn) return;

        switch (request.Kind)
        {
            case "play":
                Play(room, index, request.Text);
                break;
            case "pass":
                Pass(room, index);
                break;
        }
    }

    private void NextBid(Room room, bool called)
    {
        if (called) return;

        _bidTurn = (_bidTurn + 1) % 3;
        if (_bidTurn == _turn)
        {
            // 一圈都不叫，重新发牌
            room.Log("system", "三家都不叫，重新发牌。");
            Start(room);
            return;
        }

        _deadline = Now + BidMs;
        room.SyncAll();
    }

    private void BecomeLandlord(Room room, int index)
    {
        _bidding = false;
        _landlord = index;
        _seats[index].Hand.AddRange(_bottom);
        _seats[index].Hand = _seats[index].Hand.OrderBy(c => c).ToList();

        _turn = index;
        _lastCombo = null;
        _lastOwner = -1;
        _deadline = Now + TurnMs;

        room.Log("result", $"{_seats[index].Name} 叫了地主，拿走三张底牌（{HandText(_bottom)}），开始出牌。");
        room.SyncAll();
    }

    private void Play(Room room, int index, string? text)
    {
        var seat = _seats[index];

        if (!TryParse(text, out var ranks, out var error))
        {
            room.Log("system", $"{seat.Name}：{error}");
            return;
        }

        // 必须都在手里
        var pool = seat.Hand.GroupBy(c => RankOf(c)).ToDictionary(g => g.Key, g => g.Count());
        var need = ranks.GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (rank, count) in need)
        {
            if (!pool.TryGetValue(rank, out var have) || have < count)
            {
                room.Log("system", $"{seat.Name}：手里没有这么多 {RankName(rank)}");
                return;
            }
        }

        var combo = Detect(ranks);
        if (combo is null)
        {
            room.Log("system", $"{seat.Name} 出的牌不成牌型。");
            return;
        }

        if (_lastCombo is not null && _lastOwner != index && !Beats(combo, _lastCombo))
        {
            room.Log("system", $"{seat.Name} 这手压不过上家。");
            return;
        }

        // 从手牌移除
        foreach (var (rank, count) in need)
        {
            var removed = 0;
            seat.Hand.RemoveAll(c =>
            {
                if (removed >= count || RankOf(c) != rank) return false;
                removed++;
                return true;
            });
        }

        if (combo.IsBomb)
        {
            _multi *= 2;
            room.Log("result", $"💥 {seat.Name} 放了{(combo.Type == "rocket" ? "王炸" : "炸弹")}，倍数翻到 {_multi} 倍。");
        }

        _lastCombo = combo;
        _lastOwner = index;

        room.Log("result", $"{seat.Name} 出牌：{HandText(ranks)}（{TypeName(combo)}）");

        if (seat.Hand.Count == 0)
        {
            Finish(room, winner: index);
            return;
        }

        _turn = (index + 1) % 3;
        _deadline = Now + TurnMs;
        room.SyncAll();
    }

    private static string TypeName(Combo c) => c.Type switch
    {
        "single" => "单张",
        "pair" => "对子",
        "triple" => "三张",
        "triple1" => "三带一",
        "triple2" => "三带二",
        "straight" => $"{c.Length} 连顺",
        "pairs" => $"{c.Length} 连对",
        "bomb" => "炸弹",
        "rocket" => "王炸",
        _ => "牌",
    };

    private void Pass(Room room, int index)
    {
        var seat = _seats[index];

        // 两家都过了，轮到出牌的人重新自由出牌
        _turn = (index + 1) % 3;

        if (_turn == _lastOwner)
        {
            _lastCombo = null;
            _lastOwner = -1;
            room.Log("system", $"两家都过，{_seats[_turn].Name} 重新领出。");
        }
        else
        {
            room.Log("system", $"{seat.Name} 过牌。");
        }

        _deadline = Now + TurnMs;
        room.SyncAll();
    }

    private void Finish(Room room, int winner)
    {
        _finished = true;

        var landlordWon = winner == _landlord;
        var winners = new List<string>();
        var losers = new List<string>();

        for (var i = 0; i < 3; i++)
        {
            var isLandlord = i == _landlord;
            var won = landlordWon ? isLandlord : !isLandlord;
            if (won) winners.Add(_seats[i].PlayerId);
            else losers.Add(_seats[i].PlayerId);
        }

        _banner = landlordWon
            ? $"地主 {_seats[_landlord].Name} 赢了（{_multi} 倍）"
            : $"农民赢了，地主 {_seats[_landlord].Name} 输了（{_multi} 倍）";

        room.Log("result", _banner);

        room.Finish(new GameOutcome
        {
            Banner = _banner,
            LosingSide = landlordWon ? "农民" : "地主",
            LosingPlayerIds = losers,
        });

        room.SyncAll();
    }

    public GameView BuildView(Room room, Player viewer)
    {
        var me = _seats.FirstOrDefault(s => s.PlayerId == viewer.Id);
        var view = new GameView
        {
            Title = "斗地主",
            Subtitle = _finished
                ? "本局结束"
                : _bidding
                    ? $"叫地主：{_seats[Math.Clamp(_bidTurn, 0, _seats.Count - 1)].Name}"
                    : $"轮到 {_seats[Math.Clamp(_turn, 0, _seats.Count - 1)].Name}",
            DeadlineAtMs = _finished ? null : _deadline,
            ResultBanner = _finished ? _banner : null,
        };

        if (me is not null && !_finished)
        {
            view.PrivateNoteLabel = $"我的手牌（{me.Hand.Count} 张）";
            view.PrivateNote = HandText(me.Hand);
        }

        if (_finished)
        {
            view.Instruction = "本局结束。";
        }
        else if (_bidding)
        {
            view.Instruction = me is not null && _seats.IndexOf(me) == _bidTurn
                ? "轮到你叫地主了。"
                : $"等 {_seats[_bidTurn].Name} 决定叫不叫。";
        }
        else
        {
            var tail = _lastCombo is not null && _lastOwner >= 0
                ? $"桌面：{_seats[_lastOwner].Name} 的{TypeName(_lastCombo)}"
                : "桌面空着，可以先出任意牌型";

            view.Instruction = me is not null && _seats.IndexOf(me) == _turn
                ? $"轮到你出牌。{tail}"
                : $"等 {_seats[_turn].Name} 出牌。{tail}";

            if (me is not null && _seats.IndexOf(me) == _turn)
            {
                view.AcceptsText = true;
                view.TextHint = "输入要出的牌，如 334455 / 34567 / 大王";
                view.TextActionKind = "play";
            }
        }

        for (var i = 0; i < _seats.Count; i++)
        {
            var seat = _seats[i];
            var card = new GamePlayerCard
            {
                Id = seat.PlayerId,
                Name = seat.Name,
                IsYou = seat.PlayerId == viewer.Id,
                IsHost = seat.PlayerId == room.HostId,
                IsAlive = true,
                Sub = $"剩 {seat.Hand.Count} 张",
            };

            if (i == _landlord)
            {
                card.Badge = "地主";
                card.Accent = "warn";
            }
            else if (_landlord >= 0)
            {
                card.Badge = "农民";
            }

            if (!_finished)
            {
                if (_bidding && i == _bidTurn) card.Accent = "good";
                if (!_bidding && i == _turn) card.Accent = "good";
            }

            view.Players.Add(card);
        }

        if (!_finished)
        {
            if (_bidding && me is not null && _seats.IndexOf(me) == _bidTurn)
            {
                view.Actions.Add(new GameAction { Kind = "call", Label = "叫地主" });
                view.Actions.Add(new GameAction { Kind = "pass", Label = "不叫" });
            }
            else if (!_bidding && me is not null && _seats.IndexOf(me) == _turn)
            {
                if (_lastCombo is not null && _lastOwner >= 0)
                    view.Actions.Add(new GameAction { Kind = "pass", Label = "要不起" });
            }
        }

        if (_finished && viewer.Id == room.HostId)
        {
            view.Actions.Add(new GameAction { Kind = "lobby", Label = "回到大厅" });
        }

        return view;
    }

    public void OnPlayerDisconnected(Room room, Player player)
    {
        // 掉线就等超时自动过牌
    }

    public void OnPlayerLeft(Room room, Player player)
    {
        if (_finished) return;

        var index = _seats.FindIndex(s => s.PlayerId == player.Id);
        if (index < 0) return;

        _finished = true;
        _banner = $"{player.Name} 离开了，本局作废。";
        room.Log("system", _banner);
        room.SyncAll();
    }
}
