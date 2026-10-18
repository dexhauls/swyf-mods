using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Arcade;
using Arcade.Games;

internal static class Program
{
    private static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "frames";
        Directory.CreateDirectory(outDir);
        var rng = new Random(1);
        int failures = 0;
        Game[] games = { new Pacman(), new Snake(), new Tetris(), new Breakout(), new Pong() };
        foreach (var g in games)
        {
            try
            {
                var c = new PixelCanvas(g.Width, g.Height);
                var icon = new PixelCanvas(32, 32);
                g.DrawIcon(icon);
                Save(icon, Path.Combine(outDir, g.Id + "-icon.png"), 4);

                int overs = 0;
                var sw = Stopwatch.StartNew();
                long steps = 0;
                for (int round = 0; round < 3; round++)
                {
                    g.Restart();
                    var input = new ArcadeInput();
                    for (int i = 0; i < 60 * 120 && !g.Over; i++, steps++)
                    {
                        Bot(g, input, rng, i);
                        g.Step(input);
                        input.ConsumePresses();
                        if (round == 0 && (i == 30 || i == 60 * 4 || i == 60 * 12 || i == 60 * 30)) Shot(g, c, outDir, i);
                    }
                    if (g.Over) overs++;
                    Shot(g, c, outDir, -1 - round);
                }
                double us = sw.Elapsed.TotalMilliseconds * 1000 / steps;
                Console.WriteLine($"{g.Name,-10} OK  steps={steps} games over={overs}/3 last score={g.Score} status=\"{g.Status}\" {us:F1} µs/step");
            }
            catch (Exception e)
            {
                failures++;
                Console.WriteLine($"{g.Name,-10} FAIL {e}");
            }
        }
        return failures;
    }

    // A simple scripted player: random direction changes plus the game's action key now and then.
    private static void Bot(Game g, ArcadeInput input, Random rng, int i)
    {
        input.Left = input.Right = input.Up = input.Down = false;
        if (i % 20 == 0)
        {
            int d = rng.Next(4);
            input.LeftPressed = d == 0; input.RightPressed = d == 1; input.UpPressed = d == 2; input.DownPressed = d == 3;
        }
        int hold = (i / 25) % 4;
        input.Left = hold == 0; input.Right = hold == 1;
        if (g is Pong) { input.Up = hold == 2; input.Down = hold == 3; }
        if (g is Breakout || g is Tetris) input.Action = i % 90 == 45;
    }

    private static void Shot(Game g, PixelCanvas c, string dir, int step)
    {
        g.Draw(c);
        string name = step >= 0 ? $"{g.Id}-{step / 60.0:F1}s.png" : $"{g.Id}-end{-step}.png";
        Save(c, Path.Combine(dir, name), 2);
    }

    private static void Save(PixelCanvas c, string path, int scale)
    {
        using (var bmp = new Bitmap(c.Width * scale, c.Height * scale, PixelFormat.Format32bppArgb))
        {
            var px = c.Pixels;
            for (int y = 0; y < c.Height; y++)
                for (int x = 0; x < c.Width; x++)
                {
                    var p = px[(c.Height - 1 - y) * c.Width + x];
                    var col = Color.FromArgb(255, p.r, p.g, p.b);
                    for (int sy = 0; sy < scale; sy++)
                        for (int sx = 0; sx < scale; sx++) bmp.SetPixel(x * scale + sx, y * scale + sy, col);
                }
            bmp.Save(path, ImageFormat.Png);
        }
    }
}
