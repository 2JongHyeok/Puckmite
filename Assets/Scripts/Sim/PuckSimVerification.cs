using System;
using System.Collections.Generic;
using Vector2 = UnityEngine.Vector2;

namespace Puckmite.Sim
{
    /// <summary>
    /// Headless self-checks for <see cref="PuckSim"/>. Pure logic (only depends on Vector2), so it
    /// runs in batch mode or from an editor menu without any scene. Each check returns a
    /// <see cref="CheckResult"/>; <see cref="RunAll"/> bundles them.
    /// </summary>
    public static class PuckSimVerification
    {
        public readonly struct CheckResult
        {
            public readonly string Name;
            public readonly bool Passed;
            public readonly string Detail;

            public CheckResult(string name, bool passed, string detail)
            {
                Name = name;
                Passed = passed;
                Detail = detail;
            }
        }

        public static IReadOnlyList<CheckResult> RunAll()
        {
            return new List<CheckResult>
            {
                DeterminismCheck(),
                FrictionStoppingDistanceCheck(),
                MomentumConservationCheck(),
                EventDeterminismCheck(),
                EventEmissionCheck(),
                TunnelingCheck(),
                CollisionOrderIndependenceCheck(),
                WallDampingCheck(),
                ImpactDampingCheck(),
                RemovePuckCheck(),
                CrossTeamDamageCheck(),
                DestructionCheck(),
                SetHealthCheck(),
                CellOccupancyCheck(),
                CellTypeCheck(),
                XpGainCheck(),
                LevelCapCheck(),
                SettleDamageCellsCheck(),
                BuffKindCheck(),
                BuffSumCheck(),
                EnemyPlannerCheck(),
                ShopPlacementCheck(),
                ShopUpgradeSumCheck(),
                ShopEmptyCellCheck(),
                HoleDestructionCheck(),
                HoleCloneCheck(),
                HoleClearedCheck(),
                LayoutRollCheck(),
                LayoutCloneCheck(),
                BombExplosionCheck(),
                AnchorCheck(),
                AnchorPairCheck(),
                AnchorCornerPinCheck(),
                AnchorAimCheck(),
                SniperDamageCheck(),
                SniperPlanCheck(),
            };
        }

        /// <summary>Simulate twice from the same initial state and require bit-identical final states.</summary>
        public static CheckResult DeterminismCheck()
        {
            PuckSim sim = BuildCollisionScene();
            Vector2 launch = new Vector2(7.3f, 2.1f);

            PuckSim first = sim.Simulate(0, launch);
            PuckSim second = sim.Simulate(0, launch);

            if (StatesEqual(first, second, out string difference))
            {
                return new CheckResult("Determinism", true, "Two runs from the same initial state matched exactly.");
            }

            return new CheckResult("Determinism", false, difference);
        }

        /// <summary>A puck launched at speed v on constant deceleration a stops within 1% of v^2 / (2a).</summary>
        public static CheckResult FrictionStoppingDistanceCheck()
        {
            const float friction = 3f;
            const float speed = 12f;
            const int puckId = 0;

            // Board is long enough that the puck stops well before reaching any wall.
            PuckSim sim = new PuckSim(new Vector2(-1f, -6f), new Vector2(40f, 6f), friction, 0.8f, 0.01f);
            Vector2 start = new Vector2(0f, 0f);
            sim.AddPuck(new Puck(puckId, start, 0.5f, 1f, PuckOwner.Player));

            PuckSim result = sim.Simulate(puckId, new Vector2(speed, 0f));
            result.TryGetPuck(puckId, out Puck stopped);

            float travelled = (stopped.Position - start).magnitude;
            float expected = (speed * speed) / (2f * friction);
            float relativeError = Math.Abs(travelled - expected) / expected;

            bool passed = relativeError < 0.01f && stopped.Velocity == Vector2.zero;
            string detail =
                $"travelled={travelled:F4}, expected v^2/(2a)={expected:F4}, relError={relativeError * 100f:F3}% (limit 1%).";
            return new CheckResult("Friction stopping distance", passed, detail);
        }

        /// <summary>A head-on collision conserves total momentum. Frictionless, so only the collision changes velocity.</summary>
        public static CheckResult MomentumConservationCheck()
        {
            // Frictionless and a large board: nothing but the collision alters velocity, and no wall is reached.
            PuckSim sim = new PuckSim(new Vector2(-100f, -100f), new Vector2(100f, 100f), 0f, 1f, 0f);
            sim.AddPuck(new Puck(0, new Vector2(-5f, 0f), 0.5f, 1f, PuckOwner.Player) { Velocity = new Vector2(5f, 0f) });
            sim.AddPuck(new Puck(1, new Vector2(5f, 0f), 0.5f, 2f, PuckOwner.Enemy) { Velocity = new Vector2(-3f, 0f) });

            Vector2 momentumBefore = TotalMomentum(sim);
            Vector2 firstVelocityBefore = sim.Pucks[0].Velocity;

            // Enough steps for the pucks to meet and separate; not enough to reach the far walls.
            for (int step = 0; step < 500; step++)
            {
                sim.Step();
            }

            Vector2 momentumAfter = TotalMomentum(sim);
            bool collided = sim.Pucks[0].Velocity != firstVelocityBefore;
            float error = (momentumAfter - momentumBefore).magnitude;

            bool passed = error < 1e-4f && collided;
            string detail =
                $"before={Format(momentumBefore)}, after={Format(momentumAfter)}, |diff|={error:E3}, collided={collided}.";
            return new CheckResult("Momentum conservation", passed, detail);
        }

        /// <summary>Two runs from the same initial state must produce identical event streams, not just identical positions.</summary>
        public static CheckResult EventDeterminismCheck()
        {
            // Aim puck 0 (at (-6,-6)) straight at puck 1 (at (2,3)) with enough speed to collide and
            // then scatter into the walls, so the stream contains both event kinds. A 0-event stream
            // would make this check vacuous, so a puck collision is also required below.
            Vector2 launch = new Vector2(16f, 18f);
            List<PuckSimEvent> first = CollectEvents(BuildCollisionScene(), 0, launch);
            List<PuckSimEvent> second = CollectEvents(BuildCollisionScene(), 0, launch);

            int collisions = 0;
            int wallBounces = 0;
            for (int i = 0; i < first.Count; i++)
            {
                if (first[i].Type == PuckSimEventType.PuckCollision)
                {
                    collisions++;
                }
                else
                {
                    wallBounces++;
                }
            }

            bool matched = EventsEqual(first, second, out string difference);
            if (!matched)
            {
                return new CheckResult("Event determinism", false, difference);
            }

            if (collisions < 1)
            {
                return new CheckResult("Event determinism", false,
                    $"Vacuous: the two streams matched but had {first.Count} events and no puck collision to compare.");
            }

            return new CheckResult("Event determinism", true,
                $"Two runs matched: {first.Count} events ({collisions} collisions, {wallBounces} wall bounces).");
        }

        /// <summary>
        /// Events fire with the right identities and counts: a head-on impact emits exactly one
        /// PuckCollision (and no wall bounce, and no per-step spam), and a corner hit emits two wall
        /// bounces in a single step.
        /// </summary>
        public static CheckResult EventEmissionCheck()
        {
            // Head-on, frictionless, board large enough that no wall is ever reached.
            PuckSim head = new PuckSim(new Vector2(-100f, -100f), new Vector2(100f, 100f), 0f, 1f, 0f);
            head.AddPuck(new Puck(0, new Vector2(-5f, 0f), 0.5f, 1f, PuckOwner.Player) { Velocity = new Vector2(5f, 0f) });
            head.AddPuck(new Puck(1, new Vector2(5f, 0f), 0.5f, 1f, PuckOwner.Enemy) { Velocity = new Vector2(-5f, 0f) });

            int collisionCount = 0;
            int headWallCount = 0;
            PuckSimEvent collision = default;
            for (int step = 0; step < 400; step++) // long enough to meet and fully separate
            {
                IReadOnlyList<PuckSimEvent> events = head.Step();
                for (int i = 0; i < events.Count; i++)
                {
                    if (events[i].Type == PuckSimEventType.PuckCollision)
                    {
                        collisionCount++;
                        collision = events[i];
                    }
                    else
                    {
                        headWallCount++;
                    }
                }
            }

            bool collisionOk =
                collisionCount == 1 &&
                collision.Type == PuckSimEventType.PuckCollision &&
                collision.PuckA == 0 && collision.PuckB == 1 &&
                collision.Impulse > 0f &&
                headWallCount == 0;

            // Corner hit: velocity steep enough that one step crosses both the x and y walls at once.
            PuckSim corner = new PuckSim(new Vector2(0f, 0f), new Vector2(10f, 10f), 0f, 1f, 0.01f);
            corner.AddPuck(new Puck(5, new Vector2(1f, 1f), 0.5f, 1f, PuckOwner.Player) { Velocity = new Vector2(-100f, -100f) });

            IReadOnlyList<PuckSimEvent> cornerEvents = corner.Step();
            int cornerWallCount = 0;
            bool cornerIdsOk = true;
            for (int i = 0; i < cornerEvents.Count; i++)
            {
                if (cornerEvents[i].Type == PuckSimEventType.WallBounce)
                {
                    cornerWallCount++;
                    if (cornerEvents[i].PuckA != 5)
                    {
                        cornerIdsOk = false;
                    }
                }
            }

            bool cornerOk = cornerWallCount == 2 && cornerIdsOk;
            bool passed = collisionOk && cornerOk;
            string detail =
                $"head-on: collisions={collisionCount} (expect 1), impulse={collision.Impulse:F3}, walls={headWallCount} (expect 0); " +
                $"corner: wallBounces={cornerWallCount} (expect 2), idsOk={cornerIdsOk}.";
            return new CheckResult("Event emission", passed, detail);
        }

