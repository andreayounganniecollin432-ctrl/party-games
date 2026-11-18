using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;

namespace PartyGames;

/// <summary>
/// 玩家头像 / 窗口背景的贴图管理。
/// 图片路径来自配置，换成新路径会自动重载；加载失败静默回退。
/// </summary>
internal sealed class Artwork : IDisposable
{
    private readonly ITextureProvider textures;
    private readonly IPluginLog log;

    private ISharedImmediateTexture? background;
    private ISharedImmediateTexture? avatar;
    private string backgroundPath = string.Empty;
    private string avatarPath = string.Empty;
    private DateTime nextTry = DateTime.MinValue;

    public Artwork(ITextureProvider textures, IPluginLog log)
    {
        this.textures = textures;
        this.log = log;
    }

    public void Sync(string bgPath, string avatarFilePath)
    {
        if (DateTime.UtcNow < nextTry) return;

        if (!string.Equals(bgPath, backgroundPath, StringComparison.OrdinalIgnoreCase))
        {
            backgroundPath = bgPath ?? string.Empty;
            background = TryLoad(backgroundPath, "背景");
        }

        if (!string.Equals(avatarFilePath, avatarPath, StringComparison.OrdinalIgnoreCase))
        {
            avatarPath = avatarFilePath ?? string.Empty;
            avatar = TryLoad(avatarPath, "头像");
        }
    }

    private ISharedImmediateTexture? TryLoad(string path, string label)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return null;
            var tex = textures.GetFromFile(path);
            log.Information($"[PartyGames] 已加载{label}: {path}");
            return tex;
        }
        catch (Exception ex)
        {
            nextTry = DateTime.UtcNow.AddSeconds(30);
            log.Warning($"[PartyGames] {label}加载失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>在窗口客户区铺满背景图，按 dim 压暗。</summary>
    public void DrawBackground(int dimPercent)
    {
        if (background is null) return;
        try
        {
            var wrap = background.GetWrapOrDefault();
            if (wrap is null) return;

            var draw = ImGui.GetWindowDrawList();
            var pos = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();
            var dim = Math.Clamp(dimPercent, 0, 100) / 100f;
            var tint = ImGui.GetColorU32(new Vector4(dim, dim, dim, 1.0f));
            draw.AddImage(wrap.Handle, pos, new Vector2(pos.X + size.X, pos.Y + size.Y), Vector2.Zero, Vector2.One, tint);
        }
        catch
        {
            /* 画不出来就算了 */
        }
    }

    /// <summary>一个方形头像；没有图片时返回 false，让调用方画色块兜底。</summary>
    public bool DrawAvatar(float size)
    {
        if (avatar is null) return false;
        try
        {
            var wrap = avatar.GetWrapOrDefault();
            if (wrap is null) return false;
            ImGui.Image(wrap.Handle, new Vector2(size, size));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>在指定位置画头像（不占布局）。</summary>
    public bool DrawAvatarAt(Vector2 pos, float size)
    {
        if (avatar is null) return false;
        try
        {
            var wrap = avatar.GetWrapOrDefault();
            if (wrap is null) return false;
            var dl = ImGui.GetWindowDrawList();
            var rounding = size / 2f;
            dl.AddImageRounded(wrap.Handle, pos, new Vector2(pos.X + size, pos.Y + size),
                Vector2.Zero, Vector2.One, 0xFFFFFFFFu, rounding);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        background = null;
        avatar = null;
    }
}
