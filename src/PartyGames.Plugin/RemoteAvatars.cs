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
/// 别人的头像：把服务端转发的 base64 变成贴图，按玩家 id 长期缓存。
///
/// 以前是「谁不在 client.Avatars 里就把贴图丢掉」，重连/换房间的那几帧列表是空的，
/// 头像就被清掉，只能退回首字。现在缓存不跟着房间走：贴图留着，解码失败还会退避重试。
/// </summary>
internal sealed class RemoteAvatars : IDisposable
{
    /// <summary>最多缓存多少个人的头像，超了先淘汰最久没出现的（房间里的人不淘汰）。</summary>
    private const int MaxEntries = 64;

    /// <summary>解码失败最多试几次。</summary>
    private const int MaxAttempts = 3;

    private sealed class Entry
    {
        public string Base64 = string.Empty;
        public IDalamudTextureWrap? Texture;
        public int Attempts;
        public long NextTryAtMs;
        public bool Loading;
    }

    private readonly Dictionary<string, Entry> entries = new();
    private readonly Dictionary<string, long> lastSeenMs = new();

    /// <summary>Picks up whatever the client has; starts a decode for anything new or changed, retries failures.</summary>
    public void Sync(IGameClient client, ITextureProvider? textures)
    {
        var now = Environment.TickCount64;

        foreach (var (id, base64) in client.Avatars)
        {
            lastSeenMs[id] = now;

            if (!entries.TryGetValue(id, out var entry) || entry.Base64 != base64)
            {
                // 新头像（或者换了图）：重新解码
                entry = new Entry { Base64 = base64 };
                entries[id] = entry;
            }
            else if (entry.Texture is not null || entry.Loading)
            {
                continue;
            }
            else if (entry.Attempts >= MaxAttempts || now < entry.NextTryAtMs)
            {
                // 失败过：还在退避期里，或者已经放弃了
                continue;
            }

            entry.Attempts++;
            entry.NextTryAtMs = now + 1500 * entry.Attempts;

            if (textures is not null)
            {
                _ = LoadAsync(entry, base64, textures);
            }
        }

        if (entries.Count > MaxEntries) Trim(client.Avatars);
    }

    /// <summary>缓存不跟着房间走，只在容量超标时淘汰最久没出现的人。</summary>
    private void Trim(IReadOnlyDictionary<string, string> current)
    {
        var stale = new List<string>(entries.Keys);
        stale.Sort((a, b) => Seen(a).CompareTo(Seen(b)));

        foreach (var id in stale)
        {
            if (entries.Count <= MaxEntries) break;
            if (current.ContainsKey(id)) continue;   // 当前房间里的人一个都不动

            entries.Remove(id);
            lastSeenMs.Remove(id);
        }
    }

    private long Seen(string id) => lastSeenMs.TryGetValue(id, out var seen) ? seen : 0;

    private static async Task LoadAsync(Entry entry, string base64, ITextureProvider textures)
    {
        if (entry.Loading) return;
        entry.Loading = true;

        try
        {
            var bytes = Convert.FromBase64String(base64);
            var texture = await textures.CreateFromImageAsync(bytes, "PartyGames avatar", CancellationToken.None);

            // 解码期间可能换了图：只有还是同一张才收下
            if (entry.Base64 == base64) entry.Texture = texture;
        }
        catch
        {
            // 图片坏了/格式不支持：保持首字圆兜底，Sync 里按退避再试（最多 3 次）
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
        lastSeenMs.Clear();
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
