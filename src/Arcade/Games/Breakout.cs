using UnityEngine;

namespace Arcade.Games
{
    internal sealed class Breakout : Game
    {
        private const int W = 240, H = 280, BrickCols = 10, BrickRows = 6, BrickW = 22, BrickH = 8, BrickTop = 34;
        private const float PaddleY = 262, PaddleH = 6, BallR = 3;

        public override string Id => "breakout";
        public override string Name => Lang.T("Cihličky", "Breakout");
        public override string Tagline => Lang.T("Rozbij všechny cihly", "Smash all the bricks");
        public override string Controls => Lang.T("←→ / AD: pálka · Mezerník: odpálit míček · P: pauza", "←→ / AD: paddle · Space: launch ball · P: pause");
        public override int Width => W;
        public override int Height => H;
        public override string Status => Lang.T($"Životy {_lives} · Kolo {_level}", $"Lives {_lives} · Level {_level}");
        public override string Banner => _stuck && !Over ? Lang.T("Mezerník = odpálit", "Space = launch") : null;

        private static readonly Color32[] RowColors =
        {
            PixelCanvas.Hex(0xec4a4a), PixelCanvas.Hex(0xf29a2e), PixelCanvas.Hex(0xf4d23c),
            PixelCanvas.Hex(0x4fd65a), PixelCanvas.Hex(0x31b8e8), PixelCanvas.Hex(0x7b5cf0),
        };

        private readonly bool[,] _bricks = new bool[BrickCols, BrickRows];
        private int _left, _lives, _level;
        private float _paddleX, _paddleW;
        private Vector2 _ball, _vel;
        private bool _stuck;
        private float _speed;

        protected override void Reset()
        {
            _lives = 3;
            _level = 1;
            NewLevel();
        }

        private void NewLevel()
        {
            for (int x = 0; x < BrickCols; x++)
                for (int y = 0; y < BrickRows; y++) _bricks[x, y] = true;
            _left = BrickCols * BrickRows;
            _paddleW = Mathf.Max(28, 44 - (_level - 1) * 3);
            _paddleX = (W - _paddleW) / 2;
            _speed = 150 + (_level - 1) * 18;
            Serve();
        }

        private void Serve()
        {
            _stuck = true;
            _ball = new Vector2(_paddleX + _paddleW / 2, PaddleY - BallR - 1);
            _vel = Vector2.zero;
        }

        public override void Step(ArcadeInput input)
        {
            float move = (input.Right ? 1 : 0) - (input.Left ? 1 : 0);
            _paddleX = Mathf.Clamp(_paddleX + move * 230 * Dt, 0, W - _paddleW);

            if (_stuck)
            {
                _ball = new Vector2(_paddleX + _paddleW / 2, PaddleY - BallR - 1);
                if (input.Action || input.UpPressed)
                {
                    _stuck = false;
                    float a = M.Deg2Rad * (Rng.Next(2) == 0 ? -60 : -120);
                    _vel = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * _speed;
                }
                return;
            }

            // Sub-steps so the ball never skips through a brick.
            int steps = Mathf.CeilToInt(_vel.magnitude * Dt / 2f);
            float dt = Dt / steps;
            for (int i = 0; i < steps && !_stuck && !Over; i++) Move(dt);
        }

        private void Move(float dt)
        {
            _ball += _vel * dt;
            if (_ball.x < BallR) { _ball.x = BallR; _vel.x = Mathf.Abs(_vel.x); }
            if (_ball.x > W - BallR) { _ball.x = W - BallR; _vel.x = -Mathf.Abs(_vel.x); }
            if (_ball.y < BallR) { _ball.y = BallR; _vel.y = Mathf.Abs(_vel.y); }

            // Paddle: the bounce angle depends on where the ball hits it.
            if (_vel.y > 0 && _ball.y + BallR >= PaddleY && _ball.y + BallR <= PaddleY + PaddleH + 4 &&
                _ball.x >= _paddleX - BallR && _ball.x <= _paddleX + _paddleW + BallR)
            {
                float t = Mathf.Clamp((_ball.x - _paddleX) / _paddleW * 2 - 1, -1, 1);
                float a = M.Deg2Rad * (-90 + t * 62);
                _speed = Mathf.Min(_speed * 1.015f, 340);
                _vel = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * _speed;
                _ball.y = PaddleY - BallR;
            }

            if (_ball.y > H + BallR)
            {
                _lives--;
                if (_lives <= 0) { Over = true; return; }
                Serve();
                return;
            }

            int cx = Mathf.FloorToInt(_ball.x / (BrickW + 2)), cy = Mathf.FloorToInt((_ball.y - BrickTop) / (BrickH + 2));
            for (int by = cy - 1; by <= cy + 1; by++)
                for (int bx = cx - 1; bx <= cx + 1; bx++)
                {
                    if (bx < 0 || by < 0 || bx >= BrickCols || by >= BrickRows || !_bricks[bx, by]) continue;
                    float x0 = bx * (BrickW + 2) + 1, y0 = BrickTop + by * (BrickH + 2);
                    float nx = Mathf.Clamp(_ball.x, x0, x0 + BrickW), ny = Mathf.Clamp(_ball.y, y0, y0 + BrickH);
                    float dx = _ball.x - nx, dy = _ball.y - ny;
                    if (dx * dx + dy * dy > BallR * BallR) continue;
                    _bricks[bx, by] = false;
                    _left--;
                    Score += (BrickRows - by) * 10;
                    if (Mathf.Abs(dx) > Mathf.Abs(dy)) _vel.x = -_vel.x; else _vel.y = -_vel.y;
                    if (_left == 0) { _level++; NewLevel(); }
                    return;
                }
        }

        private static readonly Color32 Bg = PixelCanvas.Hex(0x0e1226);

        public override void Draw(PixelCanvas c)
        {
            c.Clear(Bg);
            for (int y = 0; y < H; y += 16) c.Rect(0, y, W, 1, PixelCanvas.Hex(0x121733));
            for (int bx = 0; bx < BrickCols; bx++)
                for (int by = 0; by < BrickRows; by++)
                    if (_bricks[bx, by]) c.Block(bx * (BrickW + 2) + 1, BrickTop + by * (BrickH + 2), BrickW, BrickH, RowColors[by]);
            c.Block(Mathf.RoundToInt(_paddleX), (int)PaddleY, Mathf.RoundToInt(_paddleW), (int)PaddleH, PixelCanvas.Hex(0xd8dcf0));
            c.Circle(_ball.x, _ball.y, BallR, new Color32(255, 255, 255, 255));
            for (int i = 0; i < _lives - 1; i++) c.Circle(8 + i * 9, 14, 3, PixelCanvas.Hex(0x8a90b8));
        }

        public override void DrawIcon(PixelCanvas c)
        {
            c.Clear(Bg);
            int s = c.Width / 6;
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 3; x++)
                    if (!(y == 2 && x == 1)) c.Block(x * s * 2 + 1, s / 2 + y * (s / 2 + 2) + 4, s * 2 - 2, s / 2, RowColors[y * 2]);
            c.Block(s * 2, c.Height - s, s * 2, s / 3 + 1, PixelCanvas.Hex(0xd8dcf0));
            c.Circle(c.Width * 0.62f, c.Height * 0.62f, s * 0.3f, new Color32(255, 255, 255, 255));
        }
    }
}
