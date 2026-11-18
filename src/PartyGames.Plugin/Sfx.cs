using System;
using System.Runtime.InteropServices;

namespace PartyGames;

/// <summary>
/// 音效与语音播报。
/// 不引第三方包：
///   - 提示音走 winmm.dll 的 PlaySound（WAV）
///   - 语音走 Windows 自带的 SAPI（COM），中文 Windows 上一般有中文语音
/// 全部包在 try/catch 里，播不出来也不影响游戏。
/// </summary>
internal static class Sfx
{
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_FILENAME = 0x00020000;
    private const uint SND_NODEFAULT = 0x0002;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

    private static dynamic? _voice;
    private static DateTime _lastSpeak = DateTime.MinValue;

    /// <summary>播放一个 WAV 文件（异步，不阻塞界面）。</summary>
    public static void PlayWav(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return;
            PlaySound(path, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
        }
        catch
        {
            /* 忽略 */
        }
    }

    /// <summary>用系统语音念一句话（异步）。</summary>
    public static void Speak(string text, int minGapMs = 1500)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if ((DateTime.UtcNow - _lastSpeak).TotalMilliseconds < minGapMs) return;
            _lastSpeak = DateTime.UtcNow;

            if (_voice is null)
            {
                var type = Type.GetTypeFromProgID("SAPI.SpVoice");
                if (type is null) return;
                _voice = Activator.CreateInstance(type);
                try { _voice.Volume = 100; } catch { }
                try { _voice.Rate = 0; } catch { }
            }

            // flags=1 异步播放
            _voice.Speak(text, 1);
        }
        catch
        {
            /* 没装语音包或 COM 不可用就算了 */
        }
    }

    public static void Stop()
    {
        try { PlaySound(null, IntPtr.Zero, 0); } catch { }
        try { _voice?.Speak(string.Empty, 3); } catch { }
    }
}
