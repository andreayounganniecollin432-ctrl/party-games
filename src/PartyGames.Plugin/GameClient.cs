using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;
using PartyGames.Protocol;

namespace PartyGames;

public enum ClientState
{
    Disconnected,
    Connecting,
    Connected,
}

/// <summary>
/// WebSocket client. The receive loop only pushes raw frames into a queue;
/// <see cref="Pump"/> applies them on the UI thread, so all visible state is
/// mutated from exactly one thread.
/// </summary>
public sealed class GameClient : IGameClient, IDisposable
{
    private readonly ConcurrentQueue<string> inbox = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly IPluginLog log;
    private ClientWebSocket? socket;
    private CancellationTokenSource? cts;

    public GameClient(IPluginLog log) => this.log = log;

    public ClientState State { get; private set; } = ClientState.Disconnected;
    public string Status { get; private set; } = "未连接";
    public string? PlayerId { get; private set; }
    public string? PlayerName { get; private set; }
    public RoomSnapshot? Room { get; private set; }
    public List<GameDescriptor> Games { get; } = new();
    public List<RoomEvent> Events { get; } = new();

    /// <summary>Server-wide numbers, pushed while we are not in a room.</summary>
    public int ServerOnline { get; private set; }

    public int ServerRooms { get; private set; }

    /// <summary>This player's record across all stored matches.</summary>
    public List<StatRow> MyStats { get; } = new();

    /// <summary>Everyone connected to the server right now.</summary>
    public List<string> OnlineNames { get; } = new();

    /// <summary>玩家 id → base64 头像。服务端在加入房间时发一份，之后只推变化。</summary>
    public Dictionary<string, string> Avatars { get; } = new();
    public string? LastError { get; private set; }
    public long ServerOffsetMs { get; private set; }

    public bool IsConnected => State == ClientState.Connected;
    public bool IsInRoom => Room is not null;
    public long ServerNowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ServerOffsetMs;

    public async Task ConnectAsync(string url, string displayName, string? resumeRoom, string? resumePlayerId)
    {
        if (State != ClientState.Disconnected) return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "ws" && uri.Scheme != "wss"))
        {
            Status = "地址要以 ws:// 或 wss:// 开头";
            return;
        }

        State = ClientState.Connecting;
        Status = "连接中…";
        LastError = null;
        cts = new CancellationTokenSource();

        try
        {
            socket = new ClientWebSocket();
            await socket.ConnectAsync(uri, cts.Token);

            State = ClientState.Connected;
            Status = "已连接";
            _ = Task.Run(() => ReceiveLoopAsync(cts.Token));

            await SendAsync(Msg.Hello, new HelloRequest
            {
                DisplayName = displayName,
                ProtocolVersion = Proto.Version,
                ResumeRoom = string.IsNullOrWhiteSpace(resumeRoom) ? null : resumeRoom,
                ResumePlayerId = string.IsNullOrWhiteSpace(resumePlayerId) ? null : resumePlayerId,
            });
        }
        catch (Exception ex)
        {
            log.Warning(ex, "PartyGames: connect failed");
            State = ClientState.Disconnected;
            Status = $"连接失败：{ex.Message}";
            socket?.Dispose();
            socket = null;
        }
    }

    public void Disconnect()
    {
        try { cts?.Cancel(); } catch { /* ignore */ }
        try { socket?.Dispose(); } catch { /* ignore */ }

        socket = null;
        cts = null;
        State = ClientState.Disconnected;
        Room = null;
        Events.Clear();
        Avatars.Clear();
    }

    public async Task SendAsync(string type, object? payload)
    {
        var target = socket;
        if (target is null || target.State != WebSocketState.Open) return;

        var bytes = Encoding.UTF8.GetBytes(PgJson.SerializeEnvelope(Envelope.Of(type, payload)));

        await sendLock.WaitAsync();
        try
        {
            await target.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "PartyGames: send failed");
        }
        finally
        {
            sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();

        try
        {
            while (!cancellationToken.IsCancellationRequested && socket is { State: WebSocketState.Open })
            {
                stream.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    stream.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                inbox.Enqueue(Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log.Warning(ex, "PartyGames: receive loop stopped");
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                State = ClientState.Disconnected;
                Status = "连接已断开";
                Room = null;
            }
        }
    }

    /// <summary>Applies queued frames. Must be called from the UI thread.</summary>
    public void Pump()
    {
        while (inbox.TryDequeue(out var json)) Apply(json);
    }

    private void Apply(string json)
    {
        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(json, PgJson.Options);
        }
        catch (JsonException)
        {
            return;
        }

        if (envelope is null) return;

        switch (envelope.Type)
        {
            case Msg.Welcome:
            {
                var welcome = PgJson.Read<WelcomeResponse>(envelope.Data);
                if (welcome is null) return;

                PlayerId = welcome.PlayerId;
                PlayerName = welcome.DisplayName;
                ServerOffsetMs = welcome.ServerTimeMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (!string.IsNullOrEmpty(welcome.Notice)) Status = welcome.Notice!;

                Games.Clear();
                Games.AddRange(welcome.Games);
                break;
            }

            case Msg.Room:
            {
                var snapshot = PgJson.Read<RoomSnapshot>(envelope.Data);
                if (snapshot is null) return;

                if (snapshot.Phase == "none")
                {
                    Room = null;
                    Events.Clear();
                    break;
                }

                ServerOffsetMs = snapshot.ServerTimeMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (snapshot.Backlog is { Count: > 0 } && Events.Count == 0)
                {
                    Events.AddRange(snapshot.Backlog);
                }

                if (snapshot.Avatars is { Count: > 0 })
                {
                    foreach (var avatar in snapshot.Avatars)
                    {
                        if (string.IsNullOrEmpty(avatar.ImageBase64)) continue;
                        Avatars[avatar.PlayerId] = avatar.ImageBase64;
                    }
                }

                Room = snapshot;
                break;
            }

            case Msg.Status:
            {
                var status = PgJson.Read<StatusMessage>(envelope.Data);
                if (status is null) return;

                ServerOnline = status.Online;
                ServerRooms = status.Rooms;

                MyStats.Clear();
                MyStats.AddRange(status.Stats);

                OnlineNames.Clear();
                OnlineNames.AddRange(status.OnlinePlayers);
                break;
            }

            case Msg.Avatar:
            {
                var avatar = PgJson.Read<AvatarMessage>(envelope.Data);
                if (avatar is null || string.IsNullOrEmpty(avatar.PlayerId)) return;

                if (string.IsNullOrEmpty(avatar.ImageBase64)) Avatars.Remove(avatar.PlayerId);
                else Avatars[avatar.PlayerId] = avatar.ImageBase64;
                break;
            }

            case Msg.Event:
            {
                var roomEvent = PgJson.Read<EventMessage>(envelope.Data)?.Event;
                if (roomEvent is null) return;

                Events.Add(roomEvent);
                if (Events.Count > 400) Events.RemoveRange(0, Events.Count - 400);
                break;
            }

            case Msg.Error:
            {
                var error = PgJson.Read<ErrorResponse>(envelope.Data);
                LastError = error?.Message;
                break;
            }

            case Msg.Reject:
            {
                var reject = PgJson.Read<RejectResponse>(envelope.Data);
                Status = reject?.Message ?? "被服务器拒绝";
                Disconnect();
                break;
            }
        }
    }

    public void Dispose() => Disconnect();
}
