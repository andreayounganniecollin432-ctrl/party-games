using System.Net.WebSockets;
using System.Text.Json;
using PartyGames.Protocol;
using PartyGames.Server.Games;

namespace PartyGames.Server;

/// <summary>
/// Connection registry plus the single game loop. Everything mutating rooms runs
/// under one lock, which keeps room state consistent without per-object locking.
/// </summary>
public sealed class Hub : IHostedService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Connection> _connections = new();
    private readonly ILogger<Hub> _logger;
    private Timer? _timer;
    private int _tickCount;

    public Hub(ILogger<Hub> logger) => _logger = logger;

    public int RoomCount
    {
        get { lock (_gate) return _rooms.Count; }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => SafeTick(), null, 250, 250);
        _logger.LogInformation("PartyGames hub started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        return Task.CompletedTask;
    }

    public Connection Register(WebSocket socket)
    {
        var connection = new Connection(socket);
        lock (_gate)
        {
            _connections[connection.Id] = connection;
            RefreshStats();
        }

        _logger.LogInformation("connection {Id} opened", connection.Id);
        return connection;
    }

    public void Unregister(Connection connection)
    {
        lock (_gate)
        {
            _connections.Remove(connection.Id);

            if (connection.RoomCode is not null &&
                _rooms.TryGetValue(connection.RoomCode, out var room) &&
                connection.PlayerId is not null)
            {
                var player = room.FindPlayer(connection.PlayerId);
                if (player is not null && ReferenceEquals(player.Conn, connection))
                {
                    room.HandleDisconnect(player);
                }
            }
        }

        _logger.LogInformation("connection {Id} closed", connection.Id);
    }

    private void SafeTick()
    {
        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "hub tick failed");
        }
    }

    private void Tick()
    {
        lock (_gate)
        {
            var now = Room.Now;

            foreach (var room in _rooms.Values.ToList())
            {
                try
                {
                    room.Tick(now);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "room {Code} tick failed", room.Code);
                }
            }

            foreach (var room in _rooms.Values.ToList())
            {
                if (room.IsExpired(now))
                {
                    _rooms.Remove(room.Code);
                    _logger.LogInformation("room {Code} expired", room.Code);
                }
            }

            RefreshStats();

            // 每 4 秒给不在房间里的人推一次在线人数和战绩（选择游戏界面要用）。
            if (++_tickCount % 16 == 0) BroadcastStatus();
        }
    }

    /// <summary>Pushes the server snapshot to everyone sitting on the game-selection screen.</summary>
    private void BroadcastStatus()
    {
        var online = ServerStats.Current.Online;
        var rooms = ServerStats.Current.Rooms;

        foreach (var connection in _connections.Values)
        {
            if (connection.RoomCode is not null) continue;
            SendStatus(connection, online, rooms);
        }
    }

    private void SendStatus(Connection connection, int online, int rooms)
    {
        var names = _connections.Values
            .Where(other => !other.IsClosed && !string.IsNullOrEmpty(other.DisplayName))
            .Select(other => other.DisplayName!)
            .Distinct()
            .ToList();

        connection.Send(Envelope.Of(Msg.Status, new StatusMessage
        {
            Online = online,
            Rooms = rooms,
            OnlinePlayers = names,
            Stats = Room.BuildStatsFor(connection.DisplayName ?? string.Empty),
        }));
    }

    /// <summary>Rebuilds the global snapshot used by /status and the lobby header.</summary>
    private void RefreshStats()
    {
        // 只展示还有人在线的房间；全员掉线的房间要过几分钟才回收，没必要占版面。
        var rooms = _rooms.Values
            .Where(room => room.Players.Any(player => player.IsOnline))
            .Select(room => new RoomStatus(
                room.Code,
                room.GameName,
                room.Phase,
                room.Players
                    .Select(player => new PlayerStatus(
                        player.Name,
                        player.IsOnline,
                        player.IsReady,
                        player.Id == room.HostId))
                    .ToList()))
            .ToList();

        // 在线人数按"已连接"算，不按"在房间"算——否则站在选择界面的人一个都不算。
        var connected = _connections.Values
            .Where(connection => !connection.IsClosed)
            .ToList();

        var online = connected.Count;
        var names = connected
            .Where(connection => !string.IsNullOrEmpty(connection.DisplayName))
            .Select(connection => connection.DisplayName!)
            .Distinct()
            .ToList();

        ServerStats.Update(new ServerStatus(online, rooms.Count, rooms, names));
    }

    public Task OnMessageAsync(Connection connection, string raw)
    {
        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(raw, PgJson.Options);
        }
        catch (JsonException)
        {
            SendError(connection, "bad_json", "消息不是合法的 JSON。");
            return Task.CompletedTask;
        }

        if (envelope is null || string.IsNullOrEmpty(envelope.Type)) return Task.CompletedTask;

        lock (_gate)
        {
            try
            {
                Dispatch(connection, envelope);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "dispatch {Type} failed", envelope.Type);
                SendError(connection, "server_error", "服务器处理出错。");
            }
        }

        return Task.CompletedTask;
    }

    private void Dispatch(Connection connection, Envelope envelope)
    {
        switch (envelope.Type)
        {
            case Msg.Hello:
                HandleHello(connection, PgJson.Read<HelloRequest>(envelope.Data));
                break;

            case Msg.CreateRoom:
                HandleCreate(connection, PgJson.Read<CreateRoomRequest>(envelope.Data));
                break;

            case Msg.JoinRoom:
                HandleJoin(connection, PgJson.Read<JoinRoomRequest>(envelope.Data));
                break;

            case Msg.Ready:
                HandleReady(connection, PgJson.Read<ReadyRequest>(envelope.Data));
                break;

            case Msg.StartGame:
                HandleStart(connection);
                break;

            case Msg.Action:
                HandleAction(connection, PgJson.Read<ActionRequest>(envelope.Data));
                break;

            case Msg.Chat:
                HandleChat(connection, PgJson.Read<ChatRequest>(envelope.Data));
                break;

            case Msg.LeaveRoom:
                LeaveRoom(connection);
                break;

            case Msg.Ping:
                var ping = PgJson.Read<PingRequest>(envelope.Data);
                Send(connection, Msg.Pong, new PongResponse
                {
                    ClientTimeMs = ping?.ClientTimeMs ?? 0,
                    ServerTimeMs = Room.Now,
                });
                break;

            default:
                SendError(connection, "unknown_type", $"不认识的消息类型：{envelope.Type}");
                break;
        }
    }

    private void HandleHello(Connection connection, HelloRequest? request)
    {
        if (request is null) return;

        if (request.ProtocolVersion != Proto.Version)
        {
            Send(connection, Msg.Reject, new RejectResponse
            {
                Code = "protocol",
                Message = $"协议版本不一致（服务器 {Proto.Version}，客户端 {request.ProtocolVersion}）。请更新插件或服务器。",
            });
            return;
        }

        var name = Sanitize(request.DisplayName, 24);
        if (name.Length == 0) name = "玩家" + connection.Id[..4];

        connection.DisplayName = name;
        connection.World = Sanitize(request.World, 20);
        if (!string.IsNullOrWhiteSpace(request.ContentId)) connection.ContentId = request.ContentId;

        string? notice = null;

        if (!string.IsNullOrWhiteSpace(request.ResumeRoom) &&
            !string.IsNullOrWhiteSpace(request.ResumePlayerId) &&
            _rooms.TryGetValue(request.ResumeRoom!, out var room))
        {
            var player = room.FindPlayer(request.ResumePlayerId);
            if (player is not null && !player.IsOnline)
            {
                room.Rebind(player, connection);
                room.Log("system", $"{player.Name} 重新连接了。");
                room.Sync(player);
                notice = $"已回到房间 {room.Code}（{room.GameName}）";
            }
        }

        connection.PlayerId ??= Guid.NewGuid().ToString("N")[..10];

        Send(connection, Msg.Welcome, new WelcomeResponse
        {
            PlayerId = connection.PlayerId!,
            DisplayName = name,
            ServerTimeMs = Room.Now,
            Notice = notice,
            Games = GameRegistry.All().ToList(),
        });

        SendStatus(connection, ServerStats.Current.Online, ServerStats.Current.Rooms);
    }

    private void HandleCreate(Connection connection, CreateRoomRequest? request)
    {
        if (connection.PlayerId is null)
        {
            SendError(connection, "no_hello", "请先发送 hello。");
            return;
        }

        if (request is null) return;

        var descriptor = GameRegistry.Find(request.GameId) ?? GameRegistry.All().FirstOrDefault();
        if (descriptor is null)
        {
            SendError(connection, "no_games", "服务器没有可用的游戏。");
            return;
        }

        LeaveRoom(connection);

        var code = NewRoomCode();
        var room = new Room(code, descriptor, request.MaxPlayers);
        _rooms[code] = room;

        var host = room.AddPlayer(connection);
        room.HostId = host.Id;

        room.Log("system", $"{host.Name} 创建了房间 {code}（{descriptor.Name}）。");
        room.Sync(host);
        _logger.LogInformation("room {Code} created by {Name}", code, host.Name);
    }

    private void HandleJoin(Connection connection, JoinRoomRequest? request)
    {
        if (connection.PlayerId is null)
        {
            SendError(connection, "no_hello", "请先发送 hello。");
            return;
        }

        if (request is null) return;

        var code = Sanitize(request.Code, 8).ToUpperInvariant();
        if (code.Length == 0)
        {
            SendError(connection, "bad_code", "房间码是空的。");
            return;
        }

        if (!_rooms.TryGetValue(code, out var room))
        {
            SendError(connection, "no_room", $"找不到房间 {code}。");
            return;
        }

        if (room.Phase != RoomPhase.Lobby)
        {
            SendError(connection, "in_progress", "这局已经开始了，等下一局吧。");
            return;
        }

        if (room.Players.Count >= room.MaxPlayers)
        {
            SendError(connection, "full", "房间满了。");
            return;
        }

        if (room.FindPlayer(connection.PlayerId) is not null)
        {
            room.Sync(room.FindPlayer(connection.PlayerId)!);
            return;
        }

        LeaveRoom(connection);

        var player = room.AddPlayer(connection);
        room.Log("system", $"{player.Name} 加入了房间。");
        room.SyncAll();

        // New joiners get the recent history so they are not staring at a blank log.
        connection.Send(Envelope.Of(Msg.Room, room.BuildSnapshot(player, includeBacklog: true)));
    }

    private void HandleReady(Connection connection, ReadyRequest? request)
    {
        var room = ConnectionRoom(connection);
        if (room is null || connection.PlayerId is null) return;

        room.SetReady(connection.PlayerId, request?.Ready ?? false);
    }

    private void HandleStart(Connection connection)
    {
        var room = ConnectionRoom(connection);
        if (room is null || connection.PlayerId is null) return;

        var player = room.FindPlayer(connection.PlayerId);
        if (player is null) return;

        if (!room.TryStartGame(player, out var error))
        {
            SendError(connection, "cannot_start", error ?? "无法开始游戏。");
        }
    }

    private void HandleAction(Connection connection, ActionRequest? request)
    {
        if (request is null) return;

        var room = ConnectionRoom(connection);
        if (room is null || connection.PlayerId is null)
        {
            SendError(connection, "not_in_room", "你不在任何房间里。");
            return;
        }

        var player = room.FindPlayer(connection.PlayerId);
        if (player is null) return;

        switch (request.Kind)
        {
            case "leave":
                LeaveRoom(connection);
                return;

            case "ready":
                room.SetReady(player.Id, true);
                return;

            case "unready":
                room.SetReady(player.Id, false);
                return;

            case "start":
                if (!room.TryStartGame(player, out var error))
                {
                    SendError(connection, "cannot_start", error ?? "无法开始游戏。");
                }
                return;

            case "lobby":
                if (player.Id == room.HostId) room.ReturnToLobby();
                return;

            case "setting":
                room.HandleSetting(player, request.Target, request.Text);
                return;

            case "resetsession":
                if (player.Id == room.HostId) room.ResetSession();
                return;

            default:
                room.HandleGameAction(player, request);
                return;
        }
    }

    private void HandleChat(Connection connection, ChatRequest? request)
    {
        var room = ConnectionRoom(connection);
        if (room is null || connection.PlayerId is null) return;

        var player = room.FindPlayer(connection.PlayerId);
        if (player is null) return;

        var text = Sanitize(request?.Text, 140);
        if (text.Length == 0) return;

        room.Log("chat", text, player.Name);
    }

    private void LeaveRoom(Connection connection)
    {
        if (connection.RoomCode is null) return;

        if (_rooms.TryGetValue(connection.RoomCode, out var room) && connection.PlayerId is not null)
        {
            room.RemovePlayer(connection.PlayerId);
        }

        connection.RoomCode = null;
        connection.Send(Envelope.Of(Msg.Room, new RoomSnapshot
        {
            Phase = "none",
            ServerTimeMs = Room.Now,
            View = new GameView { Title = "已离开房间" },
        }));

        SendStatus(connection, ServerStats.Current.Online, ServerStats.Current.Rooms);
    }

    private Room? ConnectionRoom(Connection connection) =>
        connection.RoomCode is not null && _rooms.TryGetValue(connection.RoomCode, out var room) ? room : null;

    private string NewRoomCode()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var chars = new char[Proto.CodeLength];
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = Proto.CodeAlphabet[Random.Shared.Next(Proto.CodeAlphabet.Length)];
            }

            var code = new string(chars);
            if (!_rooms.ContainsKey(code)) return code;
        }

        return Guid.NewGuid().ToString("N")[..Proto.CodeLength].ToUpperInvariant();
    }

    private static string Sanitize(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var filtered = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return filtered.Length <= max ? filtered : filtered[..max];
    }

    private static void Send(Connection connection, string type, object? payload) =>
        connection.Send(Envelope.Of(type, payload));

    private static void SendError(Connection connection, string code, string message) =>
        Send(connection, Msg.Error, new ErrorResponse { Code = code, Message = message });
}
