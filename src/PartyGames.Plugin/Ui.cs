using Dalamud.Bindings.ImGui;
using Glasswork.Backend;

namespace PartyGames;

/// <summary>
/// 输入框的薄封装。
///
/// 游戏里直接走 ImGui；离线预览（<see cref="ReadOnly"/>）时只把外观画出来——这个 Dalamud 版本的 ImGui 输入控件在
/// headless 环境下会访问违例（预览工具自带的表单场景同样崩），而预览要的是排版，不是真的能打字。
/// </summary>
public static class Ui
{
    /// <summary>Set by the preview host; never true in the game.</summary>
    public static bool ReadOnly { get; set; }

    public static bool InputText(string label, ref string value, int maxLength)
    {
        if (ReadOnly)
        {
            return Paint(label, value, null, maxLength);
        }

        return Theme.Glass
            ? Gw.TextField(ref value, label, string.Empty, maxLength)
            : ImGui.InputText(label, ref value, maxLength);
    }

    public static bool InputTextWithHint(string label, string hint, ref string value, int maxLength, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        if (ReadOnly)
        {
            return Paint(label, value, hint, maxLength);
        }

        return Theme.Glass
            ? Gw.TextField(ref value, label, hint, maxLength, flags: flags)
            : ImGui.InputTextWithHint(label, hint, ref value, maxLength, flags);
    }

    /// <summary>Draws what the field would look like and reports "no change", so the caller's flow is untouched.</summary>
    private static bool Paint(string label, string value, string? hint, int maxLength)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetItemRectSize().X;
        if (width <= 1f)
        {
            width = ImGui.GetContentRegionAvail().X;
        }

        var height = ImGui.GetTextLineHeight() + ImGui.GetStyle().FramePadding.Y * 2f;

        if (string.IsNullOrEmpty(label) || label.StartsWith("##", System.StringComparison.Ordinal))
        {
            var rect = new System.Numerics.Vector2(origin.X + width, origin.Y + height);
            drawList.AddRectFilled(origin, rect, ImGui.GetColorU32(ImGuiCol.FrameBg), 4f);
            drawList.AddRect(origin, rect, ImGui.GetColorU32(ImGuiCol.Border), 4f);
            ImGui.SetCursorScreenPos(new System.Numerics.Vector2(origin.X + 6f, origin.Y + ImGui.GetStyle().FramePadding.Y));
            var shown = string.IsNullOrEmpty(value) ? hint ?? string.Empty : value;
            ImGui.TextUnformatted(shown);
            ImGui.SetCursorScreenPos(new System.Numerics.Vector2(origin.X, origin.Y + height));
            return false;
        }

        // 带标签的那种：标签在左，框在右，和 ImGui 的实际布局一致。
        var fieldWidth = ImGui.GetItemRectSize().X > 1f ? ImGui.GetItemRectSize().X : 200f;
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        var fieldOrigin = ImGui.GetCursorScreenPos();
        var field = new System.Numerics.Vector2(fieldOrigin.X + fieldWidth, fieldOrigin.Y + height);
        drawList.AddRectFilled(fieldOrigin, field, ImGui.GetColorU32(ImGuiCol.FrameBg), 4f);
        drawList.AddRect(fieldOrigin, field, ImGui.GetColorU32(ImGuiCol.Border), 4f);
        ImGui.SetCursorScreenPos(new System.Numerics.Vector2(fieldOrigin.X + 6f, fieldOrigin.Y + ImGui.GetStyle().FramePadding.Y));
        ImGui.TextUnformatted(string.IsNullOrEmpty(value) ? hint ?? string.Empty : value);
        ImGui.SetCursorScreenPos(new System.Numerics.Vector2(origin.X, fieldOrigin.Y + height));
        return false;
    }
}
