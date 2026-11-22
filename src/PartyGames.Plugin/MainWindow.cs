using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Windowing;
using Glasswork;
using Glasswork.Backend;
using PartyGames.Protocol;

namespace PartyGames;

/// <summary>
/// The whole plugin UI. It renders whatever <see cref="GameView"/> the server sent,
/// so adding a new game on the server needs no changes here.
/// </summary>
public sealed class MainWindow : Window
{
    private static readonly Vector4 Accent = new(0.55f, 0.82f, 1.00f, 1.00f);
    private static readonly Vector4 Gold = new(1.00f, 0.84f, 0.38f, 1.00f);
    private static readonly Vector4 Good = new(0.50f, 0.95f, 0.58f, 1.00f);
    private static readonly Vector4 Warn = new(1.00f, 0.50f, 0.45f, 1.00f);
    private static readonly Vector4 Muted = new(0.65f, 0.65f, 0.68f, 1.00f);

    /// <summary>Watermark in the corner: the assembly version, so a build never lies about which one it is.</summary>
    private static readonly string PluginVersion = "v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    private readonly IUiHost host;
    private string serverUrl;
    private string displayName;
    private string joinCode = string.Empty;
    private string chatInput = string.Empty;
    private bool refocusChat;
    private bool chatParked;
    private bool parkBlocked;
    private string actionInput = string.Empty;
    private int maxPlayers;
    private string lastAnnounce = string.Empty;
    private bool lastHadActions;
    private List<string> images = new();
    private int lastLogCount;
    private readonly List<int> pickedCards = new();
    private string lastHandText = string.Empty;
    private int lastVoiceLogCount;
    private int lastEchoCount;
    private int xqSelR = -1;
    private int xqSelC = -1;
    private string xqBoardKey = string.Empty;
    private string avatarPath = string.Empty;
    private string backgroundPath = string.Empty;
    private readonly Artwork artwork;
    private readonly RemoteAvatars remoteAvatars = new();
    private string lastAvatarRoom = string.Empty;
    private string avatarError = string.Empty;
    private bool showAvatarPath;
    private string avatarPathInput = string.Empty;

    /// <summary>头像分辨率上限（宽和高都不能超）。</summary>
    private const int AvatarMaxSide = 1000;
    private bool collapsed;
    private readonly Vector2 expandedSize = new(760, 900);

    public MainWindow(IUiHost host)
        : base("Coral's Games###PartyGamesMainWindow")
    {
        this.host = host;
        Size = new Vector2(620, 780);
        SizeCondition = ImGuiCond.FirstUseEver;

        // 玻璃层自己负责背景，控件也换成 Glasswork 的；起不来就还是原来的 ImGui 外观
        if (host.Glass is not null)
        {
            // 玻璃自己就是整个窗口的外观：不要 ImGui 的标题栏和底板
            Flags |= ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoTitleBar;
            Theme.Glass = true;
            Size = new Vector2(760, 900);
        }

        serverUrl = host.Configuration.ServerUrl;
        displayName = string.IsNullOrWhiteSpace(host.Configuration.DisplayName)
            ? host.CharacterName
            : host.Configuration.DisplayName;
        maxPlayers = Math.Clamp(host.Configuration.PreferredMaxPlayers, Proto.MinPlayers, Proto.MaxPlayers);
        avatarPath = host.Configuration.AvatarPath;
        backgroundPath = host.Configuration.BackgroundPath;
        artwork = new Artwork(host.Textures, host.Log);

        // 老配置里可能指着一张超大图：提示一下，并且不会上传
        if (!string.IsNullOrEmpty(avatarPath))
        {
            try
            {
                using var image = System.Drawing.Image.FromFile(ResolveAvatarPath() ?? avatarPath);
                if (image.Width > AvatarMaxSide || image.Height > AvatarMaxSide)
                {
                    avatarError = $"当前头像 {image.Width}×{image.Height} 超过 {AvatarMaxSide}×{AvatarMaxSide}，不会上传，请重新选择。";
                }
            }
            catch
            {
                avatarError = "当前头像读不出来，请重新选择。";
            }
        }
    }

    public override void Draw()
    {
        Theme.PushWindowStyle();
        try
        {
            DrawInner();
        }
        finally
        {
            Theme.PopWindowStyle();
        }
    }

    private void DrawInner()
    {
        var client = host.Client;

        // 连接页 / 大厅 / 收起状态都没有聊天框：别把键盘一直按着不放
        if (client.Room is null || collapsed) ReleaseChatFocus();

        // 头像每帧同步（玻璃模式下背景不用图，但头像要用）
        artwork.Sync(string.Empty, ResolveAvatarPath() ?? string.Empty);
        remoteAvatars.Sync(client, host.Textures);
        TrySendAvatar(client);

        // 外貌：玻璃层铺在窗口底下，折射的是游戏画面；没有玻璃才退回自绘背景图。
        if (host.Glass is { } glass)
        {
            glass.DrawWindowBackground(22f, 0.55f, glass.Look.IsLight);
        }
        else
        {
            artwork.DrawBackground(host.Configuration.BackgroundDim);
        }

        // 窗口按钮放在背景之后画，否则会被背景盖住
        if (Theme.Glass)
        {
            // 记下内容起点：SetCursorPos(0,0) 会跑到窗口左上角（圆角外），头像会显示不全
            var contentStart = new Vector2(ImGui.GetCursorPosX(), ImGui.GetCursorPosY());
            DrawGlassControls();
            ImGui.SetCursorPos(contentStart);

            if (collapsed)
            {
                DrawCollapsedHeader(client);
                return;
            }
        }

        // 版本水印：用于确认跑的是哪个 build（玻璃模式下版本号在页眉里，右上角留给关闭按钮）
        if (!Theme.Glass)
        {
            try
            {
                var dl = ImGui.GetForegroundDrawList();
                var wp = ImGui.GetWindowPos();
                var ws = ImGui.GetWindowSize();
                dl.AddText(new Vector2(wp.X + ws.X - 78, wp.Y + 4),
                    ImGui.GetColorU32(new Vector4(0.85f, 0.75f, 0.45f, 0.9f)), PluginVersion);
            }
            catch
            {
                // 画水印失败不影响任何功能
            }
        }
        if (!client.IsConnected)
        {
            DrawConnect(client);
            return;
        }

        DrawTopBar(client);

        if (client.Room is null)
        {
            DrawLobby(client);
        }
        else
        {
            DrawRoom(client);
        }
    }

    // -- connect ----------------------------------------------------------

