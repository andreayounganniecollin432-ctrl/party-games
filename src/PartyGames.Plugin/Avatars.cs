using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;

namespace PartyGames;

/// <summary>
/// 玩家头像：自己的用本地自定义图片（圆形裁切），别人的用名字首字 + 散列颜色——协议里只传名字，
/// 不传别人的头像图，所以只能画首字。
/// </summary>
internal static class Avatars
{
    private static readonly Vector4[] Palette =
    [
        new(0.85f, 0.66f, 0.36f, 1f),
        new(0.47f, 0.66f, 0.82f, 1f),
        new(0.76f, 0.55f, 0.66f, 1f),
        new(0.48f, 0.74f, 0.62f, 1f),
        new(0.80f, 0.70f, 0.45f, 1f),
        new(0.65f, 0.60f, 0.82f, 1f),
    ];

    /// <summary>Draws one round avatar at <paramref name="topLeft"/>. <paramref name="dim"/> 0..1 fades it.</summary>
    public static void Draw(ImDrawListPtr drawList, Artwork? artwork, Vector2 topLeft, float size, string? name, bool isYou, float dim = 1f, IDalamudTextureWrap? remote = null)
    {
        // 自己：有自定义图片就用图片（DrawAvatarAt 自带圆形裁切）
        if (isYou && artwork is not null && dim >= 0.99f && artwork.DrawAvatarAt(topLeft, size))
        {
            return;
        }

        // 别人上传的头像：已经解码成贴图就画图，还在解码就先画首字
        if (remote is not null)
        {
            drawList.AddImageRounded(remote.Handle, topLeft, topLeft + new Vector2(size, size),
                Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, dim)), size / 2f);
            return;
        }

        var center = topLeft + new Vector2(size / 2f, size / 2f);
        var color = ColorFor(name);
        color.W *= dim;

        drawList.AddCircleFilled(center, size / 2f, ImGui.GetColorU32(color), 24);
        drawList.AddCircle(center, size / 2f, ImGui.GetColorU32(new Vector4(0.10f, 0.09f, 0.07f, 0.9f * dim)), 24, 1.5f);

        var initial = string.IsNullOrEmpty(name) ? "?" : name[..1];
        var textSize = ImGui.CalcTextSize(initial);
        var position = center - textSize / 2f;

        drawList.AddText(position + new Vector2(1f, 1f), ImGui.GetColorU32(new Vector4(0.08f, 0.07f, 0.06f, dim)), initial);
        drawList.AddText(position, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, dim)), initial);
    }

    /// <summary>Colour keyed by the first character, so the same player always gets the same one.</summary>
    public static Vector4 ColorFor(string? name)
    {
        var seed = string.IsNullOrEmpty(name) ? 0 : Math.Abs(name[0]) % Palette.Length;
        return Palette[seed];
    }
}
