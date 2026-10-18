using UnityEngine;

namespace Arcade.Games
{
    /// <summary>
    /// Bits of Unity math the game's stripped UnityEngine no longer has (Mathf constants, some Vector2Int operators).
    /// </summary>
    internal static class M
    {
        public const float PI = 3.14159265f;
        public const float Deg2Rad = PI / 180f;
        public const float Rad2Deg = 180f / PI;

        public static Vector2Int Neg(Vector2Int v) => new Vector2Int(-v.x, -v.y);
        public static Vector2Int Mul(Vector2Int v, int k) => new Vector2Int(v.x * k, v.y * k);
    }
}