    private void DrawConnect(IGameClient client)
    {
        // 自定义头像（只在这两页显示）
        var avatarStart = ImGui.GetCursorScreenPos();
        const float avatarSize = 44f;
        ImGui.SetCursorScreenPos(new Vector2(avatarStart.X + avatarSize + 12f, avatarStart.Y));

        Theme.Header("跨服文字游戏 · 未连接", false);
        Avatars.Draw(ImGui.GetWindowDrawList(), artwork, avatarStart, avatarSize, host.CharacterName, true);
        Theme.Section("连接");

        ImGui.SetNextItemWidth(360);
        Ui.InputText("服务器", ref serverUrl, 200);

        ImGui.SetNextItemWidth(360);
        Ui.InputText("昵称", ref displayName, 24);

        ImGui.Spacing();

        var connecting = client.State == ClientState.Connecting;

        if (connecting) ImGui.BeginDisabled();
        if (Theme.PrimaryButton(connecting ? "连接中…" : "连接", new Vector2(120, 0)))
        {
            SaveSettings();
            _ = client.ConnectAsync(serverUrl.Trim(), displayName.Trim(),
                host.Configuration.LastRoomCode, host.Configuration.LastPlayerId);
        }

        if (!connecting && !string.IsNullOrWhiteSpace(host.Configuration.LastRoomCode))
        {
            ImGui.SameLine();
            if (Theme.GhostButton($"回到房间 {host.Configuration.LastRoomCode}", new Vector2(200, 0)))
            {
                SaveSettings();
                _ = client.ConnectAsync(serverUrl.Trim(), displayName.Trim(),
                    host.Configuration.LastRoomCode, host.Configuration.LastPlayerId);
            }
        }

        if (connecting) ImGui.EndDisabled();

        ImGui.Spacing();
        TextMuted(client.Status);

        if (!string.IsNullOrEmpty(client.LastError))
        {
            TextColored(Warn, client.LastError!);
        }

        ImGui.Spacing();
        Theme.Label("把房间码发给朋友，3-20 人就能开局。");

        Theme.Section("提示");

        var sync = host.Configuration.SyncChatToGame;
        if (Toggle("聊天消息用屏幕提示显示（不占游戏聊天框）", ref sync))
        {
            host.Configuration.SyncChatToGame = sync;
            host.Configuration.Save();
        }

        // 提示条停留时间：2-10 秒，改完立刻生效
        var toastSeconds = Math.Clamp(host.Configuration.ToastSeconds, 2, 10);
        Theme.PushSliderColors();
        ImGui.SetNextItemWidth(220f);
        var toastChanged = SliderInt("屏幕提示停留（秒）", ref toastSeconds, 2, 10);
        Theme.PopSliderColors();
        if (toastChanged)
        {
            host.Configuration.ToastSeconds = Math.Clamp(toastSeconds, 2, 10);
            host.Configuration.Save();
        }

        Theme.Label($"提示条在屏幕顶部停 {toastSeconds} 秒，连发时最多叠 5 条。");

        Theme.Section("外观（头像会上传到服务器，房间内所有人都能看到）");

        // 头像：点按钮开 Windows 文件对话框，超过 1000×1000 的直接拒绝
        if (Theme.Glass ? Gw.Button("选择头像图片…", ButtonStyle.Glass, ButtonSize.Small)
                        : Theme.GhostButton("选择头像图片…", new Vector2(150, 0)))
        {
            host.Dialogs.OpenFileDialog("选择头像（不超过 1000×1000）", ".png,.jpg,.jpeg,.bmp", (ok, path) =>
            {
                if (ok && !string.IsNullOrEmpty(path))
                {
                    UseAvatar(path);
                }
            });
        }

        ImGui.SameLine(0f, 8f);
        if (Theme.Glass ? Gw.Button("清除", ButtonStyle.Glass, ButtonSize.Small)
                        : Theme.GhostButton("清除", new Vector2(56, 0)))
        {
            ClearAvatar();
        }

        ImGui.SameLine(0f, 8f);
        if (Theme.Glass ? Gw.Button("粘贴路径", ButtonStyle.Glass, ButtonSize.Small)
                        : Theme.GhostButton("粘贴路径", new Vector2(96, 0)))
        {
            showAvatarPath = !showAvatarPath;
        }

        ImGui.SameLine(0f, 8f);
        Theme.Label(string.IsNullOrEmpty(avatarPath) ? "没有选图，用名字首字" : System.IO.Path.GetFileName(avatarPath));

        // 兜底：对话框万一被游戏窗口挡住，可以直接贴完整路径
        if (showAvatarPath)
        {
            ImGui.SetNextItemWidth(360f);
            Ui.InputText("##avatarpath", ref avatarPathInput, 260);
            ImGui.SameLine(0f, 8f);
            if (Theme.PrimaryButton("使用这个路径", new Vector2(104, 0)))
            {
                UseAvatar(avatarPathInput.Trim().Trim('"'));
            }
        }

        if (!string.IsNullOrEmpty(avatarError))
        {
            TextColored(Warn, avatarError);
        }

        Theme.Label("只接受 1000×1000 以内的 PNG/JPG；换图后进房间会自动重新上传。");
        var dim = host.Configuration.BackgroundDim;
        ImGui.SetNextItemWidth(200);
        Theme.PushSliderColors();
        ImGui.SetNextItemWidth(220f);
        var dimChanged = SliderInt("背景压暗", ref dim, 0, 100);
        Theme.PopSliderColors();
        if (dimChanged)
        {
            host.Configuration.BackgroundDim = dim;
            host.Configuration.Save();
        }

        Theme.Label("图片只保存在你本地，不会上传。");

        Theme.Section("怎么玩");
        Theme.Label("1. 在上面填服务器地址，点「连接」。");
        Theme.Label("2. 在选游戏页挑一个，或者输朋友给的 5 位房间码。");
        Theme.Label("3. 一个人也能玩：房主在房间里把「机器人」调上来，座位就坐满了。");
    }

    // -- lobby ------------------------------------------------------------

    private void DrawTopBar(IGameClient client)
    {
        // 头像最后画：页眉里有一条分隔线，先画头像的话线会从脸上穿过去，看着像被裁了一刀
        var avatarStart = ImGui.GetCursorScreenPos();
        const float avatarSize = 30f;

        ImGui.SetCursorScreenPos(new Vector2(avatarStart.X + avatarSize + 10f, avatarStart.Y));
        Theme.Header($"已连接 · {client.PlayerName ?? "?"}" + (client.Room is not null ? $" · 房间 {client.Room.Code}" : ""), true);

        Avatars.Draw(ImGui.GetWindowDrawList(), artwork, avatarStart, avatarSize, host.CharacterName, true);

        if (Theme.GhostButton("断开", new Vector2(72, 0)))
        {
            client.Disconnect();
            return;
        }

        ImGui.SameLine();
        var syncOn = host.Configuration.SyncChatToGame;
        if (Toggle("聊天提示", ref syncOn))
        {
            host.Configuration.SyncChatToGame = syncOn;
            host.Configuration.Save();
        }

        Theme.Divider();
        ImGui.Spacing();
    }

    private void DrawLobby(IGameClient client)
    {
        TextMuted($"服务器在线 {client.ServerOnline} 人 · {client.ServerRooms} 个房间");

        if (client.OnlineNames.Count > 0)
        {
            TextWrapped($"在线玩家：{string.Join("、", client.OnlineNames)}");
        }

        if (client.MyStats.Count > 0)
        {
            DrawStatRows(client.MyStats, "我的战绩");
        }
        else
        {
            TextMuted("我的战绩：暂无记录（打完一局就有了）");
        }

        Theme.Section("开始一局");

        if (Theme.Glass)
        {
            DrawLobbyActionsGlass(client);
        }
        else
        {
            if (Theme.PrimaryButton("进入聊天大厅", new Vector2(132, 0)))
            {
                _ = client.SendAsync(Msg.JoinRoom, new JoinRoomRequest { Code = "LOBBY" });
            }

            ImGui.SameLine(0f, 14f);
            Theme.Label("人数上限");
            ImGui.SameLine(0f, 6f);
            if (Theme.Stepper(ref maxPlayers, Proto.MinPlayers, Proto.MaxPlayers, "##maxplayers"))
            {
                host.Configuration.PreferredMaxPlayers = maxPlayers;
                SaveSettings();
            }

            ImGui.SameLine(0f, 16f);
            Theme.Label("房间码");
            ImGui.SameLine(0f, 6f);
            ImGui.SetNextItemWidth(96f);
            Ui.InputText("##joincode", ref joinCode, 8);
            ImGui.SameLine(0f, 6f);
            if (Theme.PrimaryButton("加入", new Vector2(64, 0)))
            {
                var code = joinCode.Trim().ToUpperInvariant();
                if (code.Length > 0)
                {
                    _ = client.SendAsync(Msg.JoinRoom, new JoinRoomRequest { Code = code });
                }
            }

            ImGui.Spacing();
        }

        if (client.Games.Count == 0)
        {
            TextMuted("服务器没有报告任何游戏。");
        }

        foreach (var game in client.Games)
        {
            // 聊天大厅有单独的入口按钮，不在目录里重复出现
            if (game.Id == "lobby") continue;

            ImGui.PushID(game.Id);
            DrawGameRow(client, game);
            ImGui.PopID();
        }

    }

