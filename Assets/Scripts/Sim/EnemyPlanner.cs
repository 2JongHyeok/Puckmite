using System;
using System.Collections.Generic;
using Vector2 = UnityEngine.Vector2;

namespace Puckmite.Sim
{
    /// <summary>Tunable weights for scoring a candidate shot's final state. All are "per point": buffs per
    /// buff point gained, damage per health point, cells per damage cell an own stone rests on.</summary>
    public struct EnemyPlanWeights
    {
        public float BuffAttack;
        public float BuffShield;
        public float BuffHeal;        // set per plan by the view: 0 at full health, above shield otherwise (사용자 지정 2026-08-10)
        public float DamageDealt;     // health removed from opposing stones
        public float StoneDestroyed;  // bonus per opposing stone destroyed
        public float OwnDamage;       // health lost by own stones (subtracted)
        public float OwnOnDamageCell; // damage cells own stones end on — future settlement loss (subtracted)
    }

    /// <summary>Search settings — the difficulty knobs.</summary>
    public struct EnemyPlanConfig
    {
        public int CueDirections;   // full circle around a board stone
        public int EntryDirections; // fan into the board from an entry spot
        public IReadOnlyList<float> PowerFractions;

        /// <summary>True: exact prediction, cascades included. False: player-equivalent prediction — like
        /// the aiming preview, a struck stone vanishes from the roll-out, so where it would have scattered
        /// to is unknown; only the contact itself (its damage) is anticipated (design doc 8.4/6.2).</summary>
        public bool FullRollout;

        /// <summary>Where in the score ranking the pick lands: 0 = the best shot, 0.5 = the middle one.
        /// Deliberate imperfection for lower difficulties, still fully deterministic.</summary>
        public float PickRank;

        /// <summary>저격형 (design doc 4.3): player stone ids this shot should hit FIRST, best target
        /// first (the caller sorts by lowest health). Null or empty = ordinary planning. The bonuses
        /// dwarf the normal weights, so the ranking becomes: first-contact the top target, else
        /// first-contact any listed stone (better-ranked preferred), else touch as many listed stones as
        /// possible — "맞출 수 있는 스톤 탐색, 안 되면 최대한 맞추도록". The cue is armed in every
        /// roll-out so the 2-damage first hit is predicted too.</summary>
        public IReadOnlyList<int> SnipePriority;
    }

    /// <summary>The shot the planner picked.</summary>
    public struct EnemyPlan
    {
        public bool UseNewStone;
        public int StoneId;           // cue shot: the board stone to hit; new stone: the entering stone's Id
        public Vector2 EntryPosition; // new stone only
        public Vector2 Velocity;
        public float Score;
        public int CandidatesEvaluated;
    }

    /// <summary>
    /// Deterministic enemy shot search (design doc 7.9: the AI rolls the same sim headlessly, many times).
    /// It tries a fixed grid of candidates — every own board stone fanned in a full circle, every offered
    /// entry spot fanned into the board — at a few power levels, predicts each one's outcome, scores it,
    /// and picks by rank (the best, or deliberately worse on lower difficulties). No randomness: the same
    /// state and inputs always pick the same shot (ties resolved by candidate order). The scoring policy is
    /// a placeholder — design doc 10.2 leaves enemy AI 미정 — with the weights supplied by the caller.
    /// </summary>
    public static class EnemyPlanner
    {
        private const float EntryFanHalfAngle = 80f * (MathF.PI / 180f); // fan spread around straight-inward
        private const int RolloutMaxSteps = 2000;

        // Snipe bonuses (design doc 4.3), far above anything the normal weights can produce so the tiers
        // never mix: first-contacting the top target beats first-contacting a lesser one, which beats
        // merely brushing listed stones later in the roll-out.
        private const float SnipeFirstContactBonus = 100000f;
        private const float SnipeRankStep = 1000f;    // subtracted per priority rank below the top
        private const float SnipeTouchBonus = 100f;   // per listed stone the cue contacts at all

        private struct Candidate
        {
            public EnemyPlan Plan;
            public int Index; // enumeration order, the deterministic tie-break
        }

