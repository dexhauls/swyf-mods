using System.Collections.Generic;
using UnityEngine;

namespace Arcade.Games
{
    internal sealed class Snake : Game
    {
        private const int N = 20, Cell = 12;

        public override string Id => "snake";
        public override string Name => Lang.T("Had", "Snake");
        public override string Tagline => Lang.T("Žer jablka a nenaraz", "Eat apples, don't crash");
        public override string Controls => Lang.T("Šipky / WASD: zatáčení · P: pauza", "Arrows / WASD: steer · P: pause");
        public override int Width => N * Cell;
        public override int Height => N * Cell;
        public override string Status => Lang.T($"Délka {_body.Count}", $"Length {_body.Count}");

        private readonly LinkedList<Vector2Int> _body = new LinkedList<Vector2Int>();
        private readonly Queue<Vector2Int> _turns = new Queue<Vector2Int>();
        private Vector2Int _dir, _food;
        private float _timer;
        private int _grow;
        private float _anim;

        protected override void Reset()
        {
            _body.Clear();
            _turns.Clear();
            for (int i = 0; i < 4; i++) _body.AddLast(new Vector2Int(9 - i, 10));
            _dir = Vector2Int.right;
            _grow = 0;
            _timer = 0;
            PlaceFood();
        }

        private float Interval => Mathf.Max(0.055f, 0.13f - _body.Count * 0.0025f);

        public override void Step(ArcadeInput input)
        {
            _anim += Dt;
            Vector2Int last = _turns.Count > 0 ? LastTurn() : _dir;
            if (input.UpPressed) Queue(last, Vector2Int.down);       // screen y grows downwards
            else if (input.DownPressed) Queue(last, Vector2Int.up);
            else if (input.LeftPressed) Queue(last, Vector2Int.left);
            else if (input.RightPressed) Queue(last, Vector2Int.right);

            _timer += Dt;
            if (_timer < Interval) return;
            _timer = 0;
            if (_turns.Count > 0) _dir = _turns.Dequeue();

            var head = _body.First.Value + _dir;
            if (head.x < 0 || head.y < 0 || head.x >= N || head.y >= N || Hits(head)) { Over = true; return; }
            _body.AddFirst(head);
            if (head == _food)
            {
                Score += 10;
                _grow += 2;
                PlaceFood();
            }
            if (_grow > 0) _grow--; else _body.RemoveLast();
        }

        private Vector2Int LastTurn()
        {
            Vector2Int v = _dir;
            foreach (var t in _turns) v = t;
            return v;
        }

        private void Queue(Vector2Int current, Vector2Int next)
        {
            if (next == current || next == M.Neg(current) || _turns.Count >= 2) return;
            _turns.Enqueue(next);
        }

        private bool Hits(Vector2Int p)
        {
            var node = _body.First;
            while (node != null)
            {
                // The tail moves away this step unless we're growing.
                if (node.Value == p && !(node == _body.Last && _grow == 0)) return true;
                node = node.Next;
            }
            return false;
        }

        private void PlaceFood()
        {
            var free = new List<Vector2Int>();
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                    if (!_body.Contains(new Vector2Int(x, y))) free.Add(new Vector2Int(x, y));
            _food = free.Count > 0 ? free[Rng.Next(free.Count)] : new Vector2Int(-1, -1);
        }

        private static readonly Color32 Bg1 = PixelCanvas.Hex(0x14301f), Bg2 = PixelCanvas.Hex(0x183824);
        private static readonly Color32 Body = PixelCanvas.Hex(0x5fd35a), BodyDark = PixelCanvas.Hex(0x3aa83f), HeadC = PixelCanvas.Hex(0x9cf27a);

        public override void Draw(PixelCanvas c)
        {
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                    c.Rect(x * Cell, y * Cell, Cell, Cell, (x + y) % 2 == 0 ? Bg1 : Bg2);

            if (_food.x >= 0) DrawApple(c, _food.x * Cell + Cell / 2f, _food.y * Cell + Cell / 2f + 0.5f, 4.6f + Mathf.Sin(_anim * 6) * 0.4f);

            int i = 0;
            foreach (var p in _body)
            {
                var col = i == 0 ? HeadC : (i % 2 == 0 ? Body : BodyDark);
                c.Rect(p.x * Cell + 1, p.y * Cell + 1, Cell - 2, Cell - 2, col);
                i++;
            }
            // Eyes on the head, looking where it goes.
            var h = _body.First.Value;
            float cx = h.x * Cell + Cell / 2f, cy = h.y * Cell + Cell / 2f;
            var side = new Vector2(-_dir.y, _dir.x);
            for (int s = -1; s <= 1; s += 2)
            {
                float ex = cx + _dir.x * 2 + side.x * 2.6f * s, ey = cy + _dir.y * 2 + side.y * 2.6f * s;
                c.Circle(ex, ey, 1.8f, new Color32(255, 255, 255, 255));
                c.Px(Mathf.FloorToInt(ex + _dir.x * 0.6f), Mathf.FloorToInt(ey + _dir.y * 0.6f), new Color32(10, 20, 10, 255));
            }
        }

        private static void DrawApple(PixelCanvas c, float x, float y, float r)
        {
            c.Circle(x, y, r, PixelCanvas.Hex(0xe8413b));
            c.Circle(x - r * 0.35f, y - r * 0.35f, r * 0.3f, PixelCanvas.Hex(0xff8a7a));
            c.Rect(Mathf.RoundToInt(x), Mathf.RoundToInt(y - r - 2), 1, 3, PixelCanvas.Hex(0x6b3e1f));
            c.Rect(Mathf.RoundToInt(x + 1), Mathf.RoundToInt(y - r - 2), 2, 1, PixelCanvas.Hex(0x58c447));
        }

        public override void DrawIcon(PixelCanvas c)
        {
            c.Clear(Bg1);
            int s = c.Width / 8;
            int[,] path = { { 1, 5 }, { 2, 5 }, { 3, 5 }, { 3, 4 }, { 3, 3 }, { 4, 3 }, { 5, 3 } };
            for (int i = 0; i < path.GetLength(0); i++)
                c.Rect(path[i, 0] * s, path[i, 1] * s, s - 1, s - 1, i == path.GetLength(0) - 1 ? HeadC : Body);
            DrawApple(c, 6.5f * s, 5.5f * s, s * 0.45f);
        }
    }
}