        // Drives the sim one step at a time, launching the given puck, and copies every step's events
        // out of the reused buffer into one list until the sim comes to rest (or hits the step cap).
        private static List<PuckSimEvent> CollectEvents(PuckSim sim, int puckId, Vector2 launch, int maxSteps = 100000)
        {
            sim.SetVelocity(puckId, launch);
            List<PuckSimEvent> collected = new List<PuckSimEvent>();
            int steps = 0;
            while (steps < maxSteps && !sim.AllAtRest())
            {
                IReadOnlyList<PuckSimEvent> events = sim.Step();
                for (int i = 0; i < events.Count; i++)
                {
                    collected.Add(events[i]);
                }

                steps++;
            }

            return collected;
        }

        private static bool EventsEqual(List<PuckSimEvent> a, List<PuckSimEvent> b, out string difference)
        {
            if (a.Count != b.Count)
            {
                difference = $"Event count differs: {a.Count} vs {b.Count}.";
                return false;
            }

            for (int i = 0; i < a.Count; i++)
            {
                PuckSimEvent x = a[i];
                PuckSimEvent y = b[i];
                bool same =
                    x.Type == y.Type &&
                    x.PuckA == y.PuckA &&
                    x.PuckB == y.PuckB &&
                    x.Impulse == y.Impulse;

                if (!same)
                {
                    difference = $"Event index {i} differs: {Format(x)} vs {Format(y)}.";
                    return false;
                }
            }

            difference = string.Empty;
            return true;
        }

        /// <summary>A very fast head-on shot must still collide, not pass through the target (design doc 7.4 / 7.10).</summary>
        public static CheckResult TunnelingCheck()
        {
            PuckSim sim = new PuckSim(new Vector2(-60f, -10f), new Vector2(60f, 10f), 0f, 0.9f, 0f);
            sim.AddPuck(new Puck(0, new Vector2(-40f, 0f), 0.5f, 1f, PuckOwner.Player));
            sim.AddPuck(new Puck(1, new Vector2(40f, 0f), 0.5f, 1f, PuckOwner.Enemy));

            // 1200 u/s moves 10 units per 1/120 step — many radii — so it would tunnel without substepping.
            sim.SetVelocity(0, new Vector2(1200f, 0f));

            bool collided = false;
            for (int step = 0; step < 100 && !collided; step++)
            {
                IReadOnlyList<PuckSimEvent> events = sim.Step();
                for (int i = 0; i < events.Count; i++)
                {
                    if (events[i].Type == PuckSimEventType.PuckCollision)
                    {
                        collided = true;
                    }
                }
            }

            sim.TryGetPuck(1, out Puck target);
            bool targetMoved = target.Velocity != Vector2.zero;
            bool passed = collided && targetMoved;
            string detail = $"collided={collided}, target moved={targetMoved} (target vel {Format(target.Velocity)}).";
            return new CheckResult("No tunneling (fast head-on)", passed, detail);
        }

        /// <summary>
        /// The result must not depend on the order pucks sit in the list — collisions resolve in Id order.
        /// Builds the same scene twice with the pucks added in different list orders and requires identical
        /// per-Id final states and event streams.
        /// </summary>
        public static CheckResult CollisionOrderIndependenceCheck()
        {
            Puck p0 = new Puck(0, new Vector2(-3f, 0f), 0.5f, 1f, PuckOwner.Player) { Velocity = new Vector2(7f, 0f) };
            Puck p1 = new Puck(1, new Vector2(0f, 0.25f), 0.6f, 1.3f, PuckOwner.Enemy);
            Puck p2 = new Puck(2, new Vector2(3f, -0.15f), 0.5f, 1f, PuckOwner.Enemy) { Velocity = new Vector2(-4f, 0f) };

            PuckSim ascending = new PuckSim(new Vector2(-20f, -20f), new Vector2(20f, 20f), 3f, 0.9f, 0.01f);
            ascending.AddPuck(p0);
            ascending.AddPuck(p1);
            ascending.AddPuck(p2);

            PuckSim shuffled = new PuckSim(new Vector2(-20f, -20f), new Vector2(20f, 20f), 3f, 0.9f, 0.01f);
            shuffled.AddPuck(p2);
            shuffled.AddPuck(p0);
            shuffled.AddPuck(p1);

            List<PuckSimEvent> eventsA = RunToRestCollecting(ascending);
            List<PuckSimEvent> eventsB = RunToRestCollecting(shuffled);

            if (!StatesEqualById(ascending, shuffled, out string stateDiff))
            {
                return new CheckResult("Collision order independence", false, stateDiff);
            }

            if (!EventsEqual(eventsA, eventsB, out string eventDiff))
            {
                return new CheckResult("Collision order independence", false, eventDiff);
            }

            return new CheckResult("Collision order independence", true,
                $"Two list orders gave identical per-Id states and event streams ({eventsA.Count} events).");
        }

        /// <summary>A wall bounce keeps WallRestitution of the reflected speed. Frictionless, so only the wall changes it.</summary>
        public static CheckResult WallDampingCheck()
        {
            // WallRestitution 0.5: the reflected component should come back at half speed.
            PuckSim sim = new PuckSim(new Vector2(-20f, -5f), new Vector2(20f, 5f), new PuckSimConfig(0f, 1f, 0f, 0.5f));
            sim.AddPuck(new Puck(0, new Vector2(0f, 0f), 0.5f, 1f, PuckOwner.Player));
            sim.SetVelocity(0, new Vector2(10f, 0f)); // straight at the right wall

            float speedAfterBounce = -1f;
            for (int step = 0; step < 2000; step++)
            {
                IReadOnlyList<PuckSimEvent> events = sim.Step();
                bool bounced = false;
                for (int i = 0; i < events.Count; i++)
                {
                    if (events[i].Type == PuckSimEventType.WallBounce)
                    {
                        bounced = true;
                    }
                }

                if (bounced)
                {
                    sim.TryGetPuck(0, out Puck p);
                    speedAfterBounce = p.Velocity.magnitude;
                    break;
                }
            }

            bool passed = speedAfterBounce > 0f && Math.Abs(speedAfterBounce - 5f) < 1e-3f;
            string detail = $"speed after bounce={speedAfterBounce:F4} (expect 10 * 0.5 = 5, frictionless).";
            return new CheckResult("Wall damping", passed, detail);
        }

        /// <summary>Impact damping bleeds speed on a puck-puck collision, independent of restitution.</summary>
        public static CheckResult ImpactDampingCheck()
        {
            // Frictionless, elastic bounce (restitution 1) but keep only half the speed on impact.
            PuckSim sim = new PuckSim(new Vector2(-50f, -10f), new Vector2(50f, 10f),
                new PuckSimConfig(0f, 1f, 0f, 1f, 0.5f));
            sim.AddPuck(new Puck(0, new Vector2(-5f, 0f), 0.5f, 1f, PuckOwner.Player) { Velocity = new Vector2(10f, 0f) });
            sim.AddPuck(new Puck(1, new Vector2(5f, 0f), 0.5f, 1f, PuckOwner.Enemy));

            bool collided = false;
            for (int step = 0; step < 400 && !collided; step++)
            {
                IReadOnlyList<PuckSimEvent> events = sim.Step();
                for (int i = 0; i < events.Count; i++)
                {
                    if (events[i].Type == PuckSimEventType.PuckCollision)
                    {
                        collided = true;
                    }
                }
            }

            sim.TryGetPuck(1, out Puck target);
            // Equal-mass elastic head-on transfers the full 10 to the target; impact damping keeps half.
            bool passed = collided && Math.Abs(target.Velocity.magnitude - 5f) < 1e-3f;
            string detail = $"collided={collided}, target speed={target.Velocity.magnitude:F4} (expect 10 * 0.5 = 5).";
            return new CheckResult("Impact damping", passed, detail);
        }

        /// <summary>RemovePuck drops exactly the requested puck and leaves the rest addressable by Id.</summary>
        public static CheckResult RemovePuckCheck()
        {
            PuckSim sim = new PuckSim(new Vector2(-10f, -10f), new Vector2(10f, 10f), 3f, 0.9f, 0.01f);
            sim.AddPuck(new Puck(0, new Vector2(-3f, 0f), 0.5f, 1f, PuckOwner.Player));
            sim.AddPuck(new Puck(1, new Vector2(0f, 0f), 0.5f, 1f, PuckOwner.Enemy));
            sim.AddPuck(new Puck(2, new Vector2(3f, 0f), 0.5f, 1f, PuckOwner.Enemy));

            bool removed = sim.RemovePuck(1);
            bool gone = !sim.TryGetPuck(1, out _);
            bool othersKept = sim.TryGetPuck(0, out _) && sim.TryGetPuck(2, out _);
            bool countOk = sim.Pucks.Count == 2;
            bool missingReturnsFalse = !sim.RemovePuck(99);

            bool passed = removed && gone && othersKept && countOk && missingReturnsFalse;
            string detail =
                $"removed={removed}, id1 gone={gone}, id0&2 kept={othersKept}, count={sim.Pucks.Count} (expect 2), missing->false={missingReturnsFalse}.";
            return new CheckResult("RemovePuck", passed, detail);
        }

