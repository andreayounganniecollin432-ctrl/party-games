using PartyGames.Protocol;

namespace PartyGames.Server;

/// <summary>
/// 机器人填充 / AI 托管：房间里的座位可以由服务端机器人坐满，一个人也能把八个游戏都测一遍。
///
/// 机器人不写死每个游戏的规则，而是走和人类完全相同的入口——拿一份服务端为自己生成的 <see cref="GameView"/>，
/// 从里面挑一个合法动作。所以新加游戏时，只要它的 <c>BuildView</c> 会给出动作或文本输入，机器人就能玩；
/// 只有像接龙这种需要"接住上一个字"的，才用到下面的词库兜底。
/// </summary>
internal static class Bots
{
    /// <summary>How long a bot waits between decisions, so a match reads at human speed.</summary>
    private const int MinDelayMs = 800;
    private const int MaxDelayMs = 1800;

    /// <summary>Actions a bot never takes on its own: leaving, resigning, or starting for the host.</summary>
    private static readonly HashSet<string> Forbidden = new(StringComparer.Ordinal)
    {
        "leave", "resign", "lobby", "resetsession", "ready", "unready", "start",
    };

    /// <summary>Generic descriptions that never name a word, so 谁是卧底's "contains your word" rule can't trip.</summary>
    private static readonly string[] UndercoverLines =
    [
        "我身边挺常见的",
        "这个不太好直接说",
        "最近刚接触过",
        "价格差别很大",
        "家里就有一份",
        "朋友推荐才注意到",
        "用起来还算顺手",
        "听说过但没细究",
        "跟季节有点关系",
        "我第一反应是那个",
        "有人特别喜欢它",
        "需要点耐心才行",
    ];

    private static readonly string[] WerewolfLines =
    [
        "我先听预言家怎么说",
        "我觉得 2 号有点急",
        "昨天夜里很安静",
        "我保一下 5 号",
        "我不太确定，先弃票",
    ];

    private static readonly string[] SoupGuesses =
    [
        "他是想喝水，但酒保看出他病了",
        "他打了个嗝，需要被吓一下",
        "酒保以为他要抢劫",
    ];

    /// <summary>Two-character words for 词语接龙, picked by the character the previous word ended on.</summary>
    private static readonly string[] Words =
    [
        "苹果", "果实", "实在", "在乎", "呼吸", "吸收", "收拾", "拾起", "起来", "来回",
        "回忆", "忆苦", "苦瓜", "瓜子", "子女", "女儿", "儿童", "童年", "年轻", "轻松",
        "松鼠", "鼠年", "年纪", "纪念", "念头", "头发", "发明", "明天", "天空", "空气",
        "气球", "球鞋", "鞋子", "子弟", "弟弟", "弟兄", "兄弟", "弟子", "子夜", "夜晚",
        "晚上", "上面", "面包", "包子", "子弹", "弹簧", "黄鹂", "离开", "开心", "心情",
        "情况", "况味", "味道", "道理", "理由", "由来", "来源", "源头", "头绪", "结论",
        "论文", "文化", "化学", "学习", "习惯", "惯性", "性格", "格外", "外面", "面前",
        "前进", "进步", "步伐", "替代", "代替", "替身", "身体", "体育", "育人", "人民",
        "民族", "族人", "人类", "类型", "形象", "象征", "征服", "服从", "从来", "来往",
    ];

    private static readonly string[] BotNames =
    [
        "阿光", "莉瑟", "塔塔露", "娜娜莫", "雅修特拉", "桑克瑞德", "于里昂热", "埃斯蒂尼安",
        "古·拉哈", "阿尔菲诺", "阿莉塞", "希尔迪布兰德",
    ];

    public static string NameFor(int index)
    {
        var name = BotNames[index % BotNames.Length];
        return index < BotNames.Length ? name : $"{name}{index / BotNames.Length + 1}";
    }

    /// <summary>Lets every bot whose turn it is take one legal action.</summary>
    public static void Drive(Room room, long nowMs)
    {
        if (room.Phase != RoomPhase.Playing || room.Game is null)
        {
            return;
        }

        foreach (var bot in room.Players.Where(p => p.IsBot).ToList())
        {
            if (!room.TryTakeBotTurn(bot.Id, nowMs, out var delay))
            {
                continue;
            }

            GameView view;
            try
            {
                view = room.Game.BuildView(room, bot);
            }
            catch
            {
                continue;
            }

            if (TryChoose(room, bot, view, out var request))
            {
                try
                {
                    room.HandleGameAction(bot, request);
                }
                catch
                {
                    // A rejected action is the same as a human misclick: the bot simply waits for its next turn.
                }
            }

            room.SetBotTurn(bot.Id, nowMs + delay);
        }
    }

