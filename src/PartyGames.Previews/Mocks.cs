using System;
using System.Collections.Generic;
using PartyGames.Protocol;

namespace PartyGames.Previews;

/// <summary>
/// Room snapshots shaped like the ones the server actually sends, one per game, so a rendered screen shows the same
/// fields (<c>Title</c>, <c>Subtitle</c>, <c>PrivateNote</c>, cards, actions) the plugin gets in play.
/// </summary>
internal static class Mocks
{
    public static RoomSnapshot Undercover() => Room("undercover", "谁是卧底", "谁是卧底 · 第 2 轮", "描述阶段",
        instruction: "用一句话描述你的词,不要直接说出它.",
        privateLabel: "你的词", privateNote: "奶茶",
        deadlineInSeconds: 27,
        players:
        [
            Card("p1", "鹿岛渚咲", you: true, host: true, badge: "已描述", accent: "good"),
            Card("p2", "夜露", badge: "已描述", accent: "good"),
            Card("p3", "白鸦", badge: "描述中"),
            Card("p4", "阿莉塞", votes: 2),
            Card("p5", "小豆芽", votes: 1),
            Card("p6", "莫古力", offline: true),
        ],
        acceptsText: true, textHint: "一句话描述你的词...",
        log:
        [
            ("夜露", "甜甜的,夏天常喝"),
            ("白鸦", "冷的,加珍珠"),
            ("鹿岛渚咲", "要说的话在杯子里"),
            ("系统", "第 2 轮描述阶段开始,每人一句话."),
        ],
        scores: [Score("p1", "鹿岛渚咲", 3, 0, you: true), Score("p2", "夜露", 3, 0), Score("p3", "白鸦", 2, -1), Score("p4", "阿莉塞", 2, -1), Score("p5", "小豆芽", 1, -2), Score("p6", "莫古力", 1, -2)]);

    public static RoomSnapshot Werewolf() => Room("werewolf", "狼人杀", "狼人杀 · 第 1 夜", "夜晚",
        instruction: "夜里可以杀一个人.选好目标,别让好人看出你.",
        privateLabel: "你的身份", privateNote: "狼人(还有 2 名同伴:夜露、白鸦)",
        deadlineInSeconds: 19,
        players:
        [
            Card("p1", "鹿岛渚咲", you: true, host: true, sub: "狼人"),
            Card("p2", "夜露", sub: "狼人"),
            Card("p3", "白鸦", sub: "狼人"),
            Card("p4", "阿莉塞", sub: "预言家"),
            Card("p5", "小豆芽", sub: "女巫"),
            Card("p6", "莫古力", sub: "平民"),
            Card("p7", "阿光", sub: "平民"),
            Card("p8", "莉瑟", sub: "平民"),
        ],
        actions:
        [
            Action("kill", "杀 阿莉塞", target: "p4", danger: true),
            Action("kill", "杀 小豆芽", target: "p5", danger: true),
            Action("kill", "杀 莫古力", target: "p6", danger: true),
            Action("none", "今晚不动手"),
        ],
        log:
        [
            ("系统", "天黑请闭眼."),
            ("系统", "狼人请睁眼,商量今晚要杀谁."),
            ("夜露", "先刀预言家吧"),
        ],
        scores: [Score("p1", "鹿岛渚咲", 2, 0, you: true), Score("p2", "夜露", 2, 0), Score("p3", "白鸦", 2, 0), Score("p4", "阿莉塞", 2, 0), Score("p5", "小豆芽", 2, 0), Score("p6", "莫古力", 2, 0), Score("p7", "阿光", 2, 0), Score("p8", "莉瑟", 2, 0)],
        bigMatchNote: true);

    public static RoomSnapshot Roulette() => Room("roulette", "俄罗斯轮盘", "俄罗斯轮盘", "轮到 白鸦",
        instruction: "轮到你了.扣下扳机会怎样,没人知道.",
        deadlineInSeconds: 24,
        players:
        [
            Card("p1", "鹿岛渚咲", you: true, host: true),
            Card("p2", "夜露"),
            Card("p3", "白鸦", sub: "正在决定"),
            Card("p4", "阿莉塞", dead: true, badge: "出局"),
            Card("p5", "小豆芽", dead: true, badge: "出局"),
        ],
        actions:
        [
            Action("pull", "扣扳机", danger: true),
        ],
        log:
        [
            ("系统", "弹巢里装了 2 发子弹,六格."),
            ("夜露", "空枪,轮到 白鸦"),
            ("阿莉塞", "砰!出局."),
        ],
        scores: [Score("p1", "鹿岛渚咲", 3, 0, you: true), Score("p2", "夜露", 2, -1), Score("p3", "白鸦", 3, 0), Score("p4", "阿莉塞", 2, -1), Score("p5", "小豆芽", 2, -1)]);