        /// <summary>Cross-team collisions cost each puck 1 health; same-team collisions cost none (design doc 3.3).</summary>
        public static CheckResult CrossTeamDamageCheck()
        {
            PuckSim cross = new PuckSim(new Vector2(-50f, -10f), new Vector2(50f, 10f), 0f, 1f, 0f);
            cross.AddPuck(new Puck(0, new Vector2(-5f, 0f), 0.5f, 1f, PuckOwner.Player) { Velocity = new Vector2(10f, 0f), Health = 5 });
            cross.AddPuck(new Puck(1, new Vector2(5f, 0f), 0.5f, 1f, PuckOwner.Enemy) { Health = 5 });
            StepUntilPuckCollision(cross);
            cross.TryGetPuck(0, out Puck c0);
            cross.TryGetPuck(1, out Puck c1);
            bool crossOk = c0.Health == 4 && c1.Health == 4;

            PuckSim same = new PuckSim(new Vector2(-50f, -10f), new Vector2(50f, 10f), 0f, 1f, 0f);
            same.AddPuck(new Puck(0, new Vector2(-5f, 0f), 0.5f, 1f, PuckOwner.Player) { Velocity = new Vector2(10f, 0f), Health = 5 });
            same.AddPuck(new Puck(1, new Vector2(5f, 0f), 0.5f, 1f, PuckOwner.Player) { Health = 5 });
            StepUntilPuckCollision(same);
            same.TryGetPuck(0, out Puck s0);
            same.TryGetPuck(1, out Puck s1);
            bool sameOk = s0.Health == 5 && s1.Health == 5;

            bool passed = crossOk && sameOk;
            string detail = $"cross-team: {c0.Health}&{c1.Health} (expect 4&4); same-team: {s0.Health}&{s1.Health} (expect 5&5).";
            return new CheckResult("Cross-team damage", passed, detail);
        }

        /// <summary>A puck at 0 health is destroyed after the step, but that step's collision physics still applies (3.3 / 7.2).</summary>
        public static CheckResult DestructionCheck()
        {
            PuckSim sim = new PuckSim(new Vector2(-50f, -10f), new Vector2(50f, 10f), 0f, 1f, 0f);
            sim.AddPuck(new Puck(0, new Vector2(-5f, 0f), 0.5f, 1f, PuckOwner.Player) { Velocity = new Vector2(10f, 0f), Health = 1 });
            sim.AddPuck(new Puck(1, new Vector2(5f, 0f), 0.5f, 1f, PuckOwner.Enemy) { Health = 5 });

            bool destroyedEvent = false;
            for (int step = 0; step < 400 && !destroyedEvent; step++)
            {
                IReadOnlyList<PuckSimEvent> events = sim.Step();
                for (int i = 0; i < events.Count; i++)
                {
                    if (events[i].Type == PuckSimEventType.PuckDestroyed && events[i].PuckA == 0)
                    {
                        destroyedEvent = true;
                    }
                }
            }

            bool gone = !sim.TryGetPuck(0, out _);
            sim.TryGetPuck(1, out Puck survivor);
            bool survivorHit = survivor.Health == 4 && survivor.Velocity != Vector2.zero;

            bool passed = destroyedEvent && gone && survivorHit;
            string detail =
                $"destroyed event={destroyedEvent}, puck0 gone={gone}, survivor health={survivor.Health} (expect 4), survivor moving={survivor.Velocity != Vector2.zero}.";
            return new CheckResult("Destruction", passed, detail);
        }

        /// <summary>SetHealth changes the target puck's health and leaves others untouched.</summary>
        public static CheckResult SetHealthCheck()
        {
            PuckSim sim = new PuckSim(new Vector2(-10f, -10f), new Vector2(10f, 10f), 3f, 0.9f, 0.01f);
            sim.AddPuck(new Puck(0, new Vector2(-3f, 0f), 0.5f, 1f, PuckOwner.Player) { Health = 5 });
            sim.AddPuck(new Puck(1, new Vector2(3f, 0f), 0.5f, 1f, PuckOwner.Enemy) { Health = 5 });

            bool set = sim.SetHealth(0, 2);
            sim.TryGetPuck(0, out Puck a);
            sim.TryGetPuck(1, out Puck b);
            bool missingReturnsFalse = !sim.SetHealth(99, 3);

            bool passed = set && a.Health == 2 && b.Health == 5 && missingReturnsFalse;
            string detail = $"set={set}, id0 health={a.Health} (expect 2), id1 health={b.Health} (expect 5), missing->false={missingReturnsFalse}.";
            return new CheckResult("SetHealth", passed, detail);
        }

        /// <summary>Circle-vs-cell occupancy: centre cell alone, the 4-cell cross-point, and the threshold edge (design doc 3.2).</summary>
        public static CheckResult CellOccupancyCheck()
        {
            Vector2 min = new Vector2(-12.5f, -12.5f);
            Vector2 max = new Vector2(12.5f, 12.5f);
            const float radius = 1.5f;
            const float threshold = 0.3f;
            List<int> cells = new List<int>();

            // Board centre -> only the centre cell (col 2, row 2 => index 12).
            BoardCells.GetOccupiedCells(min, max, new Vector2(0f, 0f), radius, threshold, cells);
            bool centreOk = cells.Count == 1 && cells.Contains(12);

            // Exactly on the shared corner at (2.5, 2.5) -> the four surrounding cells.
            BoardCells.GetOccupiedCells(min, max, new Vector2(2.5f, 2.5f), radius, threshold, cells);
            bool crossOk = cells.Count == 4 && cells.Contains(12) && cells.Contains(13) && cells.Contains(17) && cells.Contains(18);

            // Crossing the x=2.5 boundary just under the threshold -> centre only; just over -> centre + right.
            BoardCells.GetOccupiedCells(min, max, new Vector2(1.29f, 0f), radius, threshold, cells);
            bool underOk = cells.Count == 1 && cells.Contains(12);
            BoardCells.GetOccupiedCells(min, max, new Vector2(1.31f, 0f), radius, threshold, cells);
            bool overOk = cells.Count == 2 && cells.Contains(12) && cells.Contains(13);

            bool passed = centreOk && crossOk && underOk && overOk;
            string detail = $"centre={centreOk}, cross-point={crossOk}, under-threshold={underOk}, over-threshold={overOk}.";
            return new CheckResult("Cell occupancy", passed, detail);
        }

        /// <summary>Inner 3x3 cells are Buff, the outer ring is Damage (design doc 3.1).</summary>
        public static CheckResult CellTypeCheck()
        {
            bool centreBuff = BoardCells.TypeOf(2, 2) == CellType.Buff;
            bool innerCornersBuff = BoardCells.TypeOf(1, 1) == CellType.Buff && BoardCells.TypeOf(3, 3) == CellType.Buff;
            bool outerCornersDamage = BoardCells.TypeOf(0, 0) == CellType.Damage && BoardCells.TypeOf(4, 4) == CellType.Damage;
            bool edgesDamage = BoardCells.TypeOf(0, 2) == CellType.Damage && BoardCells.TypeOf(2, 4) == CellType.Damage;

            bool passed = centreBuff && innerCornersBuff && outerCornersDamage && edgesDamage;
            string detail =
                $"centre buff={centreBuff}, inner corners buff={innerCornersBuff}, outer corners damage={outerCornersDamage}, edges damage={edgesDamage}.";
            return new CheckResult("Cell type", passed, detail);
        }

