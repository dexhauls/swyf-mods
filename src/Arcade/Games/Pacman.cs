using UnityEngine;

namespace Arcade.Games
{
    /// <summary>
    /// Pac-Man on the classic 28×31 maze: pellets, power pellets, fruit, a tunnel and four ghosts with their
    /// original personalities (Blinky chases, Pinky ambushes, Inky flanks, Clyde wanders) and scatter/chase waves.
    /// </summary>
    internal sealed class Pacman : Game
    {
        private const int Cols = 28, Rows = 31, T = 8;

        public override string Id => "pacman";
        public override string Name => "Pac-Man";
        public override string Tagline => Lang.T("Sněz tečky, utíkej duchům", "Eat the dots, flee the ghosts");
        public override string Controls => Lang.T("Šipky / WASD: pohyb · P: pauza", "Arrows / WASD: move · P: pause");
        public override int Width => Cols * T;
        public override int Height => Rows * T;
        public override string Status => Lang.T($"Životy {_lives} · Kolo {_level}", $"Lives {_lives} · Level {_level}");
        public override string Banner => _phase == Phase.Ready ? Lang.T("Připrav se!", "Get ready!") : _phase == Phase.Clear ? Lang.T("Kolo hotové!", "Level clear!") : null;

        private static readonly string[] Maze =
        {
            "############################",
            "#............##............#",
            "#.####.#####.##.#####.####.#",
            "#o####.#####.##.#####.####o#",
            "#.####.#####.##.#####.####.#",
            "#..........................#",
            "#.####.##.########.##.####.#",
            "#.####.##.########.##.####.#",
            "#......##....##....##......#",
            "######.##### ## #####.######",
            "     #.##### ## #####.#     ",
            "     #.##          ##.#     ",
            "     #.## ###--### ##.#     ",
            "######.## #      # ##.######",
            "      .   #      #   .      ",
            "######.## #      # ##.######",
            "     #.## ######## ##.#     ",
            "     #.##          ##.#     ",
            "     #.## ######## ##.#     ",
            "######.## ######## ##.######",
            "#............##............#",
            "#.####.#####.##.#####.####.#",
            "#.####.#####.##.#####.####.#",
            "#o..##.......  .......##..o#",
            "###.##.##.########.##.##.###",
            "###.##.##.########.##.##.###",
            "#......##....##....##......#",
            "#.##########.##.##########.#",
            "#.##########.##.##########.#",
            "#..........................#",
            "############################",
        };

        private enum Phase { Ready, Play, Dying, Clear }
        private enum GhostMode { House, Exiting, Normal, Eyes, Entering }

        private sealed class Ghost
        {
            public Vector2 Pos;
            public Vector2Int Dir;
            public GhostMode Mode;
            public bool Frightened, Reverse;
            public float ReleaseAt, Bob = 1;
            public Color32 Color;
            public Vector2Int Corner;
        }

        private readonly char[,] _tiles = new char[Cols, Rows];
        private readonly Ghost[] _ghosts = new Ghost[4];
        private Vector2 _pac;
        private Vector2Int _dir, _want;
        private int _pellets, _eaten, _lives, _level, _combo;
        private Phase _phase;
        private float _phaseTimer, _lifeTime, _frightTimer, _waveTime, _anim, _freeze, _fruitTimer;
        private int _wave;
        private bool _bonusLife;
        private Vector2 _popupPos;
        private int _popupScore;
        private float _popupTimer;

        private static readonly float[] Waves = { 7, 20, 7, 20, 5, 20, 5, float.MaxValue };
        private static readonly Vector2Int Up = new Vector2Int(0, -1), Down = new Vector2Int(0, 1);
        private static readonly Vector2Int Left = new Vector2Int(-1, 0), Right = new Vector2Int(1, 0);
        private static readonly Vector2Int[] Order = { Up, Left, Down, Right }; // the original tie-break order
        private static readonly Vector2Int HouseDoor = new Vector2Int(13, 11);
        private const float DoorX = 13 * T + 4, AboveDoorY = 11 * T + 4, HouseY = 14 * T + 4;

        protected override void Reset()
        {
            _lives = 3;
            _level = 1;
            _bonusLife = false;
            FillMaze();
            StartLife();
        }

        private void FillMaze()
        {
            _pellets = 0;
            _eaten = 0;
            for (int y = 0; y < Rows; y++)
                for (int x = 0; x < Cols; x++)
                {
                    char ch = Maze[y][x];
                    _tiles[x, y] = ch;
                    if (ch == '.' || ch == 'o') _pellets++;
                }
        }

