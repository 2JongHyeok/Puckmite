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
                    x.BounceCount == y.BounceCount;
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
                    x.BounceCount == y.BounceCount;

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
