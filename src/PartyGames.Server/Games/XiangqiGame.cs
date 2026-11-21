using PartyGames.Protocol;

namespace PartyGames.Server.Games;

/// <summary>
/// 中国象棋：9 列 × 10 行，红方在下（大写）、黑方在上（小写）。
/// 走法用四位数坐标文本："起点行 起点列 终点行 终点列"，例如 "0907"。
/// 服务端负责全部合法性（含将军、将死、飞将）。
/// </summary>
public sealed class XiangqiGame : IGameModule
{
    public string Id => "xiangqi";
    public string DisplayName => "中国象棋";
    public string Blurb => "九列十行，車馬炮兵各按规矩走。将死对方为胜，被将死为负。";
    public int MinPlayers => 2;
    public int MaxPlayers => 2;

    public bool SupportsScoring => true;
    public ScoreDefaults Scoring => new(Start: 3, Penalty: 1, BigThreshold: 0, BigStart: 3, PenaltyRoll: false);

    private const int Rows = 10;
    private const int Cols = 9;

    private readonly char[,] _board = new char[Rows, Cols];
    private readonly List<string> _ids = new();
    private readonly List<string> _names = new();

    private int _turn;            // 0 = 红（下），1 = 黑（上）
    private bool _finished;
    private string? _banner;
    private long _deadline;
    private string _lastMove = string.Empty;

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static bool IsRed(char c) => char.IsUpper(c);
    private static bool IsBlack(char c) => char.IsLower(c);
    private static bool SameSide(char a, char b) =>
        (IsRed(a) && IsRed(b)) || (IsBlack(a) && IsBlack(b));

    private int MySide(string playerId) => _ids.IndexOf(playerId);

    public void Start(Room room)
    {
        _finished = false;
        _banner = null;
        _turn = 0;
        _lastMove = string.Empty;
        _ids.Clear();
        _names.Clear();

        foreach (var p in room.Players.Take(2))
        {
            _ids.Add(p.Id);
            _names.Add(p.Name);
        }

        if (_ids.Count < 2)
        {
            _finished = true;
            _banner = "象棋需要 2 个人。";
            room.Log("system", _banner);
            room.SyncAll();
            return;
        }

        Array.Clear(_board, 0, _board.Length);
        // 黑方（上）
        Place(0, 0, 'r'); Place(0, 1, 'n'); Place(0, 2, 'b'); Place(0, 3, 'a');
        Place(0, 4, 'k'); Place(0, 5, 'a'); Place(0, 6, 'b'); Place(0, 7, 'n');
        Place(0, 8, 'r');
        Place(2, 1, 'c'); Place(2, 7, 'c');
        for (var c = 0; c < Cols; c += 2) Place(3, c, 'p');
        // 红方（下）
        Place(9, 0, 'R'); Place(9, 1, 'N'); Place(9, 2, 'B'); Place(9, 3, 'A');
        Place(9, 4, 'K'); Place(9, 5, 'A'); Place(9, 6, 'B'); Place(9, 7, 'N');
        Place(9, 8, 'R');
        Place(7, 1, 'C'); Place(7, 7, 'C');
        for (var c = 0; c < Cols; c += 2) Place(6, c, 'P');

        _deadline = Now + 60_000;
        room.Log("system", $"{_names[0]}（红）对 {_names[1]}（黑），红先行。");
        room.SyncAll();
    }

    private void Place(int r, int c, char piece) => _board[r, c] = piece;

    public void Tick(Room room, long nowMs)
    {
        if (_finished || nowMs < _deadline) return;
        _deadline = Now + 60_000;
        room.Log("system", $"{_names[_turn]} 超时，计时重置。");
        room.SyncAll();
    }

    public void HandleAction(Room room, Player player, ActionRequest request)
    {
        if (_finished) return;

        var side = MySide(player.Id);
        if (side < 0) return;

        // 认输：对方获胜，游戏结束
        if (request.Kind == "resign")
        {
            Finish(room, 1 - side, "对手认输");
            return;
        }

        if (request.Kind != "move" || side != _turn) return;

        var text = (request.Text ?? string.Empty).Trim();
        if (text.Length < 4 || !int.TryParse(text, out _)) return;

        var r1 = text[0] - '0';
        var c1 = text[1] - '0';
        var r2 = text[2] - '0';
        var c2 = text[3] - '0';

        if (!InBounds(r1, c1) || !InBounds(r2, c2)) return;

        var piece = _board[r1, c1];
        if (piece == '\0') return;
        if (side == 0 && !IsRed(piece)) return;
        if (side == 1 && !IsBlack(piece)) return;

        if (!CanMove(r1, c1, r2, c2)) return;
        if (!MoveLeavesKingSafe(r1, c1, r2, c2, side)) return;

        var captured = _board[r2, c2];
        _board[r2, c2] = piece;
        _board[r1, c1] = '\0';
        _lastMove = $"{r1}{c1}{r2}{c2}";

        var note = captured == '\0' ? string.Empty : $"，吃掉 {PieceName(captured)}";
        room.Log("result", $"{_names[side]} 走 {PieceName(piece)} {SquareName(r1, c1)}→{SquareName(r2, c2)}{note}");

        // 吃将？直接结束（正常对局靠将死，这里兜底）
        if (captured == 'k' || captured == 'K')
        {
            Finish(room, side, "擒王");
            return;
        }

        _turn = 1 - _turn;
        _deadline = Now + 60_000;

        if (!HasAnyLegalMove(_turn))
        {
            var reason = InCheck(_turn) ? "将死" : "无子可走";
            Finish(room, side, reason);
            return;
        }

        if (InCheck(_turn)) room.Log("system", $"{_names[_turn]} 被将军了！");
        room.SyncAll();
    }

