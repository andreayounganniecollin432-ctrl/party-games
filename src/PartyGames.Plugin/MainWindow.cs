using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
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

    private readonly Plugin plugin;
    private string serverUrl;
    private string displayName;
    private string joinCode = string.Empty;
    private string chatInput = string.Empty;
    private string actionInput = string.Empty;
    private int maxPlayers;
    private string lastAnnounce = string.Empty;
    private bool lastHadActions;
    private int lastLogCount;
    private string avatarPath = string.Empty;
    private string backgroundPath = string.Empty;
    private readonly Artwork artwork;

    public MainWindow(Plugin plugin)
        : base("Party Games###PartyGamesMainWindow")
    {
        this.plugin = plugin;
        Size = new Vector2(620, 780);
        SizeCondition = ImGuiCond.FirstUseEver;

        serverUrl = plugin.Configuration.ServerUrl;
        displayName = string.IsNullOrWhiteSpace(plugin.Configuration.DisplayName)
            ? plugin.CharacterName
            : plugin.Configuration.DisplayName;
        maxPlayers = plugin.Configuration.PreferredMaxPlayers;
        avatarPath = plugin.Configuration.AvatarPath;
        backgroundPath = plugin.Configuration.BackgroundPath;
        artwork = new Artwork(plugin.Textures, plugin.Log);
    }

    public override void Draw()
    {
        var client = plugin.Client;

        // 外观：先把图片同步到配置里的路径，再铺背景
        artwork.Sync(backgroundPath, avatarPath);
        artwork.DrawBackground(plugin.Configuration.BackgroundDim);

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

    private void DrawConnect(GameClient client)
    {
        // 自定义头像（只在这两页显示）
        if (artwork.DrawAvatar(48))
        {
            ImGui.SameLine();
        }

        Theme.Header("跨服文字游戏 · 未连接", false);
        Theme.Section("连接");

        ImGui.SetNextItemWidth(360);
        ImGui.InputText("服务器", ref serverUrl, 200);

        ImGui.SetNextItemWidth(360);
        ImGui.InputText("昵称", ref displayName, 24);

        ImGui.Spacing();

        var connecting = client.State == ClientState.Connecting;

        if (connecting) ImGui.BeginDisabled();
        if (Theme.PrimaryButton(connecting ? "连接中…" : "连接", new Vector2(120, 0)))
        {
            SaveSettings();
            _ = client.ConnectAsync(serverUrl.Trim(), displayName.Trim(),
                plugin.Configuration.LastRoomCode, plugin.Configuration.LastPlayerId);
        }

        if (!connecting && !string.IsNullOrWhiteSpace(plugin.Configuration.LastRoomCode))
        {
            ImGui.SameLine();
            if (ImGui.Button($"回到房间 {plugin.Configuration.LastRoomCode}", new Vector2(200, 0)))
            {
                SaveSettings();
                _ = client.ConnectAsync(serverUrl.Trim(), displayName.Trim(),
                    plugin.Configuration.LastRoomCode, plugin.Configuration.LastPlayerId);
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

        Theme.Section("外观（本地图片，可留空）");

        ImGui.SetNextItemWidth(430);
        if (ImGui.InputTextWithHint("背景图", "留空使用默认背景", ref backgroundPath, 260, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            plugin.Configuration.BackgroundPath = backgroundPath;
            plugin.Configuration.Save();
            artwork.Invalidate();
        }

        ImGui.SetNextItemWidth(430);
        if (ImGui.InputTextWithHint("头像图", "留空使用名字首字", ref avatarPath, 260, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            plugin.Configuration.AvatarPath = avatarPath;
            plugin.Configuration.Save();
            artwork.Invalidate();
        }

        var dim = plugin.Configuration.BackgroundDim;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("背景压暗", ref dim, 0, 100))
        {
            plugin.Configuration.BackgroundDim = dim;
            plugin.Configuration.Save();
        }

        Theme.Label("图片只保存在你本地，不会上传。");
    }

    // -- lobby ------------------------------------------------------------

    private void DrawTopBar(GameClient client)
    {
        if (artwork.DrawAvatar(36))
        {
            ImGui.SameLine();
        }

        Theme.Header($"已连接 · {client.PlayerName ?? "?"}" + (client.Room is not null ? $" · 房间 {client.Room.Code}" : ""), true);

        if (ImGui.Button("断开"))
        {
            client.Disconnect();
            return;
        }

        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawLobby(GameClient client)
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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        TextColored(Accent, "选择一个游戏开局");
        ImGui.Spacing();

        if (client.Games.Count == 0)
        {
            TextMuted("服务器没有报告任何游戏。");
        }

        foreach (var game in client.Games)
        {
            ImGui.PushID(game.Id);
            TextColored(Accent, game.Name);
            TextWrapped(game.Blurb);
            TextMuted($"{game.MinPlayers}-{game.MaxPlayers} 人");

            ImGui.SetNextItemWidth(140);
            ImGui.SliderInt("人数上限", ref maxPlayers, game.MinPlayers, game.MaxPlayers);

            if (ImGui.Button("创建房间", new Vector2(140, 0)))
            {
                plugin.Configuration.PreferredMaxPlayers = maxPlayers;
                SaveSettings();
                _ = client.SendAsync(Msg.CreateRoom, new CreateRoomRequest { GameId = game.Id, MaxPlayers = maxPlayers });
            }

            ImGui.PopID();
            ImGui.Separator();
            ImGui.Spacing();
        }

        TextColored(Accent, "加入房间");
        ImGui.SetNextItemWidth(150);
        ImGui.InputText("房间码", ref joinCode, 8);
        ImGui.SameLine();

        if (ImGui.Button("加入", new Vector2(90, 0)))
        {
            var code = joinCode.Trim().ToUpperInvariant();
            if (code.Length > 0)
            {
                _ = client.SendAsync(Msg.JoinRoom, new JoinRoomRequest { Code = code });
            }
        }
    }

    // -- room -------------------------------------------------------------

    private void DrawRoom(GameClient client)
    {
        var snapshot = client.Room!;
        var view = snapshot.View;

        // 局势一变就念一句：优先念结果横幅，其次念当前指示（也就是"轮到谁/该做什么"）
        try
        {
            // 有可选动作 = 轮到你决断了，单独强调一次
            var hasActions = view.Actions is { Count: > 0 };
            if (hasActions && !lastHadActions && plugin.Configuration.SoundEnabled && plugin.Configuration.VoiceEnabled)
            {
                Sfx.PlayClip("turn-you", "轮到你啦");
            }
            lastHadActions = hasActions;

            var key = (view.Instruction ?? string.Empty) + "|" + (view.ResultBanner ?? string.Empty);
            if (key != lastAnnounce)
            {
                lastAnnounce = key;
                if (plugin.Configuration.SoundEnabled)
                {
                    var say = !string.IsNullOrEmpty(view.ResultBanner) ? view.ResultBanner : view.Instruction;
                    if (plugin.Configuration.VoiceEnabled && !string.IsNullOrEmpty(say))
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
            TextColored(Gold, $"{view.PrivateNoteLabel ?? "提示"}：{view.PrivateNote}");
        }

        if (!string.IsNullOrEmpty(view.Instruction))
        {
            ImGui.Spacing();
            TextWrapped(view.Instruction);
        }

        ImGui.Spacing();
        DrawFields(client, view);
        DrawPlayers(view);
        DrawScoreboard(view);
        DrawStats(view);
        ImGui.Spacing();
        DrawActions(client, view);
        DrawActionInput(client, view);
        DrawLog(client);
        DrawChat(client);
    }

    /// <summary>Host-only lane for room settings (currently the scoring rules).</summary>
    private static void DrawFields(GameClient client, GameView view)
    {
        if (view.Fields.Count == 0) return;

        ImGui.Separator();
        TextMuted("积分规则（房主可调）");

        for (var i = 0; i < view.Fields.Count; i++)
        {
            var field = view.Fields[i];
            ImGui.PushID($"field{i}");

            if (field.Kind == "bool")
            {
                var on = field.Value == "1";
                if (ImGui.Button($"{field.Label}：{(on ? "开" : "关")}", new Vector2(150, 0)) && field.Editable)
                {
                    Send(client, field.Key, on ? "0" : "1");
                }
            }
            else
            {
                var value = int.TryParse(field.Value, out var parsed) ? parsed : field.Min;

                TextMuted(field.Label);
                ImGui.SameLine();

                if (ImGui.SmallButton("-##minus") && field.Editable)
                {
                    Send(client, field.Key, Math.Max(field.Min, value - 1).ToString());
                }

                ImGui.SameLine();
                ImGui.Text($"{value}");
                ImGui.SameLine();

                if (ImGui.SmallButton("+##plus") && field.Editable)
                {
                    Send(client, field.Key, Math.Min(field.Max, value + 1).ToString());
                }
            }

            ImGui.PopID();
        }

        ImGui.Spacing();
    }

    private static void Send(GameClient client, string key, string value)
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
        TextMuted(title);

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
        ImGui.BeginChild("##players", new Vector2(0, 168), true, ImGuiWindowFlags.None);

        foreach (var player in view.Players)
        {
            // 自己的头像：自定义图片 → 没有就画个首字色块（别人的头像协议里没传）
            if (player.IsYou)
            {
                if (!artwork.DrawAvatar(22))
                {
                    var draw = ImGui.GetWindowDrawList();
                    var at = ImGui.GetCursorScreenPos();
                    draw.AddRectFilled(at, new Vector2(at.X + 22, at.Y + 22), ImGui.GetColorU32(new Vector4(0.62f, 0.48f, 0.25f, 1f)), 4f);
                    var ch = string.IsNullOrEmpty(player.Name) ? "?" : player.Name[..1];
                    draw.AddText(new Vector2(at.X + 6, at.Y + 3), ImGui.GetColorU32(new Vector4(0.12f, 0.10f, 0.08f, 1f)), ch);
                    ImGui.Dummy(new Vector2(22, 22));
                }
                ImGui.SameLine();
            }

            var line = player.Name + (player.IsYou ? "（你）" : string.Empty);

            if (!string.IsNullOrEmpty(player.Badge)) line += $"  [{player.Badge}]";
            if (player.Votes is { } votes) line += $"  ← {votes} 票";
            if (player.IsOffline) line += "  (离线)";
            if (!string.IsNullOrEmpty(player.Sub)) line += $"  — {player.Sub}";

            if (!player.IsAlive) ImGui.TextDisabled(line);
            else if (player.Accent == "good") TextColored(Good, line);
            else if (player.Accent == "bad") TextColored(Warn, line);
            else ImGui.Text(line);
        }

        ImGui.EndChild();
    }

    private static void DrawActions(GameClient client, GameView view)
    {
        if (view.Actions.Count == 0) return;

        for (var i = 0; i < view.Actions.Count; i++)
        {
            var action = view.Actions[i];

            if (i % 2 == 1) ImGui.SameLine();

            ImGui.PushID($"action{i}");
            if (!action.Enabled) ImGui.BeginDisabled();

            if (ImGui.Button(action.Label, new Vector2(250, 0)))
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

        if (view.Actions.Count % 2 == 1) ImGui.Spacing();
    }

    private void DrawActionInput(GameClient client, GameView view)
    {
        if (!view.AcceptsText) return;

        ImGui.Spacing();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 96);

        var submit = ImGui.InputTextWithHint("##actiontext", view.TextHint ?? "输入…", ref actionInput, 80,
            ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();
        if (ImGui.Button("提交", new Vector2(88, 0))) submit = true;

        if (!submit || string.IsNullOrWhiteSpace(actionInput)) return;

        _ = client.SendAsync(Msg.Action, new ActionRequest
        {
            Kind = view.TextActionKind,
            Text = actionInput.Trim(),
        });

        actionInput = string.Empty;
    }

    private void DrawLog(GameClient client)
    {
        ImGui.Spacing();
        ImGui.Separator();
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
                    // 气泡统一靠左；头像只用名字首字（自定义头像留给登录/选游戏界面）
                    var who = roomEvent.Actor ?? "?";
                    var clock = DateTimeOffset.FromUnixTimeMilliseconds(roomEvent.AtMs).ToLocalTime().ToString("HH:mm");
                    ChatBubble.Draw(who, roomEvent.Text, clock);
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

    private void DrawChat(GameClient client)
    {
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 96);

        var send = ImGui.InputTextWithHint("##chat", "说点什么…", ref chatInput, 140,
            ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();
        if (ImGui.Button("发送", new Vector2(88, 0))) send = true;

        if (!send || string.IsNullOrWhiteSpace(chatInput)) return;

        _ = client.SendAsync(Msg.Chat, new ChatRequest { Text = chatInput.Trim() });
        chatInput = string.Empty;
    }

    // -- helpers ----------------------------------------------------------

    private void SaveSettings()
    {
        plugin.Configuration.ServerUrl = serverUrl.Trim();
        plugin.Configuration.DisplayName = displayName.Trim();
        plugin.Configuration.Save();
    }

    private static void TextColored(Vector4 color, string text)
    {
        var value = color;
        ImGui.TextColored(value, text);
    }

    private static void TextMuted(string text) => TextColored(Muted, text);

    private static void TextWrapped(string text) => ImGui.TextWrapped(text);
}
