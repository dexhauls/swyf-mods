using UnityEngine;

namespace Arcade.Games
{
    internal sealed class Pong : Game
    {
        private const int W = 240, H = 180, Win = 7;
        private const float PadW = 4, PadH = 30, PadX = 10, BallS = 4;

        public override string Id => "pong";
        public override string Name => "Pong";
        public override string Tagline => Lang.T("Porazíš počítač?", "Can you beat the computer?");
        public override string Controls => Lang.T("↑↓ / WS: pálka · P: pauza · hraje se do 7", "↑↓ / WS: paddle · P: pause · first to 7");
        public override int Width => W;
        public override int Height => H;
        public override string Status => Lang.T($"Ty {_me} : {_cpu} PC", $"You {_me} : {_cpu} CPU");
        public override string OverTitle => _me >= Win ? Lang.T("Vyhrál jsi!", "You win!") : Lang.T("Prohrál jsi", "You lose");
        public override string Banner => _serveTimer > 0 && !Over ? (_me + _cpu == 0 ? Lang.T("Hraje se do 7", "First to 7") : null) : null;

        private float _myY, _cpuY, _cpuAim;
        private Vector2 _ball, _vel;
        private int _me, _cpu, _rally;
        private float _serveTimer, _clock;

        protected override void Reset()
        {
            _me = _cpu = 0;
            _myY = _cpuY = (H - PadH) / 2;
            Serve(Rng.Next(2) == 0 ? 1 : -1);
        }

        private void Serve(int towards)
        {
            _ball = new Vector2(W / 2f, H / 2f);
            float a = M.Deg2Rad * (Rng.Next(-30, 31));
            _vel = new Vector2(Mathf.Cos(a) * towards, Mathf.Sin(a)) * 150;
            _serveTimer = 0.9f;
            _rally = 0;
        }

        public override void Step(ArcadeInput input)
        {
            _clock += Dt;
            float move = (input.Down ? 1 : 0) - (input.Up ? 1 : 0);
            _myY = Mathf.Clamp(_myY + move * 210 * Dt, 0, H - PadH);

            // The computer chases the ball with a capped speed and a slightly wrong aim, so it can be beaten.
            if (_vel.x > 0) _cpuAim = Mathf.MoveTowards(_cpuAim, _ball.y - PadH / 2 + Mathf.Sin(_clock * 1.7f) * 9, 400 * Dt);
            else _cpuAim = Mathf.MoveTowards(_cpuAim, (H - PadH) / 2, 120 * Dt);
            float cpuSpeed = 118 + Mathf.Min(_rally, 12) * 4;
            _cpuY = Mathf.Clamp(Mathf.MoveTowards(_cpuY, _cpuAim, cpuSpeed * Dt), 0, H - PadH);

            if (_serveTimer > 0) { _serveTimer -= Dt; return; }

            int steps = Mathf.CeilToInt(_vel.magnitude * Dt / 2f);
            for (int i = 0; i < steps && !Over; i++) Move(Dt / steps);
        }

        private void Move(float dt)
        {
            _ball += _vel * dt;
            if (_ball.y < BallS / 2) { _ball.y = BallS / 2; _vel.y = Mathf.Abs(_vel.y); }
            if (_ball.y > H - BallS / 2) { _ball.y = H - BallS / 2; _vel.y = -Mathf.Abs(_vel.y); }

            if (_vel.x < 0 && _ball.x - BallS / 2 <= PadX + PadW && _ball.x > PadX - 2 && Hit(_myY)) Bounce(_myY, 1);
            if (_vel.x > 0 && _ball.x + BallS / 2 >= W - PadX - PadW && _ball.x < W - PadX + 2 && Hit(_cpuY)) Bounce(_cpuY, -1);

            if (_ball.x < -6) { _cpu++; Point(1); }
            else if (_ball.x > W + 6) { _me++; Score += 100 + _rally * 10; Point(-1); }
        }

        private bool Hit(float padY) => _ball.y + BallS / 2 >= padY && _ball.y - BallS / 2 <= padY + PadH;

        private void Bounce(float padY, int dir)
        {
            float t = Mathf.Clamp((_ball.y - (padY + PadH / 2)) / (PadH / 2), -1, 1);
            float speed = Mathf.Min(_vel.magnitude * 1.06f, 330);
            float a = t * 55 * M.Deg2Rad;
            _vel = new Vector2(Mathf.Cos(a) * dir, Mathf.Sin(a)) * speed;
            _rally++;
        }

        private void Point(int towards)
        {
            if (_me >= Win || _cpu >= Win) { Over = true; return; }
            Serve(towards);
        }

        private static readonly Color32 Bg = PixelCanvas.Hex(0x0b0f1c), Fg = PixelCanvas.Hex(0xe8ecff);

        public override void Draw(PixelCanvas c)
        {
            c.Clear(Bg);
            for (int y = 4; y < H; y += 12) c.Rect(W / 2 - 1, y, 2, 6, PixelCanvas.Hex(0x2a3256));
            Digits(c, _me, W / 2 - 30, 10);
            Digits(c, _cpu, W / 2 + 18, 10);
            c.Rect((int)PadX, Mathf.RoundToInt(_myY), (int)PadW, (int)PadH, PixelCanvas.Hex(0x7bd4ff));
            c.Rect((int)(W - PadX - PadW), Mathf.RoundToInt(_cpuY), (int)PadW, (int)PadH, PixelCanvas.Hex(0xff8a7a));
            if (_serveTimer <= 0 || ((int)(_serveTimer * 6) % 2 == 0))
                c.Rect(Mathf.RoundToInt(_ball.x - BallS / 2), Mathf.RoundToInt(_ball.y - BallS / 2), (int)BallS, (int)BallS, Fg);
        }

        // Big 3×5 digits for the score, like the arcade original.
        private static readonly string[] Font = { "111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001", "111100111001111", "111100111101111", "111001001001001", "111101111101111", "111101111001111" };

        private static void Digits(PixelCanvas c, int value, int x, int y)
        {
            string s = value.ToString();
            for (int i = 0; i < s.Length; i++)
            {
                string g = Font[s[i] - '0'];
                for (int p = 0; p < 15; p++)
                    if (g[p] == '1') c.Rect(x + i * 16 + (p % 3) * 4, y + (p / 3) * 4, 4, 4, PixelCanvas.Hex(0x3a4474));
            }
        }

        public override void DrawIcon(PixelCanvas c)
        {
            c.Clear(Bg);
            int s = c.Width;
            for (int y = 2; y < s; y += 6) c.Rect(s / 2 - 1, y, 2, 3, PixelCanvas.Hex(0x2a3256));
            c.Rect(s / 8, s / 4, 3, s / 3, PixelCanvas.Hex(0x7bd4ff));
            c.Rect(s - s / 8 - 3, s / 2, 3, s / 3, PixelCanvas.Hex(0xff8a7a));
            c.Rect(s * 5 / 8, s * 3 / 8, 3, 3, Fg);
        }
    }
}
