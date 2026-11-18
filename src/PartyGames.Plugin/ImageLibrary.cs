using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace PartyGames;

/// <summary>
/// 图片库：扫描插件目录下的 images\ 文件夹。
/// 不走系统对话框、不依赖 Dalamud 服务——把图片丢进去就能在设置里选。
/// </summary>
internal static class ImageLibrary
{
    public static string Folder => Path.Combine(BaseDir, "images");

    private static string _baseDir = string.Empty;

    public static string BaseDir
    {
        get
        {
            if (_baseDir.Length == 0)
            {
                try
                {
                    var loc = typeof(ImageLibrary).Assembly.Location;
                    _baseDir = string.IsNullOrEmpty(loc)
                        ? AppContext.BaseDirectory
                        : Path.GetDirectoryName(loc) ?? AppContext.BaseDirectory;
                }
                catch
                {
                    _baseDir = AppContext.BaseDirectory;
                }
            }
            return _baseDir;
        }
    }

    /// <summary>列出可用图片；目录不存在时自动创建并返回空列表。</summary>
    public static List<string> Scan()
    {
        var list = new List<string>();
        try
        {
            if (!Directory.Exists(Folder))
            {
                Directory.CreateDirectory(Folder);
                return list;
            }

            foreach (var f in Directory.GetFiles(Folder))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp")
                {
                    list.Add(Path.GetFileName(f));
                }
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            /* 扫不出来就当空的 */
        }
        return list;
    }

    public static string PathOf(string fileName) => Path.Combine(Folder, fileName);

    /// <summary>在资源管理器里打开图片文件夹。</summary>
    public static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = '"' + Folder + '"',
                UseShellExecute = true,
            });
        }
        catch
        {
            /* 打不开就算了 */
        }
    }
}
