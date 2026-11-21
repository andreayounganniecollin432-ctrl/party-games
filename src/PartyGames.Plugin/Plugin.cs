using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;

namespace PartyGames;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/pg";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly WindowSystem windowSystem = new("PartyGames");
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

        Client = new GameClient(log);
        mainWindow = new MainWindow(this);
        windowSystem.AddWindow(mainWindow);

        pluginInterface.UiBuilder.Draw += OnDraw;
        pluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;
        pluginInterface.UiBuilder.OpenConfigUi += ToggleMainWindow;

        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开 Party Games 窗口。",
        });

        if (Configuration.OpenOnLogin)
        {
            mainWindow.IsOpen = true;
        }

        log.Information("PartyGames loaded");
    }

    public string Name => "Party Games";

    public Configuration Configuration { get; }

    public GameClient Client { get; }

    public string CharacterName { get; }

    public IPluginLog Log { get; }

    /// <summary>加载玩家头像/背景图片用。</summary>
    public ITextureProvider Textures { get; }

    /// <summary>游戏聊天框（Echo 频道）</summary>
    public IChatGui Chat { get; }


    private void OnDraw()
    {
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

        Toasts.Draw();
        windowSystem.Draw();
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
        Client.Dispose();
    }
}
