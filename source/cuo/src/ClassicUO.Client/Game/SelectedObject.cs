// SPDX-License-Identifier: BSD-2-Clause

using System.Runtime.CompilerServices;
using ClassicUO.Game.GameObjects;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game
{
    internal static class SelectedObject
    {
        public static Point TranslatedMousePositionByViewport;
        public static BaseGameObject Object;
        public static BaseGameObject LastLeftDownObject;
        public static Entity HealthbarObject;
        public static Item SelectedContainer;
        public static Item CorpseObject;

        private static readonly bool[,] _InternalArea = new bool[44, 44];

        static SelectedObject()
        {
            for (int y = 21, i = 0; y >= 0; --y, i++)
            {
                for (int x = 0; x < 22; x++)
                {
                    if (x < i)
                    {
                        continue;
                    }

                    _InternalArea[x, y] = _InternalArea[43 - x, 43 - y] = _InternalArea[43 - x, y] = _InternalArea[x, 43 - y] = true;
                }
            }
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsPointInLand(int x, int y)
        {
            x = TranslatedMousePositionByViewport.X - x;
            y = TranslatedMousePositionByViewport.Y - y;

            return x >= 0 && x < 44 && y >= 0 && y < 44 && _InternalArea[x, y];
        }

        public static bool IsPointInStretchedLand(ref UltimaBatcher2D.YOffsets yOffsets, int x, int y)
        {
            //y -= 22;
            x += 22;

            int testX = TranslatedMousePositionByViewport.X - x;
            int testY = TranslatedMousePositionByViewport.Y;

            int y0 = -yOffsets.Top;
            int y1 = 22 - yOffsets.Left;
            int y2 = 44 - yOffsets.Bottom;
            int y3 = 22 - yOffsets.Right;

            // The four edge tests below were originally written as
            //   testY >= testX * (dy) / -22 + ...
            // using C# integer division, which truncates toward zero. The
            // edge line is therefore rasterised as a staircase, and a tile's
            // bottom edge (testX negative -> one truncation direction) does
            // NOT line up with the tile below it whose top edge has testX
            // positive (opposite truncation). The 1-px mismatch leaves a thin
            // seam — most visible at the bottom corner — where the cursor
            // hit-tests no land at all.
            //
            // Cross-multiplying every inequality by 22 (a positive constant,
            // so the comparison direction is preserved) removes the division
            // entirely. The tests become exact: a pixel exactly on a shared
            // edge satisfies BOTH adjacent tiles, an overlap instead of a gap,
            // so the cursor always lands on some tile.
            int dyTop = testY - y - y0;
            int dyBot = testY - y - y2;

            return testX * (y1 - y0) >= -22 * dyTop
                && 22 * dyTop >= testX * (y3 - y0)
                && 22 * dyBot <= testX * (y3 - y2)
                && -22 * dyBot >= testX * (y1 - y2);
        }
    }
}