        private void StartLife()
        {
            _pac = Center(13, 23);
            _dir = Left;
            _want = Left;
            _ghosts[0] = new Ghost { Pos = Center(13, 11), Dir = Left, Mode = GhostMode.Normal, Color = PixelCanvas.Hex(0xff2a2a), Corner = new Vector2Int(25, -3) };
            _ghosts[1] = new Ghost { Pos = new Vector2(DoorX, HouseY), Dir = Down, Mode = GhostMode.House, ReleaseAt = 0.6f, Color = PixelCanvas.Hex(0xffb8ff), Corner = new Vector2Int(2, -3) };
            _ghosts[2] = new Ghost { Pos = new Vector2(11 * T + 4, HouseY), Dir = Up, Mode = GhostMode.House, ReleaseAt = Mathf.Max(1, 4.5f - _level * 0.5f), Color = PixelCanvas.Hex(0x2ef0f0), Corner = new Vector2Int(27, 34) };
            _ghosts[3] = new Ghost { Pos = new Vector2(15 * T + 4, HouseY), Dir = Up, Mode = GhostMode.House, ReleaseAt = Mathf.Max(2, 9f - _level), Color = PixelCanvas.Hex(0xffb852), Corner = new Vector2Int(0, 34) };
            _lifeTime = 0;
            _frightTimer = 0;
            _wave = 0;
            _waveTime = 0;
            _fruitTimer = 0;
            _freeze = 0;
            _phase = Phase.Ready;
            _phaseTimer = 1.6f;
        }

        private static Vector2 Center(int col, int row) => new Vector2(col * T + 4, row * T + 4);
        private static Vector2Int TileOf(Vector2 p) => new Vector2Int(Mathf.FloorToInt(p.x / T), Mathf.FloorToInt(p.y / T));

        private bool Walkable(int col, int row, bool door = false)
        {
            if (row == 14 && (col < 0 || col >= Cols)) return true;  // tunnel
            if (col < 0 || row < 0 || col >= Cols || row >= Rows) return false;
            char ch = _tiles[col, row];
            return ch == '-' ? door : ch != '#';
        }

        private bool Walkable(Vector2Int t) => Walkable(t.x, t.y);

        private float SpeedScale => 1 + Mathf.Min(_level - 1, 6) * 0.04f;

        public override void Step(ArcadeInput input)
        {
            _anim += Dt;
            if (input.UpPressed) _want = Up;
            else if (input.DownPressed) _want = Down;
            else if (input.LeftPressed) _want = Left;
            else if (input.RightPressed) _want = Right;
            if (_popupTimer > 0) _popupTimer -= Dt;

            switch (_phase)
            {
                case Phase.Ready:
                    if ((_phaseTimer -= Dt) <= 0) _phase = Phase.Play;
                    return;
                case Phase.Dying:
                    if ((_phaseTimer -= Dt) <= 0)
                    {
                        _lives--;
                        if (_lives <= 0) { Over = true; return; }
                        StartLife();
                    }
                    return;
                case Phase.Clear:
                    if ((_phaseTimer -= Dt) <= 0)
                    {
                        _level++;
                        FillMaze();
                        StartLife();
                    }
                    return;
            }

            if (_freeze > 0) { _freeze -= Dt; return; } // short pause after eating a ghost
            _lifeTime += Dt;

            if (_frightTimer > 0)
            {
                _frightTimer -= Dt;
                if (_frightTimer <= 0) foreach (var g in _ghosts) g.Frightened = false;
            }
            else
            {
                _waveTime += Dt;
                if (_waveTime >= Waves[_wave])
                {
                    _waveTime = 0;
                    _wave = Mathf.Min(_wave + 1, Waves.Length - 1);
                    foreach (var g in _ghosts) if (g.Mode == GhostMode.Normal) g.Reverse = true;
                }
            }

            MovePac();
            foreach (var g in _ghosts) MoveGhost(g);
            EatHere();
            CheckGhosts();

            if (_fruitTimer > 0)
            {
                _fruitTimer -= Dt;
                if ((_pac - FruitPos).sqrMagnitude < 36)
                {
                    int pts = Mathf.Min(100 * _level, 5000);
                    AddScore(pts);
                    Popup(FruitPos, pts);
                    _fruitTimer = 0;
                }
            }
        }

        private static readonly Vector2 FruitPos = new Vector2(13 * T + 4, 17 * T + 4);

