using PartyGames.Protocol;

namespace PartyGames.Server.Games;

/// <summary>
/// 海龟汤：房主当主持人，拿到汤底；其他人看汤面提问，主持人用「是/否/无关」回答。
/// 有人觉得猜到了就提交汤底，主持人判定对错。猜对的人赢，主持人公布则主持人赢。
/// </summary>
public sealed class TurtleSoupGame : IGameModule
{
    public string Id => "turtlesoup";
    public string DisplayName => "海龟汤";
    public string Blurb => "主持人知道汤底，其他人靠「是/否」提问推理。猜对的人赢。";
    public int MinPlayers => 2;
    public int MaxPlayers => 12;

    public bool SupportsScoring => true;

    public ScoreDefaults Scoring => new(Start: 3, Penalty: 1, BigThreshold: 0, BigStart: 2, PenaltyRoll: true);

    private static readonly (string Surface, string Truth)[] Puzzles =
    {
        ("一个男人走进酒吧，跟酒保要了一杯水。酒保却掏出一把枪指着他。男人说了声谢谢就走了。为什么？",
         "男人在打嗝，需要水止嗝，但喝水不一定管用；酒保看出他在打嗝，用枪吓他，惊吓止住了嗝。"),

        ("海边餐厅里，一个男人点了一碗海龟汤，喝了一口就放下勺子，走出去自杀了。为什么？",
         "他曾在海难中漂流，同伴给他吃「海龟汤」活了下来。今天他尝到真正的海龟汤，发现味道完全不同，意识到当年吃的是同伴的肉。"),

        ("一对姐妹去参加母亲的葬礼，妹妹在葬礼上看见一个很合心意的男人，回家后把姐姐杀了。为什么？",
         "她以为只要再办一次葬礼，就能再见到那个男人。"),

        ("一个人住在山顶的小屋里，半夜听到敲门声，开门却没人。反复几次后，第二天他发现山脚下有一具尸体。",
         "死者是从山上摔下的登山者，尸体挂在崖边被风吹动，撞得小屋的门咚咚响。"),

        ("沙漠中央发现一具赤裸的尸体，手里握着一根没划过的火柴。他是怎么死的？",
         "他和同伴乘热气球飞越沙漠，气球超载下坠，抽签决定谁跳下去减轻重量，他抽到了最短的签。"),

        ("一个女孩住在大楼十层。每天她坐电梯到七楼，再走楼梯上去；但下雨天她会直接坐到自己家那层。为什么？",
         "她个子矮，只够得到七楼的按钮。下雨天带着伞，可以用伞尖按到十楼。"),

        ("丈夫每天晚上都会给妻子留一盏灯，某天他忘了，第二天妻子死了。为什么？",
         "妻子是盲人，靠灯光让丈夫知道自己还醒着；那天灯没亮，她以为丈夫出了事，情绪失控。"),

        ("一个男人在餐厅吃饭，突然冲出餐厅跑回家，杀了自己的妻子。为什么？",
         "他听到邻桌有人在说「这汤里有毒」，而他妻子每天给他煲同样的汤——他意识到妻子一直在给他下毒。"),
    };

    private sealed class Seat
    {
        public string PlayerId = string.Empty;
        public string Name = string.Empty;
    }

    private readonly List<Seat> _seats = new();
    private readonly HashSet<string> _wantToGuess = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _rng = new();

    private string _surface = string.Empty;
    private string _truth = string.Empty;
    private string _hostId = string.Empty;
    private string? _question;
    private string? _questionBy;
    private string? _questionById;
    private string? _guess;
    private string? _guessBy;
    private string? _guessById;
    private bool _finished;
    private string? _banner;
    private int _questionsAnswered;

    public void Start(Room room)
    {
        _seats.Clear();
        _wantToGuess.Clear();
        _question = null;
        _questionBy = null;
        _questionById = null;
        _guess = null;
        _guessBy = null;
        _guessById = null;
        _finished = false;
        _banner = null;
        _questionsAnswered = 0;

        foreach (var player in room.Players)
        {
            _seats.Add(new Seat { PlayerId = player.Id, Name = player.Name });
        }

        // 房主默认当主持人；房主不在就随机挑一个。
        _hostId = _seats.Any(s => s.PlayerId == room.HostId) ? room.HostId : _seats[0].PlayerId;

        var puzzle = Puzzles[_rng.Next(Puzzles.Length)];
        _surface = puzzle.Surface;
        _truth = puzzle.Truth;

        var hostName = _seats.FirstOrDefault(s => s.PlayerId == _hostId)?.Name ?? "房主";
        room.Log("system", $"海龟汤开始，{hostName} 是主持人。");
        room.Log("result", $"汤面：{_surface}");
        room.SyncAll();
    }

    public void Tick(Room room, long nowMs)
    {
        // 海龟汤没有强制计时，靠主持人公布或有人猜对结束。
    }

    public void HandleAction(Room room, Player player, ActionRequest request)
    {
        if (_finished) return;

        var seat = _seats.FirstOrDefault(s => s.PlayerId == player.Id);
        if (seat is null) return;

        var isHost = player.Id == _hostId;

        if (isHost)
        {
            HandleHostAction(room, player, request);
            return;
        }

        HandlePlayerAction(room, seat, request);
    }

