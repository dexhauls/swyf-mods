using System.Collections.Generic;
using UnityEngine;

namespace Arcade.Games
{
    internal sealed class Tetris : Game
    {
        private const int Cols = 10, Rows = 20, Cell = 12, Side = 76;

        public override string Id => "tetris";
        public override string Name => "Tetris";
        public override string Tagline => Lang.T("Skládej kostky do řad", "Stack blocks into lines");
        public override string Controls => Lang.T("←→: posun · ↑ / X: otočit · Z: otočit zpět · ↓: rychleji · Mezerník: shodit · P: pauza", "←→: move · ↑ / X: rotate · Z: rotate back · ↓: soft drop · Space: hard drop · P: pause");
        public override int Width => Cols * Cell + Side;
        public override int Height => Rows * Cell;
        public override string Status => Lang.T($"Řádky {_lines} · Úroveň {Level}", $"Lines {_lines} · Level {Level}");

        // Spawn orientation of each piece in its N×N box (I, O, T, S, Z, J, L).
        private static readonly string[][] Shapes =
        {
            new[] { "....", "####", "....", "...." },
            new[] { "##", "##" },
            new[] { ".#.", "###", "..." },
            new[] { ".##", "##.", "..." },
            new[] { "##.", ".##", "..." },
            new[] { "#..", "###", "..." },
            new[] { "..#", "###", "..." },
        };

        private static readonly Color32[] Colors =
        {
            PixelCanvas.Hex(0x31d7e8), PixelCanvas.Hex(0xf4d23c), PixelCanvas.Hex(0xb04ee0), PixelCanvas.Hex(0x4fd65a),
            PixelCanvas.Hex(0xec4a4a), PixelCanvas.Hex(0x3f6ff0), PixelCanvas.Hex(0xf29a2e),
        };

        private readonly int[,] _board = new int[Cols, Rows]; // 0 = empty, else piece index + 1
        private readonly List<int> _bag = new List<int>();
        private bool[,] _piece;
        private int _kind, _next, _px, _py;
        private int _lines;
        private float _fall, _lock, _dasTimer;
        private int _dasDir, _lockMoves;
        private readonly List<int> _clearing = new List<int>();
        private float _clearTimer;

        private int Level => 1 + _lines / 10;
        private float FallInterval => Mathf.Max(0.05f, 0.8f - (Level - 1) * 0.07f);

        protected override void Reset()
        {
            System.Array.Clear(_board, 0, _board.Length);
            _bag.Clear();
            _lines = 0;
            _clearing.Clear();
            _next = Draw7();
            Spawn();
        }

        private int Draw7()
        {
            if (_bag.Count == 0)
            {
                for (int i = 0; i < 7; i++) _bag.Add(i);
                for (int i = 6; i > 0; i--) { int j = Rng.Next(i + 1); (_bag[i], _bag[j]) = (_bag[j], _bag[i]); }
            }
            int k = _bag[0];
            _bag.RemoveAt(0);
            return k;
        }

        private static bool[,] Matrix(int kind)
        {
            var rows = Shapes[kind];
            int n = rows.Length;
            var m = new bool[n, n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++) m[x, y] = rows[y][x] == '#';
            return m;
        }

        private static bool[,] RotateCw(bool[,] m)
        {
            int n = m.GetLength(0);
            var r = new bool[n, n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++) r[n - 1 - y, x] = m[x, y];
            return r;
        }

        private void Spawn()
        {
            _kind = _next;
            _next = Draw7();
            _piece = Matrix(_kind);
            _px = (Cols - _piece.GetLength(0)) / 2;
            _py = _kind == 0 ? -1 : 0;
            _fall = 0;
            _lock = 0;
            _lockMoves = 0;
            if (Collides(_piece, _px, _py)) Over = true;
        }

        private bool Collides(bool[,] m, int ox, int oy)
        {
            int n = m.GetLength(0);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    if (!m[x, y]) continue;
                    int bx = ox + x, by = oy + y;
                    if (bx < 0 || bx >= Cols || by >= Rows) return true;
                    if (by >= 0 && _board[bx, by] != 0) return true;
                }
            return false;
        }

        private bool Grounded => Collides(_piece, _px, _py + 1);

        private bool TryMove(int dx)
        {
            if (Collides(_piece, _px + dx, _py)) return false;
            _px += dx;
            BumpLock();
            return true;
        }

        private void Rotate(bool cw)
        {
            if (_kind == 1) return;
            var r = RotateCw(_piece);
            if (!cw) { r = RotateCw(RotateCw(r)); }
            int[] kicks = { 0, -1, 1, -2, 2 };
            foreach (int k in kicks)
            {
                if (!Collides(r, _px + k, _py)) { _piece = r; _px += k; BumpLock(); return; }
                if (!Collides(r, _px + k, _py - 1)) { _piece = r; _px += k; _py -= 1; BumpLock(); return; }
            }
        }

        private void BumpLock()
        {
            if (Grounded && _lockMoves < 15) { _lock = 0; _lockMoves++; }
        }

