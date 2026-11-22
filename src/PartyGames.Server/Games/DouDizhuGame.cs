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
    private int _bidStarter;         // 第一个被问「叫不叫」的人：两圈都不叫时强制他当地主
    private int _bidAsked;           // 叫地主阶段已经问过几家
    private bool _robPhase;          // true = 已经有人叫了，正在「抢地主」
    private int _robAsked;           // 抢地主阶段已经问过几家
    private int _caller = -1;        // 叫地主的人
    private int _robber = -1;        // 最后一次抢地主的人
    private int _robCount;           // 抢了几次 → 底分倍数
    private int _multi;              // 倍数：抢地主 + 炸弹
    private bool _bidding = true;
    private int _redeals;
    private bool _finished;
    private string? _banner;
    private long _deadline;

    /// <summary>托管：掉线的人由服务器代打，牌局不会因为一个人不在而卡住。</summary>
    private readonly bool[] _auto = new bool[3];

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

    /// <summary>把"点数序列"格式化成文字（不含花色）。</summary>
    private static string RanksText(IEnumerable<int> ranks) =>
        string.Join(" ", ranks.Select(RankName));

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

        // 四带二：四张 + 两张单牌 或 两对
        if (ranks.Count == 6 || ranks.Count == 8)
        {
            var four = groupsByRank.Where(kv => kv.Value == 4).Select(kv => kv.Key).ToList();
            if (four.Count == 1)
            {
                var rest = ranks.Count - 4;
                var restGroups = ranks.Where(r => r != four[0]).GroupBy(r => r).ToList();
                var okSingles = rest == 2 && restGroups.Count == 2 && restGroups.All(g => g.Count() == 1);
                var okPairs = rest == 4 && restGroups.Count == 2 && restGroups.All(g => g.Count() == 2);
                if (okSingles || okPairs)
                    return new Combo
                    {
                        Type = okSingles ? "four2" : "four2pair",
                        Rank = four[0],
                        Length = 1,
                        Count = ranks.Count,
                    };
            }
        }
        // 飞机：连续的三张（至少两组），可带同数量的单牌或对子
        if (groups.Count >= 2)
        {
            var triples = groupsByRank.Where(kv => kv.Value >= 3).Select(kv => kv.Key).OrderBy(k => k).ToList();
            if (triples.Count >= 2 && triples.Max() <= 11)
            {
                var consecutive = true;
                for (var i = 1; i < triples.Count; i++)
                    if (triples[i] != triples[i - 1] + 1) { consecutive = false; break; }

                if (consecutive)
                {
                    var n = triples.Count;
                    var core = n * 3;
                    var rest = ranks.Count - core;
                    var wingOk = rest == 0 || rest == n || rest == n * 2;

                    if (wingOk)
                    {
                        // 带牌不能是王或者超过两种点数（简单校验）
                        var type = rest == 0 ? "plane" : rest == n ? "plane1" : "plane2";
                        return new Combo
                        {
                            Type = type,
                            Rank = triples.Max(),
                            Length = n,
                            Count = ranks.Count,
                        };
                    }
                }
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
        // 谁在托管要跟着人走：重发牌不会让掉线的人变回在线，
        // 不然重新发牌以后又得一个个等超时。
        var auto = new HashSet<string>();
        for (var i = 0; i < _seats.Count && i < _auto.Length; i++)
        {
            if (_auto[i]) auto.Add(_seats[i].PlayerId);
        }

        _seats.Clear();
        _finished = false;
        _banner = null;
        _multi = 1;
        _landlord = -1;
        _lastCombo = null;
        _lastOwner = -1;
        _bidding = true;
        _robPhase = false;
        _caller = -1;
        _robber = -1;
        _robCount = 0;
        _bidAsked = 0;
        _robAsked = 0;
        Array.Clear(_auto);

        foreach (var player in room.Players.Take(3))
        {
            _seats.Add(new Seat { PlayerId = player.Id, Name = player.Name });
        }

        for (var i = 0; i < _seats.Count && i < _auto.Length; i++)
        {
            _auto[i] = auto.Contains(_seats[i].PlayerId);
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
        _bidStarter = _bidTurn;
        _turn = _bidTurn;
        _deadline = Now + BidMs;

        room.Log("system", $"发牌完毕，{_seats[_bidTurn].Name} 先叫地主。");
        room.SyncAll();
    }

    public void Tick(Room room, long nowMs)
    {
        if (_finished) return;

        // 三家都托管了：没人会回来接着打，直接收场，免得空转到天荒地老
        var active = 0;
        for (var i = 0; i < _seats.Count; i++) if (!_auto[i]) active++;
        if (active == 0)
        {
            FinishMatch(room, "三家都托管了，本局结束。", new List<string>());
            return;
        }

        var who = _bidding ? _bidTurn : _turn;
        if (who < 0 || who >= _seats.Count) return;

        var auto = _auto[who];
        if (!auto && nowMs < _deadline) return;

        if (_bidding)
        {
            if (!auto) room.Log("system", $"{_seats[who].Name} 没吭声，默认不叫。");
            BidDecision(room, who, agree: false);
            return;
        }

        if (!auto) room.Log("system", $"{_seats[who].Name} 超时，自动过牌。");
        AutoTurn(room, who);
    }

    /// <summary>托管/超时：有牌可压就过；自己领出时打最小的单张，保证牌局不会卡死。</summary>
    private void AutoTurn(Room room, int index)
    {
        if (_lastCombo is null && _seats[index].Hand.Count > 0)
        {
            var card = _seats[index].Hand.OrderBy(RankOf).First();
            Play(room, index, RankName(RankOf(card)));
            return;
        }

        Pass(room, index);
    }

    public void HandleAction(Room room, Player player, ActionRequest request)
    {
        if (_finished) return;

        // 认输：地主认输则农民赢，农民认输则地主赢；叫牌阶段还没定地主，其他人直接赢
        if (request.Kind == "resign")
        {
            var who = _seats.FindIndex(s => s.PlayerId == player.Id);
            if (who < 0) return;

            room.Log("system", $"{_seats[who].Name} 认输了。");

            if (_landlord >= 0)
            {
                Finish(room, who == _landlord ? (_landlord + 1) % 3 : _landlord);
            }
            else
            {
                FinishMatch(room, $"{_seats[who].Name} 认输，本局结束：其他人获胜。",
                    new List<string> { player.Id }, "认输的人");
            }

            return;
        }

        var index = _seats.FindIndex(s => s.PlayerId == player.Id);
        if (index < 0) return;

        if (_bidding)
        {
            if (index != _bidTurn) return;

            // 叫地主阶段只认「叫 / 不叫」，抢地主阶段只认「抢 / 不抢」
            if (!_robPhase)
            {
                if (request.Kind == "call") BidDecision(room, index, agree: true);
                else if (request.Kind == "pass") BidDecision(room, index, agree: false);
                return;
            }

            if (request.Kind == "rob") BidDecision(room, index, agree: true);
            else if (request.Kind == "pass") BidDecision(room, index, agree: false);
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

    /// <summary>
    /// 叫地主 / 抢地主共用一条流程：
    /// 先绕一圈问「叫不叫」，第一个叫的人定下来之后，其余两家依次问「抢不抢」。
    /// 每家最多抢一次，最后抢的人当地主；一次没抢就是叫的人当地主。
    /// </summary>
    private void BidDecision(Room room, int index, bool agree)
    {
        if (!_robPhase)
        {
            if (agree)
            {
                _caller = index;
                _robPhase = true;
                _robAsked = 0;
                _bidTurn = (index + 1) % 3;
                _deadline = Now + BidMs;
                room.Log("result", $"{_seats[index].Name} 叫地主，另外两家可以抢。");
                room.SyncAll();
                return;
            }

            _bidAsked++;
            room.Log("system", $"{_seats[index].Name} 不叫。");

            if (_bidAsked >= _seats.Count)
            {
                _redeals++;
                if (_redeals >= 2)
                {
                    // 连续两圈没人叫，强制第一家当地主，避免无限重发
                    room.Log("system", "三家都不叫，强制地主。");
                    FinalizeLandlord(room, _bidStarter);
                    return;
                }

                room.Log("system", "三家都不叫，重新发牌。");
                Start(room);
                return;
            }

            _bidTurn = (index + 1) % _seats.Count;
            _deadline = Now + BidMs;
            room.SyncAll();
            return;
        }

        // 抢地主：agree = 抢，否则不抢
        if (agree)
        {
            _robber = index;
            _robCount++;
            room.Log("result", $"{_seats[index].Name} 抢地主！（{_robCount} 抢 → {1 + _robCount} 倍）");
        }
        else
        {
            room.Log("system", $"{_seats[index].Name} 不抢。");
        }

        _robAsked++;
        if (_robAsked >= Math.Max(0, _seats.Count - 1))
        {
            FinalizeLandlord(room);
            return;
        }

        _bidTurn = (index + 1) % _seats.Count;
        _deadline = Now + BidMs;
        room.SyncAll();
    }

    /// <summary>定地主：抢过就是最后抢的人，没人抢就是叫的人，两圈没人叫就是被指定的那家。</summary>
    private void FinalizeLandlord(Room room, int? forced = null)
    {
        var index = forced ?? (_robber >= 0 ? _robber : _caller);
        if (index < 0 || index >= _seats.Count) index = 0;

        _bidding = false;
        _robPhase = false;
        _landlord = index;
        _multi = 1 + _robCount;
        _seats[index].Hand.AddRange(_bottom);
        _seats[index].Hand = _seats[index].Hand.OrderBy(c => c).ToList();

        _turn = index;
        _lastCombo = null;
        _lastOwner = -1;
        _deadline = Now + TurnMs;

        var how = forced is not null
            ? "被指定为地主"
            : _robCount > 0
                ? $"抢到地主（抢 {_robCount} 次，{_multi} 倍）"
                : $"叫了地主（{_multi} 倍）";

        room.Log("result", $"{_seats[index].Name} {how}，拿走三张底牌（{HandText(_bottom)}），开始出牌。");
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

        room.Log("result", $"{seat.Name} 出牌：{RanksText(ranks)}（{TypeName(combo)}）");

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
        "four2" => "四带二",
        "four2pair" => "四带两对",
        "plane" => "飞机",
        "plane1" => "飞机带单",
        "plane2" => "飞机带对",
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

    /// <summary>整局收场（有人走掉 / 认输 / 全员托管），只结算 <paramref name="losingIds"/> 的积分。</summary>
    private void FinishMatch(Room room, string banner, List<string> losingIds, string? losingSide = null)
    {
        _finished = true;
        _banner = banner;
        room.Log("result", banner);

        room.Finish(new GameOutcome
        {
            Banner = banner,
            LosingSide = losingSide,
            LosingPlayerIds = losingIds,
        });

        room.SyncAll();
    }

    public GameView BuildView(Room room, Player viewer)
    {
        var me = _seats.FirstOrDefault(s => s.PlayerId == viewer.Id);
        var meIndex = me is null ? -1 : _seats.IndexOf(me);
        var myTurn = !_bidding && meIndex >= 0 && meIndex == _turn;

        string SeatName(int index) =>
            index >= 0 && index < _seats.Count ? _seats[index].Name : "?";

        var subtitle = _finished
            ? "本局结束"
            : _bidding
                ? (_robPhase ? $"抢地主：{SeatName(_bidTurn)}" : $"叫地主：{SeatName(_bidTurn)}")
                : $"轮到 {SeatName(_turn)}（{_multi} 倍）";

        var view = new GameView
        {
            Title = "斗地主",
            Subtitle = subtitle,
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
            var mine = meIndex == _bidTurn;
            view.Instruction = _robPhase
                ? mine
                    ? $"轮到你抢地主（现在 {1 + _robCount} 倍，你抢了就翻倍）。"
                    : $"有人叫了地主，等 {SeatName(_bidTurn)} 决定抢不抢。"
                : mine
                    ? "轮到你叫地主了。"
                    : $"等 {SeatName(_bidTurn)} 决定叫不叫。";
        }
        else
        {
            var tail = _lastCombo is not null && _lastOwner >= 0
                ? $"桌面：{SeatName(_lastOwner)} 的{TypeName(_lastCombo)}"
                : "桌面空着，可以先出任意牌型";

            view.Instruction = myTurn
                ? $"轮到你出牌。{tail}"
                : $"等 {SeatName(_turn)} 出牌。{tail}";

            if (myTurn)
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
                Sub = _auto[i] ? $"剩 {seat.Hand.Count} 张 · 托管" : $"剩 {seat.Hand.Count} 张",
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

        // 叫地主 / 抢地主：按钮只发给「当前该做决定的那家」
        if (!_finished && _bidding && meIndex == _bidTurn)
        {
            if (_robPhase)
            {
                view.Actions.Add(new GameAction { Kind = "rob", Label = "抢地主" });
                view.Actions.Add(new GameAction { Kind = "pass", Label = "不抢" });
            }
            else
            {
                view.Actions.Add(new GameAction { Kind = "call", Label = "叫地主" });
                view.Actions.Add(new GameAction { Kind = "pass", Label = "不叫" });
            }
        }

        if (!_finished && !_bidding && myTurn && _lastCombo is not null && _lastOwner >= 0)
        {
            view.Actions.Add(new GameAction { Kind = "pass", Label = "要不起" });
        }

        if (_finished && viewer.Id == room.HostId)
        {
            view.Actions.Add(new GameAction { Kind = "lobby", Label = "回到大厅" });
        }

        return view;
    }

    public void OnPlayerDisconnected(Room room, Player player)
    {
        // 掉线保留座位，转身交给托管：轮到他就立刻替他过牌，其他人不用干等 45 秒
        if (_finished) return;

        var index = _seats.FindIndex(s => s.PlayerId == player.Id);
        if (index < 0) return;

        _auto[index] = true;
        room.SyncAll();
    }

    public void OnPlayerReconnected(Room room, Player player)
    {
        var index = _seats.FindIndex(s => s.PlayerId == player.Id);
        if (index < 0 || !_auto[index] || _finished) return;

        _auto[index] = false;
        room.Log("system", $"{player.Name} 回来了，取消托管。");
        room.SyncAll();
    }

    public void OnPlayerLeft(Room room, Player player)
    {
        if (_finished) return;

        var index = _seats.FindIndex(s => s.PlayerId == player.Id);
        if (index < 0) return;

        // 斗地主得三家齐：走一个就打不下去，直接收场，剩下的人赢
        _auto[index] = true;
        if (_seats.Count - 1 < MinPlayers)
        {
            FinishMatch(room, $"{player.Name} 直接离开了，本局结束：剩下的人获胜。",
                new List<string> { player.Id }, "离开的人");
        }
    }
}
