using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Glasswork;
using Glasswork.Backend;
using Glasswork.Backend.Preview;
using PartyGames.Protocol;

namespace PartyGames.Previews;

/// <summary>
/// 选游戏:the screen a connected player lands on when they are in no room. Header, personal record, the catalogue as
/// tiles, then the two ways into someone else's game.
/// </summary>
public sealed class LobbyScreen : IPreviewScreen
{
    public string Name => "lobby";

    public Vector2 WindowSize => new(820f, 960f);

    public Look Look { get; } = Look.Eorzea();

    public ReadOnlySpan<PreviewInput> Script => [];

    public void Draw() => LobbyView.Draw();
}

internal static class LobbyView
{
    private const float TileHeight = 128f;
    private const float TileGap = 12f;
    private const float CreateButtonWidth = 96f;
    private const float JoinCodeWidth = 150f;
    private const float JoinButtonWidth = 72f;
    private const float ChatButtonWidth = 128f;
    private const float FooterHeight = 56f;
    private const int Columns = 2;

    private static string joinCode = "7K3QP";

    public static void Draw()
    {
        var look = Frame.Look;
        var scale = Frame.Scale;

        Chrome.Panel(look, scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(look.SpaceSmall, look.SpaceSmall) * scale);

        DrawHeader(look, scale);
        DrawRecord(look, scale);
        DrawCatalog(look, scale);
        DrawFooter(look, scale);

        ImGui.PopStyleVar();
    }

