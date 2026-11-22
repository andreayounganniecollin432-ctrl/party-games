using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Glasswork;
using Glasswork.Backend;
using Glasswork.Backend.Preview;
using PartyGames.Protocol;

namespace PartyGames.Previews;

/// <summary>
/// 房间 / 对局:the generic screen the plugin draws from a <see cref="RoomSnapshot"/> alone. One renderer covers every
/// game, because the server authors the whole view — the same reason the plugin has no per-game UI.
/// </summary>
public abstract class RoomScreen : IPreviewScreen
{
    protected abstract RoomSnapshot Snapshot { get; }

    public string Name { get; }

    public Vector2 WindowSize { get; } = new(820f, 860f);

    public Look Look { get; } = Look.Eorzea();

    public ReadOnlySpan<PreviewInput> Script => [];

    protected RoomScreen(string name) => Name = name;

    public void Draw() => RoomView.Draw(Snapshot);
}

internal static class RoomView
{
    private const float PlayerCardHeight = 52f;
    private const float PlayerGap = 10f;
    private const float CardGap = 8f;
    private const float ActionHeight = 34f;
    private const float ActionWidth = 118f;
    private const int PlayerColumns = 2;
    private const int MaxLogLines = 4;

    public static void Draw(RoomSnapshot snapshot)
    {
        var look = Frame.Look;
        var scale = Frame.Scale;
        var view = snapshot.View;

        Chrome.Panel(look, scale);

        var area = Rect.FromSize(ImGui.GetWindowPos() + new Vector2(look.SpaceExtraLarge, look.SpaceLarge) * scale,
            new Vector2(ImGui.GetWindowSize().X - look.SpaceExtraLarge * 2f * scale, ImGui.GetWindowSize().Y - look.SpaceLarge * 2f * scale));
        var flow = new Flow(area);

        DrawHeader(flow, snapshot, look, scale);
        DrawNote(flow, snapshot, look, scale);
        DrawPlayers(flow, view, look, scale);

        if (snapshot.GameId == "doudizhu")
        {
            DrawHand(flow, view, look, scale);
        }

        if (snapshot.GameId == "xiangqi" && view.Board.Count > 0)
        {
            DrawBoard(flow, view, look, scale);
        }

        DrawScoreboard(flow, view, look, scale);
        DrawActions(flow, view, look, scale);
        DrawInput(flow, view, look, scale);
        DrawLog(flow, snapshot, look, scale);
    }

    private static void DrawHeader(Flow flow, RoomSnapshot snapshot, Look look, float scale)
    {
        var view = snapshot.View;
        var y = flow.Y;

        // 房间码 and the clock share the top line with the game name, in the muted ink.
        TextPaint.Draw(ImGui.GetWindowDrawList(), new Vector2(flow.X, y), $"{snapshot.GameName} · 房间 {snapshot.Code}", TextRole.Footnote, look.SecondaryLabel, in look, scale);

        if (view.DeadlineAtMs is { } deadline)
        {
            var seconds = deadline / 1000;
            DrawPill(new Vector2(flow.Right - 54f * scale, y - 2f * scale), new Vector2(54f * scale, 22f * scale), $"{seconds}s", seconds < 10 ? look.Warning : look.Accent, look, scale);
        }

        y += TextPaint.LineHeight(TextRole.Footnote, in look, scale) + 4f * scale;
        var title = view.Title;
        TextPaint.Draw(ImGui.GetWindowDrawList(), new Vector2(flow.X, y), title, TextRole.Title2, look.Label, in look, scale);
        if (!string.IsNullOrEmpty(view.Subtitle))
        {
            var width = TextPaint.Measure(title, TextRole.Title2, in look, scale).X;
            TextPaint.Draw(ImGui.GetWindowDrawList(), new Vector2(flow.X + width + look.SpaceSmall * scale, y + 4f * scale), view.Subtitle, TextRole.Body, look.Accent, in look, scale);
        }

        y += TextPaint.LineHeight(TextRole.Title2, in look, scale) + 2f * scale;

        if (!string.IsNullOrEmpty(view.ResultBanner))
        {
            TextPaint.Draw(ImGui.GetWindowDrawList(), new Vector2(flow.X, y), view.ResultBanner, TextRole.Headline, look.Success, in look, scale);
            y += TextPaint.LineHeight(TextRole.Headline, in look, scale) + 4f * scale;
        }

        flow.Y = y;
    }