        public override void Step(ArcadeInput input)
        {
            if (_clearing.Count > 0)
            {
                _clearTimer -= Dt;
                if (_clearTimer <= 0) { RemoveLines(); Spawn(); }
                return;
            }

            // Horizontal movement with auto-repeat.
            int dir = input.Left && !input.Right ? -1 : input.Right && !input.Left ? 1 : 0;
            if (input.LeftPressed) { TryMove(-1); _dasDir = -1; _dasTimer = 0.17f; }
            else if (input.RightPressed) { TryMove(1); _dasDir = 1; _dasTimer = 0.17f; }
            else if (dir != 0 && dir == _dasDir)
            {
                _dasTimer -= Dt;
                if (_dasTimer <= 0) { TryMove(dir); _dasTimer = 0.05f; }
            }
            else _dasDir = dir;

            if (input.UpPressed || input.RotateKey) Rotate(true);
            if (input.RotateBack) Rotate(false);

            if (input.Action)
            {
                int dropped = 0;
                while (!Collides(_piece, _px, _py + 1)) { _py++; dropped++; }
                Score += dropped * 2;
                Lock();
                return;
            }

            float interval = input.Down ? Mathf.Min(0.035f, FallInterval) : FallInterval;
            _fall += Dt;
            if (Grounded)
            {
                _lock += Dt;
                if (_lock >= 0.5f) Lock();
                return;
            }
            if (_fall >= interval)
            {
                _fall = 0;
                _py++;
                if (input.Down) Score += 1;
            }
        }

        private void Lock()
        {
            int n = _piece.GetLength(0);
            bool above = false;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    if (!_piece[x, y]) continue;
                    int by = _py + y;
                    if (by < 0) { above = true; continue; }
                    _board[_px + x, by] = _kind + 1;
                }
            if (above) { Over = true; return; }

            for (int y = 0; y < Rows; y++)
            {
                bool full = true;
                for (int x = 0; x < Cols && full; x++) full = _board[x, y] != 0;
                if (full) _clearing.Add(y);
            }
            if (_clearing.Count > 0)
            {
                int[] pts = { 0, 100, 300, 500, 800 };
                Score += pts[_clearing.Count] * Level;
                _clearTimer = 0.25f;
                _piece = new bool[1, 1];
                return;
            }
            Spawn();
        }

        private void RemoveLines()
        {
            foreach (int row in _clearing)
            {
                for (int y = row; y > 0; y--)
                    for (int x = 0; x < Cols; x++) _board[x, y] = _board[x, y - 1];
                for (int x = 0; x < Cols; x++) _board[x, 0] = 0;
            }
            _lines += _clearing.Count;
            _clearing.Clear();
        }

        private static readonly Color32 Bg = PixelCanvas.Hex(0x0d1020), Grid = PixelCanvas.Hex(0x161b33), Panel = PixelCanvas.Hex(0x141830);

        public override void Draw(PixelCanvas c)
        {
            c.Clear(Panel);
            c.Rect(0, 0, Cols * Cell, Rows * Cell, Bg);
            for (int x = 1; x < Cols; x++) c.Rect(x * Cell, 0, 1, Rows * Cell, Grid);
            for (int y = 1; y < Rows; y++) c.Rect(0, y * Cell, Cols * Cell, 1, Grid);

            bool flash = _clearing.Count > 0 && ((int)(_clearTimer * 20) % 2 == 0);
            for (int y = 0; y < Rows; y++)
                for (int x = 0; x < Cols; x++)
                {
                    int v = _board[x, y];
                    if (v == 0) continue;
                    var col = _clearing.Contains(y) && flash ? new Color32(255, 255, 255, 255) : Colors[v - 1];
                    c.Block(x * Cell, y * Cell, Cell, Cell, col);
                }

            if (_clearing.Count == 0 && !Over)
            {
                int ghost = _py;
                while (!Collides(_piece, _px, ghost + 1)) ghost++;
                DrawPiece(c, _piece, _px * Cell, ghost * Cell, Cell, Colors[_kind], ghostOnly: true);
                DrawPiece(c, _piece, _px * Cell, _py * Cell, Cell, Colors[_kind], ghostOnly: false);
            }

            // Next piece box.
            int bx = Cols * Cell + 8, by = 10, bw = Side - 16, bh = 52;
            c.Rect(bx, by, bw, bh, Bg);
            c.Frame(bx, by, bw, bh, PixelCanvas.Hex(0x2a3160));
            var next = Matrix(_next);
            int n = next.GetLength(0), cs = 10;
            int ox = bx + (bw - n * cs) / 2, oy = by + (bh - n * cs) / 2 + (_next == 0 ? 5 : 0);
            DrawPiece(c, next, ox, oy, cs, Colors[_next], ghostOnly: false);

            // Level meter: lines towards the next level.
            int mx = Cols * Cell + 8, my = 74, mh = Rows * Cell - my - 10;
            c.Rect(mx, my, 6, mh, Bg);
            int fill = (int)(mh * ((_lines % 10) / 10f));
            c.Rect(mx, my + mh - fill, 6, fill, PixelCanvas.Hex(0x7b2ff7));
        }

        private static void DrawPiece(PixelCanvas c, bool[,] m, int ox, int oy, int cs, Color32 col, bool ghostOnly)
        {
            int n = m.GetLength(0);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    if (!m[x, y]) continue;
                    int px = ox + x * cs, py = oy + y * cs;
                    if (py + cs <= 0) continue;
                    if (ghostOnly) c.Frame(px + 1, py + 1, cs - 2, cs - 2, new Color32(col.r, col.g, col.b, 110));
                    else c.Block(px, py, cs, cs, col);
                }
        }

        public override void DrawIcon(PixelCanvas c)
        {
            c.Clear(Bg);
            int s = c.Width / 6;
            void B(int x, int y, int k) => c.Block(x * s, y * s, s, s, Colors[k]);
            B(0, 5, 5); B(1, 5, 5); B(2, 5, 5); B(0, 4, 5);
            B(3, 5, 1); B(4, 5, 1); B(3, 4, 1); B(4, 4, 1);
            B(5, 5, 0); B(5, 4, 0); B(5, 3, 0); B(5, 2, 0);
            B(2, 1, 2); B(1, 2, 2); B(2, 2, 2); B(3, 2, 2);
        }
    }
}