    /// <summary>收起来时的那一行：名字 + 房间 + 状态。</summary>
    private void DrawCollapsedHeader(IGameClient client)
    {
        var room = client.Room is { } snapshot ? $" · 房间 {snapshot.Code}" : string.Empty;
        var subtitle = client.IsConnected ? $"已连接 · {client.PlayerName ?? "?"}{room}" : "跨服文字游戏 · 未连接";
        Theme.Header(subtitle, client.IsConnected);
    }

    /// <summary>选好的头像：先验分辨率，再存配置并让服务端重传。</summary>
    private void UseAvatar(string path)
    {
        avatarError = string.Empty;

        try
        {
            using var image = System.Drawing.Image.FromFile(path);
            if (image.Width > AvatarMaxSide || image.Height > AvatarMaxSide)
            {
                avatarError = $"这张图是 {image.Width}×{image.Height}，超过 {AvatarMaxSide}×{AvatarMaxSide}，不能用作头像。";
                return;
            }
        }
        catch (Exception exception)
        {
            avatarError = "读不出这张图：" + exception.Message;
            return;
        }

        avatarPath = path;
        host.Configuration.AvatarPath = path;
        host.Configuration.Save();
        artwork.Invalidate();
        lastAvatarRoom = string.Empty;   // 换图了，进房间时重传
    }

    private void ClearAvatar()
    {
        avatarPath = string.Empty;
        avatarError = string.Empty;
        host.Configuration.AvatarPath = string.Empty;
        host.Configuration.Save();
        artwork.Invalidate();
        lastAvatarRoom = string.Empty;
    }

    /// <summary>进入新房间时把自己的头像传上去（服务端转给全房）。</summary>
    private void TrySendAvatar(IGameClient client)
    {
        var room = client.Room;

        if (room is null)
        {
            lastAvatarRoom = string.Empty;
            return;
        }

        if (room.Code == lastAvatarRoom)
        {
            return;
        }

        lastAvatarRoom = room.Code;

        var path = ResolveAvatarPath();
        if (path is null)
        {
            return;
        }

        var payload = AvatarUpload.Prepare(path, out var reason);
        if (payload is null)
        {
            host.Log?.Information($"[PartyGames] 头像未上传：{reason}");
            return;
        }

        _ = client.SendAsync(Msg.Avatar, new AvatarUploadRequest { ImageBase64 = payload });
        host.Log?.Information($"[PartyGames] 已上传头像（{reason}）");
    }

    /// <summary>
    /// 配置里存的可能是相对路径（images\xxx.png）。老版本是按游戏工作目录解析的，
    /// 所以这里按「插件目录 → 游戏工作目录 → 游戏 exe 目录」依次找，第一个存在的就算。
    /// </summary>
    private string? ResolveAvatarPath()
    {
        var configured = host.Configuration.AvatarPath;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        if (System.IO.Path.IsPathRooted(configured))
        {
            return System.IO.File.Exists(configured) ? configured : null;
        }

        foreach (var root in AvatarRoots())
        {
            if (string.IsNullOrEmpty(root)) continue;

            var candidate = System.IO.Path.Combine(root, configured);
            if (System.IO.File.Exists(candidate))
            {
                return candidate;
            }
        }

        // 都没找到：把第一个候选路径交回去，让上层报"读不出来"
        var fallback = System.IO.Path.GetDirectoryName(typeof(MainWindow).Assembly.Location);
        return fallback is null ? configured : System.IO.Path.Combine(fallback, configured);
    }

    private static IEnumerable<string> AvatarRoots()
    {
        yield return System.IO.Path.GetDirectoryName(typeof(MainWindow).Assembly.Location) ?? string.Empty;
        yield return Environment.CurrentDirectory;
        yield return System.IO.Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty;
    }

    /// <summary>玻璃模式下没有 ImGui 标题栏：右上角补两个胶囊按钮，收起 / 关闭。</summary>
    private void DrawGlassControls()
    {
        // 用内容区坐标 + SameLine 摆到行尾：Glasswork 的按钮要求参与 ImGui 的布局流
        var rowY = 6f;
        ImGui.SetCursorPos(new Vector2(0f, rowY));
        ImGui.Dummy(new Vector2(0f, 0f));
        ImGui.SameLine(MathF.Max(0f, ImGui.GetContentRegionAvail().X - 104f));
        if (Gw.Button(collapsed ? "展开" : "收起", ButtonStyle.Glass, ButtonSize.Small))
        {
            SetCollapsed(!collapsed);
        }

        ImGui.SameLine(0f, 6f);
        if (Gw.Button("关闭", ButtonStyle.Glass, ButtonSize.Small))
        {
            IsOpen = false;
        }
    }

    /// <summary>收起时只留一条页眉，高度压到一行。</summary>
    private void SetCollapsed(bool value)
    {
        collapsed = value;
        ImGui.SetWindowSize(collapsed ? new Vector2(ImGui.GetWindowWidth(), 54f) : expandedSize);
    }

    /// <summary>聊天大厅 / 人数上限 / 房间码，玻璃下各占一行——胶囊控件比原生控件宽，挤一行会换行错位。</summary>
    private void DrawLobbyActionsGlass(IGameClient client)
    {
        Gw.BeginRow("聊天大厅", "不玩牌，只聊天");
        if (Gw.Button("进入", ButtonStyle.Default, ButtonSize.Small))
        {
            _ = client.SendAsync(Msg.JoinRoom, new JoinRoomRequest { Code = "LOBBY" });
        }

        Gw.EndRow();

        Gw.BeginRow("人数上限", "开局时的座位数");
        if (Gw.Button("-", ButtonStyle.Glass, ButtonSize.Small))
        {
            maxPlayers = Math.Max(Proto.MinPlayers, maxPlayers - 1);
            host.Configuration.PreferredMaxPlayers = maxPlayers;
            SaveSettings();
        }

        ImGui.SameLine(0f, 6f);
        Theme.Value(maxPlayers.ToString());
        ImGui.SameLine(0f, 6f);
        if (Gw.Button("+", ButtonStyle.Glass, ButtonSize.Small))
        {
            maxPlayers = Math.Min(Proto.MaxPlayers, maxPlayers + 1);
            host.Configuration.PreferredMaxPlayers = maxPlayers;
            SaveSettings();
        }

        Gw.EndRow();

        Gw.BeginRow("房间码", "5 位，不区分大小写");
        ImGui.SetNextItemWidth(180f);
        Ui.InputText("##joincode", ref joinCode, 8);
        if (Gw.Button("加入", ButtonStyle.Prominent, ButtonSize.Small))
        {
            var code = joinCode.Trim().ToUpperInvariant();
            if (code.Length > 0)
            {
                _ = client.SendAsync(Msg.JoinRoom, new JoinRoomRequest { Code = code });
            }
        }

        Gw.EndRow();
        Gw.Divider();
    }

    /// <summary>One catalogue row: name, blurb, player range and the create button, inside a card.</summary>
    private void DrawGameRow(IGameClient client, GameDescriptor game)
    {
        if (Theme.Glass)
        {
            Gw.BeginRow(game.Name, game.Blurb, $"{game.MinPlayers}-{game.MaxPlayers} 人");
            if (Gw.Button("创建房间", ButtonStyle.Prominent, ButtonSize.Small))
            {
                var size = Math.Clamp(maxPlayers, game.MinPlayers, game.MaxPlayers);
                host.Configuration.PreferredMaxPlayers = size;
                SaveSettings();
                _ = client.SendAsync(Msg.CreateRoom, new CreateRoomRequest { GameId = game.Id, MaxPlayers = size });
            }

            Gw.EndRow();
            return;
        }

        const float height = 48f;
        var topLeft = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;

        Theme.Card(topLeft, width, height);

        var buttonWidth = 96f;
        var textWidth = width - buttonWidth - 34f;

        ImGui.SetCursorScreenPos(new Vector2(topLeft.X + 10f, topLeft.Y + 7f));
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Gold);
        ImGui.TextUnformatted(game.Name);
        ImGui.PopStyleColor();
        ImGui.SameLine(0f, 8f);
        Theme.Label($"{game.MinPlayers}-{game.MaxPlayers} 人");

