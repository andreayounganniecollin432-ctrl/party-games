using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Glasswork;
using Glasswork.Backend;

namespace PartyGames;

/// <summary>
/// 统一视觉：深色水墨 + 金边。
/// 所有颜色和排版 helper 集中在这里，改一处全局生效。
/// 图标一律用 DrawList 画，不依赖字体里有没有那个字形（游戏字体只保证有中文和基本拉丁）。
/// </summary>
public static class Theme
{
    /// <summary>True while the glass host is alive: drawing then goes through Glasswork's widgets.</summary>
    public static bool Glass { get; set; }

    private static Color? Ink(Vector4? color) => color is { } value ? Color.FromVector4(value) : null;
    // 主色
    public static readonly Vector4 Gold = new(0.89f, 0.76f, 0.45f, 1.00f);
    public static readonly Vector4 GoldDim = new(0.62f, 0.53f, 0.33f, 1.00f);
    public static readonly Vector4 Text = new(0.91f, 0.87f, 0.80f, 1.00f);
    public static readonly Vector4 Muted = new(0.62f, 0.58f, 0.52f, 1.00f);
    public static readonly Vector4 Good = new(0.55f, 0.82f, 0.60f, 1.00f);
    public static readonly Vector4 Warn = new(0.92f, 0.62f, 0.45f, 1.00f);
    public static readonly Vector4 Bad = new(0.88f, 0.48f, 0.48f, 1.00f);
    public static readonly Vector4 CardFill = new(1.00f, 1.00f, 1.00f, 0.035f);
    public static readonly Vector4 CardEdge = new(0.89f, 0.76f, 0.45f, 0.20f);

    /// <summary>窗口顶部：状态点 + 插件名 + 一行副标题。</summary>
    public static void Header(string subtitle, bool online)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var lineHeight = ImGui.GetTextLineHeight();

        // 状态点画出来，不用 "●"：那个字形不一定在字体里。
        drawList.AddCircleFilled(new Vector2(origin.X + 5f, origin.Y + lineHeight * 0.55f), 4f,
            ImGui.GetColorU32(online ? Good : Bad), 14);

        ImGui.Dummy(new Vector2(14f, lineHeight));
        ImGui.SameLine(0f, 2f);

        if (Glass)
        {
            Gw.Text("Coral's Games", TextRole.Title2, Ink(Gold));
            ImGui.SameLine(0f, 10f);
            Gw.Text(subtitle, TextRole.Footnote, Ink(Muted));
            Divider();
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, Gold);
        ImGui.SetWindowFontScale(1.22f);
        ImGui.TextUnformatted("Coral's Games");
        ImGui.SetWindowFontScale(1.0f);
        ImGui.PopStyleColor();

        ImGui.SameLine(0f, 10f);
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        ImGui.TextUnformatted(subtitle);
        ImGui.PopStyleColor();

