using Puckmite.Sim;

namespace Puckmite.Game
{
    /// <summary>
    /// Everything that outlives a single scene: progression, gold, the upgrade board and the stats bought
    /// on it, and the health the player carries between runs (design doc 2.1/5). Pure C# so a scene load
    /// cannot touch it — the battle and shop controllers read and write it through
    /// <see cref="GameFlow.Campaign"/>.
    /// </summary>
    public sealed class CampaignState
    {
        // Progression (design doc 2.1): 3 stages of 5 runs, enemy counts fixed per run.
        public const int StageCount = 3;
        public static readonly int[] RunEnemyCounts = { 1, 1, 2, 3, 1 }; // last is the boss run (boss itself: step 14)

        public int Stage = 1;
        public int Run = 1;

        // Two separate values on purpose: restarting the current run must give back what the player walked
        // in with, not the healed total earned by clearing it — otherwise the run-end heal can be re-earned.
        public int RunStartHealth;  // health the player entered the CURRENT run with (0 = full)
        public int NextRunHealth;   // healed total to carry into the NEXT run, set when a run is cleared

        // The campaign's purse and purchases (design doc 5.5/5.6): the upgrade board, the stats bought on
        // it and the gold all persist until a defeat wipes them.
        public int Gold;
        public int BonusAttack;
        public int BonusShield;
        public int BonusRunHeal;
        public int BonusMaxHealth;

        // Battle stones bought in shops (design doc 5.6): one more roster stone from the next run, for the
        // rest of the campaign. Completely separate from per-visit shop stones (design doc 5.4).
        public int ExtraBattleStones;

        public readonly ShopBoard ShopBoard = new ShopBoard();

        public bool IsBossRun => Run == RunEnemyCounts.Length;

        public int EnemyCountForRun
        {
            get
            {
                int index = Run - 1;
                if (index < 0)
                {
                    index = 0;
                }
                else if (index >= RunEnemyCounts.Length)
                {
                    index = RunEnemyCounts.Length - 1;
                }

                return RunEnemyCounts[index];
            }
        }

        /// <summary>Moves to the next run (or the next stage); the healed total earned by clearing the last
        /// one becomes what the next starts from. Called when the shop is left (design doc 2.1).</summary>
        public void AdvanceRun()
        {
            if (IsBossRun)
            {
                Stage++;
                Run = 1;
            }
            else
            {
                Run++;
            }

            RunStartHealth = NextRunHealth;
        }

        /// <summary>No continue, no permanent unlocks (design doc 2.1): a defeat resets the whole campaign —
        /// the upgrade board, the stats bought on it and the gold all go back to the start.</summary>
        public void Reset()
        {
            Stage = 1;
            Run = 1;
            RunStartHealth = 0;
            NextRunHealth = 0;
            Gold = 0;
            BonusAttack = 0;
            BonusShield = 0;
            BonusRunHeal = 0;
            BonusMaxHealth = 0;
            ExtraBattleStones = 0;
            ShopBoard.Clear();
        }
    }
}
