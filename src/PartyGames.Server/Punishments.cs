using System.Text.Json;
using PartyGames.Protocol;

namespace PartyGames.Server;

/// <summary>
/// 惩罚库：大冒险 / 真心话两组，内容由用户自己维护。
/// 文件变化后会自动重新读取，改完不用重启服务端。
/// </summary>
public static class Punishments
{
    private sealed class PunishFile
    {
        public List<string> Dare { get; set; } = new();
        public List<string> Truth { get; set; } = new();
    }

    private static readonly object Gate = new();
    private static List<string> _dare = new();
    private static List<string> _truth = new();
    private static DateTime _stamp = DateTime.MinValue;
    private static bool _loaded;

    public static string FilePath => Path.Combine(MatchLog.DataDirectory, "punishments.json");

    /// <summary>按类别抽一条；列表为空时返回一个兜底文案。</summary>
    public static string Pick(string category, Random rng)
    {
        EnsureLoaded();

        var list = category == "大冒险" ? _dare : _truth;
        if (list.Count == 0)
        {
            return category == "大冒险"
                ? "临时想一个大家都满意的大冒险"
                : "回答一个大家最想问的问题";
        }

        return list[rng.Next(list.Count)];
    }

    public static IReadOnlyList<string> All(string category)
    {
        EnsureLoaded();
        return category == "大冒险" ? _dare : _truth;
    }

    private static void EnsureLoaded()
    {
        lock (Gate)
        {
            var stamp = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;

            if (_loaded && stamp == _stamp) return;

            if (!File.Exists(FilePath))
            {
                WriteTemplate();
                stamp = File.GetLastWriteTimeUtc(FilePath);
            }

            try
            {
                var text = File.ReadAllText(FilePath);
                var parsed = JsonSerializer.Deserialize<PunishFile>(text, PgJson.Options);

                _dare = parsed?.Dare?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();
                _truth = parsed?.Truth?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Punishments: 读取 {FilePath} 失败：{ex.Message}");

                // 用户手改坏了 JSON 时不要整库变空——保留上次成功加载的内容，
                // 第一次就失败则退回内置默认。
                if (_dare.Count == 0 && _truth.Count == 0)
                {
                    var fallback = Defaults();
                    _dare = fallback.Dare;
                    _truth = fallback.Truth;
                }
            }

            _stamp = stamp;
            _loaded = true;
        }
    }

    private static PunishFile Defaults() => new()
    {
        Dare = new List<string>
        {
            "学三声猫叫，要像",
            "用最浮夸的语气夸右边那位三次",
            "站起来转三圈再坐下",
            "模仿一位队长的开场白",
            "下一局全程用第三人称称呼自己",
        },
        Truth = new List<string>
        {
            "最近一次说谎是什么时候，说了什么？",
            "群里你最想道歉的人是谁？",
            "你手机里最舍不得删的一张图是什么？",
            "说出一个你从没告诉过别人的小秘密",
            "如果只能保留一个游戏，你删哪个？",
        },
    };

    private static void WriteTemplate()
    {
        var template = Defaults();

        try
        {
            Directory.CreateDirectory(MatchLog.DataDirectory);

            // 中文不要转义成 \uXXXX，用户要直接编辑这个文件。
            var options = new JsonSerializerOptions(PgJson.Options)
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };

            File.WriteAllText(FilePath, JsonSerializer.Serialize(template, options));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Punishments: 无法创建 {FilePath}：{ex.Message}");
        }
    }
}
