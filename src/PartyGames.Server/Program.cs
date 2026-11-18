using PartyGames.Protocol;
using PartyGames.Server;
using PartyGames.Server.Games;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<Hub>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Hub>());
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader()));

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();

app.Map(Proto.WebSocketPath, async (HttpContext context, Hub hub) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket only. Connect with ws://<host>/ws");
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var connection = hub.Register(socket);

    try
    {
        await connection.RunAsync(hub.OnMessageAsync, context.RequestAborted);
    }
    finally
    {
        hub.Unregister(connection);
    }
});

app.MapGet("/api/games", () => Results.Json(GameRegistry.All()));

app.MapGet("/api/history", (int? limit) =>
    Results.Json(MatchLog.Recent(Math.Clamp(limit ?? 20, 1, 300))));

app.MapGet("/api/punishments", () => Results.Json(new
{
    dare = Punishments.All("大冒险"),
    truth = Punishments.All("真心话"),
}));

// 方便直接在浏览器里看题库（含外网隧道访问）。
app.MapGet("/punishments", () =>
{
    var dare = Punishments.All("大冒险");
    var truth = Punishments.All("真心话");

    var text = new System.Text.StringBuilder();
    text.AppendLine($"大冒险（{dare.Count} 条）");
    for (var i = 0; i < dare.Count; i++) text.AppendLine($"  {i + 1}. {dare[i]}");

    text.AppendLine();
    text.AppendLine($"真心话（{truth.Count} 条）");
    for (var i = 0; i < truth.Count; i++) text.AppendLine($"  {i + 1}. {truth[i]}");

    text.AppendLine();
    text.AppendLine($"编辑文件：{Punishments.FilePath}");
    text.AppendLine("改完保存即可生效，不用重启服务端。");

    return Results.Text(text.ToString(), "text/plain; charset=utf-8");
});

app.MapGet("/api/status", () => Results.Json(ServerStats.Current));

app.MapGet("/status", () =>
    Results.Content(ServerStats.RenderHtml(ServerStats.Current), "text/html; charset=utf-8"));

app.MapGet("/api/health", (Hub hub) => Results.Json(new
{
    ok = true,
    protocol = Proto.Version,
    rooms = hub.RoomCount,
    dataDir = MatchLog.DataDirectory,
}));

app.Run();
