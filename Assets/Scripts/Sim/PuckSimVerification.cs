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
    }
}
