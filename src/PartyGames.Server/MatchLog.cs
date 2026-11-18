using System.Text.Json;
using PartyGames.Protocol;

namespace PartyGames.Server;

/// <summary>One finished match, as written to disk for external tools (e.g. the QQ bot).</summary>
public sealed class MatchRecord
{
    public string RoomCode { get; set; } = string.Empty;
    public string GameId { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public long StartedAtMs { get; set; }
    public long EndedAtMs { get; set; }
    public string Result { get; set; } = string.Empty;
    public string? LosingSide { get; set; }
    public List<MatchPlayer> Players { get; set; } = new();
}

public sealed class MatchPlayer
{
    public string Name { get; set; } = string.Empty;
    public int Score { get; set; }
    public int Delta { get; set; }
    public string? Note { get; set; }
    public bool Lost { get; set; }
}

/// <summary>
/// Append-only match history. The JSON file is the integration point for anything
/// outside the game (the QQ bot reads it directly; no network or token needed).
/// </summary>
public static class MatchLog
{
    private const int Keep = 300;

    private static readonly object Gate = new();
    private static readonly List<MatchRecord> History = new();

    public static string DataDirectory { get; } = ResolveDataDirectory();

    public static string HistoryPath => Path.Combine(DataDirectory, "matches.json");

    /// <summary>
    /// Loads the on-disk history once at startup. Without this, every server restart
    /// would overwrite matches.json with only the matches from the new process —
    /// which quietly breaks any "this week" style statistics.
    /// </summary>
    static MatchLog()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return;

            var text = File.ReadAllText(HistoryPath);
            var loaded = JsonSerializer.Deserialize<List<MatchRecord>>(text, PgJson.Options);
            if (loaded is { Count: > 0 })
            {
                History.AddRange(loaded.TakeLast(Keep));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"MatchLog: could not load history: {ex.Message}");
        }
    }

    private static string ResolveDataDirectory()
    {
        var custom = Environment.GetEnvironmentVariable("PARTYGAMES_DATA");
        if (!string.IsNullOrWhiteSpace(custom)) return custom;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PartyGames");
    }

    public static IReadOnlyList<MatchRecord> Recent(int limit)
    {
        lock (Gate)
        {
            return History.Count == 0
                ? Array.Empty<MatchRecord>()
                : History.Skip(Math.Max(0, History.Count - limit)).Reverse().ToList();
        }
    }

    public static void Record(MatchRecord record)
    {
        lock (Gate)
        {
            History.Add(record);
            if (History.Count > Keep) History.RemoveRange(0, History.Count - Keep);

            try
            {
                Directory.CreateDirectory(DataDirectory);

                var json = JsonSerializer.Serialize(History, PgJson.Options);
                File.WriteAllText(HistoryPath, json);

                File.WriteAllText(
                    Path.Combine(DataDirectory, "latest.json"),
                    JsonSerializer.Serialize(record, PgJson.Options));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"MatchLog: could not write history: {ex.Message}");
            }
        }

        // 战报推到 QQ（如果 notify.json 打开了）。
        QqNotifier.Enqueue(record);
    }
}