        // Reused buffers: the occupied-cells query while scoring, the candidate list per plan, and the
        // stones already struck within one predicted roll-out.
        private static readonly List<int> _cells = new List<int>();
        private static readonly List<Candidate> _candidates = new List<Candidate>();
        private static readonly List<int> _struckIds = new List<int>();

        // Descending score; enumeration order breaks ties, so the ordering is total and List.Sort being
        // unstable cannot make the outcome depend on its internals.
        private static readonly Comparison<Candidate> ByScoreDesc = (a, b) =>
            a.Plan.Score != b.Plan.Score ? b.Plan.Score.CompareTo(a.Plan.Score) : a.Index.CompareTo(b.Index);

        /// <summary>
        /// Enumerates and scores every candidate shot for the acting side, then picks by the config's rank.
        /// Cue shots come from <paramref name="ownStoneIds"/> (its stones on the board); a new stone is
        /// tried from each of <paramref name="entrySpots"/> when <paramref name="hasNewStone"/> is set,
        /// using <paramref name="newStone"/> as the template (Id, radius, mass, owner, health — position
        /// and velocity are overwritten; ignored entirely otherwise). <paramref name="actingOwner"/> is the
        /// side being played: its losses score down, the other side's score up. False when there is nothing
        /// to roll at all.
        /// </summary>
        public static bool TryPlan(
            PuckSim sim,
            PuckOwner actingOwner,
            IReadOnlyList<int> ownStoneIds,
            bool hasNewStone,
            Puck newStone,
            IReadOnlyList<Vector2> entrySpots,
            float maxPower,
            float occupancyThreshold,
            EnemyPlanWeights weights,
            EnemyPlanConfig config,
            out EnemyPlan plan)
        {
            _candidates.Clear();

            // Cue shots: full circle around each own board stone.
            for (int s = 0; s < ownStoneIds.Count; s++)
            {
                int id = ownStoneIds[s];
                if (!sim.TryGetPuck(id, out Puck cue))
                {
                    continue;
                }

                for (int d = 0; d < config.CueDirections; d++)
                {
                    float angle = 2f * MathF.PI * d / config.CueDirections;
                    Vector2 direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                    for (int p = 0; p < config.PowerFractions.Count; p++)
                    {
                        Vector2 velocity = direction * (maxPower * config.PowerFractions[p]);
                        PuckSim clone = sim.Clone();
                        clone.SetVelocity(id, velocity);
                        if (HasSnipePriority(config))
                        {
                            clone.SetSniperArmed(id, true); // predict the armed 2-damage first hit too
                        }

                        float score = config.FullRollout
                            ? ScoreFullRollout(clone, sim, actingOwner, ownStoneIds, id, -1, newStone, occupancyThreshold, weights, config)
                            : ScorePredictedRollout(clone, sim, actingOwner, ownStoneIds, id, cue.Health, false, occupancyThreshold, weights, config);
                        AddCandidate(new EnemyPlan { UseNewStone = false, StoneId = id, Velocity = velocity, Score = score });
                    }
                }
            }

            // New stone: fan into the board from each free entry spot (design doc 3.4 — it starts on the edge).
            if (hasNewStone)
            {
                float boardCentreX = (sim.BoardMin.x + sim.BoardMax.x) * 0.5f;
                for (int e = 0; e < entrySpots.Count; e++)
                {
                    Vector2 spot = entrySpots[e];
                    float inwardX = spot.x > boardCentreX ? -1f : 1f;
                    for (int d = 0; d < config.EntryDirections; d++)
                    {
                        float angle = -EntryFanHalfAngle + 2f * EntryFanHalfAngle * d / (config.EntryDirections - 1);
                        Vector2 direction = new Vector2(inwardX * MathF.Cos(angle), MathF.Sin(angle));
                        for (int p = 0; p < config.PowerFractions.Count; p++)
                        {
                            Vector2 velocity = direction * (maxPower * config.PowerFractions[p]);
                            PuckSim clone = sim.Clone();
                            Puck stone = newStone;
                            stone.Position = spot;
                            stone.Velocity = velocity;
                            if (HasSnipePriority(config))
                            {
                                stone.SniperArmed = true; // predict the armed 2-damage first hit too
                            }
                            clone.AddPuck(stone);

                            float score = config.FullRollout
                                ? ScoreFullRollout(clone, sim, actingOwner, ownStoneIds, newStone.Id, newStone.Id, newStone, occupancyThreshold, weights, config)
                                : ScorePredictedRollout(clone, sim, actingOwner, ownStoneIds, newStone.Id, newStone.Health, true, occupancyThreshold, weights, config);
                            AddCandidate(new EnemyPlan
                            {
                                UseNewStone = true,
                                StoneId = newStone.Id,
                                EntryPosition = spot,
                                Velocity = velocity,
                                Score = score,
                            });
                        }
                    }
                }
            }

            if (_candidates.Count == 0)
            {
                plan = default;
                return false;
            }

            _candidates.Sort(ByScoreDesc);
            int pick = (int)MathF.Round(config.PickRank * (_candidates.Count - 1));
            pick = Math.Clamp(pick, 0, _candidates.Count - 1);

            plan = _candidates[pick].Plan;
            plan.CandidatesEvaluated = _candidates.Count;
            return true;
        }

