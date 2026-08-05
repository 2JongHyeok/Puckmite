namespace Puckmite.Sim
{
    /// <summary>
    /// Physics parameters for a <see cref="PuckSim"/>, gathered in one place so the debug panel can tune
    /// them and callers do not pass a long list of loose floats. The values themselves are 미정 in the
    /// design doc; the default for <see cref="WallRestitution"/> is 1 (perfect reflection, no wall damping)
    /// so the plain constructor keeps the original behaviour.
    /// </summary>
    public readonly struct PuckSimConfig
    {
        public readonly float Friction;          // constant deceleration, units/s^2
        public readonly float Restitution;       // puck-to-puck bounciness, [0, 1]
        public readonly float RestThreshold;     // speed (units/s) at or below which a puck snaps to rest
        public readonly float WallRestitution;   // reflected speed kept after a wall bounce, [0, 1]; 1 = no damping
        public readonly float CollisionSpeedKept; // fraction of both pucks' speed kept after an impact, [0, 1]; 1 = no loss

        public PuckSimConfig(float friction, float restitution, float restThreshold,
            float wallRestitution = 1f, float collisionSpeedKept = 1f)
        {
            Friction = friction;
            Restitution = restitution;
            RestThreshold = restThreshold;
            WallRestitution = wallRestitution;
            CollisionSpeedKept = collisionSpeedKept;
        }
    }
}