        ImGui.SetCursorScreenPos(new Vector2(topLeft.X + 10f, topLeft.Y + 25f));
        Theme.Label(Theme.Fit(game.Blurb, textWidth));

        ImGui.SetCursorScreenPos(new Vector2(topLeft.X + width - buttonWidth - 10f, topLeft.Y + (height - ImGui.GetFrameHeight()) * 0.5f));
        if (Theme.PrimaryButton("创建房间", new Vector2(buttonWidth, 0)))
        {
            var size = Math.Clamp(maxPlayers, game.MinPlayers, game.MaxPlayers);
            host.Configuration.PreferredMaxPlayers = size;
            SaveSettings();
            _ = client.SendAsync(Msg.CreateRoom, new CreateRoomRequest { GameId = game.Id, MaxPlayers = size });
        }

        ImGui.SetCursorScreenPos(new Vector2(topLeft.X, topLeft.Y + height + 5f));
    }

    // -- room -------------------------------------------------------------

    private void DrawRoom(IGameClient client)
    {
        var snapshot = client.Room!;
        var view = snapshot.View;

        // 局势一变就念一句：优先念结果横幅，其次念当前指示（也就是"轮到谁/该做什么"）
        try
        {
            // 有可选动作 = 轮到你决断了，单独强调一次
            var hasActions = view.Actions is { Count: > 0 };
            if (hasActions && !lastHadActions && host.Configuration.SoundEnabled && host.Configuration.VoiceEnabled)
            {
                Sfx.PlayClip("dz-lundao", "轮到你了");
            }
            lastHadActions = hasActions;

            // 新出的牌 → 用珊瑚的声音念牌型
            if (snapshot.GameId == "doudizhu" && host.Configuration.SoundEnabled)
            {
                if (client.Events.Count > lastVoiceLogCount)
                {
                    for (var i = lastVoiceLogCount; i < client.Events.Count; i++)
                    {
                        var e = client.Events[i];
                        var t = e.Text ?? string.Empty;
                        if (t.Contains("过牌")) { Sfx.PlayClip("dz-buyao"); continue; }

                        var open = t.IndexOf('（');
                        var close = t.IndexOf('）');
                        if (open < 0 || close <= open) continue;
                        var kind = t[(open + 1)..close];
                        var clip = kind switch
                        {
                            "单张" => "dz-danzhang",
                            "对子" => "dz-duizi",
                            "三张" => "dz-sanzhang",
                            "三带一" => "dz-sandaiyi",
                            "三带二" => "dz-sandaier",
                            "顺子" => "dz-shunzi",
                            "连对" => "dz-liandui",
                            "飞机" => "dz-feiji",
                            "炸弹" => "dz-zhadan",
                            "王炸" => "dz-wangzha",
                            _ => null,
                        };
                        if (clip is not null) Sfx.PlayClip(clip, kind);
                    }
                    lastVoiceLogCount = client.Events.Count;
                }
            }

            var key = (view.Instruction ?? string.Empty) + "|" + (view.ResultBanner ?? string.Empty);
            if (key != lastAnnounce)
            {
                lastAnnounce = key;
                if (host.Configuration.SoundEnabled)
                {
                    // 斗地主：牌型和"轮到你了"都由珊瑚语音播，这里不再用系统语音念
                    var say = snapshot.GameId == "doudizhu"
                        ? string.Empty
                        : (!string.IsNullOrEmpty(view.ResultBanner) ? view.ResultBanner : view.Instruction);
                    if (host.Configuration.VoiceEnabled && !string.IsNullOrEmpty(say))
                    {
                        say = say.Replace("*", " ").Replace("【", string.Empty).Replace("】", string.Empty).Replace("\n", " ");
                        if (say.Length > 60) say = say[..60];
                        Sfx.Speak(say, 1200);
                    }
                }
            }
        }
        catch
        {
            /* 播报失败不影响游戏 */
        }

        TextMuted($"{snapshot.GameName} · 房间 {snapshot.Code}");
        ImGui.Spacing();
        TextColored(Accent, view.Title);

        if (!string.IsNullOrEmpty(view.Subtitle))
        {
            ImGui.SameLine();
            TextMuted(view.Subtitle);
        }

        if (view.DeadlineAtMs is { } deadline)
        {
            var remaining = deadline - client.ServerNowMs;
            if (remaining > 0)
            {
                ImGui.SameLine();
                TextColored(remaining < 10_000 ? Warn : Muted, $"{remaining / 1000.0:0.0}s");
            }
        }

        if (!string.IsNullOrEmpty(view.ResultBanner))
        {
            ImGui.Spacing();
            TextColored(Good, view.ResultBanner);
        }

        if (!string.IsNullOrEmpty(view.PrivateNote))
        {
            ImGui.Spacing();
            // 斗地主的手牌在下面的牌面里，这里只留一句提示，免得花色字形缺失时满屏方块。
            var note = snapshot.GameId == "doudizhu" ? "（点下面的牌面出牌）" : view.PrivateNote;
            TextColored(Gold, $"{view.PrivateNoteLabel ?? "提示"}：{note}");
        }

        if (!string.IsNullOrEmpty(view.Instruction))
        {
            ImGui.Spacing();
            TextWrapped(view.Instruction);
        }

        ImGui.Spacing();

        if (snapshot.GameId == "doudizhu") { 
        // 右侧：出牌记录（图形）
        var y0 = ImGui.GetCursorPosY();
        var sideW = 250f;
        ImGui.SetCursorPos(new Vector2(Math.Max(320f, ImGui.GetWindowWidth() - sideW - 16f), y0));
        ImGui.BeginChild("##side", new Vector2(sideW, 360f), true, ImGuiWindowFlags.None);
        // 出牌记录（图形）
        var plays = FindRecentPlays(client, 4);
        if (plays.Count > 0)
        {
            TextMuted($"出牌记录（最近 {plays.Count} 手）");
            foreach (var (actor, cards) in plays)
            {
                TextColored(Muted, actor);
                for (var i = 0; i < cards.Length; i++)
                {
                    if (i > 0 && i % 5 != 0) ImGui.SameLine();
                    DrawCardFace(cards[i]);
                }
                ImGui.Spacing();
            }
        }

        ImGui.EndChild();
        ImGui.SetCursorPos(new Vector2(0, y0));
        }

        var playing = snapshot.Phase == RoomPhase.Playing;

        DrawFields(client, view);
        DrawPlayers(view);

        // 棋类先画棋盘：它是这屏的主角，不能被积分榜挤到折叠线以下。
        if (playing && snapshot.GameId == "xiangqi") DrawXiangqiBoard(client, view);

        DrawScoreboard(view);
        DrawStats(view);
        ImGui.Spacing();
        // 认输：仅象棋与斗地主，点了直接结算
        if (playing
            && (snapshot.GameId == "xiangqi" || snapshot.GameId == "doudizhu")
            && string.IsNullOrEmpty(view.ResultBanner))
        {
            if (Theme.DangerButton("认输", new Vector2(80, 0)))
            {
                _ = client.SendAsync(Msg.Action, new ActionRequest { Kind = "resign" });
            }
            ImGui.SameLine();
        }
        if (playing && snapshot.GameId == "doudizhu")
        {
            DrawCardPicker(client, view);
        }

        // 斗地主对局中动作由选牌区接管，但「回到大厅 / 离开房间」这类房间级动作还要留
        if (playing && snapshot.GameId == "doudizhu")
        {
            DrawSystemActions(client, view);
        }
        else
        {
            DrawActions(client, view);
        }
        DrawActionInput(client, view);
        DrawLog(client, view);
        DrawChat(client);
    }

    /// <summary>Host-only lane for room settings (currently the scoring rules).</summary>
    private static void DrawFields(IGameClient client, GameView view)
    {
        if (view.Fields.Count == 0) return;

        Theme.Divider();
        TextMuted("积分规则（房主可调）");

        for (var i = 0; i < view.Fields.Count; i++)
        {
            var field = view.Fields[i];
            ImGui.PushID($"field{i}");

            if (field.Kind == "bool")
            {
                var on = field.Value == "1";
                if (Theme.GhostButton($"{field.Label}：{(on ? "开" : "关")}", new Vector2(132, 0)) && field.Editable)
                {
                    Send(client, field.Key, on ? "0" : "1");
                }
            }
            else
            {
                var value = int.TryParse(field.Value, out var parsed) ? parsed : field.Min;

                if (Theme.Glass)
                {
                    Gw.BeginRow(field.Label, $"{field.Min} - {field.Max}");
                    if (Gw.Button("-", ButtonStyle.Glass, ButtonSize.Small) && field.Editable)
                    {
                        Send(client, field.Key, Math.Max(field.Min, value - 1).ToString());
                    }

                    ImGui.SameLine(0f, 6f);
                    Theme.Value(value.ToString());
                    ImGui.SameLine(0f, 6f);
                    if (Gw.Button("+", ButtonStyle.Glass, ButtonSize.Small) && field.Editable)
                    {
                        Send(client, field.Key, Math.Min(field.Max, value + 1).ToString());
                    }

                    Gw.EndRow();
                    ImGui.PopID();
                    continue;
                }

                TextMuted(field.Label);
                ImGui.SameLine();

                if (Theme.GhostButton("-", new Vector2(20, 0)) && field.Editable)
                {
                    Send(client, field.Key, Math.Max(field.Min, value - 1).ToString());
                }

                ImGui.SameLine();
                ImGui.Text($"{value}");
                ImGui.SameLine();

                if (Theme.GhostButton("+", new Vector2(20, 0)) && field.Editable)
                {
                    Send(client, field.Key, Math.Min(field.Max, value + 1).ToString());
                }
            }

            ImGui.PopID();
        }

        ImGui.Spacing();
    }

    /// <summary>把新收到的聊天打印到游戏聊天框（Echo 频道，只有自己看得到）。</summary>
    private void SyncChatToGameChat(IGameClient client)
    {
        if (client.Events.Count <= lastEchoCount) return;

        for (var i = lastEchoCount; i < client.Events.Count; i++)
        {
            var e = client.Events[i];
            if (e.Kind != "chat") continue;
            var who = string.IsNullOrEmpty(e.Actor) ? "派对" : e.Actor;
            Toasts.Push($"{who}：{e.Text}");
        }

        lastEchoCount = client.Events.Count;
    }
    /// <summary>花色单字。不用 ♠♥♣♦：游戏字体不一定带这些字形，缺了就是一排方块。</summary>
    private static string SuitWord(char suit) => suit switch
    {
        '♠' => "黑",
        '♥' => "红",
        '♣' => "梅",
        '♦' => "方",
        _ => string.Empty,
    };

    private static string XqPiece(char p) => p switch
    {
        'K' => "帅", 'A' => "仕", 'B' => "相", 'N' => "马", 'R' => "车", 'C' => "炮", 'P' => "兵",
        'k' => "将", 'a' => "士", 'b' => "象", 'n' => "马", 'r' => "车", 'c' => "炮", 'p' => "卒",
        _ => string.Empty,
    };

    /// <summary>象棋棋盘：9×10，点击选子再点落点。</summary>
    private void DrawXiangqiBoard(IGameClient client, GameView view)
    {
        if (view.Board.Count < 10) return;

        var key = string.Join("|", view.Board);
        if (key != xqBoardKey)
        {
            xqBoardKey = key;
            xqSelR = -1;
            xqSelC = -1;
        }

        // 棋盘按可用宽度/高度算格子大小，窗口拉大拉小都能完整放下
        var availWidth = ImGui.GetContentRegionAvail().X;
        var availHeight = ImGui.GetContentRegionAvail().Y;
        var cell = Math.Clamp(MathF.Min((availWidth - 40f) / 9f, (availHeight - 24f) / 10f), 22f, 46f);
        var w = cell * 8;
        var h = cell * 9;
        var rawPos = ImGui.GetCursorScreenPos();
        var boardPx = cell * 8 + 24;
        var origin = new Vector2(rawPos.X + MathF.Max(0f, (ImGui.GetContentRegionAvail().X - boardPx) / 2f), rawPos.Y);
        var dl = ImGui.GetWindowDrawList();

        // 底色
        dl.AddRectFilled(new Vector2(origin.X - 12, origin.Y - 12),
            new Vector2(origin.X + w + 12, origin.Y + h + 12),
            ImGui.GetColorU32(new Vector4(0.13f, 0.12f, 0.10f, 0.92f)), 6f);

        var lineCol = ImGui.GetColorU32(new Vector4(0.55f, 0.45f, 0.28f, 0.85f));

        // 横线
        for (var r = 0; r < 10; r++)
        {
            var y = origin.Y + r * cell;
            dl.AddLine(new Vector2(origin.X, y), new Vector2(origin.X + w, y), lineCol, 1.4f);
        }

        // 竖线（中间楚河汉界断开）
        for (var col = 0; col < 9; col++)
        {
            var x = origin.X + col * cell;
            if (col == 0 || col == 8)
            {
                dl.AddLine(new Vector2(x, origin.Y), new Vector2(x, origin.Y + h), lineCol, 1.4f);
            }
            else
            {
                dl.AddLine(new Vector2(x, origin.Y), new Vector2(x, origin.Y + 4 * cell), lineCol, 1.4f);
                dl.AddLine(new Vector2(x, origin.Y + 5 * cell), new Vector2(x, origin.Y + h), lineCol, 1.4f);
            }
        }

        // 高亮：选中格 + 可落点
        var targets = new HashSet<string>(view.Targets);

        for (var r = 0; r < 10; r++)
        {
            for (var col = 0; col < 9; col++)
            {
                var ch = view.Board[r][col];
                var cx = origin.X + col * cell;
                var cy = origin.Y + r * cell;

                if (r == xqSelR && col == xqSelC)
                    dl.AddCircleFilled(new Vector2(cx, cy), cell * 0.46f, ImGui.GetColorU32(new Vector4(0.95f, 0.75f, 0.25f, 0.55f)), 20);

                foreach (var t in view.Targets)
                {
                    if (t.Length == 4 && t[2] - '0' == r && t[3] - '0' == col)
                    {
                        dl.AddCircleFilled(new Vector2(cx, cy), 6f, ImGui.GetColorU32(new Vector4(0.15f, 0.55f, 0.25f, 0.75f)), 12);
                        break;
                    }
                }

                if (ch == '.') continue;

                var isRed = char.IsUpper(ch);
                var fill = new Vector4(0.22f, 0.20f, 0.16f, 1f);
                var ring = isRed ? new Vector4(0.88f, 0.36f, 0.30f, 1f) : new Vector4(0.85f, 0.80f, 0.68f, 1f);

                dl.AddCircleFilled(new Vector2(cx, cy), cell * 0.42f, ImGui.GetColorU32(fill), 24);
                dl.AddCircle(new Vector2(cx, cy), cell * 0.42f, ImGui.GetColorU32(ring), 24, 2f);

                var text = XqPiece(ch);
                var ts = ImGui.CalcTextSize(text);
                dl.AddText(new Vector2(cx - ts.X / 2, cy - ts.Y / 2), ImGui.GetColorU32(ring), text);
            }
        }

        // 点击处理
        ImGui.SetCursorScreenPos(origin);
        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton("##xqboard", new Vector2(w + cell * 0.6f, h + cell * 0.6f));
        var clicked = ImGui.IsItemClicked();
        var mouse = ImGui.GetMousePos();

        if (clicked)
        {
            var col = (int)MathF.Round((mouse.X - origin.X) / cell);
            var row = (int)MathF.Round((mouse.Y - origin.Y) / cell);
            if (row >= 0 && row < 10 && col >= 0 && col < 9)
            {
                if (xqSelR >= 0)
                {
                    var move = $"{xqSelR}{xqSelC}{row}{col}";
                    if (targets.Contains(move))
                    {
                        _ = client.SendAsync(Msg.Action, new ActionRequest { Kind = "move", Text = move });
                        xqSelR = -1;
                        xqSelC = -1;
                        return;
                    }
                }

                var ch = view.Board[row][col];
                if (ch != '.' && targets.Any(t => t.Length == 4 && t[0] - '0' == row && t[1] - '0' == col))
                {
                    xqSelR = row;
                    xqSelC = col;
                }
                else
                {
                    xqSelR = -1;
                    xqSelC = -1;
                }
            }
        }
    }
    /// <summary>取出最近 N 手出牌（演员 + 牌面）。</summary>
    private static List<(string Actor, string[] Cards)> FindRecentPlays(IGameClient client, int max)
    {
        var list = new List<(string, string[])>();
        for (var i = client.Events.Count - 1; i >= 0 && list.Count < max; i--)
        {
            var e = client.Events[i];
            if (e.Kind != "result") continue;
            var text = e.Text ?? string.Empty;
            var at = text.IndexOf("出牌：", StringComparison.Ordinal);
            if (at < 0) continue;
            var rest = text[(at + 3)..];
            var end = rest.IndexOf('（');
            if (end > 0) rest = rest[..end];
            var tokens = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;
            list.Add((e.Actor ?? "?", tokens));
        }
        list.Reverse();
        return list;
    }

    private static string[] FindLastPlay(IGameClient client)
    {
        for (var i = client.Events.Count - 1; i >= 0; i--)
        {
            var e = client.Events[i];
            if (e.Kind != "result") continue;
            var text = e.Text ?? string.Empty;
            var at = text.IndexOf("出牌：", StringComparison.Ordinal);
            if (at < 0) continue;
            var rest = text[(at + 3)..];
            var end = rest.IndexOf('（');
            if (end > 0) rest = rest[..end];
            var tokens = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 0) return tokens;
        }
        return Array.Empty<string>();
    }

    /// <summary>画一张牌面（无交互）。</summary>
    private static void DrawCardFace(string token)
    {
        var size = new Vector2(44, 60);
        var p0 = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();

        var suit = token.Length > 0 && "♠♥♣♦".IndexOf(token[token.Length - 1]) >= 0 ? token[token.Length - 1] : '\0';
        var rank = suit == '\0' ? token : token[..^1];
        var isRed = suit == '♥' || suit == '♦';
        if (token is "小王" or "大王") isRed = false;

        var a = p0;
        var b = new Vector2(p0.X + size.X, p0.Y + size.Y);

        dl.AddRectFilled(new Vector2(a.X + 2, a.Y + 3), new Vector2(b.X + 2, b.Y + 3),
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.35f)), 5f);
        dl.AddRectFilled(a, b, ImGui.GetColorU32(new Vector4(0.97f, 0.96f, 0.93f, 1f)), 5f);
        dl.AddRect(a, b, ImGui.GetColorU32(new Vector4(0.55f, 0.52f, 0.45f, 1f)), 5f, 0, 1.2f);

        var ink = ImGui.GetColorU32(isRed ? new Vector4(0.80f, 0.12f, 0.12f, 1f) : new Vector4(0.12f, 0.12f, 0.14f, 1f));
        dl.AddText(new Vector2(a.X + 4, a.Y + 3), ink, rank);


        var big = suit == '\0' ? (token == "小王" ? "小" : "大") : SuitWord(suit);
        var bigSize = ImGui.CalcTextSize(big);
        dl.AddText(new Vector2(a.X + (size.X - bigSize.X) / 2, b.Y - bigSize.Y - 6), ink, big);

        ImGui.Dummy(size);
    }
    /// <summary>斗地主图形选牌：把手牌拆成按钮，点选后一键出牌。</summary>
    private void DrawCardPicker(IGameClient client, GameView view)
    {
        var hand = view.PrivateNote;
        if (string.IsNullOrWhiteSpace(hand)) return;

        if (hand != lastHandText)
        {
            lastHandText = hand!;
            pickedCards.Clear();
        }

        var tokens = hand!.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // 叫地主 / 抢地主阶段：这轮没牌可出，只给当前该做决定的人两个按钮，手牌当参考牌摆着
        var bid = view.Actions.FirstOrDefault(a => a.Kind is "call" or "rob");
        if (bid is not null)
        {
            Theme.Section(bid.Kind == "rob" ? "抢地主" : "叫地主");

            if (Theme.PrimaryButton(bid.Label, new Vector2(132, 0)))
            {
                _ = client.SendAsync(Msg.Action, new ActionRequest { Kind = bid.Kind });
            }

            ImGui.SameLine(0f, 10f);
            var decline = view.Actions.FirstOrDefault(a => a.Kind == "pass" && a.Enabled);
            if (decline is not null && Theme.GhostButton(decline.Label, new Vector2(104, 0)))
            {
                _ = client.SendAsync(Msg.Action, new ActionRequest { Kind = "pass" });
            }

            ImGui.SameLine(0f, 10f);
            Theme.Label(bid.Kind == "rob" ? "抢的人越多倍数越高" : "叫了地主，另外两家还能抢");

            ImGui.Spacing();
            Theme.Section($"我的手牌（{tokens.Length} 张）");
            for (var i = 0; i < tokens.Length; i++)
            {
                if (i > 0 && i % 7 != 0) ImGui.SameLine(0f, 4f);
                DrawCardFace(tokens[i]);
            }

            ImGui.Spacing();
            return;
        }

        Theme.Section($"选牌（{pickedCards.Count}/{tokens.Length}）");

        var cardSize = new Vector2(48, 66);
        var perRow = Math.Max(4, (int)(ImGui.GetContentRegionAvail().X / (cardSize.X + 4f)));

        for (var i = 0; i < tokens.Length; i++)
        {
            if (i > 0 && i % perRow != 0) ImGui.SameLine();

            var picked = pickedCards.Contains(i);
            var token = tokens[i];
            var suit = token.Length > 0 && "♠♥♣♦".IndexOf(token[token.Length - 1]) >= 0 ? token[token.Length - 1] : '\0';
            var rank = suit == '\0' ? token : token.Substring(0, token.Length - 1);
            var isRed = suit == '♥' || suit == '♦';
            if (token == "小王" || token == "大王") isRed = false;

            var size = new Vector2(48, 66);
            var p0 = ImGui.GetCursorScreenPos();

            ImGui.PushID("card" + i);
            ImGui.InvisibleButton("face", size);
            var hovered = ImGui.IsItemHovered();
            var clicked = ImGui.IsItemClicked();
            ImGui.PopID();

            var dl = ImGui.GetWindowDrawList();
            var lift = picked ? 6f : 0f;
            var a = new Vector2(p0.X, p0.Y - lift);
            var b = new Vector2(p0.X + size.X, p0.Y + size.Y - lift);

            dl.AddRectFilled(new Vector2(a.X + 2, a.Y + 3), new Vector2(b.X + 2, b.Y + 3),
                ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.35f)), 6f);

            var face = picked
                ? new Vector4(0.99f, 0.95f, 0.86f, 1f)
                : hovered ? new Vector4(1f, 1f, 1f, 1f) : new Vector4(0.97f, 0.96f, 0.93f, 1f);
            dl.AddRectFilled(a, b, ImGui.GetColorU32(face), 6f);
            dl.AddRect(a, b,
                ImGui.GetColorU32(picked ? new Vector4(0.85f, 0.65f, 0.25f, 1f) : new Vector4(0.55f, 0.52f, 0.45f, 1f)),
                6f, 0, picked ? 2.5f : 1.2f);

            var ink = isRed ? new Vector4(0.80f, 0.12f, 0.12f, 1f) : new Vector4(0.12f, 0.12f, 0.14f, 1f);
            var inkU = ImGui.GetColorU32(ink);

            dl.AddText(new Vector2(a.X + 5, a.Y + 3), inkU, rank);


            var bigText = suit == '\0' ? (token == "小王" ? "小" : "大") : SuitWord(suit);
            var bigSize = ImGui.CalcTextSize(bigText);
            dl.AddText(new Vector2(a.X + (size.X - bigSize.X) / 2, b.Y - bigSize.Y - 8), inkU, bigText);

            if (clicked)
            {
                if (picked) pickedCards.Remove(i);
                else pickedCards.Add(i);
            }
        }
        ImGui.Spacing();

        var canPlay = view.AcceptsText && pickedCards.Count > 0;
        if (!canPlay) ImGui.BeginDisabled();
        if (Theme.PrimaryButton("出牌", new Vector2(120, 0)))
        {
            var text = string.Join(" ", pickedCards.OrderBy(i => i).Select(i => tokens[i]));
            _ = client.SendAsync(Msg.Action, new ActionRequest { Kind = "play", Text = text });
            pickedCards.Clear();
        }
        if (!canPlay) ImGui.EndDisabled();

        ImGui.SameLine();
        if (Theme.GhostButton("清空", new Vector2(80, 0))) pickedCards.Clear();

        ImGui.SameLine();
        var canPass = view.Actions.Any(a => a is { Kind: "pass", Enabled: true });
        if (!canPass) ImGui.BeginDisabled();
        if (Theme.GhostButton("要不起", new Vector2(88, 0)))
        {
            _ = client.SendAsync(Msg.Action, new ActionRequest { Kind = "pass" });
            pickedCards.Clear();
        }

        if (!canPass) ImGui.EndDisabled();

        ImGui.SameLine();
        Theme.Label(canPlay ? "点牌选中，再点出牌" : "现在不是你的回合");
    }
    private static void Send(IGameClient client, string key, string value)
    {
        _ = client.SendAsync(Msg.Action, new ActionRequest
        {
            Kind = "setting",
            Target = key,
            Text = value,
        });
    }

    /// <summary>This player's all-time record, by game (in-room view).</summary>
    private static void DrawStats(GameView view)
    {
        if (view.Stats.Count == 0) return;

        ImGui.Spacing();
        DrawStatRows(view.Stats, "我的战绩");
    }

    private static void DrawStatRows(List<StatRow> rows, string title)
    {
        Theme.Section(title);

        var totalWins = 0;
        var totalLosses = 0;

        foreach (var row in rows)
        {
            totalWins += row.Wins;
            totalLosses += row.Losses;

            var line = $"{row.Game}  {row.Wins} 胜 {row.Losses} 负（共 {row.Wins + row.Losses} 局）";
            if (row.Wins > row.Losses) TextColored(Good, line);
            else if (row.Losses > row.Wins) TextColored(Warn, line);
            else ImGui.Text(line);
        }

        if (rows.Count > 1)
        {
            TextMuted($"合计  {totalWins} 胜 {totalLosses} 负（共 {totalWins + totalLosses} 局）");
        }
    }

    /// <summary>Ranked board for the whole room session.</summary>
    private static void DrawScoreboard(GameView view)
    {
        if (view.Scores.Count == 0) return;

        ImGui.Spacing();
        TextMuted("积分榜");

        for (var i = 0; i < view.Scores.Count; i++)
        {
            var entry = view.Scores[i];

            var line = $"{i + 1}. {entry.Name}  {entry.Score}";
            if (entry.Delta != 0) line += $"（{entry.Delta:+0;-0}）";
            if (!string.IsNullOrEmpty(entry.Note)) line += $"  {entry.Note}";

            if (entry.IsYou) TextColored(Accent, line);
            else if (i == 0) TextColored(Good, line);
            else ImGui.Text(line);
        }
    }

    private void DrawPlayers(GameView view)
    {
        const float rowHeight = 27f;
        const float avatarSize = 20f;
        const float textOffset = avatarSize + 9f;

        var playerListHeight = MathF.Min(240f, 10f + view.Players.Count * rowHeight);
        ImGui.BeginChild("##players", new Vector2(0, playerListHeight), true, ImGuiWindowFlags.None);

        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();

        for (var i = 0; i < view.Players.Count; i++)
        {
            var player = view.Players[i];
            var rowTop = origin.Y + i * rowHeight;

            Avatars.Draw(drawList, artwork, new Vector2(origin.X + 2f, rowTop + 4f), avatarSize,
                player.Name, player.IsYou, player.IsAlive ? 1f : 0.45f, remoteAvatars.Wrap(player.Id));

            ImGui.SetCursorScreenPos(new Vector2(origin.X + textOffset, rowTop + 5f));

            var line = player.Name + (player.IsYou ? "（你）" : string.Empty);
            if (!string.IsNullOrEmpty(player.Badge)) line += $"  [{player.Badge}]";
            if (player.Votes is { } votes) line += $"  ({votes} 票)";
            if (player.IsOffline) line += "  (离线)";
            if (!string.IsNullOrEmpty(player.Sub)) line += $"  · {player.Sub}";

            if (!player.IsAlive) ImGui.TextDisabled(line);
            else if (player.Accent == "good") TextColored(Good, line);
            else if (player.Accent == "bad") TextColored(Warn, line);
            else if (player.Accent == "warn") TextColored(Gold, line);
            else if (player.IsYou) TextColored(Accent, line);
            else ImGui.TextUnformatted(line);
        }

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(0f, view.Players.Count * rowHeight));
        ImGui.EndChild();
    }
    /// <summary>房间级动作：对局中也要能回到大厅。</summary>
    private static void DrawSystemActions(IGameClient client, GameView view)
    {
        foreach (var action in view.Actions)
        {
            if (action.Kind is not ("lobby" or "leave" or "resetsession")) continue;

            ImGui.PushID($"sys{action.Kind}");
            if (Theme.GhostButton(action.Label, new Vector2(120, 0)))
            {
                _ = client.SendAsync(Msg.Action, new ActionRequest { Kind = action.Kind, Target = action.Target });
            }

            ImGui.SameLine(0f, 8f);
            ImGui.PopID();
        }

        ImGui.Spacing();
    }

    private static void DrawActions(IGameClient client, GameView view)
    {
        if (view.Actions.Count == 0) return;

        // 窗口窄就一列，宽就两列；按钮宽度跟着走，不再写死 250
        var available = ImGui.GetContentRegionAvail().X;
        var columns = available >= 520f ? 2 : 1;
        var buttonWidth = MathF.Min(columns == 2 ? (available - 8f) / 2f : available, 400f);

        for (var i = 0; i < view.Actions.Count; i++)
        {
            var action = view.Actions[i];

            if (columns == 2 && i % 2 == 1) ImGui.SameLine(0f, 8f);

            ImGui.PushID($"action{i}");
            if (!action.Enabled) ImGui.BeginDisabled();

            var clicked = action.Danger
                ? Theme.DangerButton(action.Label, new Vector2(buttonWidth, 0))
                : i == 0 ? Theme.PrimaryButton(action.Label, new Vector2(buttonWidth, 0))
                : Theme.GhostButton(action.Label, new Vector2(buttonWidth, 0));

            if (clicked)
            {
                _ = client.SendAsync(Msg.Action, new ActionRequest
                {
                    Kind = action.Kind,
                    Target = action.Target,
                });
            }

            if (!action.Enabled) ImGui.EndDisabled();
            ImGui.PopID();
        }

        if (columns == 2 && view.Actions.Count % 2 == 1) ImGui.Spacing();
    }

    private void DrawActionInput(IGameClient client, GameView view)
    {
        if (!view.AcceptsText) return;

        ImGui.Spacing();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 96);

        var submit = Ui.InputTextWithHint("##actiontext", view.TextHint ?? "输入…", ref actionInput, 80,
            ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();
        if (Theme.PrimaryButton("提交", new Vector2(88, 0))) submit = true;

        if (!submit || string.IsNullOrWhiteSpace(actionInput)) return;

        _ = client.SendAsync(Msg.Action, new ActionRequest
        {
            Kind = view.TextActionKind,
            Text = actionInput.Trim(),
        });

        actionInput = string.Empty;
    }

    private void DrawLog(IGameClient client, GameView view)
    {
        ImGui.Spacing();
        Theme.Divider();
        ImGui.BeginChild("##log", new Vector2(0, 176), true, ImGuiWindowFlags.None);

        // 只有「来了新消息」且「本来就在底部」时才跟着滚。
        // 之前是每帧都判断当前是否在底部——用户拖动的一瞬间读数还是旧的，
        // 于是每帧都把他拽回底部，表现为滚不动、一拖就弹回去。
        var count = client.Events.Count;
        var atBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 2f;

        foreach (var roomEvent in client.Events)
        {
            var text = string.IsNullOrEmpty(roomEvent.Actor) ? roomEvent.Text : $"{roomEvent.Actor}：{roomEvent.Text}";

            switch (roomEvent.Kind)
            {
                case "result":
                    TextColored(Good, text);
                    break;
                case "system":
                    TextMuted(text);
                    break;
                case "chat":
                    // 头像按名字对回玩家卡片，才能拿到 id → 远端头像
                    var who = roomEvent.Actor ?? "?";
                    var clock = DateTimeOffset.FromUnixTimeMilliseconds(roomEvent.AtMs).ToLocalTime().ToString("HH:mm");
                    var card = view.Players.FirstOrDefault(p => string.Equals(p.Name, who, StringComparison.Ordinal));
                    var wrap = card is not null ? remoteAvatars.Wrap(card.Id) : null;
                    ChatBubble.Draw(who, roomEvent.Text, clock, artwork, wrap, card?.IsYou ?? false);
                    break;
                default:
                    ImGui.TextDisabled(text);
                    break;
            }
        }

        if (count != lastLogCount && atBottom) ImGui.SetScrollHereY(1f);
        lastLogCount = count;

        ImGui.EndChild();
    }

    private void DrawChat(IGameClient client)
    {
        const float sendWidth = 88f;
        var chatFieldWidth = MathF.Min(MathF.Max(160f, ImGui.GetContentRegionAvail().X - sendWidth - 12f), 520f);

        // —— 键盘归谁 ——
        // 插件窗口还是活动窗口的时候，就把焦点按在聊天框上。
        // Dalamud 只在「有 ImGui 输入框正在编辑」（io.WantTextInput）时才把按键从游戏手里截下来，
        // 所以焦点不在框里时，回车就会直接开游戏自己的聊天输入框——这就是一直没修好的那个问题。
        // 鼠标点进游戏画面时 Dalamud 会清掉 ImGui 的窗口焦点，我们随即放手，键盘还给游戏。
        var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        var editing = ImGui.GetIO().WantTextInput;

        // 在窗口里点一下 = 重新按住；Esc = 交还键盘（不然一直按着，走路都会被吃掉）
        var clickedInside = ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
                            ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows |
                                                  ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        if (clickedInside) parkBlocked = false;

        if (chatParked && ImGui.IsKeyPressed(ImGuiKey.Escape, false))
        {
            parkBlocked = true;
            ReleaseChatFocus();
        }
        else if (!focused)
        {
            ReleaseChatFocus();
        }
        else if (!parkBlocked && (!editing || ImGui.IsKeyPressed(ImGuiKey.Enter, false)))
        {
            refocusChat = true;
        }

        bool send;
        bool active;

        if (Theme.Glass)
        {
            if (refocusChat)
            {
                ImGui.SetKeyboardFocusHere();
                refocusChat = false;
            }

            send = GlassChatField("##chat", "说点什么…", ref chatInput, chatFieldWidth, out active);
        }
        else
        {
            ImGui.SetNextItemWidth(chatFieldWidth);
            if (refocusChat)
            {
                ImGui.SetKeyboardFocusHere();
                refocusChat = false;
            }

            send = Ui.InputTextWithHint("##chat", "说点什么…", ref chatInput, 140,
                ImGuiInputTextFlags.EnterReturnsTrue);
            active = ImGui.IsItemActive();
        }

        chatParked = active;

        ImGui.SameLine(0f, 8f);
        if (Theme.PrimaryButton("发送", new Vector2(sendWidth, 0))) send = true;

        if (!send || string.IsNullOrWhiteSpace(chatInput)) return;

        _ = client.SendAsync(Msg.Chat, new ChatRequest { Text = chatInput.Trim() });
        chatInput = string.Empty;
        refocusChat = true;
    }

    /// <summary>
    /// 玻璃模式下的聊天输入框：胶囊自己画，输入框用原生 ImGui。
    /// 这里特意不用 Glasswork 的 TextField —— 聊天框必须能被程序按在焦点上
    /// （回车要落到插件里，而不是游戏自己的聊天框），原生 InputText 的聚焦行为是确定的。
    /// </summary>
    private bool GlassChatField(string id, string hint, ref string value, float width, out bool active)
    {
        var p0 = ImGui.GetCursorScreenPos();
        var height = ImGui.GetFrameHeight() + 10f;
        var radius = height * 0.5f;
        var bottomRight = new Vector2(p0.X + width, p0.Y + height);
        var dl = ImGui.GetWindowDrawList();

        dl.AddRectFilled(p0, bottomRight,
            ImGui.GetColorU32(new Vector4(0.07f, 0.07f, 0.10f, 0.80f)), radius);

        ImGui.SetCursorScreenPos(new Vector2(p0.X + 14f, p0.Y + 5f));
        ImGui.SetNextItemWidth(width - 28f);

        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);

        var submit = ImGui.InputTextWithHint(id, hint, ref value, 140, ImGuiInputTextFlags.EnterReturnsTrue);
        active = ImGui.IsItemActive();

        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);

        // 描边最后画：压住输入框的边，聚焦时亮成金色
        dl.AddRect(p0, bottomRight,
            ImGui.GetColorU32(chatParked
                ? new Vector4(0.89f, 0.76f, 0.45f, 0.95f)
                : new Vector4(0.38f, 0.35f, 0.30f, 0.75f)),
            radius, 0, chatParked ? 2f : 1.2f);

        // 占位：让后面的 SameLine 从胶囊右边接上
        ImGui.SetCursorScreenPos(p0);
        ImGui.Dummy(new Vector2(width, height));
        return submit;
    }

    /// <summary>把「键盘归插件」放掉，按键还给游戏。</summary>
    private void ReleaseChatFocus()
    {
        if (!chatParked) return;

        chatParked = false;
        refocusChat = false;
        ImGuiP.ClearActiveID();
    }

    // -- helpers ----------------------------------------------------------

    private void SaveSettings()
    {
        host.Configuration.ServerUrl = serverUrl.Trim();
        host.Configuration.DisplayName = displayName.Trim();
        host.Configuration.Save();
    }

    private static void TextColored(Vector4 color, string text)
    {
        var value = color;
        ImGui.TextColored(value, text);
    }

    private static void TextMuted(string text) => Theme.Label(text);

    private static void TextWrapped(string text)
    {
        if (Theme.Glass)
        {
            Gw.TextWrapped(text, TextRole.Body);
            return;
        }

        ImGui.TextWrapped(text);
    }

    /// <summary>开关：玻璃下是 Glasswork 的拨动开关，没有玻璃退回复选框。</summary>
    private static bool Toggle(string label, ref bool value)
    {
        if (!Theme.Glass)
        {
            return ImGui.Checkbox(label, ref value);
        }

        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        return Gw.Toggle(ref value, "##" + label);
    }

    /// <summary>整数滑块：Glasswork 只有 float 滑块，这里换算一下。</summary>
    private static bool SliderInt(string label, ref int value, int min, int max)
    {
        if (!Theme.Glass)
        {
            return ImGui.SliderInt(label, ref value, min, max);
        }

        var current = (float)value;
        var changed = Gw.Slider(ref current, min, max, "##" + label, 1f, "0");
        value = (int)MathF.Round(current);
        return changed;
    }
}