        private static void AddCandidate(EnemyPlan candidate)
        {
            _candidates.Add(new Candidate { Plan = candidate, Index = _candidates.Count });
        }

        private static bool HasSnipePriority(EnemyPlanConfig config)
        {
            return config.SnipePriority != null && config.SnipePriority.Count > 0;
        }

        // The snipe tiers (design doc 4.3), computed from the cue's first contact and every stone it
        // touched during the roll-out. Zero when sniping is off.
        private static float SnipeScore(EnemyPlanConfig config, int firstContactId, List<int> struckIds)
        {
            if (!HasSnipePriority(config))
            {
                return 0f;
            }

            float score = 0f;
            IReadOnlyList<int> priority = config.SnipePriority;
            for (int rank = 0; rank < priority.Count; rank++)
            {
                if (priority[rank] == firstContactId)
                {
                    score += SnipeFirstContactBonus - SnipeRankStep * rank;
                    break;
                }
            }

            for (int rank = 0; rank < priority.Count; rank++)
            {
                if (struckIds.Contains(priority[rank]))
                {
                    score += SnipeTouchBonus;
                }
            }

            return score;
        }

        // Exact prediction: steps the clone to rest, cascades and all, and scores its final state against
        // the pre-shot sim — own buffs gained, damage dealt to the opposing team, own losses, and own
        // stones parked on damage cells. cueId is the stone being launched; launchedId is the entering
        // stone's Id (health accounting), or -1 for a cue shot. The cue's contacts are tracked for the
        // snipe tiers.
        private static float ScoreFullRollout(
            PuckSim clone,
            PuckSim before,
            PuckOwner actingOwner,
            IReadOnlyList<int> ownStoneIds,
            int cueId,
            int launchedId,
            Puck newStone,
            float occupancyThreshold,
            EnemyPlanWeights weights,
            EnemyPlanConfig config)
        {
            _struckIds.Clear();
            int firstContactId = -1;

            // A bomb cue is built to be spent: its own death must not count as a loss, or every detonating
            // shot ranks below parking in a corner and the bomber never bombs (검증에서 확인된 결함).
            bool bombCue = clone.TryGetPuck(cueId, out Puck cueStartPuck) && cueStartPuck.Trait == StoneTrait.Bomb;

            for (int step = 0; step < RolloutMaxSteps && !clone.AllAtRest(); step++)
            {
                IReadOnlyList<PuckSimEvent> events = clone.Step();
                for (int i = 0; i < events.Count; i++)
                {
                    PuckSimEvent e = events[i];
                    if (e.Type != PuckSimEventType.PuckCollision)
                    {
                        continue;
                    }

                    int other;
                    if (e.PuckA == cueId)
                    {
                        other = e.PuckB;
                    }
                    else if (e.PuckB == cueId)
                    {
                        other = e.PuckA;
                    }
                    else
                    {
                        continue;
                    }

                    if (firstContactId < 0)
                    {
                        firstContactId = other;
                    }

                    if (!_struckIds.Contains(other))
                    {
                        _struckIds.Add(other);
                    }
                }
            }

            float score = SnipeScore(config, firstContactId, _struckIds)
                + ScoreOwnPlacement(clone, ownStoneIds, launchedId, occupancyThreshold, weights)
                - AnchorCentralPenalty(clone, cueId);

            // Health deltas, compared against the pre-shot state. Opposing losses score up; own losses
            // (including the launched stone, which started at the template's health) score down — except
            // a bomb cue, whose death is the point (see bombCue above).
            IReadOnlyList<Puck> pucksBefore = before.Pucks;
            for (int i = 0; i < pucksBefore.Count; i++)
            {
                Puck was = pucksBefore[i];
                if (bombCue && was.Id == cueId)
                {
                    continue;
                }

                int healthAfter = clone.TryGetPuck(was.Id, out Puck now) ? now.Health : 0;
                int lost = was.Health - healthAfter;
                if (was.Owner != actingOwner)
                {
                    score += weights.DamageDealt * lost;
                    if (healthAfter <= 0)
                    {
                        score += weights.StoneDestroyed;
                    }
                }
                else
                {
                    score -= weights.OwnDamage * lost;
                }
            }

            if (launchedId >= 0 && !bombCue)
            {
                int launchedAfter = clone.TryGetPuck(launchedId, out Puck launched) ? launched.Health : 0;
                score -= weights.OwnDamage * (newStone.Health - launchedAfter);
            }

            return score;
        }

