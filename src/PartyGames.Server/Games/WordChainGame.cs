using PartyGames.Protocol;

namespace PartyGames.Server.Games;

/// <summary>
/// 词语接龙：轮流说词，下一个词必须以上一个词的最后一个字开头。
/// 接不上、超时或主动放弃就出局，活到最后的赢。
/// </summary>
public sealed class WordChainGame : IGameModule
{
    private const int TurnMs = 30_000;

    public string Id => "wordchain";
    public string DisplayName => "词语接龙";
    public string Blurb => "轮流说词，下一个词要接住上一个词的最后一个字。接不上或超时就出局。";
    public int MinPlayers => 3;
    public int MaxPlayers => 20;

    public bool SupportsScoring => true;

    public ScoreDefaults Scoring => new(Start: 3, Penalty: 1, BigThreshold: 0, BigStart: 2, PenaltyRoll: true);

    private sealed class Seat
    {
        public string PlayerId = string.Empty;
        public string Name = string.Empty;
        public string? LastWord;
        public bool Alive = true;
    }

    private readonly List<Seat> _seats = new();
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

    private int _turn;
    private string? _required;
    private string? _lastWord;
    private long _deadline;
    private bool _finished;
    private string? _banner;

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Start(Room room)
    {
        _seats.Clear();
        _used.Clear();
        _turn = 0;
        _required = null;
        _lastWord = null;
        _finished = false;
        _banner = null;

        foreach (var player in room.Players)
        {
            _seats.Add(new Seat { PlayerId = player.Id, Name = player.Name });
        }

        _deadline = Now + TurnMs;

        room.Log("system", $"词语接龙开始：{_seats.Count} 人。{_seats[0].Name} 先说一个词开局。");
        room.SyncAll();
    }

    public void Tick(Room room, long nowMs)
    {
        if (_finished || nowMs < _deadline) return;

        var current = CurrentSeat();
        if (current is null) return;

        room.Log("result", $"{current.Name} 超时了，出局。");
        Eliminate(room, current);
    }

    public void HandleAction(Room room, Player player, ActionRequest request)
    {
        if (_finished) return;

        var current = CurrentSeat();
        if (current is null || current.PlayerId != player.Id) return;

        switch (request.Kind)
        {
            case "word":
                SubmitWord(room, current, request.Text);
                break;

            case "giveup":
                room.Log("result", $"{current.Name} 认输，出局。");
                Eliminate(room, current);
                break;
        }
    }

    public GameView BuildView(Room room, Player viewer)
    {
        var current = CurrentSeat();
        var me = _seats.FirstOrDefault(s => s.PlayerId == viewer.Id);

        var view = new GameView
        {
            Title = "词语接龙",
            Subtitle = _lastWord is null ? "还没有词" : $"上一个词：{_lastWord}",
            DeadlineAtMs = _finished ? null : _deadline,
            Instruction = _finished
                ? "本局结束。"
                : _required is null
                    ? $"{current?.Name} 开个头，说一个词。"
                    : $"要说出以「{_required}」开头的词。",
        };

        if (_finished)
        {
            view.ResultBanner = _banner;
        }

        if (me is not null && !_finished)
        {
            view.PrivateNoteLabel = "已用词";
            view.PrivateNote = _used.Count == 0 ? "还没有" : $"{_used.Count} 个";
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
                Sub = seat.LastWord,
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
            view.AcceptsText = true;
            view.TextHint = _required is null ? "说一个词…" : $"「{_required}」开头…";
            view.TextActionKind = "word";

            view.Actions.Add(new GameAction { Kind = "giveup", Label = "认输", Danger = true });
        }

        if (_finished && viewer.Id == room.HostId)
        {
            view.Actions.Add(new GameAction { Kind = "lobby", Label = "回到大厅" });
        }

        return view;
    }

    public void OnPlayerDisconnected(Room room, Player player)
    {
        // 掉线的人轮到时按超时处理。
    }

    public void OnPlayerLeft(Room room, Player player)
    {
        var seat = _seats.FirstOrDefault(s => s.PlayerId == player.Id);
        if (seat is null) return;

        if (seat.Alive)
        {
            seat.Alive = false;
            room.Log("system", $"{seat.Name} 离开了，出局。");
        }

        AdvanceTurnIfNeeded(room);
        room.SyncAll();
    }

    // -- internals --------------------------------------------------------

    private Seat? CurrentSeat() =>
        _turn >= 0 && _turn < _seats.Count && _seats[_turn].Alive ? _seats[_turn] : null;

    private void SubmitWord(Room room, Seat seat, string? text)
    {
        var word = Clean(text, 20);

        if (word.Length < 2)
        {
            room.Log("system", $"{seat.Name} 说的不是一个词（至少 2 个字）。");
            return;
        }

        if (word.Any(char.IsWhiteSpace))
        {
            room.Log("system", $"{seat.Name} 的词里不能有空格。");
            return;
        }

        if (!word.All(char.IsLetter))
        {
            room.Log("system", $"{seat.Name} 的词只能由文字组成。");
            return;
        }

        if (_required is not null &&
            !string.Equals(word[..1], _required, StringComparison.OrdinalIgnoreCase))
        {
            room.Log("system", $"{seat.Name} 接错了：要用「{_required}」开头。");
            return;
        }

        if (_used.Contains(word))
        {
            room.Log("system", $"{seat.Name} 说的「{word}」已经用过了。");
            return;
        }

        _used.Add(word);
        seat.LastWord = word;
        _lastWord = word;
        _required = word[^1].ToString();

        room.Log("chat", word, seat.Name);

        AdvanceTurnIfNeeded(room);
    }

    private void Eliminate(Room room, Seat seat)
    {
        seat.Alive = false;
        AdvanceTurnIfNeeded(room);
    }

    private void AdvanceTurnIfNeeded(Room room)
    {
        var alive = _seats.Where(s => s.Alive).ToList();

        if (alive.Count <= 1)
        {
            _finished = true;
            _banner = alive.Count == 1 ? $"{alive[0].Name} 接住了最后一棒" : "全灭";
            room.Log("result", _banner);

            var winnerId = alive.Count == 1 ? alive[0].PlayerId : null;
            var losers = _seats.Where(s => s.PlayerId != winnerId).Select(s => s.PlayerId).ToList();

            room.Finish(new GameOutcome
            {
                Banner = _banner,
                LosingSide = "其他人",
                LosingPlayerIds = losers,
            });

            room.SyncAll();
            return;
        }

        for (var step = 1; step <= _seats.Count; step++)
        {
            var index = (_turn + step) % _seats.Count;
            if (!_seats[index].Alive) continue;

            _turn = index;
            break;
        }

        _deadline = Now + TurnMs;
        room.SyncAll();
    }

    private static string Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var trimmed = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
