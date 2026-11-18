using System.Text;
using System.Text.Json;
using PartyGames.Protocol;

namespace PartyGames.Server;

/// <summary>
/// Pushes a short match report into QQ through SnowLuma's OneBot v11 HTTP API.
///
/// Why this exists: the QQ-side agent has no local tools and its web fetcher blocks
/// localhost, so it cannot read the history file or hit this server. Putting the
/// result *into the group chat* is the only channel that reaches it.
///
/// Config: %LOCALAPPDATA%\PartyGames\notify.json (see README).
/// </summary>
public static class QqNotifier
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly object Gate = new();

    private static NotifyConfig? _config;
    private static bool _loaded;

    public static string ConfigPath => Path.Combine(MatchLog.DataDirectory, "notify.json");

    public static void Enqueue(MatchRecord record)
    {
        var config = Load();
        if (config is null || !config.Enabled) return;

        _ = Task.Run(() => SendAsync(config, record));
    }

    private static NotifyConfig? Load()
    {
        lock (Gate)
        {
            if (_loaded) return _config;
            _loaded = true;

            try
            {
                if (!File.Exists(ConfigPath)) return _config = null;

                _config = JsonSerializer.Deserialize<NotifyConfig>(
                    File.ReadAllText(ConfigPath), PgJson.Options);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"QqNotifier: bad notify.json: {ex.Message}");
                _config = null;
            }

            return _config;
        }
    }

    private static async Task SendAsync(NotifyConfig config, MatchRecord record)
    {
        var message = BuildMessage(record);

        if (config.DryRun)
        {
            Console.WriteLine($"[QqNotifier dry-run] {message}");
            return;
        }

        try
        {
            var endpoint = config.Target.Equals("private", StringComparison.OrdinalIgnoreCase)
                ? "/send_private_msg"
                : "/send_group_msg";

            object payload = config.Target.Equals("private", StringComparison.OrdinalIgnoreCase)
                ? new { user_id = config.TargetId, message }
                : new { group_id = config.TargetId, message };

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                config.ApiBase.TrimEnd('/') + endpoint);

            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {config.AccessToken}");
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, PgJson.Options),
                Encoding.UTF8,
                "application/json");

            using var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode || !body.Contains("\"status\":\"ok\""))
            {
                Console.Error.WriteLine($"QqNotifier: send failed {(int)response.StatusCode}: {body}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"QqNotifier: {ex.Message}");
        }
    }

    private static string BuildMessage(MatchRecord record)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"【PartyGames】{record.GameName} 结束 · 房间 {record.RoomCode}");
        builder.AppendLine($"结果：{record.Result}");

        var ranked = record.Players
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        builder.Append("积分：");
        builder.Append(string.Join(" │ ", ranked.Select(p => $"{p.Name} {p.Score}")));

        var punished = ranked.Where(p => !string.IsNullOrEmpty(p.Note)).ToList();
        if (punished.Count > 0)
        {
            builder.AppendLine();
            builder.Append("惩罚：");
            builder.Append(string.Join("、", punished.Select(p => $"{p.Name} {p.Note}")));
        }

        return builder.ToString();
    }

    private sealed class NotifyConfig
    {
        public bool Enabled { get; set; }
        public bool DryRun { get; set; }
        public string ApiBase { get; set; } = "http://127.0.0.1:3000";
        public string AccessToken { get; set; } = string.Empty;

        /// <summary>group or private.</summary>
        public string Target { get; set; } = "group";

        public long TargetId { get; set; }
    }
}