        private void AddScore(int pts)
        {
            Score += pts;
            if (!_bonusLife && Score >= 10000) { _bonusLife = true; _lives++; }
        }

        private void Popup(Vector2 at, int pts)
        {
            _popupPos = at;
            _popupScore = pts;
            _popupTimer = 1f;
        }

        // ---- Movement on the tile grid ----

        private static float Frac(float v)
        {
            float m = (v - 4) / T;
            return m - Mathf.Floor(m);
        }

        private static float Snap(float v) => Mathf.Round((v - 4) / T) * T + 4;

        private delegate void CenterHandler();

        /// <summary>Moves along the grid, calling <paramref name="onCenter"/> at every tile center to pick a direction.</summary>
        private static void Advance(ref Vector2 pos, ref Vector2Int dir, float dist, CenterHandler onCenter, ref Vector2 refPos, ref Vector2Int refDir)
        {
            // refPos/refDir alias pos/dir for the handler (it reads and writes through the owner's fields).
            int guard = 0;
            while (dist > 0.0001f && guard++ < 8)
            {
                if (dir == Vector2Int.zero) return;
                float coord = dir.x != 0 ? pos.x : pos.y;
                float f = Frac(coord);
                if (f < 0.001f || f > 0.999f)
                {
                    if (dir.x != 0) pos.x = Snap(pos.x); else pos.y = Snap(pos.y);
                    refPos = pos; refDir = dir;
                    onCenter();
                    pos = refPos; dir = refDir;
                    if (dir == Vector2Int.zero) return;
                    // Turning: line up on the new axis.
                    if (dir.x != 0) pos.y = Snap(pos.y); else pos.x = Snap(pos.x);
                    float step = Mathf.Min(dist, T);
                    pos += (Vector2)dir * step;
                    dist -= step;
                }
                else
                {
                    float toNext = (dir.x + dir.y) > 0 ? (1 - f) * T : f * T;
                    if (dist < toNext) { pos += (Vector2)dir * dist; dist = 0; }
                    else { pos += (Vector2)dir * toNext; dist -= toNext; }
                }
                if (pos.x < -8) pos.x += 30 * T;
                else if (pos.x > Cols * T + 8) pos.x -= 30 * T;
            }
        }

        private Vector2 _tmpPos;
        private Vector2Int _tmpDir;

        private void MovePac()
        {
            if (_want == M.Neg(_dir) && _dir != Vector2Int.zero) _dir = _want; // reverse any time
            if (_dir == Vector2Int.zero)
            {
                var t = TileOf(_pac);
                if (Walkable(t + _want)) _dir = _want; else return;
            }
            float speed = 74 * SpeedScale * (_frightTimer > 0 ? 1.08f : 1f);
            Advance(ref _pac, ref _dir, speed * Dt, () =>
            {
                var t = TileOf(_tmpPos);
                if (_want != Vector2Int.zero && Walkable(t + _want)) _tmpDir = _want;
                else if (!Walkable(t + _tmpDir)) _tmpDir = Vector2Int.zero;
            }, ref _tmpPos, ref _tmpDir);
        }

        private void EatHere()
        {
            var t = TileOf(_pac);
            if (t.x < 0 || t.x >= Cols) return;
            char ch = _tiles[t.x, t.y];
            if (ch != '.' && ch != 'o') return;
            _tiles[t.x, t.y] = ' ';
            _eaten++;
            if (ch == '.') AddScore(10);
            else
            {
                AddScore(50);
                _frightTimer = Mathf.Max(1.5f, 7f - (_level - 1));
                _combo = 0;
                foreach (var g in _ghosts)
                {
                    if (g.Mode == GhostMode.Eyes || g.Mode == GhostMode.Entering) continue;
                    g.Frightened = true;
                    if (g.Mode == GhostMode.Normal) g.Reverse = true;
                }
            }
            if (_eaten == 70 || _eaten == 170) _fruitTimer = 9.5f;
            if (_eaten >= _pellets) { _phase = Phase.Clear; _phaseTimer = 2f; }
        }

        private void CheckGhosts()
        {
            foreach (var g in _ghosts)
            {
                if (g.Mode != GhostMode.Normal) continue;
                var d = g.Pos - _pac;
                if (Mathf.Abs(d.x) > 5 || Mathf.Abs(d.y) > 5) continue;
                if (g.Frightened)
                {
                    g.Frightened = false;
                    g.Mode = GhostMode.Eyes;
                    int pts = 200 << Mathf.Min(_combo, 3);
                    _combo++;
                    AddScore(pts);
                    Popup(g.Pos, pts);
                    _freeze = 0.45f;
                }
                else
                {
                    _phase = Phase.Dying;
                    _phaseTimer = 1.7f;
                    _anim = 0;
                    return;
                }
            }
        }

