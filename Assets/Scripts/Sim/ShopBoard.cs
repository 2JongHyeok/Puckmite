using System.Collections.Generic;

namespace Puckmite.Sim
{
    /// <summary>The four things a purchased cell can raise (design doc 5.5).</summary>
    public enum UpgradeKind
    {
        Attack,
        Shield,
        RunHeal,
        MaxHealth,
    }

    /// <summary>One cell of the upgrade board. Level 0 means empty — a stone resting there does nothing.</summary>
    public struct ShopCell
    {
        public UpgradeKind Kind;
        public int Level;

        public bool IsEmpty => Level <= 0;
    }

    /// <summary>What a placement did, so the caller can warn before destroying levels (design doc 5.1).</summary>
    public enum ShopPlacement
    {
        Placed,   // the cell was empty
        Upgraded, // same kind again: its level went up
        Replaced, // a different kind: the old cell and all its levels are gone
    }

    /// <summary>Totals a settlement produced, one per upgrade kind.</summary>
    public struct UpgradeTotals
    {
        public int Attack;
        public int Shield;
        public int RunHeal;
        public int MaxHealth;

        public int Of(UpgradeKind kind)
        {
            switch (kind)
            {
                case UpgradeKind.Attack: return Attack;
                case UpgradeKind.Shield: return Shield;
                case UpgradeKind.RunHeal: return RunHeal;
                default: return MaxHealth;
            }
        }
    }

    /// <summary>
    /// The persistent upgrade board (design doc 5.1): a 5x5 grid the player fills with purchased cells,
    /// kept across runs and stages. It has no damage cells and starts entirely empty. Pure state and rules —
    /// the same geometry <see cref="BoardCells"/> uses decides which cells a stone occupies, but which
    /// effect a cell carries lives here rather than in a fixed layout, because the player builds it.
    /// </summary>
    public sealed class ShopBoard
    {
        private readonly ShopCell[] _cells = new ShopCell[BoardCells.Size * BoardCells.Size];

        // Reused so a settlement allocates nothing.
        private readonly List<int> _occupied = new List<int>();

        public ShopCell CellAt(int col, int row)
        {
            return _cells[col + row * BoardCells.Size];
        }

        /// <summary>What placing this kind here would do, without doing it — a different kind wipes the
        /// cell's accumulated levels, which the player has to confirm first (design doc 5.1).</summary>
        public ShopPlacement Preview(int col, int row, UpgradeKind kind)
        {
            ShopCell cell = CellAt(col, row);
            if (cell.IsEmpty)
            {
                return ShopPlacement.Placed;
            }

            return cell.Kind == kind ? ShopPlacement.Upgraded : ShopPlacement.Replaced;
        }

        /// <summary>Puts a bought cell down. Cells cannot be moved afterwards, only stacked on or replaced.</summary>
        public ShopPlacement Place(int col, int row, UpgradeKind kind)
        {
            ShopPlacement result = Preview(col, row, kind);
            int index = col + row * BoardCells.Size;

            _cells[index] = new ShopCell
            {
                Kind = kind,
                Level = result == ShopPlacement.Upgraded ? _cells[index].Level + 1 : 1,
            };

            return result;
        }

        public void Clear()
        {
            for (int i = 0; i < _cells.Length; i++)
            {
                _cells[i] = default;
            }
        }

        /// <summary>
        /// Adds up what the stones standing on the board are worth (design doc 5.2: the final board state is
        /// what pays out). Every cell a stone overlaps counts in full, and each is worth the cell's level
        /// times the stone's, so levelling a shop stone on the walls is what makes a cell pay more.
        /// </summary>
        public UpgradeTotals SumUpgrades(PuckSim sim, float threshold)
        {
            UpgradeTotals totals = default;

            IReadOnlyList<Puck> pucks = sim.Pucks;
            for (int i = 0; i < pucks.Count; i++)
            {
                Puck p = pucks[i];
                BoardCells.GetOccupiedCells(sim.BoardMin, sim.BoardMax, p.Position, p.Radius, threshold, _occupied);

                for (int c = 0; c < _occupied.Count; c++)
                {
                    ShopCell cell = _cells[_occupied[c]];
                    if (cell.IsEmpty)
                    {
                        continue; // an empty cell does nothing (design doc 5.1)
                    }

                    int gain = cell.Level * p.Level;
                    switch (cell.Kind)
                    {
                        case UpgradeKind.Attack: totals.Attack += gain; break;
                        case UpgradeKind.Shield: totals.Shield += gain; break;
                        case UpgradeKind.RunHeal: totals.RunHeal += gain; break;
                        default: totals.MaxHealth += gain; break;
                    }
                }
            }

            return totals;
        }
    }
}