    public GameView BuildView(Room room, Player viewer)
    {
        var isHost = viewer.Id == _hostId;
        var waiting = _guess is not null
            ? $"等主持人判定「{_guessBy}」的猜测"
            : _question is not null
                ? $"等主持人回答「{_questionBy}」的问题"
                : "自由提问中";

        var view = new GameView
        {
            Title = "海龟汤",
            Subtitle = _finished ? "结束" : waiting,
            Instruction = _finished ? "本局结束。" : $"汤面：{_surface}",
        };

        if (_finished)
        {
            view.ResultBanner = _banner;
            view.Instruction = $"汤底：{_truth}";
        }

        if (!_finished && isHost)
        {
            view.PrivateNoteLabel = "汤底（只有你能看到）";
            view.PrivateNote = _truth;
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
                IsOffline = player is { IsOnline: false },
                Badge = seat.PlayerId == _hostId ? "主持人" : null,
                Accent = seat.PlayerId == _hostId ? "warn" : null,
            };

            view.Players.Add(card);
        }

        if (!_finished)
        {
            if (isHost)
            {
                if (_question is not null)
                {
                    view.Actions.Add(new GameAction { Kind = "yes", Label = "是" });
                    view.Actions.Add(new GameAction { Kind = "no", Label = "否" });
                    view.Actions.Add(new GameAction { Kind = "irrelevant", Label = "无关" });
                }

                if (_guess is not null)
                {
                    view.Actions.Add(new GameAction { Kind = "correct", Label = "猜对了", Danger = true });
                    view.Actions.Add(new GameAction { Kind = "wrong", Label = "不对" });
                }

                view.Actions.Add(new GameAction { Kind = "reveal", Label = "公布汤底", Danger = true });
            }
            else
            {
                view.AcceptsText = true;
                view.TextHint = _wantToGuess.Contains(viewer.Id)
                    ? "写下你的汤底猜测…"
                    : _question is null ? "问一个是非问题…" : "等主持人先回答上一个问题";

                if (_question is not null && !_wantToGuess.Contains(viewer.Id))
                {
                    view.AcceptsText = false;
                }

                view.TextActionKind = "ask";

                view.Actions.Add(new GameAction
                {
                    Kind = "guessmode",
                    Label = _wantToGuess.Contains(viewer.Id) ? "取消猜测" : "我要猜汤底",
                });
            }
        }
        else if (viewer.Id == room.HostId)
        {
            view.Actions.Add(new GameAction { Kind = "lobby", Label = "回到大厅" });
        }

        return view;
    }

    public void OnPlayerDisconnected(Room room, Player player)
    {
    }

    public void OnPlayerLeft(Room room, Player player)
    {
        var seat = _seats.FirstOrDefault(s => s.PlayerId == player.Id);
        if (seat is null) return;

        _seats.Remove(seat);
        _wantToGuess.Remove(player.Id);

        if (player.Id == _hostId)
        {
            End(room, null, "主持人离开了，本局结束。");
            return;
        }

        if (_questionBy == seat.Name) { _question = null; _questionBy = null; }
        if (_guessBy == seat.Name) { _guess = null; _guessBy = null; }

        room.SyncAll();
    }

    // -- actions ----------------------------------------------------------

    private void HandlePlayerAction(Room room, Seat seat, ActionRequest request)
    {
        switch (request.Kind)
        {
            case "guessmode":
                if (_wantToGuess.Contains(seat.PlayerId)) _wantToGuess.Remove(seat.PlayerId);
                else _wantToGuess.Add(seat.PlayerId);
                room.SyncAll();
                return;

            case "ask":
                SubmitText(room, seat, request.Text);
                return;
        }
    }

    private void SubmitText(Room room, Seat seat, string? text)
    {
        var clean = Clean(text, 120);
        if (clean.Length == 0) return;

        if (_wantToGuess.Contains(seat.PlayerId))
        {
            if (_guess is not null)
            {
                room.Log("system", "已经有一个猜测在等主持人判定了。");
                return;
            }

            _guess = clean;
            _guessBy = seat.Name;
            _guessById = seat.PlayerId;
            _wantToGuess.Remove(seat.PlayerId);
            room.Log("chat", clean, $"{seat.Name}（猜测）");
            room.SyncAll();
            return;
        }

        if (_question is not null)
        {
            room.Log("system", "等主持人回答上一个问题再问。");
            return;
        }

        _question = clean;
        _questionBy = seat.Name;
        _questionById = seat.PlayerId;
        room.Log("chat", clean, seat.Name);
        room.SyncAll();
    }

    private void HandleHostAction(Room room, Player host, ActionRequest request)
    {
        switch (request.Kind)
        {
            case "yes":
            case "no":
            case "irrelevant":
            {
                if (_question is null) return;

                var answer = request.Kind switch
                {
                    "yes" => "是",
                    "no" => "否",
                    _ => "无关",
                };

                _questionsAnswered++;
                room.Log("result", $"主持人：{answer}（回答了「{_questionBy}」）");
                _question = null;
                _questionBy = null;
                _questionById = null;
                room.SyncAll();
                return;
            }

            case "correct":
            {
                if (_guess is null) return;

                var winnerName = _guessBy ?? "有人";
                End(room, _guessById, $"{winnerName} 猜对了汤底！");
                return;
            }

            case "wrong":
            {
                if (_guess is null) return;

                room.Log("result", $"主持人：不对（「{_guessBy}」的猜测）");
                _guess = null;
                _guessBy = null;
                _guessById = null;
                room.SyncAll();
                return;
            }

            case "reveal":
                End(room, _hostId, "主持人公布了汤底，没有人猜对。");
                return;
        }
    }

    private void End(Room room, string? winnerId, string detail)
    {
        if (_finished) return;

        _finished = true;
        _banner = detail;
        room.Log("result", detail);

        var winnerName = _seats.FirstOrDefault(s => s.PlayerId == winnerId)?.Name;
        if (winnerName is not null) room.Log("result", $"{winnerName} 赢下这一局。");

        var losers = _seats.Where(s => s.PlayerId != winnerId).Select(s => s.PlayerId).ToList();

        room.Finish(new GameOutcome
        {
            Banner = detail,
            LosingSide = "其他人",
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