        private void MoveGhost(Ghost g)
        {
            float baseSpeed = 70 * SpeedScale;
            switch (g.Mode)
            {
                case GhostMode.House:
                    g.Pos.y += g.Bob * 24 * Dt;
                    if (g.Pos.y > HouseY + 3) g.Bob = -1;
                    if (g.Pos.y < HouseY - 3) g.Bob = 1;
                    g.Dir = g.Bob > 0 ? Down : Up;
                    if (_lifeTime >= g.ReleaseAt) g.Mode = GhostMode.Exiting;
                    return;
                case GhostMode.Exiting:
                {
                    float step = 40 * Dt;
                    if (Mathf.Abs(g.Pos.x - DoorX) > 0.01f)
                    {
                        g.Pos.x = Mathf.MoveTowards(g.Pos.x, DoorX, step);
                        g.Dir = g.Pos.x < DoorX ? Right : Left;
                    }
                    else
                    {
                        g.Pos.y = Mathf.MoveTowards(g.Pos.y, AboveDoorY, step);
                        g.Dir = Up;
                        if (Mathf.Abs(g.Pos.y - AboveDoorY) < 0.01f)
                        {
                            g.Pos = new Vector2(DoorX, AboveDoorY);
                            g.Mode = GhostMode.Normal;
                            g.Dir = Left;
                        }
                    }
                    return;
                }
                case GhostMode.Entering:
                    g.Pos.x = DoorX;
                    g.Pos.y = Mathf.MoveTowards(g.Pos.y, HouseY, 110 * Dt);
                    g.Dir = Down;
                    if (Mathf.Abs(g.Pos.y - HouseY) < 0.01f) g.Mode = GhostMode.Exiting;
                    return;
            }

            var tile = TileOf(g.Pos);
            float speed = g.Mode == GhostMode.Eyes ? 150
                : (tile.y == 14 && (tile.x <= 5 || tile.x >= 22)) ? 38
                : g.Frightened ? 42
                : baseSpeed;
            if (g.Reverse && g.Mode == GhostMode.Normal) { g.Dir = M.Neg(g.Dir); g.Reverse = false; }

            Advance(ref g.Pos, ref g.Dir, speed * Dt, () =>
            {
                var t = TileOf(_tmpPos);
                if (g.Mode == GhostMode.Eyes && t == HouseDoor)
                {
                    g.Mode = GhostMode.Entering;
                    _tmpPos = new Vector2(DoorX, AboveDoorY);
                    _tmpDir = Vector2Int.zero;
                    return;
                }
                _tmpDir = ChooseDir(g, t, _tmpDir);
            }, ref _tmpPos, ref _tmpDir);
        }

        private Vector2Int ChooseDir(Ghost g, Vector2Int t, Vector2Int current)
        {
            Vector2Int target = Target(g);
            Vector2Int best = Vector2Int.zero;
            float bestD = float.MaxValue;
            int options = 0;
            Vector2Int[] valid = new Vector2Int[4];
            foreach (var d in Order)
            {
                if (d == M.Neg(current)) continue;
                if (!Walkable(t + d)) continue;
                valid[options++] = d;
                var n = t + d;
                float dist = (n - target).sqrMagnitude;
                if (dist < bestD) { bestD = dist; best = d; }
            }
            if (options == 0) return M.Neg(current); // dead end
            if (g.Frightened && g.Mode == GhostMode.Normal) return valid[Rng.Next(options)];
            return best;
        }

        private Vector2Int Target(Ghost g)
        {
            if (g.Mode == GhostMode.Eyes) return HouseDoor;
            bool chase = _wave % 2 == 1;
            if (!chase) return g.Corner;
            var p = TileOf(_pac);
            var pd = _dir == Vector2Int.zero ? Left : _dir;
            if (g == _ghosts[0]) return p;
            if (g == _ghosts[1]) return p + M.Mul(pd, 4);
            if (g == _ghosts[2])
            {
                var ahead = p + M.Mul(pd, 2);
                var b = TileOf(_ghosts[0].Pos);
                return ahead + (ahead - b);
            }
            return (p - TileOf(g.Pos)).sqrMagnitude > 64 ? p : g.Corner;
        }

