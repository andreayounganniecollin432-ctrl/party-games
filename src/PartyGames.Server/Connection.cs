using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using PartyGames.Protocol;

namespace PartyGames.Server;

/// <summary>
/// One WebSocket peer. Reads run on the connection task, writes are funnelled
/// through an unbounded channel so game code never awaits I/O.
/// </summary>
public sealed class Connection
{
    private readonly WebSocket _socket;
    private readonly Channel<string> _outbound = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _cts = new();

    public Connection(WebSocket socket) => _socket = socket;

    public string Id { get; } = Guid.NewGuid().ToString("N")[..10];
    public string? DisplayName { get; set; }
    public string? World { get; set; }
    public string? ContentId { get; set; }

    /// <summary>Stable in-room identity. Assigned on hello and reused when a seat is resumed.</summary>
    public string? PlayerId { get; set; }

    public string? RoomCode { get; set; }
    public bool IsClosed { get; private set; }
    public long ConnectedAtMs { get; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Send(Envelope envelope) => _outbound.Writer.TryWrite(PgJson.SerializeEnvelope(envelope));

    public async Task RunAsync(Func<Connection, string, Task> onMessage, CancellationToken ct)
    {
        var writer = Task.Run(() => WriteLoopAsync(_cts.Token), CancellationToken.None);
        var buffer = new byte[16 * 1024];
        using var incoming = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                incoming.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    incoming.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (incoming.Length == 0) continue;

                var text = Encoding.UTF8.GetString(incoming.GetBuffer(), 0, (int)incoming.Length);
                await onMessage(this, text);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            Close();
            await Task.WhenAny(writer, Task.Delay(2000));
        }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var payload in _outbound.Reader.ReadAllAsync(ct))
            {
                if (_socket.State != WebSocketState.Open) break;
                var bytes = Encoding.UTF8.GetBytes(payload);
                await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }
        }
        catch
        {
            // The peer vanished; the read loop will notice and clean up.
        }
    }

    public void Close()
    {
        if (IsClosed) return;
        IsClosed = true;
        _outbound.Writer.TryComplete();
        try { _cts.Cancel(); } catch { /* already disposed */ }
    }
}
