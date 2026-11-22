using PartyGames.Protocol;

namespace PartyGames.Previews;

/// <summary>One entry per game, each drawing the room screen from that game's own snapshot.</summary>
public sealed class UndercoverScreen : RoomScreen
{
    public UndercoverScreen() : base("game-undercover") { }

    protected override RoomSnapshot Snapshot { get; } = Mocks.Undercover();
}

/// <summary>狼人杀, mid night with the wolves choosing.</summary>
public sealed class WerewolfScreen : RoomScreen
{
    public WerewolfScreen() : base("game-werewolf") { }

    protected override RoomSnapshot Snapshot { get; } = Mocks.Werewolf();
}

/// <summary>俄罗斯轮盘, someone's turn under the gun.</summary>
public sealed class RouletteScreen : RoomScreen
{
    public RouletteScreen() : base("game-roulette") { }

    protected override RoomSnapshot Snapshot { get; } = Mocks.Roulette();
}

/// <summary>堆雪球, mid round.</summary>
public sealed class SnowballScreen : RoomScreen
{
    public SnowballScreen() : base("game-snowball") { }

    protected override RoomSnapshot Snapshot { get; } = Mocks.Snowball();
}

/// <summary>词语接龙, waiting on your word.</summary>
public sealed class WordChainScreen : RoomScreen
{
    public WordChainScreen() : base("game-wordchain") { }

    protected override RoomSnapshot Snapshot { get; } = Mocks.WordChain();
}

/// <summary>海龟汤, the host answering questions.</summary>
public sealed class TurtleSoupScreen : RoomScreen
{
    public TurtleSoupScreen() : base("game-turtlesoup") { }

    protected override RoomSnapshot Snapshot { get; } = Mocks.TurtleSoup();
}

/// <summary>斗地主, your hand and the table.</summary>
public sealed class DoudizhuScreen : RoomScreen
{
    public DoudizhuScreen() : base("game-doudizhu") { }

    protected override RoomSnapshot Snapshot { get; } = Mocks.Doudizhu();
}

/// <summary>中国象棋, the board and its pieces.</summary>
public sealed class XiangqiScreen : RoomScreen
{
    public XiangqiScreen() : base("game-xiangqi") { }

    protected override RoomSnapshot Snapshot { get; } = Mocks.Xiangqi();
}
