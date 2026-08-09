using System;

namespace Puckmite.Sim
{
    /// <summary>
    /// Which buff (if any) each inner cell of the 5x5 board carries this run. Immutable once built, so
    /// clones share one instance and the background AI search can read it concurrently. The outer damage
    /// ring stays positional (<see cref="BoardCells.TypeOf"/>) and is never part of the layout. The
    /// battle board is re-rolled every run (사용자 지정 2026-08-10): the VIEW supplies the rng, and given
    /// it the generator is pure, so verification can sweep seeds.
    /// </summary>
    public sealed class BoardLayout
    {
        // Per-cell, linear index col + row * Size. Level 0 = no buff (an empty inner cell, or the outer
        // ring); the kind is meaningful only where the level is positive.
        private readonly BuffKind[] _kinds = new BuffKind[BoardCells.Size * BoardCells.Size];
        private readonly int[] _levels = new int[BoardCells.Size * BoardCells.Size];

        public const int MinBuffCells = 7; // 사용자 지정: 내부 9칸 중 7칸 이상은 버프칸
        public const int MaxBuffCells = 9;
        public const int MinPerKind = 2;   // 사용자 지정: 공격칸·쉴드칸 각각 2칸 이상
        public const int CentreLevel = 2;  // 사용자 지정: 정 가운데 칸은 항상 lv2

        private BoardLayout()
        {
        }

        public bool IsBuff(int col, int row)
        {
            return _levels[col + row * BoardCells.Size] > 0;
        }

        public BuffKind KindOf(int col, int row)
        {
            return _kinds[col + row * BoardCells.Size];
        }

        /// <summary>Buff points the cell grants per stone level — its level (centre 2, others 1), or 0
        /// off the buff cells.</summary>
        public int ValueOf(int col, int row)
        {
            return _levels[col + row * BoardCells.Size];
        }

        /// <summary>The fixed pre-randomisation board: every inner cell a buff, checkerboard
        /// attack/shield (col+row even = attack), centre worth 2. The shop sim and headless tests keep
        /// this as the default.</summary>
        public static readonly BoardLayout Classic = BuildClassic();

        private static BoardLayout BuildClassic()
        {
            BoardLayout layout = new BoardLayout();
            for (int row = 1; row <= 3; row++)
            {
                for (int col = 1; col <= 3; col++)
                {
                    int index = col + row * BoardCells.Size;
                    layout._kinds[index] = ((col + row) & 1) == 0 ? BuffKind.Attack : BuffKind.Shield;
                    layout._levels[index] = col == 2 && row == 2 ? CentreLevel : 1;
                }
            }

            return layout;
        }

        /// <summary>
        /// Rolls a run's board (사용자 지정 2026-08-10): the centre is always a lv2 buff of random kind,
        /// 7~9 of the inner 9 are buffs in total (the rest empty), every non-centre buff is lv1, and each
        /// kind fields at least two cells. Pure given the rng — the same seed rebuilds the same board.
        /// </summary>
        public static BoardLayout Roll(Random rng)
        {
            BoardLayout layout = new BoardLayout();

            // The eight non-centre inner cells, shuffled; the first count-1 of them join the centre.
            int[] cells = new int[8];
            int n = 0;
            for (int row = 1; row <= 3; row++)
            {
                for (int col = 1; col <= 3; col++)
                {
                    if (col != 2 || row != 2)
                    {
                        cells[n++] = col + row * BoardCells.Size;
                    }
                }
            }

            Shuffle(cells, rng);

            int count = rng.Next(MinBuffCells, MaxBuffCells + 1);

            // A bag of kinds guaranteeing the per-kind minimum, the rest random, then shuffled so the
            // guaranteed ones land anywhere (the centre included).
            BuffKind[] kinds = new BuffKind[count];
            for (int i = 0; i < count; i++)
            {
                kinds[i] = i < MinPerKind ? BuffKind.Attack
                    : i < MinPerKind * 2 ? BuffKind.Shield
                    : rng.Next(2) == 0 ? BuffKind.Attack : BuffKind.Shield;
            }

            Shuffle(kinds, rng);

            int centre = 2 + 2 * BoardCells.Size;
            layout._kinds[centre] = kinds[0];
            layout._levels[centre] = CentreLevel;
            for (int i = 1; i < count; i++)
            {
                layout._kinds[cells[i - 1]] = kinds[i];
                layout._levels[cells[i - 1]] = 1;
            }

            return layout;
        }

        private static void Shuffle<T>(T[] items, Random rng)
        {
            for (int i = items.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (items[i], items[j]) = (items[j], items[i]);
            }
        }
    }
}
