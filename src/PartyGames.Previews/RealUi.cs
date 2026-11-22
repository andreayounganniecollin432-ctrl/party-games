using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Glasswork;
using Glasswork.Backend.Preview;
using PartyGames;
using PartyGames.Protocol;

namespace PartyGames.Previews;

/// <summary>
/// 真实界面预览：直接实例化插件里的 <see cref="MainWindow"/>，喂一份样例状态，把它画出来。
/// 画的就是玩家在游戏里看到的那一屏——不是重画的 mock。
/// </summary>
public abstract class RealScreen : IPreviewScreen
{
    private const float Width = 620f;
    private const float Height = 780f;

    private readonly MainWindow window;

    protected RealScreen(string name, IGameClient client, bool glassUi = false, Look? look = null)
    {
        Name = name;
        Ui.ReadOnly = true;
        window = new MainWindow(new FakeUiHost(client));

        // 预览工具自己就有 Glasswork 的 Frame，所以可以把插件里"玻璃模式"的控件路径也画出来。
        if (glassUi)
        {
            Theme.Glass = true;
            Look = look ?? Look.Liquid().WithAccent(Color.FromVector4(new Vector4(0.89f, 0.76f, 0.45f, 1.00f)));
        }
    }

    public string Name { get; }

    public Vector2 WindowSize { get; } = new(Width + 40f, Height + 40f);

    public Look Look { get; protected set; } = Look.NeutralDark();

    public ReadOnlySpan<PreviewInput> Script => [];

    public void Draw()
    {
        ImGui.SetNextWindowPos(ImGui.GetCursorScreenPos(), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(Width, Height), ImGuiCond.Always);
        const ImGuiWindowFlags Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                                        ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6f);
        if (ImGui.Begin("##partygames-real", Flags))
        {
            window.Draw();
        }

        ImGui.End();
        ImGui.PopStyleVar();
    }
}

/// <summary>连接页：还没连上服务器时玩家看到的东西。</summary>
public sealed class RealConnectScreen : RealScreen
{
    public RealConnectScreen() : base("real-connect", FakeGameClient.Disconnected()) { }
}

/// <summary>连接页，玻璃控件版。</summary>
public sealed class GlassConnectScreen : RealScreen
{
    public GlassConnectScreen() : base("glass-connect", FakeGameClient.Disconnected(), glassUi: true) { }
}

/// <summary>选游戏：已连接、没进房间。</summary>
public sealed class RealLobbyScreen : RealScreen
{
    public RealLobbyScreen() : base("real-lobby", FakeGameClient.InLobby()) { }
}

/// <summary>选游戏，但走玻璃控件（游戏内的样子）。</summary>
public sealed class GlassLobbyScreen : RealScreen
{
    public GlassLobbyScreen() : base("glass-lobby", FakeGameClient.InLobby(), glassUi: true) { }
}

/// <summary>对局页，走玻璃控件。</summary>
public sealed class GlassRoomScreen : RealScreen
{
    public GlassRoomScreen() : base("glass-room", FakeGameClient.InRoom(Mocks.Undercover()), glassUi: true) { }
}

/// <summary>对局中：谁是卧底，描述阶段。</summary>
public sealed class RealUndercoverScreen : RealScreen
{
    public RealUndercoverScreen() : base("real-undercover", FakeGameClient.InRoom(Mocks.Undercover())) { }
}

/// <summary>对局中：狼人杀的夜晚。</summary>
public sealed class RealWerewolfScreen : RealScreen
{
    public RealWerewolfScreen() : base("real-werewolf", FakeGameClient.InRoom(Mocks.Werewolf())) { }
}

/// <summary>对局中：斗地主的牌桌。</summary>
public sealed class RealDoudizhuScreen : RealScreen
{
    public RealDoudizhuScreen() : base("real-doudizhu", FakeGameClient.InRoom(Mocks.Doudizhu())) { }
}

/// <summary>对局中：象棋的棋盘。</summary>
public sealed class RealXiangqiScreen : RealScreen
{
    public RealXiangqiScreen() : base("real-xiangqi", FakeGameClient.InRoom(Mocks.Xiangqi())) { }
}

public sealed class FakeUiHost : IUiHost
{
    public FakeUiHost(IGameClient client) => Client = client;

    public Configuration Configuration { get; } = new();

    public IGameClient Client { get; }

    public ITextureProvider? Textures => null;

    public IPluginLog? Log => null;

    public string CharacterName => "鹿岛渚咲";

    // 预览工具自己准备 ImGui 上下文，不跑 Dalamud 的玻璃层；这条路径就是"没有玻璃"的兜底外观。
    public Glasswork.Dalamud.GlassworkHost? Glass => null;

    public Dalamud.Interface.ImGuiFileDialog.FileDialogManager Dialogs { get; } = new();
}

/// <summary>The client's visible state, set by hand instead of by a socket.</summary>
public sealed class FakeGameClient : IGameClient
{
    public ClientState State { get; set; } = ClientState.Connected;

    public string Status { get; set; } = "已连接";

    public string? PlayerId { get; set; } = "p1";

    public string? PlayerName { get; set; } = "鹿岛渚咲";

    public RoomSnapshot? Room { get; set; }

    public List<GameDescriptor> Games { get; } = [];

    public List<RoomEvent> Events { get; } = [];

    public int ServerOnline { get; set; }

    public int ServerRooms { get; set; }

    public List<StatRow> MyStats { get; } = [];

    public List<string> OnlineNames { get; } = [];

    public Dictionary<string, string> Avatars { get; } = [];

    public string? LastError { get; set; }

    public bool IsConnected => State == ClientState.Connected;

    public long ServerNowMs => 1_000;

    public static FakeGameClient Disconnected() => new()
    {
        State = ClientState.Disconnected,
        Status = "未连接",
        PlayerId = null,
        PlayerName = null,
    };

    public static FakeGameClient InLobby()
    {
        var client = new FakeGameClient { ServerOnline = 12, ServerRooms = 2 };
        client.Games.AddRange(Catalog.Games);
        client.OnlineNames.AddRange(["鹿岛渚咲", "夜露", "白鸦"]);
        client.MyStats.AddRange(Catalog.MyStats);
        return client;
    }

    public static FakeGameClient InRoom(RoomSnapshot room)
    {
        var client = new FakeGameClient();
        client.Games.AddRange(Catalog.Games);
        client.ServerOnline = 12;
        client.ServerRooms = 2;

        // 倒计时按"还有 27 秒"来，Mock 里的截止时间是相对值。
        if (room.View.DeadlineAtMs is { } deadline)
        {
            room.View.DeadlineAtMs = client.ServerNowMs + deadline;
        }

        client.Room = room;

        if (room.Backlog is { } backlog)
        {
            client.Events.AddRange(backlog);
            room.Backlog = null;
        }

        return client;
    }

    public Task ConnectAsync(string url, string displayName, string? resumeRoom, string? resumePlayerId)
    {
        State = ClientState.Connected;
        Status = "已连接";
        PlayerName = displayName;
        return Task.CompletedTask;
    }

    public Task SendAsync(string type, object? payload) => Task.CompletedTask;

    public void Disconnect()
    {
        State = ClientState.Disconnected;
        Room = null;
    }
}