        // Player-equivalent prediction, mirroring the aiming preview (design doc 6.2/8.4): any stone the
        // moving cue strikes is dropped from the roll-out, so where it scatters to — and everything it
        // would have knocked in turn — is unknown. Only the contact itself is anticipated: one health of
        // damage on a cross-team hit (a kill when the target sat at 1), nothing on a same-team hit. The
        // cue's own path, landing spot and losses stay exactly predicted, like the preview line.
        private static float ScorePredictedRollout(
            PuckSim clone,
            PuckSim before,
            PuckOwner actingOwner,
            IReadOnlyList<int> ownStoneIds,
            int cueId,
            int cueStartHealth,
            bool cueIsNewStone,
            float occupancyThreshold,
            EnemyPlanWeights weights,
            EnemyPlanConfig config)
        {
            float score = 0f;
            _struckIds.Clear();
            int firstContactId = -1;
            bool sniperSpent = false; // the armed 2-damage hit goes to the first OPPOSING contact, like the sim's disarm

            // A bomb cue is built to be spent: its own death must not count as a loss, or every detonating
            // shot ranks below parking in a corner and the bomber never bombs (검증에서 확인된 결함).
            bool bombCue = clone.TryGetPuck(cueId, out Puck cueStartPuck) && cueStartPuck.Trait == StoneTrait.Bomb;

            for (int step = 0; step < RolloutMaxSteps && !clone.AllAtRest(); step++)
            {
                IReadOnlyList<PuckSimEvent> events = clone.Step();
                for (int i = 0; i < events.Count; i++)
                {
                    PuckSimEvent e = events[i];
                    if (e.Type != PuckSimEventType.PuckCollision)
                    {
                        continue;
                    }

                    int other;
                    if (e.PuckA == cueId)
                    {
                        other = e.PuckB;
                    }
                    else if (e.PuckB == cueId)
                    {
                        other = e.PuckA;
                    }
                    else
                    {
                        continue;
                    }

                    // One Step can report the same stone twice (substeps: struck flush against a wall, it can
                    // rebound into the cue before the step ends), and only the first contact is part of the
                    // prediction. Tracked here, not via RemovePuck's return value — a stone killed by the
                    // contact is often already gone at step end, which would discard the legitimate first hit.
                    if (_struckIds.Contains(other))
                    {
                        continue;
                    }

                    _struckIds.Add(other);

                    // The contact is foreseeable even though the scatter is not (pre-shot health is
                    // visible). An armed sniper's first opposing contact deals 2, and a bomb's direct
                    // victim takes the blast damage (design doc 4.3).
                    if (before.TryGetPuck(other, out Puck hit) && hit.Owner != actingOwner)
                    {
                        int contactDamage = 1;
                        if (bombCue)
                        {
                            contactDamage = PuckSim.BombDamage;
                        }
                        else if (!sniperSpent && HasSnipePriority(config))
                        {
                            contactDamage = 2;
                        }

                        sniperSpent = true;
                        score += weights.DamageDealt * contactDamage;
                        if (hit.Health <= contactDamage)
                        {
                            score += weights.StoneDestroyed;
                        }
                    }

                    if (firstContactId < 0)
                    {
                        firstContactId = other;
                    }

                    clone.RemovePuck(other);
                }

                if (!clone.TryGetPuck(cueId, out Puck _))
                {
                    break; // the cue itself died en route — also foreseeable
                }
            }

            score += SnipeScore(config, firstContactId, _struckIds);
            score += ScoreOwnPlacement(clone, ownStoneIds, cueIsNewStone ? cueId : -1, occupancyThreshold, weights);
            score -= AnchorCentralPenalty(clone, cueId);

            // The only own loss the player could predict is the cue's: one health per cross-team contact,
            // already applied inside the clone. Struck own stones vanished — their fate is unknown, unscored.
            // A spent bomb is not a loss (see bombCue above).
            if (!bombCue)
            {
                int cueHealthAfter = clone.TryGetPuck(cueId, out Puck cueAfter) ? cueAfter.Health : 0;
                score -= weights.OwnDamage * (cueStartHealth - cueHealthAfter);
            }

            return score;
        }