    private static void DrawNote(Flow flow, RoomSnapshot snapshot, Look look, float scale)
    {
        var view = snapshot.View;
        if (string.IsNullOrEmpty(view.PrivateNote) && string.IsNullOrEmpty(view.Instruction))
        {
            return;
        }

        // 斗地主 shows the hand as cards below, so the note card carries only the instruction.
        var hasNote = !string.IsNullOrEmpty(view.PrivateNote) && snapshot.GameId != "doudizhu";
        var width = flow.Width;
        var padding = look.SpaceMedium * scale;
        var noteHeight = hasNote ? ImGui.GetTextLineHeight() * 1.15f + padding * 2f + 18f * scale : 0f;
        var instructionHeight = string.IsNullOrEmpty(view.Instruction) ? 0f : TextPaint.MeasureWrapped(view.Instruction, TextRole.Footnote, in look, scale, width - padding * 2f, 3).Y;
        var height = noteHeight + (hasNote && instructionHeight > 0f ? CardGap * scale : 0f) + instructionHeight + (instructionHeight > 0f ? padding * 2f : 0f);

        var rect = Rect.FromSize(new Vector2(flow.X, flow.Y), new Vector2(width, height));
        Chrome.Card(rect, look, scale);

        var accent = view.PrivateNoteLabel is not null ? look.Warning : look.Accent;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(new Vector2(rect.Min.X, rect.Min.Y + padding * 0.5f), new Vector2(rect.Min.X + 3f * scale, rect.Max.Y - padding * 0.5f), accent.ToAbgr(), 2f * scale);

        var y = rect.Min.Y + padding * 0.6f;
        if (hasNote)
        {
            TextPaint.Draw(drawList, new Vector2(rect.Min.X + padding, y), view.PrivateNoteLabel ?? "提示", TextRole.Footnote, look.SecondaryLabel, in look, scale);
            y += TextPaint.LineHeight(TextRole.Footnote, in look, scale);
            TextPaint.Draw(drawList, new Vector2(rect.Min.X + padding, y), view.PrivateNote, TextRole.Headline, accent, in look, scale);
            y += TextPaint.LineHeight(TextRole.Headline, in look, scale) + CardGap * scale;
        }

        if (!string.IsNullOrEmpty(view.Instruction))
        {
            var instructionRect = Rect.FromSize(new Vector2(rect.Min.X + padding, y), new Vector2(width - padding * 2f, instructionHeight + 2f * scale));
            TextPaint.DrawWrapped(drawList, instructionRect, view.Instruction, TextRole.Footnote, look.SecondaryLabel, in look, scale, 3, TextAlign.Start);
        }

        flow.Y = rect.Max.Y + CardGap * scale;
    }