        Divider();
    }

    /// <summary>金色小标题：左侧一道竖杠 + 文字 + 细线。</summary>
    public static void Section(string title)
    {
        ImGui.Spacing();

        if (Glass)
        {
            Gw.Text(title, TextRole.Headline, Ink(Gold));
            Gw.Divider();
            return;
        }
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var height = ImGui.GetTextLineHeight();

        drawList.AddRectFilled(new Vector2(origin.X, origin.Y + 2f), new Vector2(origin.X + 3f, origin.Y + height - 2f),
            ImGui.GetColorU32(Gold), 1.5f);

        ImGui.Dummy(new Vector2(9f, height));
        ImGui.SameLine(0f, 4f);
        ImGui.PushStyleColor(ImGuiCol.Text, GoldDim);
        ImGui.TextUnformatted(title);
        ImGui.PopStyleColor();
        ImGui.Spacing();
    }

    /// <summary>A hairline in the theme's dim gold, for separating blocks.</summary>
    public static void Divider()
    {
        if (Glass)
        {
            Gw.Divider();
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        drawList.AddLine(new Vector2(origin.X, origin.Y + 2f), new Vector2(origin.X + width, origin.Y + 2f),
            ImGui.GetColorU32(new Vector4(GoldDim.X, GoldDim.Y, GoldDim.Z, 0.35f)), 1f);
        ImGui.Dummy(new Vector2(width, 5f));
    }

    /// <summary>Paints a card behind the next block; the caller positions its own content inside it.</summary>
    public static void Card(Vector2 topLeft, float width, float height)
    {
        var drawList = ImGui.GetWindowDrawList();
        var bottomRight = new Vector2(topLeft.X + width, topLeft.Y + height);
        drawList.AddRectFilled(topLeft, bottomRight, ImGui.GetColorU32(CardFill), 6f);
        drawList.AddRect(topLeft, bottomRight, ImGui.GetColorU32(CardEdge), 6f, 0, 1f);
    }

    public static void Label(string text)
    {
        if (Glass)
        {
            Gw.Text(text, TextRole.Footnote, Ink(Muted));
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    public static void MutedWrapped(string text)
    {
        if (Glass)
        {
            Gw.TextWrapped(text, TextRole.Footnote, Ink(Muted));
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    public static void Value(string text, Vector4? color = null)
    {
        if (Glass)
        {
            Gw.Text(text, TextRole.Body, Ink(color ?? Text));
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, color ?? Text);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    /// <summary>One line of body text in the theme's ink.</summary>
    public static void Line(string text, Vector4? color = null)
    {
        if (Glass)
        {
            Gw.Text(text, TextRole.Body, Ink(color ?? Text));
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, color ?? Text);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    /// <summary>主按钮（金色）。</summary>
    public static bool PrimaryButton(string label, Vector2 size)
    {
        if (Glass)
        {
            return Gw.Button(label, ButtonStyle.Prominent, ButtonSize.Regular, default, size.X);
        }

        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.62f, 0.48f, 0.25f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.78f, 0.62f, 0.33f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.88f, 0.72f, 0.40f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.15f, 0.12f, 0.08f, 1.0f));
        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar();
        return clicked;
    }

    /// <summary>次要按钮：透明底 + 金边。</summary>
    public static bool GhostButton(string label, Vector2 size)
    {
        if (Glass)
        {
            return Gw.Button(label, ButtonStyle.Glass, ButtonSize.Regular, default, size.X);
        }

        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0.15f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.89f, 0.76f, 0.45f, 0.18f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.89f, 0.76f, 0.45f, 0.30f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(GoldDim.X, GoldDim.Y, GoldDim.Z, 0.55f));
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor(5);
        ImGui.PopStyleVar(2);
        return clicked;
    }

    /// <summary>危险动作（认输、杀人、开枪）：红底金边。</summary>
    public static bool DangerButton(string label, Vector2 size)
    {
        if (Glass)
        {
            return Gw.Button(label, ButtonStyle.Destructive, ButtonSize.Regular, default, size.X);
        }

        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.42f, 0.16f, 0.16f, 0.92f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.58f, 0.22f, 0.22f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.70f, 0.28f, 0.26f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.88f, 0.48f, 0.48f, 0.55f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.98f, 0.90f, 0.88f, 1f));
        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor(5);
        ImGui.PopStyleVar(2);
        return clicked;
    }

    private const int StyleVars = 8;
    private const int StyleColors = 12;

    /// <summary>
    /// 窗户这一帧的 ImGui 外观：圆角、半透明控件、金色高亮。
    /// 必须成对调用——直接改全局 style 会污染游戏里其它插件的窗口。
    /// </summary>
    public static void PushWindowStyle()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 18f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(18f, 16f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 6f));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 0f);

        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0f, 0f, 0f, 0.32f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.89f, 0.76f, 0.45f, 0.18f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0.89f, 0.76f, 0.45f, 0.26f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.89f, 0.76f, 0.45f, 0.13f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.89f, 0.76f, 0.45f, 0.26f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.89f, 0.76f, 0.45f, 0.40f));
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.89f, 0.76f, 0.45f, 0.16f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.89f, 0.76f, 0.45f, 0.24f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.89f, 0.76f, 0.45f, 0.32f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0f, 0f, 0f, 0.14f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.89f, 0.76f, 0.45f, 0.22f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, new Vector4(0f, 0f, 0f, 0.18f));
    }

    public static void PopWindowStyle()
    {
        ImGui.PopStyleColor(StyleColors);
        ImGui.PopStyleVar(StyleVars);
    }

    /// <summary>滑块的金色系配色，Push 之后要和 <see cref="PopSliderColors"/> 配对。</summary>
    public static void PushSliderColors()
    {
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0f, 0f, 0f, 0.35f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.89f, 0.76f, 0.45f, 0.18f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0.89f, 0.76f, 0.45f, 0.28f));
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, new Vector4(0.89f, 0.76f, 0.45f, 0.90f));
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, new Vector4(1.00f, 0.88f, 0.58f, 1.00f));
    }

    public static void PopSliderColors() => ImGui.PopStyleColor(5);

    /// <summary>紧凑的 - N + 数字步进器，替代又宽又丑的 SliderInt。</summary>
    public static bool Stepper(ref int value, int min, int max, string id)
    {
        if (Glass)
        {
            Gw.Stepper(ref value, min, max, 1, id);
            return true;
        }

        var changed = false;
        var buttonSize = new Vector2(20f, 0f);

        ImGui.PushID(id);
        if (GhostButton("-", buttonSize))
        {
            value = Math.Clamp(value - 1, min, max);
            changed = true;
        }

        ImGui.SameLine(0f, 4f);
        ImGui.TextUnformatted(value.ToString());
        ImGui.SameLine(0f, 4f);
        if (GhostButton("+", buttonSize))
        {
            value = Math.Clamp(value + 1, min, max);
            changed = true;
        }

        ImGui.PopID();
        return changed;
    }

    /// <summary>One line of text cut to fit a width, with an ellipsis drawn by us (no glyph needed).</summary>
    public static string Fit(string text, float width)
    {
        if (string.IsNullOrEmpty(text) || ImGui.CalcTextSize(text).X <= width)
        {
            return text;
        }

        for (var length = text.Length - 1; length > 0; length--)
        {
            var candidate = text[..length] + "…";
            if (ImGui.CalcTextSize(candidate).X <= width)
            {
                return candidate;
            }
        }

        return string.Empty;
    }
}
