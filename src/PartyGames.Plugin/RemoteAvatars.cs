using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace PartyGames;

/// <summary>
/// 别人的头像：把服务端转发的 base64 变成贴图，按玩家 id 缓存。
/// 贴图创建是异步的，没就绪的那几帧退回首字圆——所以列表不会因为等图而卡住。
/// </summary>
internal sealed class RemoteAvatars : IDisposable
{
    private sealed class Entry
    {
        public string Base64 = string.Empty;
        public IDalamudTextureWrap? Texture;
        public bool Loading;
    }

    private readonly Dictionary<string, Entry> entries = new();

    /// <summary>Picks up whatever the client has; starts a decode for anything new or changed.</summary>
    public void Sync(IGameClient client, ITextureProvider? textures)
    {
        foreach (var (id, base64) in client.Avatars)
        {
            if (entries.TryGetValue(id, out var existing) && existing.Base64 == base64)
            {
                continue;
            }

            var entry = new Entry { Base64 = base64 };
            entries[id] = entry;

            if (textures is not null)
            {
                _ = LoadAsync(entry, base64, textures);
            }
        }

        // 离开房间的玩家：把贴图丢掉
        if (entries.Count > client.Avatars.Count)
        {
            foreach (var id in new List<string>(entries.Keys))
            {
                if (!client.Avatars.ContainsKey(id)) entries.Remove(id);
            }
        }
    }

    private static async Task LoadAsync(Entry entry, string base64, ITextureProvider textures)
    {
        if (entry.Loading) return;
        entry.Loading = true;

        try
        {
            var bytes = Convert.FromBase64String(base64);
            entry.Texture = await textures.CreateFromImageAsync(bytes, "PartyGames avatar", CancellationToken.None);
        }
        catch
        {
            // 图片坏了/格式不支持：保持首字圆兜底
            entry.Texture = null;
        }
        finally
        {
            entry.Loading = false;
        }
    }

    /// <summary>The wrap for a player, or null while it is still decoding.</summary>
    public IDalamudTextureWrap? Wrap(string? playerId)
    {
        if (string.IsNullOrEmpty(playerId)) return null;
        return entries.TryGetValue(playerId, out var entry) ? entry.Texture : null;
    }

    public void Dispose()
    {
        entries.Clear();
    }
}

/// <summary>把自己的头像文件压成一段能塞进 WebSocket 的 base64。</summary>
internal static class AvatarUpload
{
    /// <summary>服务端上限 512 KB base64；这里留点余量。</summary>
    public const int MaxBytes = 360 * 1024;

    /// <summary>头像宽高上限：超过就不让用，也不上传。</summary>
    public const int AvatarMaxSide = 1000;

    /// <summary>读文件 → 太大就缩到 192px 再编码 → base64；失败返回 null（不传，别人看到首字）。</summary>
    public static string? Prepare(string path, out string reason)
    {
        reason = string.Empty;

        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                reason = "没选头像";
                return null;
            }

            // 分辨率上限：和选图时的规则一致，防止手改配置绕过
            using (var probe = System.Drawing.Image.FromFile(path))
            {
                if (probe.Width > AvatarMaxSide || probe.Height > AvatarMaxSide)
                {
                    reason = $"分辨率 {probe.Width}×{probe.Height} 超过 {AvatarMaxSide}×{AvatarMaxSide}";
                    return null;
                }
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaxBytes)
            {
                var shrunk = Shrink(path);
                if (shrunk is null)
                {
                    reason = $"图片 {bytes.Length / 1024} KB，超过 {MaxBytes / 1024} KB 且缩放失败";
                    return null;
                }

                bytes = shrunk;
            }

            reason = $"{bytes.Length / 1024} KB";
            return Convert.ToBase64String(bytes);
        }
        catch (Exception exception)
        {
            reason = exception.Message;
            return null;
        }
    }

    /// <summary>缩到 192×192 的方形 PNG。用 GDI+（插件只在 Windows 的游戏里跑）。</summary>
    private static byte[]? Shrink(string path)
    {
        try
        {
            using var source = System.Drawing.Image.FromFile(path);
            const int side = 192;
            using var target = new System.Drawing.Bitmap(side, side);
            using (var graphics = System.Drawing.Graphics.FromImage(target))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

                // 等比裁成正方形（cover），别把脸拉扁
                var scale = Math.Max(side / (float)source.Width, side / (float)source.Height);
                var width = (int)(source.Width * scale);
                var height = (int)(source.Height * scale);
                var x = (side - width) / 2;
                var y = (side - height) / 2;
                graphics.DrawImage(source, new System.Drawing.Rectangle(x, y, width, height));
            }

            using var stream = new MemoryStream();
            target.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
