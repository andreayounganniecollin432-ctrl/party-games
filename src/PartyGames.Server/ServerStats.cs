using System.Text;

namespace PartyGames.Server;

public sealed record PlayerStatus(string Name, bool Online, bool Ready, bool Host);

public sealed record RoomStatus(string Code, string Game, string Phase, List<PlayerStatus> Players);

public sealed record ServerStatus(int Online, int Rooms, List<RoomStatus> RoomList, List<string> OnlineNames);

/// <summary>
/// 一个极简的全局快照，供网页看板和房间标题使用。
/// 只由 Hub 在锁内更新，读的时候不需要再拿锁。
/// </summary>
public static class ServerStats
{
    private static ServerStatus _current = new(0, 0, new List<RoomStatus>(), new List<string>());

    public static ServerStatus Current => _current;

    public static int TotalOnline => _current.Online;

    public static void Update(ServerStatus status) => _current = status;

    /// <summary>把快照渲染成一个可以直接看的网页。</summary>
    public static string RenderHtml(ServerStatus status)
    {
        var html = new StringBuilder();
        html.Append("""
            <!DOCTYPE html>
            <html lang="zh-CN">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta http-equiv="refresh" content="10">
            <title>PartyGames 服务器状态</title>
            <style>
              body { margin:0; padding:24px; background:#14161c; color:#dfe3ec;
                     font:15px/1.6 "Microsoft YaHei","PingFang SC",system-ui,sans-serif; }
              h1 { font-size:20px; margin:0 0 4px; }
              .muted { color:#8b93a5; font-size:13px; }
              .big { font-size:34px; color:#7fb6e8; font-weight:700; }
              .panel { background:#1c1f27; border:1px solid #2e3442; border-radius:10px;
                       padding:14px 16px; margin:14px 0; }
              table { width:100%; border-collapse:collapse; }
              th, td { text-align:left; padding:6px 8px; border-bottom:1px solid #262b36; }
              th { color:#8b93a5; font-weight:500; font-size:13px; }
              .on { color:#6fdc8c; }
              .off { color:#8b93a5; }
              .tag { font-size:12px; padding:1px 7px; border-radius:999px;
                     background:#2c3240; color:#8b93a5; margin-left:6px; }
              .empty { color:#8b93a5; padding:10px 0; }
            </style>
            </head>
            <body>
            <h1>PartyGames 服务器状态</h1>
            <div class="muted">每 10 秒自动刷新 · 数据来自运行中的服务端</div>
            """);

        html.Append($"<div class=\"panel\"><span class=\"big\">{status.Online}</span> ");
        html.Append($"<span class=\"muted\">人在线 · {status.Rooms} 个房间</span></div>");

        if (status.OnlineNames.Count > 0)
        {
            html.Append("<div class=\"panel\"><div class=\"muted\">在线玩家</div><div>");
            html.Append(string.Join("、", status.OnlineNames.Select(Web)));
            html.Append("</div></div>");
        }

        if (status.RoomList.Count == 0)
        {
            html.Append("<div class=\"panel\"><div class=\"empty\">目前没有房间在开。</div></div>");
        }

        foreach (var room in status.RoomList)
        {
            var online = room.Players.Count(p => p.Online);
            html.Append("<div class=\"panel\">");
            html.Append($"<div><b>{Web(room.Game)}</b> · 房间 {Web(room.Code)} ");
            html.Append($"<span class=\"tag\">{Web(PhaseName(room.Phase))}</span>");
            html.Append($"<span class=\"tag\">{online}/{room.Players.Count} 在线</span></div>");

            html.Append("<table><tr><th>玩家</th><th>状态</th></tr>");
            var showReady = room.Phase == "lobby";

            foreach (var player in room.Players)
            {
                var state = player.Online
                    ? (showReady && player.Ready
                        ? "<span class=\"on\">在线 · 已准备</span>"
                        : "<span class=\"on\">在线</span>")
                    : "<span class=\"off\">离线</span>";

                var hostTag = player.Host ? "<span class=\"tag\">房主</span>" : "";
                html.Append($"<tr><td>{Web(player.Name)}{hostTag}</td><td>{state}</td></tr>");
            }

            html.Append("</table></div>");
        }

        html.Append("</body></html>");
        return html.ToString();
    }

    private static string PhaseName(string phase) => phase switch
    {
        "lobby" => "大厅",
        "playing" => "游戏中",
        "finished" => "结算中",
        _ => phase,
    };

    private static string Web(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
