using Dalamud.Plugin.Services;
using PartyGames.Protocol;

namespace PartyGames;

/// <summary>
/// What the UI needs from the client: state and three verbs. Kept narrow so the window can be drawn from sample
/// state outside the game (see <c>PartyGames.Previews</c>).
/// </summary>
public interface IGameClient
{
    ClientState State { get; }

    string Status { get; }

    string? PlayerId { get; }

    string? PlayerName { get; }

    RoomSnapshot? Room { get; }

    List<GameDescriptor> Games { get; }

    List<RoomEvent> Events { get; }

    int ServerOnline { get; }

    int ServerRooms { get; }

    List<StatRow> MyStats { get; }

    List<string> OnlineNames { get; }

    /// <summary>玩家 id → base64 头像（服务端转发的别人头像）。</summary>
    Dictionary<string, string> Avatars { get; }

    string? LastError { get; }

    bool IsConnected { get; }

    long ServerNowMs { get; }

    Task ConnectAsync(string url, string displayName, string? resumeRoom, string? resumePlayerId);

    Task SendAsync(string type, object? payload);

    void Disconnect();
}

/// <summary>
/// What the UI needs from the plugin. <see cref="Plugin"/> implements it in the game; the preview project implements
/// it with sample state and no Dalamud services.
/// </summary>
public interface IUiHost
{
    Configuration Configuration { get; }

    IGameClient Client { get; }

    /// <summary>Null outside the game: artwork then falls back to the drawn gradient.</summary>
    ITextureProvider? Textures { get; }

    IPluginLog? Log { get; }

    string CharacterName { get; }

    /// <summary>The glass layer, or null when it could not start (the window then draws its own background).</summary>
    Glasswork.Dalamud.GlassworkHost? Glass { get; }

    /// <summary>Dalamud's in-game file browser (an ImGui dialog, so a full-screen game can't hide it).</summary>
    Dalamud.Interface.ImGuiFileDialog.FileDialogManager Dialogs { get; }
}