    /// <summary>The delay a bot takes before its next decision, varied so bots don't move in lockstep.</summary>
    public static int NextDelay() => Random.Shared.Next(MinDelayMs, MaxDelayMs);

    private static bool TryChoose(Room room, Player bot, GameView view, out ActionRequest request)
    {
        request = new ActionRequest();

        var actions = view.Actions
            .Where(action => action.Enabled && !Forbidden.Contains(action.Kind))
            .ToList();

        if (actions.Count > 0)
        {
            // Prefer something that moves the match along (a kill, a vote, a pull) over passing.
            var decisive = actions.Where(action => action.Kind is not ("pass" or "none")).ToList();
            var pool = decisive.Count > 0 && Random.Shared.Next(100) < 70 ? decisive : actions;
            var pick = pool[Random.Shared.Next(pool.Count)];
            request = new ActionRequest { Kind = pick.Kind, Target = pick.Target };
            return true;
        }

        // 象棋不发按钮，发的是可落点坐标（"起行起列终行终列"），走的是 move + Text。
        if (view.Targets.Count > 0)
        {
            request = new ActionRequest { Kind = "move", Text = PickMove(view) };
            return true;
        }

        if (view.AcceptsText && TryCompose(room, view, out var text))
        {
            request = new ActionRequest { Kind = view.TextActionKind, Text = text };
            return true;
        }

        return false;
    }

    private static bool TryCompose(Room room, GameView view, out string text)
    {
        switch (room.GameId)
        {
            case "undercover":
                text = Pick(UndercoverLines);
                return true;

            case "werewolf":
                text = Pick(WerewolfLines);
                return true;

            case "turtlesoup":
                text = Pick(SoupGuesses);
                return true;

            case "wordchain":
                text = NextWord(view.Instruction);
                return text.Length > 0;

            case "doudizhu":
                text = LowestCard(view.PrivateNote);
                return text.Length > 0;

            default:
                text = string.Empty;
                return false;
        }
    }

    /// <summary>
    /// 象棋: prefer a capture, then a move toward the enemy side, and shuffle otherwise. A random mover fills the board
    /// with aimless moves and a match never ends, so captures at least trade material down.
    /// </summary>
    private static string PickMove(GameView view)
    {
        var captures = new List<string>();
        var advances = new List<string>();
        foreach (var target in view.Targets)
        {
            if (target.Length < 4)
            {
                continue;
            }

            var fromRow = target[0] - 0;
            var toRow = target[2] - 0;
            var toColumn = target[3] - 0;

            if (view.Board.Count > toRow && toRow >= 0 && toColumn >= 0 && toColumn < view.Board[toRow].Length && view.Board[toRow][toColumn] != '.')
            {
                captures.Add(target);
            }
            else if (fromRow != toRow)
            {
                advances.Add(target);
            }
        }

        var pool = captures.Count > 0 ? captures : advances.Count > 0 ? advances : view.Targets;
        return pool[Random.Shared.Next(pool.Count)];
    }

    /// <summary>接龙: take the character the previous word ended on and answer with a word that starts with it.</summary>
    private static string NextWord(string? instruction)
    {
        var previous = Between(instruction, '「', '」');
        if (previous.Length == 0)
        {
            return Pick(Words);
        }

        var head = previous[^1];
        foreach (var word in Words)
        {
            if (word[0] == head)
            {
                return word;
            }
        }

        // No word in the bank starts with that character; a two-character coinage keeps the round moving.
        return $"{head}子";
    }

    /// <summary>斗地主: when a bot has to lead it plays its lowest single card, so the match can't stall.</summary>
    private static string LowestCard(string? hand)
    {
        if (string.IsNullOrWhiteSpace(hand))
        {
            return string.Empty;
        }

        var cards = hand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var order = "345678910JQKA2";
        var best = cards[0];
        var bestRank = Rank(order, best);
        foreach (var card in cards)
        {
            var rank = Rank(order, card);
            if (rank < bestRank)
            {
                best = card;
                bestRank = rank;
            }
        }

        return best;
    }

    private static int Rank(string order, string card)
    {
        var token = card is "10" ? "10" : card;
        var index = token.Length == 0 ? -1 : order.IndexOf(token, StringComparison.Ordinal);
        return index < 0 ? 99 : index;
    }

    private static string Between(string? text, char open, char close)
    {
        if (text is null)
        {
            return string.Empty;
        }

        var start = text.IndexOf(open);
        if (start < 0)
        {
            return string.Empty;
        }

        var end = text.IndexOf(close, start + 1);
        return end > start ? text[(start + 1)..end] : string.Empty;
    }

    private static string Pick(string[] bank) => bank[Random.Shared.Next(bank.Length)];
}