    public static RoomSnapshot Snowball() => Room("snowball", "堆雪球", "堆雪球 · 第 6 轮", "轮到 鹿岛渚咲",
        instruction: "滚雪球 +2、加固 +1、砸人扣对方 3 点.雪球到 12 会崩.",
        deadlineInSeconds: 22,
        players:
        [
            Card("p1", "鹿岛渚咲", you: true, host: true, badge: "雪球 7"),
            Card("p2", "夜露", badge: "雪球 11"),
            Card("p3", "白鸦", badge: "雪球 4"),
            Card("p4", "阿莉塞", badge: "雪球 2"),
            Card("p5", "小豆芽", dead: true, badge: "塌了"),
        ],
        actions:
        [
            Action("roll", "滚雪球 +2"),
            Action("pack", "加固 +1"),
            Action("hit", "砸 夜露(-3)", target: "p2", danger: true),
        ],
        log:
        [
            ("系统", "第 6 轮开始."),
            ("夜露", "加固,雪球 11."),
            ("白鸦", "滚雪球,4."),
        ],
        scores: [Score("p1", "鹿岛渚咲", 3, 0, you: true), Score("p2", "夜露", 3, 0), Score("p3", "白鸦", 2, -1), Score("p4", "阿莉塞", 1, -2), Score("p5", "小豆芽", 1, -2)]);

    public static RoomSnapshot WordChain() => Room("wordchain", "词语接龙", "词语接龙 · 第 9 轮", "轮到 鹿岛渚咲",
        instruction: "上一个词是「苹果」,你的词要以「果」开头.",
        deadlineInSeconds: 26,
        players:
        [
            Card("p1", "鹿岛渚咲", you: true, host: true),
            Card("p2", "夜露", badge: "已接"),
            Card("p3", "白鸦", badge: "已接"),
            Card("p4", "阿莉塞", dead: true, badge: "接不上"),
        ],
        acceptsText: true, textHint: "接一个词...",
        log:
        [
            ("系统", "第 9 轮开始,上一棒是「苹果」."),
            ("夜露", "果实"),
            ("白鸦", "实时"),
        ],
        scores: [Score("p1", "鹿岛渚咲", 3, 0, you: true), Score("p2", "夜露", 3, 0), Score("p3", "白鸦", 3, 0), Score("p4", "阿莉塞", 2, -1)]);

    public static RoomSnapshot TurtleSoup() => Room("turtlesoup", "海龟汤", "海龟汤 · 第 3 题", "提问阶段",
        instruction: "看汤面找矛盾,主持人只用「是 / 否 / 无关」回答.",
        privateLabel: "汤面", privateNote: "一个人走进酒吧,点了一杯水.酒保却拔出了枪.他道谢后离开.为什么?",
        deadlineInSeconds: 88,
        players:
        [
            Card("p1", "鹿岛渚咲", you: true, host: true, badge: "主持人"),
            Card("p2", "夜露", sub: "已问 4 次"),
            Card("p3", "白鸦", sub: "已问 2 次"),
            Card("p4", "阿莉塞", sub: "想猜汤底"),
        ],
        actions:
        [
            Action("yes", "是"),
            Action("no", "否"),
            Action("irrelevant", "无关"),
        ],
        log:
        [
            ("夜露", "他是口渴吗?"),
            ("系统", "主持人:是"),
            ("白鸦", "枪是真的吗?"),
            ("系统", "主持人:是"),
        ],
        scores: [Score("p1", "鹿岛渚咲", 3, 0, you: true), Score("p2", "夜露", 3, 0), Score("p3", "白鸦", 2, -1), Score("p4", "阿莉塞", 2, -1)]);

    public static RoomSnapshot Doudizhu() => Room("doudizhu", "斗地主", "斗地主", "轮到 鹿岛渚咲",
        instruction: "上家出了 对子 7,你可以压过它,也可以要不起.",
        privateLabel: "我的手牌(13 张)", privateNote: string.Empty,
        deadlineInSeconds: 28,
        players:
        [
            Card("p1", "鹿岛渚咲", you: true, host: true, sub: "地主 · 13 张"),
            Card("p2", "夜露", sub: "农民 · 9 张"),
            Card("p3", "白鸦", sub: "农民 · 11 张"),
        ],
        actions:
        [
            Action("play", "出牌"),
            Action("pass", "要不起"),
            Action("resign", "认输", danger: true),
        ],
        acceptsText: true, textHint: "输入要出的牌,如 334455 / 34567 / 大王",
        hand: "3♠ 3♥ 4♣ 5♦ 6♠ 7♥ 8♣ 9♦ 9♠ 10♥ J♣ Q♦ K♠",
        log:
        [
            ("系统", "鹿岛渚咲 叫了地主."),
            ("夜露", "对子 7(对子)"),
            ("白鸦", "过牌"),
        ],
        scores: [Score("p1", "鹿岛渚咲", 3, 0, you: true), Score("p2", "夜露", 2, -1), Score("p3", "白鸦", 2, -1)]);

