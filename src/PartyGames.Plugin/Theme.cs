using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PartyGames;

/// <summary>
/// 统一视觉：深色水墨 + 金边。
/// 所有颜色和排版 helper 集中在这里，改一处全局生效。
/// </summary>
internal static class Theme
{
    // 主色
    public static readonly Vector4 Gold = new(0.62f, 0.42f, 0.10f, 1.00f);
    public static readonly Vector4 GoldDim = new(0.48f, 0.38f, 0.20f, 1.00f);
    public static readonly Vector4 Text = new(0.13f, 0.15f, 0.19f, 1.00f);
    public static readonly Vector4 Muted = new(0.38f, 0.42f, 0.48f, 1.00f);
    public static readonly Vector4 Good = new(0.10f, 0.48f, 0.24f, 1.00f);
    public static readonly Vector4 Warn = new(0.72f, 0.36f, 0.06f, 1.00f);
    public static readonly Vector4 Bad = new(0.70f, 0.16f, 0.16f, 1.00f);

    /// <summary>窗口顶部：插件名 + 一行状态点。</summary>
    public static void Header(string subtitle, bool online)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Gold);
        ImGui.SetWindowFontScale(1.18f);
        ImGui.TextUnformatted("Party Games");
        ImGui.SetWindowFontScale(1.0f);
        ImGui.PopStyleColor();

        ImGui.SameLine();
        var dot = online ? Good : Bad;
        ImGui.PushStyleColor(ImGuiCol.Text, dot);
        ImGui.TextUnformatted(online ? "●" : "○");
        ImGui.PopStyleColor();

        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        ImGui.TextUnformatted(subtitle);
        ImGui.PopStyleColor();
    }

    /// <summary>金色分隔标题。</summary>
    public static void Section(string title)
    {
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Separator, GoldDim);
        ImGui.Separator();
        ImGui.PopStyleColor();

        ImGui.PushStyleColor(ImGuiCol.Text, GoldDim);
        ImGui.TextUnformatted(title);
        ImGui.PopStyleColor();
        ImGui.Spacing();
    }

    public static void Label(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    public static void Value(string text, Vector4? color = null)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color ?? Text);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    /// <summary>主按钮（金色）。</summary>
    public static bool PrimaryButton(string label, Vector2 size)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.62f, 0.48f, 0.25f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.78f, 0.62f, 0.33f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.88f, 0.72f, 0.40f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.15f, 0.12f, 0.08f, 1.0f));
        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor(4);
        return clicked;
    }
}
