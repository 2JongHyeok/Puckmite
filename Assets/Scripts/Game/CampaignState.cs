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
        // Progression: 2 stages of 5 runs (사용자 지정 2026-08-09 — 3에서 축소, 2스테이지 보스가 마지막).
        public const int StageCount = 2;
        public const int RunsPerStage = 5;

        /// <summary>What a run spawns: the fixed tutorial slime, a random kind (1-2 draws without the
        /// slime), or the stage's boss.</summary>
        public enum RunMonsters
        {
            Slime,
            RandomExceptSlime,
            Random,
            Boss,
        }

        /// <summary>One row of the difficulty table: what spawns and how many, the stats every monster of
        /// the run shares (kinds shift them view-side), and the stones each one fields.</summary>
        public readonly struct RunSpec
        {
            public readonly RunMonsters Monsters;
            public readonly int EnemyCount;
            public readonly int EnemyHealth;
            public readonly int EnemyShield;
            public readonly int EnemyAttack;
            public readonly int StonesPerEnemy;

            public RunSpec(RunMonsters monsters, int enemyCount, int enemyHealth, int enemyShield,
                int enemyAttack, int stonesPerEnemy)
            {
                Monsters = monsters;
                EnemyCount = enemyCount;
                EnemyHealth = enemyHealth;
                EnemyShield = enemyShield;
                EnemyAttack = enemyAttack;
                StonesPerEnemy = stonesPerEnemy;
            }
        }

        // The difficulty table (사용자 지정 2026-08-10): 몬스터 / 체력 / 쉴드 / 공격력 / 스톤 수 per run.
        private static readonly RunSpec[][] StageRunSpecs =
        {
            new[]
            {
                new RunSpec(RunMonsters.Slime,             1,  20,  0, 1, 1), // 1-1
                new RunSpec(RunMonsters.RandomExceptSlime, 1,  20,  0, 2, 1), // 1-2
                new RunSpec(RunMonsters.Random,            2,  20,  0, 2, 1), // 1-3
                new RunSpec(RunMonsters.Random,            3,  20,  0, 2, 1), // 1-4
                new RunSpec(RunMonsters.Boss,              1, 100, 30, 10, 2), // 1-5 (사용자 지정 2026-08-10: 100/30/10)
            },
            new[]
            {
                new RunSpec(RunMonsters.Random,            2,  20, 10, 3, 2), // 2-1
                new RunSpec(RunMonsters.Random,            2,  20, 20, 3, 2), // 2-2
                new RunSpec(RunMonsters.Random,            3,  20, 10, 3, 2), // 2-3
                new RunSpec(RunMonsters.Random,            3,  30, 10, 4, 3), // 2-4
                new RunSpec(RunMonsters.Boss,              1, 100, 30, 5, 4), // 2-5
            },
        };

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
        // rest of the campaign. Separate from shop stones (design doc 5.4).
        public int ExtraBattleStones;

        // Shop stones bought with 스톤 추가하기 (사용자 지정 2026-08-10): each buy permanently raises the
        // stones every later shop visit starts with — and the buy price itself — until a defeat wipes it.
        public int ExtraShopStones;

        public readonly ShopBoard ShopBoard = new ShopBoard();

        public bool IsBossRun => Run == RunsPerStage;

        public RunSpec CurrentRunSpec
        {
            get
            {
                int stage = Stage - 1;
                if (stage < 0)
                {
                    stage = 0;
                }
                else if (stage >= StageRunSpecs.Length)
                {
                    stage = StageRunSpecs.Length - 1;
                }

                RunSpec[] specs = StageRunSpecs[stage];
                int run = Run - 1;
                if (run < 0)
                {
                    run = 0;
                }
                else if (run >= specs.Length)
                {
                    run = specs.Length - 1;
                }

                return specs[run];
            }
        }

        public int EnemyCountForRun => CurrentRunSpec.EnemyCount;

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
            ExtraShopStones = 0;
            ShopBoard.Clear();
        }
    }
}
