using System;

namespace Arcade.Games
{
    /// <summary>
    /// One game. The view runs <see cref="Step"/> at a fixed 60 Hz while playing and calls <see cref="Draw"/>
    /// once per frame. A game ends itself by setting <see cref="Over"/>.
    /// </summary>
    internal abstract class Game
    {
        public const float Dt = 1f / 60f;

        public abstract string Id { get; }         // key for the best score
        public abstract string Name { get; }
        public abstract string Tagline { get; }    // one line on the menu tile
        public abstract string Controls { get; }   // hint under the game
        public abstract int Width { get; }
        public abstract int Height { get; }

        public int Score;
        public bool Over;

        /// <summary>Extra HUD text, e.g. lives and level.</summary>
        public virtual string Status => null;

        /// <summary>Text over the playfield while playing (e.g. "Připrav se!"), null = none.</summary>
        public virtual string Banner => null;

        /// <summary>Title of the game-over screen.</summary>
        public virtual string OverTitle => Lang.T("Konec hry", "Game over");

        protected readonly Random Rng = new Random();

        public void Restart()
        {
            Score = 0;
            Over = false;
            Reset();
        }

        protected abstract void Reset();
        public abstract void Step(ArcadeInput input);
        public abstract void Draw(PixelCanvas c);

        /// <summary>Small picture for the menu tile (drawn once).</summary>
        public abstract void DrawIcon(PixelCanvas c);
    }
}
