using System.Text.Json;
using System.Text.Json.Serialization;

namespace PartyGames.Protocol;

/// <summary>Protocol-wide constants. Bump <see cref="Version"/> on breaking changes.</summary>
public static class Proto
{
    public const int Version = 1;
    public const string WebSocketPath = "/ws";
    public const int MinPlayers = 3;
    public const int MaxPlayers = 20;

    /// <summary>Characters used for room codes. No 0/O/1/I to keep them readable aloud.</summary>
    public const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int CodeLength = 5;
}

/// <summary>Wire message discriminators. Kept as strings so unknown types degrade gracefully.</summary>
public static class Msg
{
    // client -> server
    public const string Hello = "hello";
    public const string CreateRoom = "create";
    public const string JoinRoom = "join";
    public const string LeaveRoom = "leave";
    public const string Ready = "ready";
    public const string StartGame = "start";
    public const string Action = "action";
    public const string Chat = "chat";
    public const string Ping = "ping";

    /// <summary>客户端上传自己的头像（base64）。</summary>
    public const string Avatar = "avatar";

    // server -> client
    public const string Welcome = "welcome";
    public const string Status = "status";
    public const string Reject = "reject";
    public const string Room = "room";
    public const string Event = "event";
    public const string Error = "error";
    public const string Pong = "pong";
}

/// <summary>Every frame on the wire: <c>{"t":"&lt;type&gt;","d":{...}}</c>.</summary>
public sealed class Envelope
{
    [JsonPropertyName("t")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("d")] public JsonElement Data { get; set; }

    public static Envelope Of(string type, object? payload) => new()
    {
        Type = type,
        Data = payload is null ? default : JsonSerializer.SerializeToElement(payload, payload.GetType(), PgJson.Options),
    };
}

public static class PgJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static string SerializeEnvelope(Envelope envelope) => JsonSerializer.Serialize(envelope, Options);

    public static T? Read<T>(JsonElement element) =>
        element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? default
            : element.Deserialize<T>(Options);
}

// ---------------------------------------------------------------------------
// client -> server payloads
// ---------------------------------------------------------------------------

public sealed class HelloRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public string? World { get; set; }
    public string? ContentId { get; set; }
    public int ProtocolVersion { get; set; } = Proto.Version;

    /// <summary>Player id from a previous session, used to take back a seat after a disconnect.</summary>
    public string? ResumePlayerId { get; set; }

    /// <summary>Room code to re-enter together with <see cref="ResumePlayerId"/>.</summary>
    public string? ResumeRoom { get; set; }
}

public sealed class CreateRoomRequest
{
    public string GameId { get; set; } = string.Empty;
    public int MaxPlayers { get; set; } = 8;
}

public sealed class JoinRoomRequest
{
    public string Code { get; set; } = string.Empty;
}

public sealed class ReadyRequest
{
    public bool Ready { get; set; }
}

public sealed class ActionRequest
{
    public string Kind { get; set; } = string.Empty;
    public string? Target { get; set; }
    public string? Text { get; set; }
}

public sealed class ChatRequest
{
    public string Text { get; set; } = string.Empty;
}

public sealed class PingRequest
{
    public long ClientTimeMs { get; set; }
}

/// <summary>Our own avatar, as a base64 image (PNG/JPEG). Sent once when we enter a room.</summary>
public sealed class AvatarUploadRequest
{
    public string ImageBase64 { get; set; } = string.Empty;
}

/// <summary>Someone's avatar, relayed by the server. A null image means it was cleared.</summary>
public sealed class AvatarMessage
{
    public string PlayerId { get; set; } = string.Empty;
    public string? ImageBase64 { get; set; }
}

// ---------------------------------------------------------------------------
// server -> client payloads
// ---------------------------------------------------------------------------

public sealed class WelcomeResponse
{
    public string PlayerId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public long ServerTimeMs { get; set; }
    public string? Notice { get; set; }

    /// <summary>Catalogue of games this server can host.</summary>
    public List<GameDescriptor> Games { get; set; } = new();
}

public sealed class RejectResponse
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class ErrorResponse
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class PongResponse
{
    public long ClientTimeMs { get; set; }
    public long ServerTimeMs { get; set; }
}

public sealed class EventMessage
{
    public RoomEvent Event { get; set; } = new();
}

/// <summary>Server-wide snapshot pushed to players who are not in a room.</summary>
public sealed class StatusMessage
{
    public int Online { get; set; }
    public int Rooms { get; set; }