    public static RoomSnapshot Xiangqi() => Room("xiangqi", "中国象棋", "中国象棋 · 第 14 手", "红方回合",
        instruction: "轮到你走.红方先行,将死对方为胜.",
        privateLabel: "你执", privateNote: "红方(下方)",
        deadlineInSeconds: 45,
        players:
        [
            Card("p1", "鹿岛渚咲", you: true, host: true, sub: "红方"),
            Card("p2", "夜露", sub: "黑方"),
        ],
        actions: [Action("resign", "认输", danger: true)],
        board: Board(),
        log:
        [
            ("系统", "第 14 手,红方走."),
            ("夜露", "炮二平五"),
            ("鹿岛渚咲", "马八进七"),
        ],
        scores: [Score("p1", "鹿岛渚咲", 3, 0, you: true), Score("p2", "夜露", 2, -1)]);

    /// <summary>The opening position of a xiangqi board, rows top (black) to bottom (red).</summary>
    private static List<string> Board() =>
    [
        "rnbakabnr",
        ".........",
        ".c.....c.",
        "p.p.p.p.p",
        ".........",
        ".........",
        "P.P.P.P.P",
        ".C.....C.",
        ".........",
        "RNBAKABNR",
    ];

    private static RoomSnapshot Room(
        string gameId,
        string gameName,
        string title,
        string subtitle,
        string instruction,
        string? privateLabel = null,
        string? privateNote = null,
        int? deadlineInSeconds = null,
        GamePlayerCard[]? players = null,
        GameAction[]? actions = null,
        bool acceptsText = false,
        string? textHint = null,
        (string Actor, string Text)[]? log = null,
        ScoreEntry[]? scores = null,
        string? hand = null,
        List<string>? board = null,
        bool bigMatchNote = false)
    {
        var view = new GameView
        {
            Title = title,
            Subtitle = subtitle,
            Instruction = instruction,
            PrivateNoteLabel = privateLabel,
            PrivateNote = privateNote,
            AcceptsText = acceptsText,
            TextHint = textHint,
            Players = new List<GamePlayerCard>(players ?? []),
            Actions = new List<GameAction>(actions ?? []),
            Scores = new List<ScoreEntry>(scores ?? []),
            Fields = bigMatchNote
                ?
                [
                    new GameField { Key = "werewolf.wolves", Label = "狼人数", Value = "3", Min = 1, Max = 19 },
                    new GameField { Key = "werewolf.seer", Label = "预言家", Value = "1", Kind = "bool", Min = 0, Max = 1 },
                    new GameField { Key = "werewolf.witch", Label = "女巫", Value = "1", Kind = "bool", Min = 0, Max = 1 },
                ]
                : [],
            Board = board ?? [],
        };

        if (deadlineInSeconds is { } seconds)
        {
            view.DeadlineAtMs = 1_000L * seconds;
        }

        var snapshot = new RoomSnapshot
        {
            Code = "7K3QP",
            GameId = gameId,
            GameName = gameName,
            Phase = RoomPhase.Playing,
            HostId = "p1",
            View = view,
        };

        if (hand is not null)
        {
            view.PrivateNote = hand;
        }

        if (log is not null)
        {
            foreach (var (actor, text) in log)
            {
                snapshot.Backlog ??= [];
                snapshot.Backlog.Add(new RoomEvent { Kind = "info", Actor = actor, Text = text, AtMs = 0 });
            }
        }

        return snapshot;
    }

    private static GamePlayerCard Card(string id, string name, bool you = false, bool host = false, string? badge = null, string? sub = null, string? accent = null, int? votes = null, bool dead = false, bool offline = false) =>
        new() { Id = id, Name = name, IsYou = you, IsHost = host, Badge = badge, Sub = sub, Accent = accent, Votes = votes, IsAlive = !dead, IsOffline = offline, IsReady = true };

    private static GameAction Action(string kind, string label, string? target = null, bool danger = false) =>
        new() { Kind = kind, Label = label, Target = target, Danger = danger, Enabled = true };

    private static ScoreEntry Score(string id, string name, int score, int delta, bool you = false) =>
        new() { Id = id, Name = name, Score = score, Delta = delta, IsYou = you };
}
