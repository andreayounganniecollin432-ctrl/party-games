using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Glasswork;
using Glasswork.Backend;

namespace PartyGames.Previews;

/// <summary>
/// The pieces every Party Games screen shares: the Look's panel behind a <c>NoBackground</c> window, the filled card
/// the sections sit on, and a text field drawn by hand — <c>Gw.TextField</c> crashes this Dalamud build when the preview
/// runs it headless, so the fill comes from the Look and the editing stays ImGui's own.
/// </summary>
internal static class Chrome
{
    /// <summary>Paints the panel and leaves the cursor on its padding, top left.</summary>
    public static Vector2 Panel(Look look, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var window = Rect.FromSize(ImGui.GetWindowPos(), ImGui.GetWindowSize());
        drawList.PushClipRect(window.Min, window.Max, false);
        Paint.Material(drawList, window, Corners.All(look.RadiusExtraLarge * scale), look.CornerStyle, in look.PanelMaterial, look, Frame.Glass, scale);
        drawList.PopClipRect();
        drawList.AddRect(window.Min, window.Max, look.PanelBorder.ToAbgr(), look.RadiusExtraLarge * scale, ImDrawFlags.RoundCornersAll, look.PanelBorderWidth);

        var origin = window.Min + new Vector2(look.SpaceExtraLarge * scale, look.SpaceLarge * scale);
        ImGui.SetCursorScreenPos(origin);
        return origin;
    }

    /// <summary>A filled card: the Look's control fill at the large radius, with a hairline border.</summary>
    public static void Card(Rect rect, Look look, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var radius = look.RadiusLarge * scale;
        Paint.Material(drawList, rect, Corners.All(radius), look.CornerStyle, in look.ControlMaterial, look, Frame.Glass, scale);
        drawList.AddRect(rect.Min, rect.Max, look.PanelBorder.ToAbgr(), radius, ImDrawFlags.RoundCornersAll, look.PanelBorderWidth);
    }

    /// <summary>Draws a text field into a capsule of our own and lets ImGui edit it.</summary>
    public static bool TextField(ref string value, string id, string hint, float width, float height, Look look, float scale, int maxLength)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var rect = Rect.FromSize(origin, new Vector2(width, height));
        var radius = look.RadiusMedium * scale;

        Paint.Material(drawList, rect, Corners.All(radius), look.CornerStyle, in look.ControlMaterial, look, Frame.Glass, scale);
        drawList.AddRect(rect.Min, rect.Max, look.PanelBorder.ToAbgr(), radius, ImDrawFlags.RoundCornersAll, look.PanelBorderWidth);

        // Drawn, not edited: every ImGui input widget faults inside this headless tool (its own forms scene does too), so a
        // preview shows the field's look and the game does the editing.
        var padding = look.SpaceSmall * scale;
        ImGui.SetCursorScreenPos(new Vector2(rect.Min.X + padding, rect.Min.Y + (height - ImGui.GetTextLineHeight()) * 0.5f));
        Gw.Text(string.IsNullOrEmpty(value) ? hint : value, TextRole.Body, string.IsNullOrEmpty(value) ? look.TertiaryLabel : look.Label);
        ImGui.SetCursorScreenPos(new Vector2(rect.Min.X, rect.Max.Y + look.SpaceSmall * scale));
        return false;
    }
}