    /// <summary>Everyone currently connected, in join order.</summary>
    public List<string> OnlinePlayers { get; set; } = new();

    /// <summary>This player's record, by game.</summary>
    public List<StatRow> Stats { get; set; } = new();
}

public sealed class RoomEvent
{
    public string Kind { get; set; } = "info"; // info | system | chat | secret | result
    public string? Actor { get; set; }
    public string Text { get; set; } = string.Empty;
    public long AtMs { get; set; }
}

// ---------------------------------------------------------------------------
// room state: this is the whole client UI contract
// ---------------------------------------------------------------------------

public sealed class RoomSnapshot
{
    public string Code { get; set; } = string.Empty;
    public string GameId { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public string Phase { get; set; } = RoomPhase.Lobby;
    public string HostId { get; set; } = string.Empty;
    public long ServerTimeMs { get; set; }
    public GameView View { get; set; } = new();

    /// <summary>Recent history, only filled in on join so a reconnecting client has context.</summary>
    public List<RoomEvent>? Backlog { get; set; }

    /// <summary>Everybody's avatar, only filled in on join (the same reason as <see cref="Backlog"/>).</summary>
    public List<AvatarMessage>? Avatars { get; set; }
}

public static class RoomPhase
{
    public const string Lobby = "lobby";
    public const string Playing = "playing";
    public const string Finished = "finished";
}

/// <summary>
/// A fully server-authored description of what the client should render.
/// The plugin contains no per-game UI code: it just draws this.
/// </summary>
public sealed class GameView
{
    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public string? Instruction { get; set; }

    /// <summary>棋盘：10 行 × 9 列，大写=红方 小写=黑方，. 表示空。</summary>
    public List<string> Board { get; set; } = new();

    /// <summary>可落点，每项是 "起行起列终行终列" 四位数。</summary>
    public List<string> Targets { get; set; } = new();
    public string? PrivateNote { get; set; }
    public string? PrivateNoteLabel { get; set; }
    public string? ResultBanner { get; set; }
    public bool AcceptsText { get; set; }
    public string? TextHint { get; set; }

    /// <summary>Action kind sent when the text box is submitted.</summary>
    public string TextActionKind { get; set; } = "describe";
    /// <summary>Absolute server timestamp (epoch ms) of the current phase deadline, if any.</summary>
    public long? DeadlineAtMs { get; set; }
    public List<GamePlayerCard> Players { get; set; } = new();
    public List<GameAction> Actions { get; set; } = new();

    /// <summary>Host-editable room settings, rendered as +/- steppers.</summary>
    public List<GameField> Fields { get; set; } = new();

    /// <summary>Ranked scoreboard for the current room session (scoring games only).</summary>
    public List<ScoreEntry> Scores { get; set; } = new();

    /// <summary>This player's all-time record, by game.</summary>
    public List<StatRow> Stats { get; set; } = new();
}

public sealed class StatRow
{
    public string Game { get; set; } = string.Empty;
    public int Wins { get; set; }
    public int Losses { get; set; }
}

public sealed class ScoreEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Score { get; set; }
    public int Delta { get; set; }
    public string? Note { get; set; }
    public bool IsYou { get; set; }
}

/// <summary>A small numeric/toggle setting the host can change from the lobby.</summary>
public sealed class GameField
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = "0";
    public string Kind { get; set; } = "int"; // int | bool
    public int Min { get; set; }
    public int Max { get; set; } = 99;
    public bool Editable { get; set; } = true;
}

public sealed class GamePlayerCard
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsYou { get; set; }
    public bool IsHost { get; set; }
    public bool IsReady { get; set; }
    public bool IsAlive { get; set; } = true;
    public bool IsOffline { get; set; }
    public int? Votes { get; set; }
    public string? Badge { get; set; }
    public string? Sub { get; set; }
    public string? Accent { get; set; } // good | bad | warn | None

    /// <summary>Whether this player uploaded an avatar; the image itself comes in <see cref="RoomSnapshot.Avatars"/>.</summary>
    public bool HasAvatar { get; set; }
}

public sealed class GameAction
{
    public string Kind { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Target { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Danger { get; set; }
}

/// <summary>Game catalogue entry, served over HTTP and embedded in the plugin.</summary>
public sealed class GameDescriptor
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Blurb { get; set; } = string.Empty;
    public int MinPlayers { get; set; } = Proto.MinPlayers;
    public int MaxPlayers { get; set; } = 8;

    /// <summary>Whether losses in this game cost points.</summary>
    public bool SupportsScoring { get; set; }
}