    private static void DrawHeader(Look look, float scale)
    {
        Gw.Text("Coral's Games"u8, TextRole.LargeTitle);
        ImGui.SameLine(0f, look.SpaceMedium * scale);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 10f * scale);
        Gw.Badge("服务器 12 人在线 · 2 个房间"u8, look.Accent);
        Gw.Text("跨服文字游戏 · 3-20 人开局 · 房间码 5 位"u8, TextRole.Footnote, look.SecondaryLabel);
        ImGui.Spacing();
    }

    private static void DrawRecord(Look look, float scale)
    {
        Gw.Text("我的战绩"u8, TextRole.Headline);
        Gw.Text("按昵称统计,跨局累计,最多 300 局"u8, TextRole.Footnote, look.SecondaryLabel);
        ImGui.Spacing();

        var width = ImGui.GetContentRegionAvail().X;
        var origin = ImGui.GetCursorScreenPos();
        var rowHeight = 24f * scale;
        var height = rowHeight * Catalog.MyStats.Length + look.SpaceMedium * scale * 2f;
        var rect = Rect.FromSize(origin, new Vector2(width, height));
        Chrome.Card(rect, look, scale);

        var padding = look.SpaceMedium * scale;
        for (var i = 0; i < Catalog.MyStats.Length; i++)
        {
            var row = Catalog.MyStats[i];
            var y = rect.Min.Y + padding + i * rowHeight;
            ImGui.SetCursorScreenPos(new Vector2(rect.Min.X + padding, y));
            Gw.Text(row.Game, TextRole.Body);
            var record = $"{row.Wins} 胜 {row.Losses} 负";
            DrawRight(record, rect.Max.X - padding, y, TextRole.Body, look.SecondaryLabel, look, scale);
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X, rect.Max.Y + look.SpaceMedium * scale));
        ImGui.Dummy(new Vector2(width, 0f));
    }

    private static void DrawCatalog(Look look, float scale)
    {
        Gw.Text("选择一个游戏"u8, TextRole.Headline);
        Gw.Text("建好房把 5 位的房间码发出去,朋友在大厅输码就能进来."u8, TextRole.Footnote, look.SecondaryLabel);
        ImGui.Spacing();

        var origin = ImGui.GetCursorScreenPos();
        var width = MathF.Max(ImGui.GetContentRegionAvail().X, 1f);
        var gap = TileGap * scale;
        var columnWidth = (width - gap) / Columns;
        var step = TileHeight * scale + gap;

        for (var i = 0; i < Catalog.Games.Length; i++)
        {
            var column = i % Columns;
            var row = i / Columns;
            var rect = Rect.FromSize(origin + new Vector2(column * (columnWidth + gap), row * step), new Vector2(columnWidth, TileHeight * scale));
            DrawTile(rect, look, scale, Catalog.Games[i]);
        }

        var rows = (Catalog.Games.Length + Columns - 1) / Columns;
        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + rows * step - gap + look.SpaceMedium * scale));
        ImGui.Dummy(new Vector2(width, 0f));
    }

    private static void DrawTile(Rect rect, Look look, float scale, GameDescriptor game)
    {
        Chrome.Card(rect, look, scale);

        var padding = look.SpaceMedium * scale;
        var contentWidth = rect.Width - padding * 2f;
        var footerY = rect.Max.Y - padding - ImGui.GetTextLineHeight();

        // Title and blurb are painted into the tile's own rect: Gw.TextWrapped lays out against the window, which runs
        // straight past the card's right edge.
        var drawList = ImGui.GetWindowDrawList();
        var titleY = rect.Min.Y + padding;
        TextPaint.Draw(drawList, new Vector2(rect.Min.X + padding, titleY), game.Name, TextRole.Headline, look.Label, in look, scale);
        var blurbTop = titleY + TextPaint.LineHeight(TextRole.Headline, in look, scale) + 2f * scale;
        var blurbRect = Rect.FromSize(
            new Vector2(rect.Min.X + padding, blurbTop),
            new Vector2(contentWidth, MathF.Max(footerY - 4f * scale - blurbTop, 1f)));
        TextPaint.DrawWrapped(drawList, blurbRect, game.Blurb, TextRole.Footnote, look.SecondaryLabel, in look, scale, 2, TextAlign.Start);

        ImGui.SetCursorScreenPos(new Vector2(rect.Min.X + padding, footerY));
        Gw.Text($"{game.MinPlayers}-{game.MaxPlayers} 人", TextRole.Footnote, look.SecondaryLabel);
        ImGui.SameLine(rect.Max.X - padding - CreateButtonWidth * scale);
        Gw.Button("创建房间"u8, ButtonStyle.Prominent, ButtonSize.Small, width: CreateButtonWidth);
    }

    /// <summary>Draws one line right-aligned to <paramref name="right"/>, measuring in the role's own face.</summary>
    private static void DrawRight(string text, float right, float y, TextRole role, Color color, Look look, float scale)
    {
        var width = TextPaint.Measure(text, role, in look, scale).X;
        TextPaint.Draw(ImGui.GetWindowDrawList(), new Vector2(right - width, y), text, role, color, in look, scale);
    }

    private static void DrawFooter(Look look, float scale)
    {
        var width = MathF.Max(ImGui.GetContentRegionAvail().X, 1f);
        var origin = ImGui.GetCursorScreenPos();
        var height = FooterHeight * scale;
        var rect = Rect.FromSize(origin, new Vector2(width, height));
        Chrome.Card(rect, look, scale);

        var padding = look.SpaceMedium * scale;
        var buttonHeight = look.ControlHeightSmall * scale;
        var buttonY = rect.Min.Y + (height - buttonHeight) * 0.5f;

        ImGui.SetCursorScreenPos(new Vector2(rect.Min.X + padding, buttonY));
        Gw.Button("进入聊天大厅"u8, ButtonStyle.Default, ButtonSize.Small, width: ChatButtonWidth);

        var joinX = rect.Min.X + padding + ChatButtonWidth * scale + look.SpaceLarge * scale;
        ImGui.SetCursorScreenPos(new Vector2(joinX, rect.Min.Y + (height - ImGui.GetTextLineHeight()) * 0.5f));
        Gw.Text("加入房间"u8, TextRole.Body, look.Label);

        var fieldX = joinX + TextPaint.Measure("加入房间", TextRole.Body, in look, scale).X + look.SpaceSmall * scale;
        var fieldWidth = JoinCodeWidth * scale;
        ImGui.SetCursorScreenPos(new Vector2(fieldX, buttonY));
        Chrome.TextField(ref joinCode, "##code", "房间码", fieldWidth, buttonHeight, look, scale, 8);

        ImGui.SetCursorScreenPos(new Vector2(fieldX + fieldWidth + look.SpaceSmall * scale, buttonY));
        Gw.Button("加入"u8, ButtonStyle.Glass, ButtonSize.Small, width: JoinButtonWidth);
    }
}
