using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PartyGames;

/// <summary>
/// QQ 风格的聊天气泡：左侧头像、右侧圆角气泡、上方小字昵称。
/// 全部用 ImGui 的 draw list 画，不依赖额外控件。
/// </summary>
internal static class ChatBubble
{
    private static readonly Vector4 SelfBubble = new(0.24f, 0.20f, 0.13f, 0.95f);
    private static readonly Vector4 OtherBubble = new(0.16f, 0.15f, 0.14f, 0.95f);
    private static readonly Vector4 SelfBorder = new(0.89f, 0.76f, 0.45f, 0.55f);
    private static readonly Vector4 OtherBorder = new(0.45f, 0.42f, 0.38f, 0.55f);

    private const float AvatarSize = 30f;
    private const float Gap = 8f;
    private const float PadX = 10f;
    private const float PadY = 6f;

    /// <summary>画一条聊天。返回占用高度。</summary>
    public static float Draw(string speaker, string text, string clock)
    {
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail().X;

        var bubbleMaxWidth = Math.Max(120f, avail - AvatarSize - Gap * 3);
        var lines = WrapText(text, bubbleMaxWidth - PadX * 2);
        var lineHeight = ImGui.GetTextLineHeight();
        var nameHeight = string.IsNullOrEmpty(speaker) ? 0f : lineHeight * 0.85f;

        var bubbleWidth = 0f;
        foreach (var l in lines) bubbleWidth = Math.Max(bubbleWidth, ImGui.CalcTextSize(l).X);
        bubbleWidth = Math.Min(bubbleMaxWidth, bubbleWidth + PadX * 2);
        var bubbleHeight = lines.Count * lineHeight + PadY * 2;

        var rowTop = origin.Y + nameHeight;
        var rowHeight = Math.Max(AvatarSize, bubbleHeight);

        // 头像（统一首字）
        var avatarMin = new Vector2(origin.X, rowTop);
        var avatarMax = avatarMin + new Vector2(AvatarSize, AvatarSize);
        {
            // 首字头像：按名字散列取色，亮字 + 深色描边，保证任何底色都看得清
            var colors = new[]
            {
                new Vector4(0.85f, 0.66f, 0.36f, 1f),
                new Vector4(0.47f, 0.66f, 0.82f, 1f),
                new Vector4(0.76f, 0.55f, 0.66f, 1f),
                new Vector4(0.48f, 0.74f, 0.62f, 1f),
                new Vector4(0.80f, 0.70f, 0.45f, 1f),
                new Vector4(0.65f, 0.60f, 0.82f, 1f),
            };
            var seed = string.IsNullOrEmpty(speaker) ? 0 : Math.Abs(speaker[0]) % colors.Length;
            var center = new Vector2(avatarMin.X + AvatarSize / 2, rowTop + AvatarSize / 2);
            dl.AddCircleFilled(center, AvatarSize / 2, ImGui.GetColorU32(colors[seed]), 24);
            dl.AddCircle(center, AvatarSize / 2, ImGui.GetColorU32(new Vector4(0.10f, 0.09f, 0.07f, 0.9f)), 24, 1.5f);

            var initial = string.IsNullOrEmpty(speaker) ? "?" : speaker[..1];
            var textSize = ImGui.CalcTextSize(initial);
            var pos = new Vector2(center.X - textSize.X / 2, center.Y - textSize.Y / 2);
            dl.AddText(pos + new Vector2(1, 1), ImGui.GetColorU32(new Vector4(0.08f, 0.07f, 0.06f, 1f)), initial);
            dl.AddText(pos, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), initial);
        }

        // 昵称（小字，气泡上方）
        var textX = avatarMax.X + Gap;
        if (!string.IsNullOrEmpty(speaker))
        {
            dl.AddText(new Vector2(textX, origin.Y), ImGui.GetColorU32(Theme.GoldDim), speaker + (string.IsNullOrEmpty(clock) ? "" : "  " + clock));
        }

        // 气泡
        var bubbleMin = new Vector2(textX, rowTop);
        var bubbleMax = new Vector2(textX + bubbleWidth, rowTop + bubbleHeight);
        dl.AddRectFilled(bubbleMin, bubbleMax, ImGui.GetColorU32(OtherBubble), 9f);
        dl.AddRect(bubbleMin, bubbleMax, ImGui.GetColorU32(OtherBorder), 9f);

        // 文本
        var ty = rowTop + PadY;
        foreach (var l in lines)
        {
            dl.AddText(new Vector2(textX + PadX, ty), ImGui.GetColorU32(Theme.Text), l);
            ty += lineHeight;
        }

        var total = nameHeight + rowHeight + 6f;
        ImGui.Dummy(new Vector2(avail, total));
        return total;
    }

    /// <summary>按像素宽度做贪心折行（中文按字符、英文按词）。</summary>
    private static List<string> WrapText(string text, float maxWidth)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            result.Add(string.Empty);
            return result;
        }

        var current = string.Empty;
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                result.Add(current);
                current = string.Empty;
                continue;
            }

            var probe = current + ch;
            if (ImGui.CalcTextSize(probe).X > maxWidth && current.Length > 0)
            {
                result.Add(current);
                current = ch.ToString();
            }
            else
            {
                current = probe;
            }
        }

        if (current.Length > 0) result.Add(current);
        if (result.Count == 0) result.Add(string.Empty);
        return result;
    }
}