        /// <summary>XP: +1 per wall bounce (owner-independent) and per same-team collision; cross-team collisions give none (design doc 3.7).</summary>
        public static CheckResult XpGainCheck()
        {
            // Wall bounces: a frictionless puck ricochets; Level/Xp must match the number of bounces counted.
            PuckSim wall = new PuckSim(new Vector2(-10f, -6f), new Vector2(10f, 6f), 0f, 1f, 0f);
            wall.AddPuck(new Puck(0, new Vector2(0f, 0f), 0.5f, 1f, PuckOwner.Player) { Velocity = new Vector2(30f, 0f) });
            int bounces = 0;
            for (int step = 0; step < 200; step++)
            {
                IReadOnlyList<PuckSimEvent> events = wall.Step();
                for (int i = 0; i < events.Count; i++)
                {
                    if (events[i].Type == PuckSimEventType.WallBounce)
                    {
                        bounces++;
                    }
                }
            }

            wall.TryGetPuck(0, out Puck w);
            int applied = bounces < 12 ? bounces : 12;
            bool wallOk = bounces >= 1 && w.Level == 1 + applied / 3 && w.Xp == (applied >= 12 ? 0 : applied % 3);

            // Same-team collision -> both gain XP.
            PuckSim ally = new PuckSim(new Vector2(-50f, -10f), new Vector2(50f, 10f), 0f, 1f, 0f);
            ally.AddPuck(new Puck(0, new Vector2(-5f, 0f), 0.5f, 1f, PuckOwner.Player) { Velocity = new Vector2(10f, 0f) });
            ally.AddPuck(new Puck(1, new Vector2(5f, 0f), 0.5f, 1f, PuckOwner.Player));
            StepUntilPuckCollision(ally);
            ally.TryGetPuck(0, out Puck a0);
            ally.TryGetPuck(1, out Puck a1);
            bool allyOk = a0.Xp == 1 && a1.Xp == 1 && a0.Level == 1 && a1.Level == 1;

            // Cross-team collision -> no XP (damage instead).
            PuckSim enemy = new PuckSim(new Vector2(-50f, -10f), new Vector2(50f, 10f), 0f, 1f, 0f);
            enemy.AddPuck(new Puck(0, new Vector2(-5f, 0f), 0.5f, 1f, PuckOwner.Player) { Velocity = new Vector2(10f, 0f) });
            enemy.AddPuck(new Puck(1, new Vector2(5f, 0f), 0.5f, 1f, PuckOwner.Enemy));
            StepUntilPuckCollision(enemy);
            enemy.TryGetPuck(0, out Puck e0);
            enemy.TryGetPuck(1, out Puck e1);
            bool enemyOk = e0.Xp == 0 && e1.Xp == 0;

            bool passed = wallOk && allyOk && enemyOk;
            string detail = $"wall: L{w.Level} Xp{w.Xp} from {bounces} bounces ({wallOk}); ally both Xp1={allyOk}; cross-team no XP={enemyOk}.";
            return new CheckResult("XP gain", passed, detail);
        }

        /// <summary>Over 12 XP tops out at level 5 with Xp 0; further XP is discarded (design doc 3.7).</summary>
        public static CheckResult LevelCapCheck()
        {
            PuckSim sim = new PuckSim(new Vector2(-10f, -6f), new Vector2(10f, 6f), 0f, 1f, 0f);
            sim.AddPuck(new Puck(0, new Vector2(0f, 0f), 0.5f, 1f, PuckOwner.Player) { Velocity = new Vector2(60f, 0f) });

            int bounces = 0;
            for (int step = 0; step < 4000; step++)
            {
                IReadOnlyList<PuckSimEvent> events = sim.Step();
                for (int i = 0; i < events.Count; i++)
                {
                    if (events[i].Type == PuckSimEventType.WallBounce)
                    {
                        bounces++;
                    }
                }
            }

            sim.TryGetPuck(0, out Puck p);
            bool passed = bounces >= 12 && p.Level == 5 && p.Xp == 0;
            string detail = $"bounces={bounces} (need >=12), level={p.Level} (expect 5), xp={p.Xp} (expect 0).";
            return new CheckResult("Level cap", passed, detail);
        }

        /// <summary>Damage-cell settlement: −(damage × occupied damage cells), buff cells cost nothing, 0 health destroys, only listed pucks (design doc 3.4).</summary>
        public static CheckResult SettleDamageCellsCheck()
        {
            Vector2 min = new Vector2(-12.5f, -12.5f);
            Vector2 max = new Vector2(12.5f, 12.5f);
            const float threshold = 0.3f;

            PuckSim sim = new PuckSim(min, max, 3f, 0.9f, 0.01f);
            sim.AddPuck(new Puck(0, new Vector2(-9f, 0f), 1.5f, 1f, PuckOwner.Player) { Health = 5 });    // 1 damage cell
            sim.AddPuck(new Puck(1, new Vector2(0f, 0f), 1.5f, 1f, PuckOwner.Player) { Health = 5 });     // centre = buff
            sim.AddPuck(new Puck(2, new Vector2(7.5f, 7.5f), 1.5f, 1f, PuckOwner.Player) { Health = 10 }); // 3 damage cells (cross-point)
            sim.AddPuck(new Puck(3, new Vector2(-9f, 6f), 1.5f, 1f, PuckOwner.Enemy) { Health = 2 });     // 1 damage cell, dies
            sim.AddPuck(new Puck(4, new Vector2(-9f, -6f), 1.5f, 1f, PuckOwner.Player) { Health = 5 });   // 1 damage cell, left unsettled

            sim.SettleDamageCells(new List<int> { 0, 1, 2, 3 }, 2, threshold);

            sim.TryGetPuck(0, out Puck p0);
            sim.TryGetPuck(1, out Puck p1);
            sim.TryGetPuck(2, out Puck p2);
            bool deadGone = !sim.TryGetPuck(3, out _);
            sim.TryGetPuck(4, out Puck p4);

            bool oneCell = p0.Health == 3;    // 5 - 1*2
            bool buffCell = p1.Health == 5;   // no damage on a buff cell
            bool threeCells = p2.Health == 4; // 10 - 3*2
            bool notSettled = p4.Health == 5; // outside the list

            bool passed = oneCell && buffCell && threeCells && deadGone && notSettled;
            string detail =
                $"1cell={p0.Health}(exp3), buff={p1.Health}(exp5), 3cell={p2.Health}(exp4), destroyed={deadGone}, unsettled={p4.Health}(exp5).";
            return new CheckResult("Damage cell settlement", passed, detail);
        }

        /// <summary>The classic layout (the default): checkerboard attack/shield, centre value 2, other inner 1, outer 0 (design doc 3.1).</summary>
        public static CheckResult BuffKindCheck()
        {
            BoardLayout classic = BoardLayout.Classic;
            bool centreAttack = classic.KindOf(2, 2) == BuffKind.Attack && classic.ValueOf(2, 2) == 2;
            bool cornerAttack = classic.KindOf(1, 1) == BuffKind.Attack && classic.ValueOf(1, 1) == 1;
            bool edgesShield = classic.KindOf(2, 1) == BuffKind.Shield && classic.KindOf(1, 2) == BuffKind.Shield && classic.ValueOf(2, 1) == 1;
            bool outerZero = classic.ValueOf(0, 0) == 0 && !classic.IsBuff(0, 0);

            bool passed = centreAttack && cornerAttack && edgesShield && outerZero;
            string detail = $"centre atk2={centreAttack}, corner atk1={cornerAttack}, edges shield={edgesShield}, outer 0={outerZero}.";
            return new CheckResult("Buff kind", passed, detail);
        }

        /// <summary>A puck sums buff from every buff cell it occupies, attack and shield separately (design doc 3.2/3.6).</summary>
        public static CheckResult BuffSumCheck()
        {
            Vector2 min = new Vector2(-12.5f, -12.5f);
            Vector2 max = new Vector2(12.5f, 12.5f);
            const float r = 1.5f;
            const float thr = 0.3f;

            BoardLayout classic = BoardLayout.Classic;
            BoardCells.SumBuffs(classic, min, max, new Vector2(0f, 0f), r, thr, out int a0, out int s0, out int h0);   // centre: attack 2
            BoardCells.SumBuffs(classic, min, max, new Vector2(0f, -5f), r, thr, out int a1, out int s1, out int h1);  // shield cell (2,1)
            BoardCells.SumBuffs(classic, min, max, new Vector2(2.5f, -2.5f), r, thr, out int a2, out int s2, out int h2); // cross-point: atk3 shield2
            BoardCells.SumBuffs(classic, min, max, new Vector2(-9f, 0f), r, thr, out int a3, out int s3, out int h3);  // damage cell: none

            bool centre = a0 == 2 && s0 == 0;
            bool shieldCell = a1 == 0 && s1 == 1;
            bool crossPoint = a2 == 3 && s2 == 2;
            bool damageCell = a3 == 0 && s3 == 0;
            bool noHeal = h0 == 0 && h1 == 0 && h2 == 0 && h3 == 0; // the classic board fields no heal cells

            bool passed = centre && shieldCell && crossPoint && damageCell && noHeal;
            string detail =
                $"centre a{a0}s{s0}(exp2,0), shield a{a1}s{s1}(exp0,1), cross a{a2}s{s2}(exp3,2), damage a{a3}s{s3}(exp0,0), noHeal={noHeal}.";
            return new CheckResult("Buff sum", passed, detail);
        }

        /// <summary>The enemy planner is deterministic (same state twice → the same shot) and returns a legal
        /// action: an own board stone, or the new stone from one of the offered entry spots.</summary>
        public static CheckResult EnemyPlannerCheck()
        {
            EnemyPlan first = PlanSampleScene(out bool foundFirst);
            EnemyPlan second = PlanSampleScene(out bool foundSecond);

            bool samePlan =
                first.UseNewStone == second.UseNewStone &&
                first.StoneId == second.StoneId &&
                first.EntryPosition == second.EntryPosition &&
                first.Velocity == second.Velocity &&
                first.Score == second.Score &&
                first.CandidatesEvaluated == second.CandidatesEvaluated;

            bool legal = first.UseNewStone
                ? first.StoneId == 9 && (first.EntryPosition == new Vector2(11f, -6f) || first.EntryPosition == new Vector2(11f, 6f))
                : first.StoneId == 1;

            bool passed = foundFirst && foundSecond && samePlan && legal;
            string detail =
                $"found={foundFirst}, deterministic={samePlan}, legal={legal} " +
                $"(new={first.UseNewStone}, stone={first.StoneId}, score={first.Score:F3}, candidates={first.CandidatesEvaluated}).";
            return new CheckResult("Enemy planner", passed, detail);
        }

