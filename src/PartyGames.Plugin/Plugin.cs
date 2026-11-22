using System.Numerics;
using Dalamud.Game.Command;
using Dalamud.Interface.ImGuiFileDialog;
using Glasswork;
using Glasswork.Dalamud;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;

namespace PartyGames;

public sealed class Plugin : IDalamudPlugin, IUiHost
{
    private const string CommandName = "/pg";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly WindowSystem windowSystem = new("PartyGames");

    /// <summary>游戏内文件浏览器：画在 ImGui 里，不会被游戏窗口盖住。</summary>
    private readonly FileDialogManager fileDialog = new();
    private readonly MainWindow mainWindow;
    private int lastToastIndex;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPluginLog log,
        IPlayerState playerState,
        ITextureProvider textureProvider,
        IChatGui chatGui)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;

        Log = log;
        Textures = textureProvider;
        Chat = chatGui;
        CharacterName = playerState.CharacterName;


        FilePicker.Diag = (m) => log.Information(m);

        Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(pluginInterface);

        // 液化玻璃：起不来就退回原来的自绘背景，绝不让插件因为玻璃挂掉。
        if (Configuration.GlassEnabled)
        {
            try
            {
                Glass = new GlassworkHost(pluginInterface, log);

                // 液化玻璃 + 金色主色，和插件原来的"水墨金边"对上
                Glass.Look = Look.Liquid().WithAccent(Color.FromVector4(new Vector4(0.89f, 0.76f, 0.45f, 1.00f)));
            }
            catch (Exception exception)
            {
                log.Warning(exception, "PartyGames: glass unavailable, drawing the flat window instead");
                Glass = null;
            }
        }

        Client = new GameClient(log);
        mainWindow = new MainWindow(this);
        windowSystem.AddWindow(mainWindow);

        pluginInterface.UiBuilder.Draw += OnDraw;
        pluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;
        pluginInterface.UiBuilder.OpenConfigUi += ToggleMainWindow;

        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开 Coral's Games 窗口。",
        });

        if (Configuration.OpenOnLogin)
        {
            mainWindow.IsOpen = true;
        }

        log.Information("PartyGames loaded");
    }

    public string Name => "Coral's Games";

    public Configuration Configuration { get; }

    public GameClient Client { get; }

    IGameClient IUiHost.Client => Client;

    public FileDialogManager Dialogs => fileDialog;

    public string CharacterName { get; }

    public IPluginLog Log { get; }

    /// <summary>加载玩家头像/背景图片用。</summary>
    public ITextureProvider Textures { get; }

    /// <summary>游戏聊天框（Echo 频道）</summary>
    public IChatGui Chat { get; }

    /// <summary>玻璃层；null 表示没起来，窗口自己画背景。</summary>
    public GlassworkHost? Glass { get; private set; }


    private void OnDraw()
    {
        // 屏幕提示停留时间以设置为准（设置页 2-10 秒）
        Toasts.Seconds = Configuration.ToastSeconds;

        // Drain network frames on the UI thread before drawing anything.
        Client.Pump();

        if (Client.Room is not null && Configuration.LastRoomCode != Client.Room.Code)
        {
            Configuration.LastRoomCode = Client.Room.Code;
            Configuration.LastPlayerId = Client.PlayerId ?? string.Empty;
            Configuration.Save();
        }

        // 常驻：窗口关着也接收聊天提示
        if (Configuration.SyncChatToGame)
        {
            var events = Client.Events;
            if (events.Count > lastToastIndex)
            {
                for (var i = lastToastIndex; i < events.Count; i++)
                {
                    var e = events[i];
                    if (e.Kind != "chat") continue;
                    var who = string.IsNullOrEmpty(e.Actor) ? "派对" : e.Actor;
                    Toasts.Push($"{who}：{e.Text}");
                }
                lastToastIndex = events.Count;
            }
        }

        if (Glass is { } glass)
        {
            try
            {
                glass.BeginFrame();
            }
            catch (Exception exception)
            {
                DisableGlass(exception);
            }
        }

        Toasts.Draw();
        fileDialog.Draw();
        windowSystem.Draw();

        if (Glass is { } closing)
        {
            try
            {
                closing.EndFrame();
            }
            catch (Exception exception)
            {
                DisableGlass(exception);
            }
        }
    }

    /// <summary>Turns the glass off for this session after a failure, so the window falls back to its own background.</summary>
    private void DisableGlass(Exception exception)
    {
        Log.Warning(exception, "PartyGames: glass failed, falling back");
        try
        {
            Glass?.Dispose();
        }
        catch
        {
            // shutting down anyway
        }

        Glass = null;
    }

    private void OnCommand(string command, string args) => ToggleMainWindow();

    public void ToggleMainWindow() => mainWindow.Toggle();

    public void Dispose()
    {
        pluginInterface.UiBuilder.Draw -= OnDraw;
        pluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;
        pluginInterface.UiBuilder.OpenConfigUi -= ToggleMainWindow;

        commandManager.RemoveHandler(CommandName);
        windowSystem.RemoveAllWindows();
        Glass?.Dispose();
        Client.Dispose();
    }
}