    private void Finish(Room room, int winner, string reason)
    {
        _finished = true;
        _banner = $"{_names[winner]} 获胜（{reason}）";
        room.Log("result", _banner);
        var loser = _ids[1 - winner];
        room.Finish(new GameOutcome
        {
            Banner = _banner!,
            LosingSide = _names[1 - winner],
            LosingPlayerIds = new List<string> { loser },
        });
        room.SyncAll();
    }

    private static bool InBounds(int r, int c) => r >= 0 && r < Rows && c >= 0 && c < Cols;

    private static string SquareName(int r, int c) => $"{(char)('a' + c)}{Rows - r}";

    private static string PieceName(char p) => p switch
    {
        'K' => "帅", 'A' => "仕", 'B' => "相", 'N' => "马", 'R' => "车", 'C' => "炮", 'P' => "兵",
        'k' => "将", 'a' => "士", 'b' => "象", 'n' => "马", 'r' => "车", 'c' => "炮", 'p' => "卒",
        _ => "?",
    };

    // ---------- 走法 ----------

    private bool CanMove(int r1, int c1, int r2, int c2)
    {
        if (r1 == r2 && c1 == c2) return false;

        var piece = _board[r1, c1];
        var target = _board[r2, c2];
        if (target != '\0' && SameSide(piece, target)) return false;

        var dr = r2 - r1;
        var dc = c2 - c1;
        var adr = Math.Abs(dr);
        var adc = Math.Abs(dc);

        switch (char.ToLowerInvariant(piece))
        {
            case 'r': // 车
                return (dr == 0 || dc == 0) && CountBetween(r1, c1, r2, c2) == 0;
            case 'c': // 炮
                {
                    if (dr != 0 && dc != 0) return false;
                    var between = CountBetween(r1, c1, r2, c2);
                    return target == '\0' ? between == 0 : between == 1;
                }
            case 'n': // 马
                {
                    if (!((adr == 2 && adc == 1) || (adr == 1 && adc == 2))) return false;
                    // 蹩马腿
                    if (adr == 2)
                    {
                        if (_board[r1 + Math.Sign(dr), c1] != '\0') return false;
                    }
                    else
                    {
                        if (_board[r1, c1 + Math.Sign(dc)] != '\0') return false;
                    }
                    return true;
                }
            case 'b': // 相/象
                {
                    if (adr != 2 || adc != 2) return false;
                    if (IsRed(piece) && r2 < 5) return false;
                    if (IsBlack(piece) && r2 > 4) return false;
                    return _board[r1 + dr / 2, c1 + dc / 2] == '\0';
                }
            case 'a': // 仕/士
                {
                    if (adr != 1 || adc != 1) return false;
                    if (c2 < 3 || c2 > 5) return false;
                    if (IsRed(piece)) return r2 >= 7 && r2 <= 9;
                    return r2 >= 0 && r2 <= 2;
                }
            case 'k': // 帅/将
                {
                    if (adr + adc != 1) return false;
                    if (c2 < 3 || c2 > 5) return false;
                    if (IsRed(piece)) return r2 >= 7 && r2 <= 9;
                    return r2 >= 0 && r2 <= 2;
                }
            case 'p': // 兵/卒
                {
                    var forward = IsRed(piece) ? -1 : 1;
                    if (dc == 0 && dr == forward) return true;
                    var crossed = IsRed(piece) ? r1 <= 4 : r1 >= 5;
                    return crossed && dr == 0 && adc == 1;
                }
        }

        return false;
    }

    private int CountBetween(int r1, int c1, int r2, int c2)
    {
        var count = 0;
        var dr = Math.Sign(r2 - r1);
        var dc = Math.Sign(c2 - c1);

        var r = r1 + dr;
        var c = c1 + dc;
        while (r != r2 || c != c2)
        {
            if (_board[r, c] != '\0') count++;
            r += dr;
            c += dc;
        }
        return count;
    }

