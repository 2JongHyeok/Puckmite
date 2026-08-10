using UnityEngine;

namespace Puckmite.View
{
    /// <summary>
    /// Every tunable number, shared by the battle and shop scenes through one asset so the two can never
    /// drift apart (the old playtest kept these serialized per scene object, and the scene's copy silently
    /// beat the code defaults). The debug panel's sliders write straight into it at runtime. All values are
    /// temporary placeholders until the balance pass (design doc 7.8 step 16, 10.1).
    /// </summary>
    public sealed class GameTuning : ScriptableObject
    {
        [Header("Physics — temporary placeholders, tune by feel (values are 미정 in the design doc)")]
        public float Friction = 10f;             // constant deceleration, units/s^2
        public float Restitution = 1f;           // puck-to-puck bounciness
        public float CollisionSpeedKept = 0.7f;  // speed kept after a puck-puck impact; 1 = no loss
        public float RestThreshold = 0.4f;
        public float WallRestitution = 0.6f;     // reflected speed kept after a wall bounce
        public float MaxPower = 50f;             // speed cap on a launch
        public float PowerScale = 6f;            // drag distance (world units) -> launch speed
        public float PowerCurve = 1f;            // drag->power exponent; 1 = linear
        public float PuckRadius = 1.5f;          // design doc: diameter 3 on a 5-wide cell
        public int StoneHealth = 3;              // base stone health, player and enemy alike (사용자 지정 2026-08)
        public int CellDamage = 1;               // damage-cell settlement amount (문서 미정, 임시)

        [Header("Character stats — 사용자 지정 기준선 (2026-08-10); 적·보스 스탯은 CampaignState의 난이도 표")]
        public int PlayerBaseHealth = 20;
        public int PlayerBaseAttack = 1;
        public int PlayerBaseShield = 0;

        [Header("Progression — 임시 (런 종료 회복량은 10.1에서 미정)")]
        public int RunEndHeal = 5;       // health restored after clearing a run (design doc 2.1)
        public int PlayerStoneCount = 2; // design doc 3.3: the player starts with 2

        [Header("Shop — 임시 (가격·강화량은 10.1에서 미정)")]
        public int GoldPerKill = 10;      // gold for each enemy taken down (design doc 5.6)
        public int GoldPerBossKill = 30;  // the boss pays its own figure (사용자 지정 2026-08-10)
        public int ShopStonesPerVisit = 1; // stones granted each visit (design doc 5.4)
        public int PriceAttack = 5;
        public int PriceShield = 5;
        public int PriceRunHeal = 5;
        public int PriceMaxHealth = 10;
        public int GainAttack = 1;    // stat gained per point of settled Attack
        public int GainShield = 1;    // 사용자 지정 2026-08-10: 상점 강화량 전부 1
        public int GainRunHeal = 2;   // 사용자 지정 2026-08-10: 레벨당 2 (lv1~4 = 2/4/6/8)
        public int GainMaxHealth = 1;
        public int ShopStonePrice = 15;        // extra shop stone, permanent from this visit on (사용자 지정 2026-08-10)
        public int ShopStonePriceStep = 15;    // added per stone already bought — the price climbs for the campaign
        public int RerollBasePrice = 5;        // every reroll, flat (사용자 지정 2026-08-10: 가격 상승 없음)
        // 상점 진열 확률 (사용자 지정 2026-08-10): 공격 10%·쉴드 30%·최대체력 30%·회복 25%·전투 스톤
        // 5%. The draw normalises by their sum, so live tuning cannot break it.
        [Range(0f, 1f)] public float OfferAttackChance = 0.10f;
        [Range(0f, 1f)] public float OfferShieldChance = 0.30f;
        [Range(0f, 1f)] public float OfferMaxHealthChance = 0.30f;
        [Range(0f, 1f)] public float OfferRunHealChance = 0.25f;
        [Range(0f, 1f)] public float OfferBattleStoneChance = 0.05f;
        public int BattleStonePrice = 25;      // battle stone: +1 roster stone until defeat (design doc 5.6)

        [Header("New stone entry")]
        public float NoStoneTurnDelay = 0.8f; // pause so a stoneless turn is readable

        [Header("Enemy AI — placeholder policy (enemy AI is 미정, design doc 10.2); weights tune the shot scoring")]
        public bool EnemyAiEnabled = true;
        public int AiDifficulty = 2;           // 0 아주쉬움 .. 4 매우어려움 (see the preset tables)
        public float EnemyThinkDelay = 0.8f;   // pause before the AI fires, so the turn is readable
        public float AiBuffAttackWeight = 1f;  // per point of attack buff the shot ends on
        public float AiBuffShieldWeight = 1f;
        public float AiDamageWeight = 3f;      // per health point dealt to player stones
        public float AiDestroyBonus = 2f;      // per player stone destroyed
        public float AiOwnDamageWeight = 2f;   // per health point its own side loses
        public float AiDamageCellPenalty = 1.5f; // per future settlement health point (scaled by cell damage)
    }
}