        // ---- Drawing ----

        private static readonly Color32 Black = new Color32(0, 0, 0, 255);
        private static readonly Color32 WallFill = PixelCanvas.Hex(0x0a0a3c), WallEdge = PixelCanvas.Hex(0x2b48ff);
        private static readonly Color32 Pellet = PixelCanvas.Hex(0xffc8a8), PacColor = PixelCanvas.Hex(0xffe23a);
        private static readonly Color32 Scared = PixelCanvas.Hex(0x2633ff), ScaredFlash = PixelCanvas.Hex(0xf2f2ff);

        private bool IsWall(int x, int y) => x < 0 || y < 0 || x >= Cols || y >= Rows || _tiles[x, y] == '#';

        public override void Draw(PixelCanvas c)
        {
            c.Clear(Black);
            bool flash = _phase == Phase.Clear && ((int)(_phaseTimer * 5) % 2 == 0);
            var edge = flash ? new Color32(255, 255, 255, 255) : WallEdge;
            for (int y = 0; y < Rows; y++)
                for (int x = 0; x < Cols; x++)
                {
                    char ch = _tiles[x, y];
                    int px = x * T, py = y * T;
                    if (ch == '#')
                    {
                        c.Rect(px, py, T, T, WallFill);
                        if (!IsWall(x, y - 1)) c.Rect(px, py, T, 1, edge);
                        if (!IsWall(x, y + 1)) c.Rect(px, py + T - 1, T, 1, edge);
                        if (!IsWall(x - 1, y)) c.Rect(px, py, 1, T, edge);
                        if (!IsWall(x + 1, y)) c.Rect(px + T - 1, py, 1, T, edge);
                    }
                    else if (ch == '-') c.Rect(px, py + 3, T, 2, PixelCanvas.Hex(0xffb8de));
                    else if (ch == '.') c.Rect(px + 3, py + 3, 2, 2, Pellet);
                    else if (ch == 'o' && ((int)(_anim * 4) % 2 == 0 || _phase != Phase.Play)) c.Circle(px + 4, py + 4, 3.2f, Pellet);
                }

            if (_fruitTimer > 0) DrawCherry(c, FruitPos.x, FruitPos.y);

            if (_phase != Phase.Clear)
            {
                if (_phase != Phase.Dying || _phaseTimer > 1.3f)
                    foreach (var g in _ghosts) DrawGhost(c, g);
                DrawPac(c);
            }
            else DrawPacShape(c, _pac.x, _pac.y, Vector2Int.right, 0);

            if (_popupTimer > 0) DrawNumber(c, _popupScore, _popupPos.x, _popupPos.y - 2, PixelCanvas.Hex(0x2ef0f0));

            for (int i = 0; i < _lives - 1; i++) DrawPacShape(c, 12 + i * 14, Rows * T - 4, Vector2Int.left, 0.6f, 4.5f);
        }

        private void DrawPac(PixelCanvas c)
        {
            if (_phase == Phase.Dying)
            {
                float t = Mathf.Clamp01((1.3f - _phaseTimer) / 1.1f);
                if (_phaseTimer > 1.3f) DrawPacShape(c, _pac.x, _pac.y, _dir == Vector2Int.zero ? Left : _dir, 0.4f);
                else if (t < 1) DrawPacShape(c, _pac.x, _pac.y, Up, Mathf.Lerp(0.2f, M.PI, t));
                return;
            }
            float mouth = _phase == Phase.Play && _dir != Vector2Int.zero ? Mathf.Abs(Mathf.Sin(_anim * 14)) * 0.85f : 0.45f;
            DrawPacShape(c, _pac.x, _pac.y, _dir == Vector2Int.zero ? Left : _dir, mouth);
        }

        private static void DrawPacShape(PixelCanvas c, float cx, float cy, Vector2Int dir, float mouth, float r = 6.5f)
        {
            float a0 = Mathf.Atan2(dir.y, dir.x);
            for (int y = Mathf.FloorToInt(cy - r); y <= Mathf.CeilToInt(cy + r); y++)
                for (int x = Mathf.FloorToInt(cx - r); x <= Mathf.CeilToInt(cx + r); x++)
                {
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                    if (dx * dx + dy * dy > r * r) continue;
                    float a = Mathf.Abs(Mathf.DeltaAngle(a0 * M.Rad2Deg, Mathf.Atan2(dy, dx) * M.Rad2Deg)) * M.Deg2Rad;
                    if (a < mouth) continue;
                    c.Px(x, y, PacColor);
                }
        }

