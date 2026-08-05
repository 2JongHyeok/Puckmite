namespace Puckmite.Sim
{
    /// <summary>The kind of thing that happened during a single <see cref="PuckSim.Step"/>.</summary>
    public enum PuckSimEventType
    {
        WallBounce,
        PuckCollision,
        PuckDestroyed,
    }

    /// <summary>
    /// One thing the simulation reports for a step: a wall bounce or a puck-to-puck impact. It is a
    /// plain value type with a discriminator (not a class hierarchy) so a step's events can live in a
    /// single reused list with no per-step heap allocation — the headless AI/preview roll-outs step
    /// the sim thousands of times. Consumers (XP, damage, view SFX) read these instead of guessing
    /// timing from puck state.
    /// </summary>
    public readonly struct PuckSimEvent
    {
        public readonly PuckSimEventType Type;

        /// <summary>WallBounce: the bouncing puck's Id. PuckCollision: the first puck's Id. PuckDestroyed: the destroyed puck's Id.</summary>
        public readonly int PuckA;

        /// <summary>PuckCollision: the second puck's Id. Unused (-1) for WallBounce.</summary>
        public readonly int PuckB;

        /// <summary>PuckCollision: magnitude of the normal impulse applied. Unused (0) for WallBounce.</summary>
        public readonly float Impulse;

        private PuckSimEvent(PuckSimEventType type, int puckA, int puckB, float impulse)
        {
            Type = type;
            PuckA = puckA;
            PuckB = puckB;
            Impulse = impulse;
        }

        public static PuckSimEvent WallBounce(int puckId)
        {
            return new PuckSimEvent(PuckSimEventType.WallBounce, puckId, -1, 0f);
        }

        public static PuckSimEvent PuckCollision(int a, int b, float impulse)
        {
            return new PuckSimEvent(PuckSimEventType.PuckCollision, a, b, impulse);
        }

        public static PuckSimEvent PuckDestroyed(int puckId)
        {
            return new PuckSimEvent(PuckSimEventType.PuckDestroyed, puckId, -1, 0f);
        }
    }
}
