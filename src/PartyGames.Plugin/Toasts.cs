using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PartyGames;

/// <summary>
/// 屏幕顶部的轻提示：新消息飘一条，3 秒后淡出。
/// 不写聊天框、不占焦点、不影响操作。
/// </summary>
internal static class Toasts
{
    private sealed class Item
    {
        public string Text = string.Empty;
        public DateTime Until;
    }

    private static readonly List<Item> Items = new();
    private const double LifeMs = 3200;

    public static void Push(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Items.Add(new Item { Text = text, Until = DateTime.UtcNow.AddMilliseconds(LifeMs) });
        if (Items.Count > 5) Items.RemoveAt(0);
    }

    public static void Draw()
    {
        if (Items.Count == 0) return;

        var now = DateTime.UtcNow;
        Items.RemoveAll(i => i.Until <= now);
        if (Items.Count == 0) return;

        var vp = ImGui.GetMainViewport();
        var centerX = vp.Pos.X + vp.Size.X / 2f;
        var y = vp.Pos.Y + 90f;

        var dl = ImGui.GetForegroundDrawList();

        foreach (var item in Items)
        {
            var left = (float)(item.Until - now).TotalMilliseconds / (float)LifeMs;
            var alpha = Math.Clamp(left * 2.2f, 0f, 1f);

            var size = ImGui.CalcTextSize(item.Text);
            var pad = new Vector2(14, 7);
            var box = size + pad * 2;
            var a = new Vector2(centerX - box.X / 2f, y);
            var b = new Vector2(centerX + box.X / 2f, y + box.Y);

            dl.AddRectFilled(a, b, ImGui.GetColorU32(new Vector4(0.08f, 0.09f, 0.12f, 0.82f * alpha)), 8f);
            dl.AddRect(a, b, ImGui.GetColorU32(new Vector4(0.89f, 0.76f, 0.45f, 0.75f * alpha)), 8f, 0, 1.2f);
            dl.AddText(new Vector2(a.X + pad.X, a.Y + pad.Y),
                ImGui.GetColorU32(new Vector4(0.95f, 0.93f, 0.88f, alpha)), item.Text);

            y += box.Y + 6f;
        }
    }
}