        private void DrawGhost(PixelCanvas c, Ghost g)
        {
            float x = g.Pos.x, y = g.Pos.y;
            if (g.Mode != GhostMode.Eyes && g.Mode != GhostMode.Entering)
            {
                bool ending = g.Frightened && _frightTimer < 2f && ((int)(_anim * 8) % 2 == 0);
                var body = g.Frightened ? (ending ? ScaredFlash : Scared) : g.Color;
                c.Circle(x, y - 1, 6.5f, body);
                c.Rect(Mathf.RoundToInt(x - 6.5f), Mathf.RoundToInt(y - 1), 13, 6, body);
                int wave = (int)(_anim * 8) % 2;
                for (int i = 0; i < 4; i++) c.Rect(Mathf.RoundToInt(x - 6.5f) + i * 4 - wave + 1, Mathf.RoundToInt(y + 5), 2, 2, body);
                if (g.Frightened)
                {
                    var face = ending ? PixelCanvas.Hex(0xff3030) : PixelCanvas.Hex(0xffc8a8);
                    c.Rect(Mathf.RoundToInt(x - 3), Mathf.RoundToInt(y - 3), 2, 2, face);
                    c.Rect(Mathf.RoundToInt(x + 1), Mathf.RoundToInt(y - 3), 2, 2, face);
                    for (int i = 0; i < 5; i++) c.Px(Mathf.RoundToInt(x - 5 + i * 2 + 0.5f), Mathf.RoundToInt(y + 2 + (i % 2)), face);
                    return;
                }
            }
            for (int s = -1; s <= 1; s += 2)
            {
                float ex = x + s * 2.6f, ey = y - 2;
                c.Circle(ex, ey, 2.3f, new Color32(255, 255, 255, 255));
                c.Rect(Mathf.FloorToInt(ex + g.Dir.x * 1.2f - 0.5f), Mathf.FloorToInt(ey + g.Dir.y * 1.2f - 0.5f), 2, 2, PixelCanvas.Hex(0x1f2bd8));
            }
        }

        private static void DrawCherry(PixelCanvas c, float x, float y)
        {
            c.Circle(x - 2.5f, y + 2, 2.6f, PixelCanvas.Hex(0xff1a3c));
            c.Circle(x + 2.5f, y + 1, 2.6f, PixelCanvas.Hex(0xff1a3c));
            c.Px((int)x - 3, (int)y + 1, new Color32(255, 255, 255, 255));
            c.Px((int)x + 2, (int)y, new Color32(255, 255, 255, 255));
            for (int i = 0; i < 5; i++) { c.Px((int)x - 2 + i / 2, (int)y - 1 - i, PixelCanvas.Hex(0xb06a2a)); c.Px((int)x + 2 - i / 3, (int)y - 2 - i, PixelCanvas.Hex(0xb06a2a)); }
        }

        // Tiny 3×5 digits for the points that pop up when you eat something.
        private static readonly string[] Font = { "111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001", "111100111001111", "111100111101111", "111001001001001", "111101111101111", "111101111001111" };

        private static void DrawNumber(PixelCanvas c, int value, float cx, float cy, Color32 col)
        {
            string s = value.ToString();
            int x0 = Mathf.RoundToInt(cx - s.Length * 2), y0 = Mathf.RoundToInt(cy - 2);
            for (int i = 0; i < s.Length; i++)
            {
                string g = Font[s[i] - '0'];
                for (int p = 0; p < 15; p++)
                    if (g[p] == '1') c.Px(x0 + i * 4 + p % 3, y0 + p / 3, col);
            }
        }

        public override void DrawIcon(PixelCanvas c)
        {
            c.Clear(Black);
            float s = c.Width;
            for (int i = 0; i < 3; i++) c.Rect((int)(s * 0.55f + i * s * 0.14f), (int)(s * 0.5f) - 1, 3, 3, Pellet);
            float r = s * 0.27f;
            float a0 = 0;
            for (int y = 0; y < c.Height; y++)
                for (int x = 0; x < c.Width; x++)
                {
                    float dx = x + 0.5f - s * 0.33f, dy = y + 0.5f - s * 0.5f;
                    if (dx * dx + dy * dy > r * r) continue;
                    if (Mathf.Abs(Mathf.Atan2(dy, dx) - a0) < 0.7f) continue;
                    c.Px(x, y, PacColor);
                }
        }
    }
}