        // A small fixed scene for the planner: one enemy stone on the board (id 1), one player stone to hit
        // (id 0), and a new stone (id 9) offered at two right-edge entry spots.
        private static EnemyPlan PlanSampleScene(out bool found)
        {
            PuckSim sim = new PuckSim(new Vector2(-12.5f, -12.5f), new Vector2(12.5f, 12.5f),
                new PuckSimConfig(10f, 1f, 0.4f, 0.6f, 0.7f));
            sim.AddPuck(new Puck(0, new Vector2(-4f, 0f), 1.5f, 1f, PuckOwner.Player) { Health = 5 });
            sim.AddPuck(new Puck(1, new Vector2(4f, 0f), 1.5f, 1f, PuckOwner.Enemy) { Health = 5 });

            Puck template = new Puck(9, Vector2.zero, 1.5f, 1f, PuckOwner.Enemy) { Health = 5 };
            List<int> ownIds = new List<int> { 1 };
            List<Vector2> entrySpots = new List<Vector2> { new Vector2(11f, -6f), new Vector2(11f, 6f) };
            EnemyPlanWeights weights = new EnemyPlanWeights
            {
                BuffAttack = 1f,
                BuffShield = 1f,
                DamageDealt = 3f,
                StoneDestroyed = 2f,
                OwnDamage = 2f,
                OwnOnDamageCell = 1.5f,
            };

            // The default-difficulty search: player-equivalent prediction, picking from the top quarter.
            EnemyPlanConfig config = new EnemyPlanConfig
            {
                CueDirections = 16,
                EntryDirections = 8,
                PowerFractions = new float[] { 0.4f, 0.7f, 1f },
                FullRollout = false,
                PickRank = 0.25f,
            };

            found = EnemyPlanner.TryPlan(sim, PuckOwner.Enemy, ownIds, true, template, entrySpots, 50f, 0.1f, weights, config, out EnemyPlan plan);
            return plan;
        }

        /// <summary>Upgrade-board placement (사용자 지정 2026-08-09): an empty cell takes the cell at level
        /// 1, the same kind again adds one XP — XpPerLevel stacks fill the gauge and raise the level — and
        /// a different kind replaces it, levels and XP lost.</summary>
        public static CheckResult ShopPlacementCheck()
        {
            ShopBoard board = new ShopBoard();

            ShopPlacement first = board.Place(1, 1, UpgradeKind.Attack);
            bool placed = first == ShopPlacement.Placed && board.CellAt(1, 1).Level == 1
                && board.CellAt(1, 1).Xp == 0 && board.CellAt(1, 1).Kind == UpgradeKind.Attack;

            ShopPlacement second = board.Place(1, 1, UpgradeKind.Attack);
            bool stacked = second == ShopPlacement.Upgraded && board.CellAt(1, 1).Level == 1 && board.CellAt(1, 1).Xp == 1;

            board.Place(1, 1, UpgradeKind.Attack);
            ShopPlacement fourth = board.Place(1, 1, UpgradeKind.Attack);
            bool levelled = fourth == ShopPlacement.Upgraded && board.CellAt(1, 1).Level == 2 && board.CellAt(1, 1).Xp == 0;

            bool previewWarns = board.Preview(1, 1, UpgradeKind.Shield) == ShopPlacement.Replaced;

            ShopPlacement fifth = board.Place(1, 1, UpgradeKind.Shield);
            bool replaced = fifth == ShopPlacement.Replaced && board.CellAt(1, 1).Level == 1
                && board.CellAt(1, 1).Xp == 0 && board.CellAt(1, 1).Kind == UpgradeKind.Shield;

            bool othersUntouched = board.CellAt(0, 0).IsEmpty && board.CellAt(2, 2).IsEmpty;

            bool passed = placed && stacked && levelled && previewWarns && replaced && othersUntouched;
            string detail = $"placed={placed}, stacked={stacked}, levelled={levelled}, previewWarns={previewWarns}, replaced={replaced}, othersEmpty={othersUntouched}.";
            return new CheckResult("Shop placement", passed, detail);
        }

        /// <summary>Settlement (design doc 5.2): every occupied cell pays cell level x stone level, and a
        /// stone straddling two cells collects both.</summary>
        public static CheckResult ShopUpgradeSumCheck()
        {
            PuckSim sim = new PuckSim(new Vector2(-12.5f, -12.5f), new Vector2(12.5f, 12.5f),
                new PuckSimConfig(10f, 1f, 0.4f, 0.6f, 0.7f));
            ShopBoard board = new ShopBoard();

            // Centre cell (2,2) at level 2 (one place plus a full XP gauge), and (3,2) at level 1.
            board.Place(2, 2, UpgradeKind.Attack);
            for (int stack = 0; stack < ShopBoard.XpPerLevel; stack++)
            {
                board.Place(2, 2, UpgradeKind.Attack);
            }

            board.Place(3, 2, UpgradeKind.Shield);

            // A level-3 stone dead centre: only cell (2,2) -> attack 2*3 = 6.
            sim.AddPuck(new Puck(0, new Vector2(0f, 0f), 1.5f, 1f, PuckOwner.Player) { Health = 5, Level = 3 });
            UpgradeTotals centre = board.SumUpgrades(sim, 0.1f);
            bool centreOnly = centre.Attack == 6 && centre.Shield == 0 && centre.RunHeal == 0 && centre.MaxHealth == 0;

            // A level-1 stone on the (2,2)|(3,2) boundary picks up both: attack 2*1, shield 1*1.
            sim.RemovePuck(0);
            sim.AddPuck(new Puck(0, new Vector2(2.5f, 0f), 1.5f, 1f, PuckOwner.Player) { Health = 5, Level = 1 });
            UpgradeTotals straddle = board.SumUpgrades(sim, 0.1f);
            bool bothCells = straddle.Attack == 2 && straddle.Shield == 1;

            bool passed = centreOnly && bothCells;
            string detail =
                $"centre a{centre.Attack}s{centre.Shield}(exp6,0), straddle a{straddle.Attack}s{straddle.Shield}(exp2,1).";
            return new CheckResult("Shop upgrade sum", passed, detail);
        }

        /// <summary>An untouched upgrade board pays nothing — a stone stopping on an empty cell does
        /// nothing at all (design doc 5.1).</summary>
        public static CheckResult ShopEmptyCellCheck()
        {
            PuckSim sim = new PuckSim(new Vector2(-12.5f, -12.5f), new Vector2(12.5f, 12.5f),
                new PuckSimConfig(10f, 1f, 0.4f, 0.6f, 0.7f));
            ShopBoard board = new ShopBoard();
            sim.AddPuck(new Puck(0, new Vector2(0f, 0f), 1.5f, 1f, PuckOwner.Player) { Health = 5, Level = 4 });

            UpgradeTotals totals = board.SumUpgrades(sim, 0.1f);
            bool passed = totals.Attack == 0 && totals.Shield == 0 && totals.RunHeal == 0 && totals.MaxHealth == 0;
            string detail = $"a{totals.Attack} s{totals.Shield} h{totals.RunHeal} m{totals.MaxHealth} (all expected 0).";
            return new CheckResult("Shop empty cells", passed, detail);
        }

