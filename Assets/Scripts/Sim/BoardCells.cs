using System.Collections.Generic;
using Vector2 = UnityEngine.Vector2;

namespace Puckmite.Sim
{
    /// <summary>Buff cells make up the inner 3x3; the outer ring deals damage (design doc 3.1).</summary>
    public enum CellType
    {
        Buff,
        Damage,
    }

    /// <summary>
    /// Pure-logic 5x5 board layout and cell-occupancy test. A puck occupies a cell when its circle
    /// penetrates that cell rectangle by at least a threshold (design doc 3.2, temp 0.3). Vector2 is the
    /// only UnityEngine dependency, so this is deterministic and headless-testable. Cells are addressed by
    /// the linear index col + row * Size, with col/row in 0..Size-1 (origin at boardMin).
    /// </summary>
    public static class BoardCells
    {
        public const int Size = 5;

        private const int InnerMin = 1; // inner 3x3 (buff) spans cols/rows 1..3
        private const int InnerMax = 3;

        public static CellType TypeOf(int col, int row)
        {
            bool inner = col >= InnerMin && col <= InnerMax && row >= InnerMin && row <= InnerMax;
            return inner ? CellType.Buff : CellType.Damage;
        }

        public static Vector2 CellSize(Vector2 boardMin, Vector2 boardMax)
        {
            return new Vector2((boardMax.x - boardMin.x) / Size, (boardMax.y - boardMin.y) / Size);
        }

        public static Vector2 CellCenter(Vector2 boardMin, Vector2 boardMax, int col, int row)
        {
            Vector2 size = CellSize(boardMin, boardMax);
            return new Vector2(
                boardMin.x + (col + 0.5f) * size.x,
                boardMin.y + (row + 0.5f) * size.y);
        }

        /// <summary>
        /// Fills <paramref name="outCells"/> with the linear index of every cell the puck occupies — a cell
        /// whose rectangle the puck's circle penetrates by at least <paramref name="threshold"/>. The list is
        /// cleared first; reuse one list to avoid per-call allocation.
        /// </summary>
        public static void GetOccupiedCells(Vector2 boardMin, Vector2 boardMax, Vector2 puckPosition, float puckRadius, float threshold, List<int> outCells)
        {
            outCells.Clear();
            Vector2 size = CellSize(boardMin, boardMax);

            for (int row = 0; row < Size; row++)
            {
                float minY = boardMin.y + row * size.y;
                float maxY = minY + size.y;
                for (int col = 0; col < Size; col++)
                {
                    float minX = boardMin.x + col * size.x;
                    float maxX = minX + size.x;

                    // Closest point on the cell rectangle to the puck centre; penetration = radius - distance.
                    float nearestX = puckPosition.x < minX ? minX : (puckPosition.x > maxX ? maxX : puckPosition.x);
                    float nearestY = puckPosition.y < minY ? minY : (puckPosition.y > maxY ? maxY : puckPosition.y);

                    float distance = new Vector2(puckPosition.x - nearestX, puckPosition.y - nearestY).magnitude;
                    if (puckRadius - distance >= threshold)
                    {
                        outCells.Add(col + row * Size);
                    }
                }
            }
        }
    }
}
