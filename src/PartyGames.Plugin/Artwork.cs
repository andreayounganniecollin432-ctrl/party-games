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
    private readonly ITextureProvider? textures;
    private readonly IPluginLog? log;

    private ISharedImmediateTexture? background;
    private ISharedImmediateTexture? avatar;
    private string backgroundPath = string.Empty;
    private string avatarPath = string.Empty;
    private DateTime nextTry = DateTime.MinValue;
    private static string _baseDir = string.Empty;

    public Artwork(ITextureProvider? textures, IPluginLog? log)
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

    private static string BaseDir
    {
        get
        {
            if (_baseDir.Length == 0)
            {
                try
                {
                    var loc = typeof(Artwork).Assembly.Location;
                    _baseDir = string.IsNullOrEmpty(loc) ? AppContext.BaseDirectory : System.IO.Path.GetDirectoryName(loc) ?? AppContext.BaseDirectory;
                }
                catch { _baseDir = AppContext.BaseDirectory; }
            }
            return _baseDir;
        }
    }

    private ISharedImmediateTexture? TryLoad(string path, string label)
    {
        // 预览里没有贴图服务：直接用画出来的渐变兜底。
        if (textures is null)
        {
            return null;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(path) && label == "背景")
            {
                // 把内置图落到 images\ 下，然后走"手动选择"那条已验证可用的 GetFromFile 路径
                try
                {
                    var target = System.IO.Path.Combine(ImageLibrary.Folder, "default-bg.png");
                    if (!System.IO.File.Exists(target))
                    {
                        System.IO.Directory.CreateDirectory(ImageLibrary.Folder);
                        using var stream = typeof(Artwork).Assembly.GetManifestResourceStream("default-bg.png");
                        if (stream is not null)
                        {
                            using var fs = System.IO.File.Create(target);
                            stream.CopyTo(fs);
                            log?.Information("[PartyGames] 已释放默认背景到: " + target);
                        }
                        else
                        {
                            log?.Warning("[PartyGames] 找不到嵌入资源 default-bg.png");
                        }
                    }

                    if (System.IO.File.Exists(target))
                    {
                        log?.Information("[PartyGames] 使用默认背景: " + target);
                        return textures.GetFromFile(target);
                    }
                }
                catch (Exception ex)
                {
                    log?.Warning("[PartyGames] 释放/加载默认背景失败: " + ex.Message);
                }
            }
            if (string.IsNullOrWhiteSpace(path))
            {
                log?.Information($"[PartyGames] {label}路径为空，使用默认样式");
                return null;
            }
            // 配置里存的是相对路径（images\xxx.png），要以插件目录为基准解析
            if (!System.IO.Path.IsPathRooted(path))
            {
                path = System.IO.Path.Combine(BaseDir, path);
            }

            if (!System.IO.File.Exists(path))
            {
                log?.Warning($"[PartyGames] {label}文件不存在: {path}");
                return null;
            }
            var tex = textures.GetFromFile(path);
            log?.Information($"[PartyGames] 已加载{label}: {path}");
            return tex;
        }
        catch (Exception ex)
        {
            nextTry = DateTime.UtcNow.AddSeconds(30);
            log?.Warning($"[PartyGames] {label}加载失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>在窗口客户区铺满背景图，按 dim 压暗。</summary>
    public void DrawBackground(int dimPercent)
    {
        if (background is null)
        {
            DrawGradient();
            return;
        }
        try
        {
            var wrap = background.GetWrapOrDefault();
            if (wrap is null)
            {
                log?.Warning("[PartyGames] 背景贴图尚未就绪，使用渐变兜底");
                DrawGradient();
                return;
            }

            var draw = ImGui.GetWindowDrawList();
            var pos = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();
            if (size.X < 8 || size.Y < 8) return;

            // 等比铺满（cover）：按窗口与图片的宽高比裁切 UV，绝不拉伸变形
            var tex = wrap.Size;
            var texW = tex.X <= 0 ? size.X : tex.X;
            var texH = tex.Y <= 0 ? size.Y : tex.Y;

            var winAspect = size.X / size.Y;
            var imgAspect = texW / texH;

            var uv0 = Vector2.Zero;
            var uv1 = Vector2.One;

            if (imgAspect > winAspect)
            {
                // 图更宽：左右裁
                var keep = winAspect / imgAspect;
                var half = (1f - keep) / 2f;
                uv0.X = half;
                uv1.X = 1f - half;
            }
            else if (imgAspect < winAspect)
            {
                // 图更高：上下裁
                var keep = imgAspect / winAspect;
                var half = (1f - keep) / 2f;
                uv0.Y = half;
                uv1.Y = 1f - half;
            }

            var dim = Math.Clamp(dimPercent, 0, 100) / 100f;
            var tint = ImGui.GetColorU32(new Vector4(dim, dim, dim, 1.0f));
            draw.AddImage(wrap.Handle, pos, new Vector2(pos.X + size.X, pos.Y + size.Y), uv0, uv1, tint);
        }
        catch
        {
            /* 画不出来就算了 */
        }
    }

    /// <summary>浅色纯色底。</summary>
    private static void DrawSolid()
    {
        try
        {
            var draw = ImGui.GetWindowDrawList();
            var pos = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();
            if (size.X < 8 || size.Y < 8) return;
            draw.AddRectFilled(pos, new Vector2(pos.X + size.X, pos.Y + size.Y),
                ImGui.GetColorU32(new Vector4(0.93f, 0.96f, 0.99f, 1f)));
        }
        catch
        {
            /* ignore */
        }
    }

    /// <summary>没有背景图时画蓝白渐变，保证不会是一片黑。</summary>
    private static void DrawGradient()
    {
        try
        {
            var draw = ImGui.GetWindowDrawList();
            var pos = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();
            if (size.X < 8 || size.Y < 8) return;

            var top = ImGui.GetColorU32(new Vector4(0.72f, 0.82f, 0.93f, 1f));
            var bottom = ImGui.GetColorU32(new Vector4(0.90f, 0.94f, 0.98f, 1f));
            draw.AddRectFilledMultiColor(pos, new Vector2(pos.X + size.X, pos.Y + size.Y), top, top, bottom, bottom);

            // 蒙版：把浅色渐变压暗，浅色文字才看得清
            draw.AddRectFilled(pos, new Vector2(pos.X + size.X, pos.Y + size.Y), ImGui.GetColorU32(new Vector4(0.07f, 0.07f, 0.09f, 0.82f)));
        }
        catch
        {
            /* ignore */
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

    /// <summary>路径变了：清掉缓存，下一帧重新加载。</summary>
    public void Invalidate()
    {
        backgroundPath = string.Empty;
        avatarPath = string.Empty;
        nextTry = DateTime.MinValue;
    }

    public void Dispose()
    {
        background = null;
        avatar = null;
    }
}