        // 반석형 중앙 지향 (사용자 지정 2026-08-10): a rolled anchor aims for the board centre or an edge
        // midpoint, never a corner — an anchor plowing a stone into a corner wall-pins it. The pull is a
        // per-unit-distance penalty that dwarfs the placement terms, so central parks always outrank
        // corner shots, in both prediction modes. Zero for every other cue (or a cue that died en route).
        private const float AnchorCentralPull = 4f;

        private static float AnchorCentralPenalty(PuckSim clone, int cueId)
        {
            if (!clone.TryGetPuck(cueId, out Puck cue) || cue.Trait != StoneTrait.Anchor)
            {
                return 0f;
            }

            Vector2 min = clone.BoardMin;
            Vector2 max = clone.BoardMax;
            Vector2 centre = (min + max) * 0.5f;
            float inset = cue.Radius;

            float best = (cue.Position - centre).magnitude;
            float d = (cue.Position - new Vector2(centre.x, max.y - inset)).magnitude;
            if (d < best) { best = d; }
            d = (cue.Position - new Vector2(centre.x, min.y + inset)).magnitude;
            if (d < best) { best = d; }
            d = (cue.Position - new Vector2(min.x + inset, centre.y)).magnitude;
            if (d < best) { best = d; }
            d = (cue.Position - new Vector2(max.x - inset, centre.y)).magnitude;
            if (d < best) { best = d; }
            return AnchorCentralPull * best;
        }

        // Buffs the acting side's surviving stones would capture where they now rest (cellValue * level,
        // like the turn-end snapshot), minus the damage cells they sit on (paid at the next own turn start).
        // extraId is the entering stone when it is not already part of ownStoneIds, else -1.
        private static float ScoreOwnPlacement(
            PuckSim clone,
            IReadOnlyList<int> ownStoneIds,
            int extraId,
            float occupancyThreshold,
            EnemyPlanWeights weights)
        {
            float score = 0f;
            for (int i = 0; i < ownStoneIds.Count + 1; i++)
            {
                int id = i < ownStoneIds.Count ? ownStoneIds[i] : extraId;
                if (id < 0 || !clone.TryGetPuck(id, out Puck p))
                {
                    continue;
                }

                BoardCells.SumBuffs(clone.Layout, clone.BoardMin, clone.BoardMax, p.Position, p.Radius, occupancyThreshold, out int attack, out int shield, out int heal);
                score += weights.BuffAttack * attack * p.Level;
                score += weights.BuffShield * shield * p.Level;
                score += weights.BuffHeal * heal * p.Level;

                BoardCells.GetOccupiedCells(clone.BoardMin, clone.BoardMax, p.Position, p.Radius, occupancyThreshold, _cells);
                for (int c = 0; c < _cells.Count; c++)
                {
                    if (BoardCells.TypeOf(_cells[c] % BoardCells.Size, _cells[c] / BoardCells.Size) == CellType.Damage)
                    {
                        score -= weights.OwnOnDamageCell;
                    }
                }
            }

            return score;
        }
    }
}