        /// <summary>The boss's hole: a stone whose centre crosses the hole cell mid-flight is destroyed —
        /// health notwithstanding — with a PuckDestroyed event, exactly like a health death upstream.</summary>
        public static CheckResult HoleDestructionCheck()
        {
            PuckSim sim = BuildHoleScene(out _);
            sim.SetHole(2, 2); // centre cell, x in [-2.5, 2.5)

            List<PuckSimEvent> events = RunToRestCollecting(sim);
            bool destroyed = !sim.TryGetPuck(0, out _) && sim.Pucks.Count == 0;
            bool eventSeen = false;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].Type == PuckSimEventType.PuckDestroyed && events[i].PuckA == 0)
                {
                    eventSeen = true;
                }
            }

            bool passed = destroyed && eventSeen;
            string detail = $"destroyed={destroyed}, PuckDestroyed event={eventSeen}.";
            return new CheckResult("Hole destruction", passed, detail);
        }

        /// <summary>Clone() carries the hole, so previews and roll-outs see it: the clone's stone dies in
        /// the copied hole exactly like the original's.</summary>
        public static CheckResult HoleCloneCheck()
        {
            PuckSim sim = BuildHoleScene(out _);
            sim.SetHole(2, 2);

            PuckSim clone = sim.Clone();
            bool holeCopied = clone.HoleCell == sim.HoleCell && clone.HoleCell == 2 + 2 * BoardCells.Size;

            clone.RunToRest();
            sim.RunToRest();
            bool bothDestroyed = clone.Pucks.Count == 0 && sim.Pucks.Count == 0;

            bool passed = holeCopied && bothDestroyed;
            string detail = $"holeCopied={holeCopied} (cell {clone.HoleCell}), destroyed clone&original={bothDestroyed}.";
            return new CheckResult("Hole clone", passed, detail);
        }

        /// <summary>No hole by default, and a cleared hole swallows nothing: the same crossing stone
        /// survives to rest on the far side.</summary>
        public static CheckResult HoleClearedCheck()
        {
            PuckSim sim = BuildHoleScene(out Puck stone);
            bool noneByDefault = sim.HoleCell == -1 && !sim.IsInsideHole(new Vector2(0f, 0f));

            sim.SetHole(2, 2);
            sim.ClearHole();
            sim.RunToRest();

            bool survived = sim.TryGetPuck(stone.Id, out Puck after) && after.Health == stone.Health;
            bool crossed = survived && after.Position.x > 2.5f; // came to rest past the hole cell

            bool passed = noneByDefault && survived && crossed;
            string detail = $"noneByDefault={noneByDefault}, survived={survived}, restX={(survived ? after.Position.x : float.NaN):F2} (expect > 2.5).";
            return new CheckResult("Hole cleared", passed, detail);
        }

        /// <summary>The per-run roll (사용자 지정 2026-08-10): sweeping seeds, every board fields attack
        /// 4~5 and shield 3~4 cells and never a heal cell (battle-board heal cut the same day), the
        /// centre always a lv2 buff, other buffs lv1, the outer ring clean — and the same seed rolls
        /// the same board twice.</summary>
        public static CheckResult LayoutRollCheck()
        {
            for (int seed = 0; seed < 200; seed++)
            {
                BoardLayout layout = BoardLayout.Roll(new System.Random(seed));
                BoardLayout again = BoardLayout.Roll(new System.Random(seed));

                int attack = 0;
                int shield = 0;
                int heal = 0;
                for (int row = 0; row < BoardCells.Size; row++)
                {
                    for (int col = 0; col < BoardCells.Size; col++)
                    {
                        bool inner = BoardCells.TypeOf(col, row) == CellType.Buff;
                        if (layout.IsBuff(col, row) != again.IsBuff(col, row)
                            || layout.ValueOf(col, row) != again.ValueOf(col, row)
                            || (layout.IsBuff(col, row) && layout.KindOf(col, row) != again.KindOf(col, row)))
                        {
                            return new CheckResult("Layout roll", false, $"seed {seed}: two rolls differ at ({col},{row}).");
                        }

                        if (!layout.IsBuff(col, row))
                        {
                            continue;
                        }

                        if (!inner)
                        {
                            return new CheckResult("Layout roll", false, $"seed {seed}: outer cell ({col},{row}) is a buff.");
                        }

                        switch (layout.KindOf(col, row))
                        {
                            case BuffKind.Attack: attack++; break;
                            case BuffKind.Shield: shield++; break;
                            default: heal++; break;
                        }

                        int expected = col == 2 && row == 2 ? BoardLayout.CentreLevel : 1;
                        if (layout.ValueOf(col, row) != expected)
                        {
                            return new CheckResult("Layout roll", false, $"seed {seed}: cell ({col},{row}) level {layout.ValueOf(col, row)}, expected {expected}.");
                        }
                    }
                }

                if (attack < BoardLayout.MinAttackCells || attack > BoardLayout.MaxAttackCells
                    || shield < BoardLayout.MinShieldCells || shield > BoardLayout.MaxShieldCells
                    || heal != 0
                    || !layout.IsBuff(2, 2))
                {
                    return new CheckResult("Layout roll", false, $"seed {seed}: atk {attack}/shd {shield}/heal {heal}, centre buff={layout.IsBuff(2, 2)}.");
                }
            }

            return new CheckResult("Layout roll", true, "200 seeds: attack 4~5, shield 3~4, no heal cells, centre lv2, others lv1, outer clean, deterministic.");
        }

        /// <summary>A clone carries the sim's layout, so previews and AI roll-outs sum buffs off the same
        /// board as the live game.</summary>
        public static CheckResult LayoutCloneCheck()
        {
            PuckSim sim = new PuckSim(new Vector2(-12.5f, -12.5f), new Vector2(12.5f, 12.5f),
                new PuckSimConfig(10f, 1f, 0.4f, 0.6f, 0.7f));
            BoardLayout rolled = BoardLayout.Roll(new System.Random(7));
            sim.Layout = rolled;
            PuckSim clone = sim.Clone();

            bool carried = ReferenceEquals(clone.Layout, rolled);
            BoardCells.SumBuffs(sim.Layout, sim.BoardMin, sim.BoardMax, new Vector2(0f, 0f), 1.5f, 0.3f, out int a0, out int s0, out int h0);
            BoardCells.SumBuffs(clone.Layout, clone.BoardMin, clone.BoardMax, new Vector2(0f, 0f), 1.5f, 0.3f, out int a1, out int s1, out int h1);
            bool sameSum = a0 == a1 && s0 == s1 && h0 == h1;
            bool centreCounts = a0 + s0 + h0 >= BoardLayout.CentreLevel; // the centre is always a lv2 buff

            bool passed = carried && sameSum && centreCounts;
            string detail = $"carried={carried}, sums a{a0}s{s0}h{h0} vs a{a1}s{s1}h{h1}, centre counts={centreCounts}.";
            return new CheckResult("Layout clone", passed, detail);
        }

        /// <summary>자폭 (design doc 4.3): a bomb meeting a player stone detonates — the bomb dies, every
        /// player stone in reach (the direct victim included) takes 2 and is shoved away, enemy stones are
        /// untouched.</summary>
        public static CheckResult BombExplosionCheck()
        {
            PuckSim sim = new PuckSim(new Vector2(-12.5f, -12.5f), new Vector2(12.5f, 12.5f),
                new PuckSimConfig(10f, 1f, 0.4f, 0.6f, 0.7f));
            sim.AddPuck(new Puck(0, new Vector2(-8f, 0f), 1.5f, 1f, PuckOwner.Player) { Health = 5, Velocity = new Vector2(18f, 0f) });
            sim.AddPuck(new Puck(1, new Vector2(0f, 0f), 1.5f, 1f, PuckOwner.Enemy) { Health = 5, Trait = StoneTrait.Bomb });
            sim.AddPuck(new Puck(2, new Vector2(0f, 4f), 1.5f, 1f, PuckOwner.Player) { Health = 5 });  // in reach (4 < 6)
            sim.AddPuck(new Puck(3, new Vector2(0f, -5f), 1.5f, 1f, PuckOwner.Enemy) { Health = 5 });  // in reach but enemy

            List<PuckSimEvent> events = RunToRestCollecting(sim);
            bool bombGone = !sim.TryGetPuck(1, out _);
            bool bombDestroyedEvent = false;
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].Type == PuckSimEventType.PuckDestroyed && events[i].PuckA == 1)
                {
                    bombDestroyedEvent = true;
                }
            }

            sim.TryGetPuck(0, out Puck striker);
            sim.TryGetPuck(2, out Puck bystander);
            sim.TryGetPuck(3, out Puck enemyNear);
            bool strikerHit = striker.Health == 3;      // blast 2, no ordinary contact damage on top
            bool bystanderHit = bystander.Health == 3;  // in reach: 2
            bool bystanderShoved = bystander.Position != new Vector2(0f, 4f);
            bool enemyUntouched = enemyNear.Health == 5 && enemyNear.Position == new Vector2(0f, -5f);

            bool passed = bombGone && bombDestroyedEvent && strikerHit && bystanderHit && bystanderShoved && enemyUntouched;
            string detail =
                $"bombGone={bombGone}, event={bombDestroyedEvent}, striker={striker.Health}(exp3), bystander={bystander.Health}(exp3), shoved={bystanderShoved}, enemyUntouched={enemyUntouched}.";
            return new CheckResult("Bomb explosion", passed, detail);
        }

        /// <summary>반석 (design doc 4.3): an anchor stone never moves in a collision, and the striker
        /// bounces back with the energy returned in full (no restitution/impact-damping loss).</summary>
        public static CheckResult AnchorCheck()
        {
            PuckSim sim = new PuckSim(new Vector2(-12.5f, -12.5f), new Vector2(12.5f, 12.5f),
                new PuckSimConfig(10f, 1f, 0.4f, 0.6f, 0.7f));
            sim.AddPuck(new Puck(0, new Vector2(-8f, 0f), 1.5f, 1f, PuckOwner.Player) { Health = 5, Velocity = new Vector2(12f, 0f) });
            sim.AddPuck(new Puck(1, new Vector2(0f, 0f), 1.5f, 1f, PuckOwner.Enemy) { Health = 2, Trait = StoneTrait.Anchor });

            sim.RunToRest();

            sim.TryGetPuck(0, out Puck striker);
            sim.TryGetPuck(1, out Puck anchor);
            bool anchorStayed = anchor.Position == new Vector2(0f, 0f);
            // Full energy return: the striker (contact at x=-3 with ~6.6 speed left) must bounce back past
            // its own approach — with the 0.7 impact bleed it would stop around x=-4.1, so requiring
            // clearly beyond that separates "returned in full" from "ordinary collision".
            bool bouncedBack = striker.Position.x < -4.5f;
            bool damaged = striker.Health == 4 && anchor.Health == 1; // ordinary cross-team 1 each

            bool passed = anchorStayed && bouncedBack && damaged;
            string detail =
                $"anchorStayed={anchorStayed}, strikerRestX={striker.Position.x:F2}(exp < -4.5), striker hp={striker.Health}(exp4), anchor hp={anchor.Health}(exp1).";
            return new CheckResult("Anchor stone", passed, detail);
        }

        /// <summary>반석끼리 (사용자 지정 2026-08-10): between two anchors the resting one keeps its
        /// ground and only the rolled one is turned back — full energy return, and the pair never comes
        /// to rest overlapping.</summary>
        public static CheckResult AnchorPairCheck()
        {
            PuckSim sim = new PuckSim(new Vector2(-12.5f, -12.5f), new Vector2(12.5f, 12.5f),
                new PuckSimConfig(10f, 1f, 0.4f, 0.6f, 0.7f));
            sim.AddPuck(new Puck(0, new Vector2(-8f, 0f), 1.5f, 1f, PuckOwner.Enemy)
            {
                Health = 2,
                Trait = StoneTrait.Anchor,
                Velocity = new Vector2(12f, 0f),
            });
            sim.AddPuck(new Puck(1, new Vector2(0f, 0f), 1.5f, 1f, PuckOwner.Enemy) { Health = 2, Trait = StoneTrait.Anchor });

            sim.RunToRest();

            sim.TryGetPuck(0, out Puck rolled);
            sim.TryGetPuck(1, out Puck rester);
            bool resterStayed = rester.Position == new Vector2(0f, 0f);
            // Full return, the AnchorCheck bar: with the 0.7 bleed an ordinary collision would leave the
            // rolled one around x=-4.1, so clearly beyond that proves the anchor treatment applied.
            bool bouncedBack = rolled.Position.x < -4.5f;
            bool separated = (rolled.Position - rester.Position).magnitude >= 2.999f;
            bool undamaged = rolled.Health == 2 && rester.Health == 2; // same team: no contact damage

            bool passed = resterStayed && bouncedBack && separated && undamaged;
            string detail =
                $"resterStayed={resterStayed}, rolledRestX={rolled.Position.x:F2}(exp < -4.5), separated={separated}, hp={rolled.Health}/{rester.Health}(exp 2/2).";
            return new CheckResult("Anchor pair", passed, detail);
        }

        /// <summary>An anchor plowing a stone against the wall must not come to rest overlapping it
        /// (사용자 보고 2026-08-10): the wall outranks the anchor, so the separation the wall refuses
        /// pushes the anchor back instead.</summary>
        public static CheckResult AnchorCornerPinCheck()
        {
            PuckSim sim = new PuckSim(new Vector2(-12.5f, -12.5f), new Vector2(12.5f, 12.5f),
                new PuckSimConfig(10f, 1f, 0.4f, 0.6f, 0.7f));
            // Health far above the contact count: the pinned stone rattles between wall and anchor,
            // paying 1 per approaching contact — dozens over a long plow. The check is geometric.
            sim.AddPuck(new Puck(0, new Vector2(-11f, -11f), 1.5f, 1f, PuckOwner.Player) { Health = 500 }); // pinned in the corner
            sim.AddPuck(new Puck(1, new Vector2(-11f, -5f), 1.5f, 1f, PuckOwner.Enemy)
            {
                Health = 500,
                Trait = StoneTrait.Anchor,
                Velocity = new Vector2(0f, -25f), // plows straight down the wall line
            });

            sim.RunToRest();

            bool pinnedAlive = sim.TryGetPuck(0, out Puck pinned);
            bool anchorAlive = sim.TryGetPuck(1, out Puck anchor);
            bool bothAlive = pinnedAlive && anchorAlive;
            bool separated = bothAlive && (pinned.Position - anchor.Position).magnitude >= 2.999f;
            bool insideWalls = bothAlive
                && pinned.Position.x >= -11.001f && pinned.Position.y >= -11.001f
                && anchor.Position.x >= -11.001f && anchor.Position.y >= -11.001f;

            bool passed = bothAlive && separated && insideWalls;
            string detail = bothAlive
                ? $"gap={(pinned.Position - anchor.Position).magnitude:F3}(exp >= 2.999), pinned=({pinned.Position.x:F2},{pinned.Position.y:F2}), anchor=({anchor.Position.x:F2},{anchor.Position.y:F2})."
                : "a stone died — the pin scenario never completed.";
            return new CheckResult("Anchor corner pin", passed, detail);
        }

        /// <summary>반석형 중앙 지향 (사용자 지정 2026-08-10): even with a bait stone parked by a corner,
        /// the planner's anchor shot comes to rest nearer the board centre or an edge midpoint than any
        /// corner.</summary>
        public static CheckResult AnchorAimCheck()
        {
            PuckSim sim = new PuckSim(new Vector2(-12.5f, -12.5f), new Vector2(12.5f, 12.5f),
                new PuckSimConfig(10f, 1f, 0.4f, 0.6f, 0.7f));
            sim.AddPuck(new Puck(0, new Vector2(-9f, -9f), 1.5f, 1f, PuckOwner.Player) { Health = 5 }); // corner bait

            Puck template = new Puck(9, Vector2.zero, 1.5f, 1f, PuckOwner.Enemy) { Health = 5, Trait = StoneTrait.Anchor };
            List<Vector2> entrySpots = new List<Vector2>
            {
                new Vector2(11f, -11f), new Vector2(11f, -6f), new Vector2(11f, 0f), new Vector2(11f, 6f), new Vector2(11f, 11f),
            };
            EnemyPlanWeights weights = new EnemyPlanWeights
            {
                BuffAttack = 1f,
                BuffShield = 1f,
                DamageDealt = 3f,
                StoneDestroyed = 2f,
                OwnDamage = 2f,
                OwnOnDamageCell = 1.5f,
            };
            EnemyPlanConfig config = new EnemyPlanConfig
            {
                CueDirections = 16,
                EntryDirections = 8,
                PowerFractions = new float[] { 0.4f, 0.7f, 1f },
                FullRollout = false,
                PickRank = 0f, // the top shot — the shaping must already rule it
            };

            bool found = EnemyPlanner.TryPlan(sim, PuckOwner.Enemy, new List<int>(), true, template, entrySpots, 50f, 0.1f, weights, config, out EnemyPlan plan);
            if (!found || !plan.UseNewStone)
            {
                return new CheckResult("Anchor aim", false, $"found={found}, usedNewStone={plan.UseNewStone}.");
            }

            Puck launched = template;
            launched.Position = plan.EntryPosition;
            launched.Velocity = plan.Velocity;
            sim.AddPuck(launched);
            sim.RunToRest();

            if (!sim.TryGetPuck(9, out Puck rest))
            {
                return new CheckResult("Anchor aim", false, "the planned anchor died in the roll-out.");
            }

            const float inset = 1.5f;
            Vector2 centre = Vector2.zero;
            float goal = DistToNearest(rest.Position,
                centre,
                new Vector2(0f, 12.5f - inset), new Vector2(0f, -12.5f + inset),
                new Vector2(-12.5f + inset, 0f), new Vector2(12.5f - inset, 0f));
            float corner = DistToNearest(rest.Position,
                new Vector2(-11f, -11f), new Vector2(-11f, 11f), new Vector2(11f, -11f), new Vector2(11f, 11f));

            bool central = goal < corner;
            string detail = $"rest=({rest.Position.x:F2},{rest.Position.y:F2}), toGoal={goal:F2} < toCorner={corner:F2} = {central}.";
            return new CheckResult("Anchor aim", central, detail);
        }

        private static float DistToNearest(Vector2 position, params Vector2[] targets)
        {
            float best = float.MaxValue;
            for (int i = 0; i < targets.Length; i++)
            {
                float d = (position - targets[i]).magnitude;
                if (d < best)
                {
                    best = d;
                }
            }

            return best;
        }

        /// <summary>저격 (design doc 4.3): the armed sniper stone's first player contact deals 2 and
        /// disarms; a later (passive) contact is the ordinary 1.</summary>
        public static CheckResult SniperDamageCheck()
        {
            PuckSim sim = new PuckSim(new Vector2(-12.5f, -12.5f), new Vector2(12.5f, 12.5f),
                new PuckSimConfig(10f, 1f, 0.4f, 0.6f, 0.7f));
            sim.AddPuck(new Puck(0, new Vector2(-8f, 0f), 1.5f, 1f, PuckOwner.Enemy) { Health = 5, Trait = StoneTrait.Sniper });
            sim.AddPuck(new Puck(1, new Vector2(0f, 0f), 1.5f, 1f, PuckOwner.Player) { Health = 5 });

            // The sniper's own roll: armed, so the first player contact costs 2 (and 1 back onto the sniper).
            sim.SetSniperArmed(0, true);
            sim.SetVelocity(0, new Vector2(12f, 0f));
            sim.RunToRest();
            sim.TryGetPuck(0, out Puck sniperAfterRoll);
            sim.TryGetPuck(1, out Puck victim);
            bool armedHit = victim.Health == 3 && sniperAfterRoll.Health == 4;
            bool disarmed = !sniperAfterRoll.SniperArmed;

            // The player strikes back at the (now disarmed) sniper stone: ordinary 1 each way.
            sim.SetVelocity(1, (sniperAfterRoll.Position - victim.Position).normalized * 12f);
            sim.RunToRest();
            sim.TryGetPuck(0, out Puck sniperAfterReturn);
            sim.TryGetPuck(1, out Puck attacker);
            bool passiveHit = sniperAfterReturn.Health == 3 && attacker.Health == 2;

            bool passed = armedHit && disarmed && passiveHit;
            string detail =
                $"armedHit victim={victim.Health}(exp3)/sniper={sniperAfterRoll.Health}(exp4), disarmed={disarmed}, passive sniper={sniperAfterReturn.Health}(exp3)/attacker={attacker.Health}(exp2).";
            return new CheckResult("Sniper damage", passed, detail);
        }

        /// <summary>저격 조준 (design doc 4.3): with a snipe priority set and a clear line, the planner's
        /// pick first-contacts the priority target rather than a higher-scoring ordinary shot.</summary>
        public static CheckResult SniperPlanCheck()
        {
            PuckSim sim = new PuckSim(new Vector2(-12.5f, -12.5f), new Vector2(12.5f, 12.5f),
                new PuckSimConfig(10f, 1f, 0.4f, 0.6f, 0.7f));
            sim.AddPuck(new Puck(0, new Vector2(-9f, 0f), 1.5f, 1f, PuckOwner.Enemy) { Health = 5, Trait = StoneTrait.Sniper });
            sim.AddPuck(new Puck(1, new Vector2(5f, 0f), 1.5f, 1f, PuckOwner.Player) { Health = 1 });  // the weak target
            sim.AddPuck(new Puck(2, new Vector2(5f, 6f), 1.5f, 1f, PuckOwner.Player) { Health = 5 });

            EnemyPlanWeights weights = new EnemyPlanWeights
            {
                BuffAttack = 1f, BuffShield = 1f, DamageDealt = 3f, StoneDestroyed = 2f, OwnDamage = 2f, OwnOnDamageCell = 1.5f,
            };
            EnemyPlanConfig config = new EnemyPlanConfig
            {
                CueDirections = 24,
                EntryDirections = 6,
                PowerFractions = new float[] { 0.4f, 0.7f, 1f },
                FullRollout = false,
                PickRank = 0f,
                SnipePriority = new[] { 1, 2 }, // weakest first
            };

            bool planned = EnemyPlanner.TryPlan(
                sim, PuckOwner.Enemy, new[] { 0 }, false, default, System.Array.Empty<Vector2>(),
                50f, 0.1f, weights, config, out EnemyPlan plan);

            // Replay the pick and watch who the cue touches first.
            int firstContact = -1;
            if (planned)
            {
                PuckSim replay = sim.Clone();
                replay.SetSniperArmed(0, true);
                replay.SetVelocity(plan.StoneId, plan.Velocity);
                for (int step = 0; step < 2000 && !replay.AllAtRest() && firstContact < 0; step++)
                {
                    IReadOnlyList<PuckSimEvent> events = replay.Step();
                    for (int i = 0; i < events.Count && firstContact < 0; i++)
                    {
                        if (events[i].Type == PuckSimEventType.PuckCollision)
                        {
                            firstContact = events[i].PuckA == 0 ? events[i].PuckB : events[i].PuckA;
                        }
                    }
                }
            }

            bool passed = planned && firstContact == 1;
            string detail = $"planned={planned}, firstContact={firstContact} (expect 1, the weakest stone).";
            return new CheckResult("Sniper plan", passed, detail);
        }

        // One stone rolling left-to-right straight through the centre cell (2,2) on the real board size.
        // Speed 18 at friction 10 travels v²/2a ≈ 16 units: through the hole cell (reached after 7.5) and
        // to rest near x ≈ +6 — past the cell for the survives-and-crosses assertion, yet well short of
        // the right wall (x = 11), which would bounce it back to the left half and break that assertion.
        private static PuckSim BuildHoleScene(out Puck stone)
        {
            PuckSim sim = new PuckSim(new Vector2(-12.5f, -12.5f), new Vector2(12.5f, 12.5f),
                new PuckSimConfig(10f, 1f, 0.4f, 0.6f, 0.7f));
            stone = new Puck(0, new Vector2(-10f, 0f), 1.5f, 1f, PuckOwner.Player)
            {
                Health = 5,
                Velocity = new Vector2(18f, 0f),
            };
            sim.AddPuck(stone);
            return sim;
        }

        private static void StepUntilPuckCollision(PuckSim sim, int maxSteps = 400)
        {
            for (int step = 0; step < maxSteps; step++)
            {
                IReadOnlyList<PuckSimEvent> events = sim.Step();
                for (int i = 0; i < events.Count; i++)
                {
                    if (events[i].Type == PuckSimEventType.PuckCollision)
                    {
                        return;
                    }
                }
            }
        }

        private static List<PuckSimEvent> RunToRestCollecting(PuckSim sim, int maxSteps = 100000)
        {
            List<PuckSimEvent> collected = new List<PuckSimEvent>();
            int steps = 0;
            while (steps < maxSteps && !sim.AllAtRest())
            {
                IReadOnlyList<PuckSimEvent> events = sim.Step();
                for (int i = 0; i < events.Count; i++)
                {
                    collected.Add(events[i]);
                }

                steps++;
            }

            return collected;
        }

        private static bool StatesEqualById(PuckSim a, PuckSim b, out string difference)
        {
            IReadOnlyList<Puck> pa = a.Pucks;
            if (pa.Count != b.Pucks.Count)
            {
                difference = $"Puck count differs: {pa.Count} vs {b.Pucks.Count}.";
                return false;
            }

            for (int i = 0; i < pa.Count; i++)
            {
                Puck x = pa[i];
                if (!b.TryGetPuck(x.Id, out Puck y))
                {
                    difference = $"Puck Id {x.Id} missing from the second sim.";
                    return false;
                }

                bool same =
                    ExactlyEqual(x.Position, y.Position) &&
                    ExactlyEqual(x.Velocity, y.Velocity) &&
                    x.BounceCount == y.BounceCount &&
                    x.Health == y.Health &&
                    x.Level == y.Level &&
                    x.Xp == y.Xp;
                if (!same)
                {
                    difference =
                        $"Puck Id {x.Id} differs: pos {Format(x.Position)} vs {Format(y.Position)}, " +
                        $"vel {Format(x.Velocity)} vs {Format(y.Velocity)}, bounces {x.BounceCount} vs {y.BounceCount}.";
                    return false;
                }
            }

            difference = string.Empty;
            return true;
        }

        // A scene that exercises friction, wall bounces, and puck-to-puck collisions at once, so the
        // determinism check covers every code path rather than a single straight shot.
        private static PuckSim BuildCollisionScene()
        {
            PuckSim sim = new PuckSim(new Vector2(-10f, -10f), new Vector2(10f, 10f), 3f, 0.9f, 0.01f);
            sim.AddPuck(new Puck(0, new Vector2(-6f, -6f), 0.5f, 1f, PuckOwner.Player));
            sim.AddPuck(new Puck(1, new Vector2(2f, 3f), 0.6f, 1.5f, PuckOwner.Enemy));
            sim.AddPuck(new Puck(2, new Vector2(-3f, 5f), 0.5f, 1f, PuckOwner.Enemy));
            return sim;
        }

        private static Vector2 TotalMomentum(PuckSim sim)
        {
            Vector2 sum = Vector2.zero;
            IReadOnlyList<Puck> pucks = sim.Pucks;
            for (int i = 0; i < pucks.Count; i++)
            {
                sum += pucks[i].Velocity * pucks[i].Mass;
            }

            return sum;
        }

        private static bool StatesEqual(PuckSim a, PuckSim b, out string difference)
        {
            IReadOnlyList<Puck> pa = a.Pucks;
            IReadOnlyList<Puck> pb = b.Pucks;
            if (pa.Count != pb.Count)
            {
                difference = $"Puck count differs: {pa.Count} vs {pb.Count}.";
                return false;
            }

            for (int i = 0; i < pa.Count; i++)
            {
                Puck x = pa[i];
                Puck y = pb[i];
                bool same =
                    x.Id == y.Id &&
                    ExactlyEqual(x.Position, y.Position) &&
                    ExactlyEqual(x.Velocity, y.Velocity) &&
                    x.Radius == y.Radius &&
                    x.Mass == y.Mass &&
                    x.Owner == y.Owner &&
                    x.BounceCount == y.BounceCount &&
                    x.Health == y.Health &&
                    x.Level == y.Level &&
                    x.Xp == y.Xp;

                if (!same)
                {
                    difference =
                        $"Puck index {i} differs: pos {Format(x.Position)} vs {Format(y.Position)}, " +
                        $"vel {Format(x.Velocity)} vs {Format(y.Velocity)}, bounces {x.BounceCount} vs {y.BounceCount}.";
                    return false;
                }
            }

            difference = string.Empty;
            return true;
        }

        // Exact, bit-for-bit comparison (Vector2's == is approximate, which would hide tiny drift).
        private static bool ExactlyEqual(Vector2 u, Vector2 v)
        {
            return u.x == v.x && u.y == v.y;
        }

        private static string Format(Vector2 v)
        {
            return $"({v.x:R}, {v.y:R})";
        }

        private static string Format(PuckSimEvent e)
        {
            return e.Type == PuckSimEventType.WallBounce
                ? $"WallBounce(puck {e.PuckA})"
                : $"PuckCollision({e.PuckA}, {e.PuckB}, impulse {e.Impulse:R})";
        }
    }
}