    private static void DrawPlayers(Flow flow, GameView view, Look look, float scale)
    {
        if (view.Players.Count == 0)
        {
            return;
        }

        var heading = "玩家";
        var drawn = 0;
        var y = flow.Y;
        TextPaint.Draw(ImGui.GetWindowDrawList(), new Vector2(flow.X, y), heading, TextRole.Body, look.SecondaryLabel, in look, scale);
        y += TextPaint.LineHeight(TextRole.Body, in look, scale) + 4f * scale;

        var gap = PlayerGap * scale;
        var cardWidth = (flow.Width - gap) / PlayerColumns;
        var rows = (view.Players.Count + PlayerColumns - 1) / PlayerColumns;

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < PlayerColumns; column++)
            {
                var index = row * PlayerColumns + column;
                if (index >= view.Players.Count)
                {
                    break;
                }

                var rect = Rect.FromSize(new Vector2(flow.X + column * (cardWidth + gap), y + row * (PlayerCardHeight * scale + gap)), new Vector2(cardWidth, PlayerCardHeight * scale));
                DrawPlayer(rect, view.Players[index], look, scale);
                drawn++;
            }
        }

        if (drawn > 0)
        {
            y += rows * (PlayerCardHeight * scale + gap) - gap;
        }

        flow.Y = y + CardGap * scale;
    }

    private static void DrawPlayer(Rect rect, GamePlayerCard player, Look look, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        Chrome.Card(rect, look, scale);

        var padding = look.SpaceSmall * scale * 1.5f;
        var nameColor = player.IsAlive ? look.Label : look.TertiaryLabel;
        var name = player.Name;
        var nameWidth = TextPaint.Measure(name, TextRole.Body, in look, scale).X;
        TextPaint.Draw(drawList, new Vector2(rect.Min.X + padding, rect.Min.Y + padding * 0.8f), name, TextRole.Body, nameColor, in look, scale);

        var tagX = rect.Min.X + padding + nameWidth + look.SpaceSmall * scale;
        if (player.IsYou)
        {
            TextPaint.Draw(drawList, new Vector2(tagX, rect.Min.Y + padding * 0.8f + 1f * scale), "你", TextRole.Footnote, look.Accent, in look, scale);
            tagX += TextPaint.Measure("你", TextRole.Footnote, in look, scale).X + look.SpaceSmall * scale;
        }

        if (player.IsHost)
        {
            TextPaint.Draw(drawList, new Vector2(tagX, rect.Min.Y + padding * 0.8f + 1f * scale), "房主", TextRole.Footnote, look.Warning, in look, scale);
        }

        if (!string.IsNullOrEmpty(player.Sub))
        {
            TextPaint.Draw(drawList, new Vector2(rect.Min.X + padding, rect.Min.Y + padding * 0.8f + TextPaint.LineHeight(TextRole.Body, in look, scale)), player.Sub, TextRole.Footnote, look.SecondaryLabel, in look, scale);
        }

        if (!string.IsNullOrEmpty(player.Badge) || player.Votes is > 0)
        {
            var badge = player.Votes is > 0 ? $"{player.Votes} 票" : player.Badge!;
            var badgeColor = player.Votes is > 0 ? look.Destructive : player.IsAlive ? look.Success : look.TertiaryLabel;
            var width = TextPaint.Measure(badge, TextRole.Footnote, in look, scale).X;
            TextPaint.Draw(drawList, new Vector2(rect.Max.X - padding - width, rect.Min.Y + padding * 0.8f + 1f * scale), badge, TextRole.Footnote, badgeColor, in look, scale);
        }
    }

    private static void DrawScoreboard(Flow flow, GameView view, Look look, float scale)
    {
        if (view.Scores.Count == 0)
        {
            return;
        }

        var y = flow.Y;
        TextPaint.Draw(ImGui.GetWindowDrawList(), new Vector2(flow.X, y), "积分", TextRole.Body, look.SecondaryLabel, in look, scale);
        y += TextPaint.LineHeight(TextRole.Body, in look, scale) + 2f * scale;

        var rowHeight = 21f * scale;
        var padding = look.SpaceSmall * scale * 1.5f;
        var height = view.Scores.Count * rowHeight + padding * 2f;
        var rect = Rect.FromSize(new Vector2(flow.X, y), new Vector2(flow.Width, height));
        Chrome.Card(rect, look, scale);

        for (var i = 0; i < view.Scores.Count; i++)
        {
            var entry = view.Scores[i];
            var rowY = rect.Min.Y + padding + i * rowHeight;
            var nameColor = entry.IsYou ? look.Accent : look.Label;
            TextPaint.Draw(ImGui.GetWindowDrawList(), new Vector2(rect.Min.X + padding, rowY), entry.Name, TextRole.Footnote, nameColor, in look, scale);

            var score = $"{entry.Score}";
            var scoreWidth = TextPaint.Measure(score, TextRole.Mono, in look, scale).X;
            TextPaint.Draw(ImGui.GetWindowDrawList(), new Vector2(rect.Max.X - padding - scoreWidth - 34f * scale, rowY), score, TextRole.Mono, entry.Score <= 1 ? look.Warning : look.Label, in look, scale);

            if (entry.Delta != 0)
            {
                var delta = entry.Delta > 0 ? $"+{entry.Delta}" : $"{entry.Delta}";
                TextPaint.Draw(ImGui.GetWindowDrawList(), new Vector2(rect.Max.X - padding - 26f * scale, rowY), delta, TextRole.Mono, entry.Delta > 0 ? look.Success : look.Destructive, in look, scale);
            }
        }

        flow.Y = rect.Max.Y + CardGap * scale;
    }

    private static void DrawActions(Flow flow, GameView view, Look look, float scale)
    {
        if (view.Actions.Count == 0)
        {
            return;
        }

        var y = flow.Y;
        var x = flow.X;
        ImGui.SetCursorScreenPos(new Vector2(flow.X, y));
        for (var i = 0; i < view.Actions.Count; i++)
        {
            var action = view.Actions[i];
            if (i > 0)
            {
                if (x + ActionWidth * scale > flow.Right)
                {
                    break;
                }

                ImGui.SameLine(0f, look.SpaceSmall * scale);
            }

            var style = action.Danger ? ButtonStyle.Destructive : i == 0 ? ButtonStyle.Prominent : ButtonStyle.Default;
            Gw.Button(action.Label, style, ButtonSize.Regular, width: ActionWidth);
            x += ActionWidth * scale + look.SpaceSmall * scale;
        }

        flow.Y = y + ActionHeight * scale + CardGap * scale;
    }

    private static void DrawInput(Flow flow, GameView view, Look look, float scale)
    {
        if (!view.AcceptsText)
        {
            return;
        }

        var y = flow.Y;
        var buttonWidth = 72f * scale;
        var fieldWidth = flow.Width - buttonWidth - look.SpaceSmall * scale;
        ImGui.SetCursorScreenPos(new Vector2(flow.X, y));
        var fieldValue = string.Empty;
        Chrome.TextField(ref fieldValue, "##action", view.TextHint ?? "说点什么...", fieldWidth, look.ControlHeight * scale, look, scale, 200);
        ImGui.SetCursorScreenPos(new Vector2(flow.X + fieldWidth + look.SpaceSmall * scale, y));
        Gw.Button("发送", ButtonStyle.Prominent, ButtonSize.Regular, width: buttonWidth);

        flow.Y = y + look.ControlHeight * scale + CardGap * scale;
    }

    private static void DrawLog(Flow flow, RoomSnapshot snapshot, Look look, float scale)
    {
        if (snapshot.Backlog is not { Count: > 0 } log)
        {
            return;
        }

        var y = flow.Y;
        var lines = Math.Min(log.Count, MaxLogLines);
        var lineHeight = TextPaint.LineHeight(TextRole.Footnote, in look, scale) + 3f * scale;
        var padding = look.SpaceSmall * scale * 1.5f;
        var rect = Rect.FromSize(new Vector2(flow.X, y), new Vector2(flow.Width, lines * lineHeight + padding * 2f));
        Chrome.Card(rect, look, scale);

        var drawList = ImGui.GetWindowDrawList();
        for (var i = 0; i < lines; i++)
        {
            var entry = log[log.Count - lines + i];
            var rowY = rect.Min.Y + padding + i * lineHeight;
            var actor = string.IsNullOrEmpty(entry.Actor) ? string.Empty : entry.Actor + ":";
            TextPaint.Draw(drawList, new Vector2(rect.Min.X + padding, rowY), actor, TextRole.Footnote, look.Accent, in look, scale);
            var actorWidth = TextPaint.Measure(actor, TextRole.Footnote, in look, scale).X;
            TextPaint.Draw(drawList, new Vector2(rect.Min.X + padding + actorWidth, rowY), entry.Text, TextRole.Footnote, look.SecondaryLabel, in look, scale);
        }

        flow.Y = rect.Max.Y + CardGap * scale;
    }

    private static void DrawHand(Flow flow, GameView view, Look look, float scale)
    {
        var cards = (view.PrivateNote ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (cards.Length == 0)
        {
            return;
        }

        var y = flow.Y;
        TextPaint.Draw(ImGui.GetWindowDrawList(), new Vector2(flow.X, y), "我的手牌", TextRole.Body, look.SecondaryLabel, in look, scale);
        y += TextPaint.LineHeight(TextRole.Body, in look, scale) + 4f * scale;

        var cardWidth = 38f * scale;
        var cardHeight = 48f * scale;
        var gap = 3f * scale;
        var drawList = ImGui.GetWindowDrawList();
        var x = flow.X;
        foreach (var card in cards)
        {
            var rect = Rect.FromSize(new Vector2(x, y), new Vector2(cardWidth, cardHeight));
            drawList.AddRectFilled(rect.Min, rect.Max, look.SecondaryBackground.ToAbgr(), 5f * scale);
            drawList.AddRect(rect.Min, rect.Max, look.Separator.ToAbgr(), 5f * scale, ImDrawFlags.RoundCornersAll, 1f);
            var suit = card is "JOKER" or "joker" ? "王" : card;
            var red = card is "JOKER" or "joker";
            var suitWidth = TextPaint.Measure(suit, TextRole.Footnote, in look, scale).X;
            TextPaint.Draw(drawList, new Vector2(rect.Min.X + (cardWidth - suitWidth) * 0.5f, rect.Min.Y + 5f * scale), suit, TextRole.Footnote, red ? look.Destructive : look.Label, in look, scale);
            x += cardWidth + gap;
            if (x + cardWidth > flow.Right)
            {
                break;
            }
        }

        flow.Y = y + cardHeight + CardGap * scale;
    }

    private static void DrawBoard(Flow flow, GameView view, Look look, float scale)
    {
        var rows = view.Board;
        var columns = rows[0].Length;
        var cell = 34f * scale;
        var width = cell * columns;
        var height = cell * rows.Count;
        var origin = new Vector2(flow.X + (flow.Width - width) * 0.5f, flow.Y);
        var rect = Rect.FromSize(origin, new Vector2(width, height));
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(rect.Min, rect.Max, look.SecondaryBackground.ToAbgr(), 6f * scale);
        for (var i = 0; i <= columns; i++)
        {
            var lineX = rect.Min.X + i * cell;
            drawList.AddLine(new Vector2(lineX, rect.Min.Y), new Vector2(lineX, rect.Max.Y), look.Separator.ToAbgr(), 1f);
        }

        for (var i = 0; i <= rows.Count; i++)
        {
            var lineY = rect.Min.Y + i * cell;
            drawList.AddLine(new Vector2(rect.Min.X, lineY), new Vector2(rect.Max.X, lineY), look.Separator.ToAbgr(), 1f);
        }

        // 楚河汉界: the empty rank the soldiers face each other across.
        var riverY = rect.Min.Y + 4.5f * cell;
        TextPaint.Draw(drawList, new Vector2(rect.Min.X + cell * 1.4f, riverY - TextPaint.LineHeight(TextRole.Caption, in look, scale) * 0.5f), "楚 河", TextRole.Caption, look.TertiaryLabel, in look, scale);
        var han = "汉 界";
        TextPaint.Draw(drawList, new Vector2(rect.Max.X - cell * 1.4f - TextPaint.Measure(han, TextRole.Caption, in look, scale).X, riverY - TextPaint.LineHeight(TextRole.Caption, in look, scale) * 0.5f), han, TextRole.Caption, look.TertiaryLabel, in look, scale);

        for (var row = 0; row < rows.Count; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var piece = Chinese(rows[row][column]);
                if (piece.Length == 0)
                {
                    continue;
                }

                var red = char.IsUpper(rows[row][column]);
                var pieceColor = red ? look.Warning : look.Label;
                var center = new Vector2(rect.Min.X + (column + 0.5f) * cell, rect.Min.Y + (row + 0.5f) * cell);
                var radius = cell * 0.42f;
                drawList.AddCircleFilled(center, radius, look.SecondaryBackground.ToAbgr());
                drawList.AddCircle(center, radius, pieceColor.ToAbgr(), 0, 1.4f * scale);
                var size = TextPaint.Measure(piece, TextRole.Headline, in look, scale);
                TextPaint.Draw(drawList, center - size * 0.5f + new Vector2(0f, 1f * scale), piece, TextRole.Headline, pieceColor, in look, scale);
            }
        }

        flow.Y = rect.Max.Y + CardGap * scale;
    }

    private static string Chinese(char piece) => piece switch
    {
        'r' or 'R' => "車",
        'n' or 'N' => "馬",
        'b' or 'B' => "象",
        'a' or 'A' => "士",
        'k' or 'K' => "將",
        'c' or 'C' => "炮",
        'p' or 'P' => "兵",
        _ => string.Empty,
    };

    private static void DrawPill(Vector2 origin, Vector2 size, string text, Color color, Look look, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var rect = Rect.FromSize(origin, size);
        drawList.AddRectFilled(rect.Min, rect.Max, look.SecondaryFill.ToAbgr(), size.Y * 0.5f);
        var width = TextPaint.Measure(text, TextRole.Footnote, in look, scale).X;
        TextPaint.Draw(drawList, new Vector2(rect.Min.X + (size.X - width) * 0.5f, rect.Min.Y + 4f * scale), text, TextRole.Footnote, color, in look, scale);
    }

    /// <summary>A vertical cursor over the panel: each block sets <see cref="Y"/> to its bottom, so the layout needs no ImGui item flow.</summary>
    internal sealed class Flow
    {
        public readonly float X;
        public readonly float Right;
        public readonly float Width;

        public Flow(Rect area)
        {
            X = area.Min.X;
            Right = area.Max.X;
            Width = area.Width;
            Y = area.Min.Y;
        }

        public float Y { get; set; }
    }
}
