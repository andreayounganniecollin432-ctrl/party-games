using PartyGames.Protocol;

namespace PartyGames.Server.Games;

/// <summary>
/// Default scoring for a game. <paramref name="BigThreshold"/> 0 disables the
/// "big match" rule where more players means a smaller starting pool.
/// <paramref name="PenaltyRoll"/> makes hitting zero trigger a 1-2 roll.
/// </summary>
public sealed record ScoreDefaults(
    int Start,
    int Penalty,
    int BigThreshold,
    int BigStart,
    bool PenaltyRoll = false);

/// <summary>What just happened, used by the room to settle points.</summary>
public sealed class GameOutcome
{
    public string Banner { get; init; } = string.Empty;

    /// <summary>Display name of the side that lost, e.g. 狼人阵营.</summary>
    public string? LosingSide { get; init; }

    /// <summary>Everyone on the losing side, dead or alive.</summary>
    public List<string> LosingPlayerIds { get; init; } = new();
}

/// <summary>
/// A self-contained match. One instance per game run; the instance itself holds
/// the mutable match state, so nothing survives into the next match.
/// </summary>
public interface IGameModule
{
    string Id { get; }
    string DisplayName { get; }
    string Blurb { get; }
    int MinPlayers { get; }
    int MaxPlayers { get; }

    /// <summary>Whether losing this game costs points.</summary>
    bool SupportsScoring => false;

    /// <summary>Defaults used when the room is created; null when scoring is off.</summary>
    ScoreDefaults? Scoring => null;

    /// <summary>Extra host-configurable options for this game, rendered in the lobby.</summary>
    IReadOnlyList<GameField> LobbyFields(Room room) => Array.Empty<GameField>();

    /// <summary>Applies one of <see cref="LobbyFields"/>. Return false for unknown keys.</summary>
    bool ApplyLobbySetting(Room room, string key, string value) => false;

    void Start(Room room);
    void HandleAction(Room room, Player player, ActionRequest request);
    void Tick(Room room, long nowMs);

    /// <summary>Built per viewer, so hidden information never leaves the server.</summary>
    GameView BuildView(Room room, Player viewer);

    void OnPlayerDisconnected(Room room, Player player);
    void OnPlayerLeft(Room room, Player player);

    /// <summary>
    /// The player took their seat back after a disconnect. Games that hand a
    /// missing player over to 托管 use this to hand control back.
    /// </summary>
    void OnPlayerReconnected(Room room, Player player) { }

    GameDescriptor Describe() => new()
    {
        Id = Id,
        Name = DisplayName,
        Blurb = Blurb,
        MinPlayers = MinPlayers,
        MaxPlayers = MaxPlayers,
        SupportsScoring = SupportsScoring,
    };
}

public static class GameRegistry
{
    private static readonly Func<IGameModule>[] Factories =
    {
        () => new UndercoverGame(),
        () => new RouletteGame(),
        () => new SnowballGame(),
        () => new WerewolfGame(),
        () => new WordChainGame(),
        () => new TurtleSoupGame(),
        () => new DouDizhuGame(),
        () => new XiangqiGame(),
        () => new LobbyGame(),
    };

    public static IGameModule? Create(string id) =>
        Factories.Select(f => f()).FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));

    public static GameDescriptor? Find(string id) =>
        Factories.Select(f => f()).Select(g => g.Describe())
                 .FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<GameDescriptor> All() =>
        Factories.Select(f => f()).Select(g => g.Describe()).ToList();
}
