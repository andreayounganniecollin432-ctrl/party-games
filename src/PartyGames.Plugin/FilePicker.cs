using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PartyGames;

/// <summary>
/// 调 Windows 原生「打开文件」对话框，只依赖 comdlg32.dll。
/// 不用 Dalamud 的 FileDialogManager（那个在当前版本无法注入），也不用 WinForms。
///
/// 对话框跑在独立 STA 线程上，不阻塞游戏的渲染线程；选完把路径放进静态字段，
/// 由 Draw 循环里的 Poll 取走。
/// </summary>
internal static class FilePicker
{
    private static readonly object Gate = new();
    private static string? pendingResult;
    private static int busy;

    /// <summary>诊断输出（由插件设为写日志）。</summary>
    public static Action<string>? Diag;

    public static bool IsBusy => Volatile.Read(ref busy) == 1;

    /// <summary>打开对话框；选完后调用 Poll 取结果。</summary>
    public static void BeginPick(string title)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;

        Diag?.Invoke("[PartyGames] BeginPick: " + title);
        var thread = new Thread(() =>
        {
            try
            {
                var path = ShowDialog(title);
                lock (Gate) pendingResult = path;
            }
            catch
            {
                lock (Gate) pendingResult = null;
            }
            finally
            {
                Volatile.Write(ref busy, 0);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    /// <summary>取走结果；没有则返回 null。</summary>
    public static string? Poll()
    {
        lock (Gate)
        {
            if (pendingResult is null) return null;
            var r = pendingResult;
            pendingResult = null;
            return r;
        }
    }

    private static string? ShowDialog(string title)
    {
        const int OFN_FILEMUSTEXIST = 0x00001000;
        const int OFN_PATHMUSTEXIST = 0x00000800;
        const int OFN_NOCHANGEDIR = 0x00000008;
        const int OFN_EXPLORER = 0x00080000;
        const int MAX = 1024;

        var ofn = new OPENFILENAME
        {
            lStructSize = Marshal.SizeOf<OPENFILENAME>(),
            lpstrFilter = "图片\0*.png;*.jpg;*.jpeg;*.bmp\0所有文件\0*.*\0\0",
            lpstrFile = new string('\0', MAX),
            nMaxFile = MAX,
            lpstrTitle = title,
            Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | OFN_EXPLORER,
        };

        ofn.hwndOwner = IntPtr.Zero;

        Diag?.Invoke("[PartyGames] 打开文件对话框…");
        var ok = GetOpenFileNameW(ref ofn);
        if (!ok)
        {
            var err = CommDlgExtendedError();
            Diag?.Invoke($"[PartyGames] 对话框返回 false，错误码 0x{err:X}（0 表示用户取消）");
            return null;
        }
        Diag?.Invoke("[PartyGames] 已选择文件");

        var raw = ofn.lpstrFile ?? string.Empty;
        var end = raw.IndexOf('\0');
        return end >= 0 ? raw[..end] : raw;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string? lpstrFilter;
        public string? lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)]
        public string lpstrFile;
        public int nMaxFile;
        public string? lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAME lpofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CommDlgExtendedError();
}