    /// <summary>找某一方的将/帅位置。</summary>
    private (int R, int C) FindKing(int side)
    {
        var want = side == 0 ? 'K' : 'k';
        for (var r = 0; r < Rows; r++)
            for (var c = 0; c < Cols; c++)
                if (_board[r, c] == want) return (r, c);
        return (-1, -1);
    }

    /// <summary>某一方是否被将军（含飞将）。</summary>
    private bool InCheck(int side)
    {
        var (kr, kc) = FindKing(side);
        if (kr < 0) return true;

        // 对面将帅照面
        var (er, ec) = FindKing(1 - side);
        if (ec == kc)
        {
            var blocked = false;
            for (var r = Math.Min(kr, er) + 1; r < Math.Max(kr, er); r++)
                if (_board[r, kc] != '\0') { blocked = true; break; }
            if (!blocked) return true;
        }

        var enemyRed = side == 1;
        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Cols; c++)
            {
                var p = _board[r, c];
                if (p == '\0') continue;
                if (enemyRed ? !IsRed(p) : !IsBlack(p)) continue;
                if (CanMove(r, c, kr, kc)) return true;
            }
        }

        return false;
    }

    private bool MoveLeavesKingSafe(int r1, int c1, int r2, int c2, int side)
    {
        var from = _board[r1, c1];
        var to = _board[r2, c2];

        _board[r2, c2] = from;
        _board[r1, c1] = '\0';

        var ok = !InCheck(side);

        _board[r1, c1] = from;
        _board[r2, c2] = to;
        return ok;
    }

    private bool HasAnyLegalMove(int side)
    {
        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Cols; c++)
            {
                var p = _board[r, c];
                if (p == '\0') continue;
                if (side == 0 ? !IsRed(p) : !IsBlack(p)) continue;

                for (var tr = 0; tr < Rows; tr++)
                {
                    for (var tc = 0; tc < Cols; tc++)
                    {
                        if (!CanMove(r, c, tr, tc)) continue;
                        if (MoveLeavesKingSafe(r, c, tr, tc, side)) return true;
                    }
                }
            }
        }
        return false;
    }

    // ---------- 视图 ----------

    public GameView BuildView(Room room, Player viewer)
    {
        var side = MySide(viewer.Id);
        var view = new GameView
        {
            Title = "中国象棋",
            Subtitle = _finished ? "本局结束" : $"轮到 {_names[_turn]}（{(_turn == 0 ? "红" : "黑")}）",
            DeadlineAtMs = _finished ? null : _deadline,
            ResultBanner = _finished ? _banner : null,
        };

        for (var r = 0; r < Rows; r++)
        {
            var row = new char[Cols];
            for (var c = 0; c < Cols; c++)
            {
                var p = _board[r, c];
                row[c] = p == '\0' ? '.' : p;
            }
            view.Board.Add(new string(row));
        }

        // 我方所有可落点，供插件高亮
        if (!_finished && side >= 0 && side == _turn)
        {
            for (var r = 0; r < Rows; r++)
            {
                for (var c = 0; c < Cols; c++)
                {
                    var p = _board[r, c];
                    if (p == '\0') continue;
                    if (side == 0 ? !IsRed(p) : !IsBlack(p)) continue;

                    for (var tr = 0; tr < Rows; tr++)
                    {
                        for (var tc = 0; tc < Cols; tc++)
                        {
                            if (!CanMove(r, c, tr, tc)) continue;
                            if (!MoveLeavesKingSafe(r, c, tr, tc, side)) continue;
                            view.Targets.Add($"{r}{c}{tr}{tc}");
                        }
                    }
                }
            }
        }

        for (var i = 0; i < _ids.Count; i++)
        {
            view.Players.Add(new GamePlayerCard
            {
                Id = _ids[i],
                Name = _names[i],
                IsYou = _ids[i] == viewer.Id,
                IsHost = _ids[i] == room.HostId,
                IsAlive = true,
                Badge = i == 0 ? "红" : "黑",
                Accent = i == _turn && !_finished ? "good" : null,
                Sub = i == _turn && !_finished ? "该 TA 走" : null,
            });
        }

        if (_finished)
        {
            view.Instruction = "本局结束。";
            if (viewer.Id == room.HostId) view.Actions.Add(new GameAction { Kind = "lobby", Label = "回到大厅" });
        }
        else if (side == _turn)
        {
            view.Instruction = InCheck(side) ? "你被将军了，必须先解将。" : "轮到你走棋。点自己的棋子，再点目标格。";
        }
        else
        {
            view.Instruction = $"等 {_names[_turn]} 走棋。";
        }

        return view;
    }

    public void OnPlayerDisconnected(Room room, Player player) { }

    public void OnPlayerLeft(Room room, Player player)
    {
        if (_finished) return;
        var side = MySide(player.Id);
        if (side < 0) return;
        Finish(room, 1 - side, "对手离开");
    }
}
