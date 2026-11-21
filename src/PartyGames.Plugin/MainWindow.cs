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
    private List<string> images = new();
    private int lastLogCount;
    private readonly List<int> pickedCards = new();
    private string lastHandText = string.Empty;
    private int lastVoiceLogCount;
    private int xqSelR = -1;
    private int xqSelC = -1;
    private string xqBoardKey = string.Empty;
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
        artwork.Sync(string.Empty, avatarPath);

        // 版本水印：用于确认跑的是哪个 build
        try
        {
            var dl = ImGui.GetForegroundDrawList();
            var wp = ImGui.GetWindowPos();
            var ws = ImGui.GetWindowSize();
            dl.AddText(new Vector2(wp.X + ws.X - 78, wp.Y + 4),
                ImGui.GetColorU32(new Vector4(0.85f, 0.75f, 0.45f, 0.9f)), "v0.3.1");
        }
        catch { }
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

        // 图片库：扫描插件目录下的 images\ ，用下拉框选，不用任何系统对话框
        if (ImGui.Button("打开图片文件夹", new Vector2(130, 0)))
        {
            ImageLibrary.OpenFolder();
        }
        ImGui.SameLine();
        if (ImGui.Button("刷新", new Vector2(60, 0)))
        {
            images = ImageLibrary.Scan();
        }
        ImGui.SameLine();
        Theme.Label("把图片丢进 images 文件夹即可");

        if (images.Count == 0) images = ImageLibrary.Scan();


        var avOptions = new List<string> { "（用名字首字）" };
        avOptions.AddRange(images);
        var avIndex = string.IsNullOrEmpty(avatarPath) ? 0 : avOptions.IndexOf(System.IO.Path.GetFileName(avatarPath));
        if (avIndex < 0) avIndex = 0;

        ImGui.SetNextItemWidth(430);
        if (ImGui.Combo("头像", ref avIndex, avOptions.ToArray(), avOptions.Count))
        {
            avatarPath = avIndex == 0 ? string.Empty : ImageLibrary.PathOf(avOptions[avIndex]);
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
                Sfx.PlayClip("dz-lundao", "轮到你了");
            }
            lastHadActions = hasActions;

            // 新出的牌 → 用珊瑚的声音念牌型
            if (snapshot.GameId == "doudizhu" && plugin.Configuration.SoundEnabled)
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

        DrawFields(client, view);
        DrawPlayers(view);
        DrawScoreboard(view);
        DrawStats(view);
        ImGui.Spacing();
        // 认输：仅象棋与斗地主，点了直接结算
        if ((snapshot.GameId == "xiangqi" || snapshot.GameId == "doudizhu")
            && string.IsNullOrEmpty(view.ResultBanner))
        {
            if (ImGui.Button("认输", new Vector2(80, 0)))
            {
                _ = client.SendAsync(Msg.Action, new ActionRequest { Kind = "resign" });
            }
            ImGui.SameLine();
        }
        if (snapshot.GameId == "doudizhu") DrawCardPicker(client, view);
        if (snapshot.GameId == "xiangqi") DrawXiangqiBoard(client, view);
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

    private static string XqPiece(char p) => p switch
    {
        'K' => "帅", 'A' => "仕", 'B' => "相", 'N' => "马", 'R' => "车", 'C' => "炮", 'P' => "兵",
        'k' => "将", 'a' => "士", 'b' => "象", 'n' => "马", 'r' => "车", 'c' => "炮", 'p' => "卒",
        _ => string.Empty,
    };

    /// <summary>象棋棋盘：9×10，点击选子再点落点。</summary>
    private void DrawXiangqiBoard(GameClient client, GameView view)
    {
        if (view.Board.Count < 10) return;

        var key = string.Join("|", view.Board);
        if (key != xqBoardKey)
        {
            xqBoardKey = key;
            xqSelR = -1;
            xqSelC = -1;
        }

        const float cell = 48f;
        var w = cell * 8;
        var h = cell * 9;
        var rawPos = ImGui.GetCursorScreenPos();
        var boardPx = cell * 8 + 24;
        var origin = new Vector2(rawPos.X + MathF.Max(0f, (ImGui.GetContentRegionAvail().X - boardPx) / 2f), rawPos.Y);
        var dl = ImGui.GetWindowDrawList();

        // 底色
        dl.AddRectFilled(new Vector2(origin.X - 12, origin.Y - 12),
            new Vector2(origin.X + w + 12, origin.Y + h + 12),
            ImGui.GetColorU32(new Vector4(0.93f, 0.86f, 0.70f, 1f)), 6f);

        var lineCol = ImGui.GetColorU32(new Vector4(0.35f, 0.28f, 0.18f, 1f));

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
                var fill = isRed ? new Vector4(0.98f, 0.94f, 0.86f, 1f) : new Vector4(0.90f, 0.88f, 0.84f, 1f);
                var ring = isRed ? new Vector4(0.70f, 0.15f, 0.15f, 1f) : new Vector4(0.15f, 0.15f, 0.18f, 1f);

                dl.AddCircleFilled(new Vector2(cx, cy), cell * 0.42f, ImGui.GetColorU32(fill), 24);
                dl.AddCircle(new Vector2(cx, cy), cell * 0.42f, ImGui.GetColorU32(ring), 24, 2f);

                var text = XqPiece(ch);
                var ts = ImGui.CalcTextSize(text);
                dl.AddText(new Vector2(cx - ts.X / 2, cy - ts.Y / 2), ImGui.GetColorU32(ring), text);
            }
        }

        // 点击处理
        ImGui.InvisibleButton("##xqboard", new Vector2(w + 24, h + 24));
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
    private static List<(string Actor, string[] Cards)> FindRecentPlays(GameClient client, int max)
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

    private static string[] FindLastPlay(GameClient client)
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
        if (suit != '\0') dl.AddText(new Vector2(a.X + 4, a.Y + 16), ink, suit.ToString());

        var big = suit == '\0' ? (token == "小王" ? "小" : "大") : suit.ToString();
        var bigSize = ImGui.CalcTextSize(big);
        dl.AddText(new Vector2(a.X + (size.X - bigSize.X) / 2, b.Y - bigSize.Y - 6), ink, big);

        ImGui.Dummy(size);
    }
    /// <summary>斗地主图形选牌：把手牌拆成按钮，点选后一键出牌。</summary>
    private void DrawCardPicker(GameClient client, GameView view)
    {
        var hand = view.PrivateNote;
        if (string.IsNullOrWhiteSpace(hand)) return;

        if (hand != lastHandText)
        {
            lastHandText = hand!;
            pickedCards.Clear();
        }

        var tokens = hand!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Theme.Section($"选牌（{pickedCards.Count}/{tokens.Length}）");

        for (var i = 0; i < tokens.Length; i++)
        {
            if (i > 0 && i % 9 != 0) ImGui.SameLine();

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
            if (suit != '\0') dl.AddText(new Vector2(a.X + 5, a.Y + 18), inkU, suit.ToString());

            var bigText = suit == '\0' ? (token == "小王" ? "小" : "大") : suit.ToString();
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
        if (ImGui.Button("清空", new Vector2(80, 0))) pickedCards.Clear();

        ImGui.SameLine();
        Theme.Label(canPlay ? "点牌选中，再点出牌" : "现在不是你的回合");
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
