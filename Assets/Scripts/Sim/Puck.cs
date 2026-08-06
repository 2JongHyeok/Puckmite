using Vector2 = UnityEngine.Vector2;

namespace Puckmite.Sim
{
    public enum PuckOwner
    {
        Player,
        Enemy,
    }

    /// <summary>Special stone behaviours for the enemy types of design doc 4.3. None is every ordinary stone.</summary>
    public enum StoneTrait
    {
        None,

        /// <summary>저격형: while armed (its owner's roll), its first contact with a player stone deals 2
        /// instead of 1, then it disarms. Passive contacts (the player striking it) always deal 1.</summary>
        Sniper,

        /// <summary>자폭형: contact with a player stone detonates it — nearby player stones take area
        /// damage and are knocked away; the bomb itself is destroyed.</summary>
        Bomb,

        /// <summary>반석형: never moved by an impact; the collision returns the full energy to whatever
        /// struck it (no restitution/impact damping loss), bouncing it far back.</summary>
        Anchor,
    }

    /// <summary>
    /// A single puck. Plain value type so cloning the simulation is a straight value copy
    /// (see <see cref="PuckSim.Clone"/>). The only UnityEngine type it touches is Vector2.
    /// </summary>
    public struct Puck
    {
        public int Id;
        public Vector2 Position;
        public Vector2 Velocity;
        public float Radius;
        public float Mass;
        public PuckOwner Owner;
        public int BounceCount;
        public int Health;
        public int Level;
        public int Xp;
        public StoneTrait Trait;
        public bool SniperArmed; // meaningful only while Trait == Sniper; set by the owner's roll, cleared on first player contact or at roll end

        public Puck(int id, Vector2 position, float radius, float mass, PuckOwner owner)
        {
            Id = id;
            Position = position;
            Velocity = Vector2.zero;
            Radius = radius;
            Mass = mass;
            Owner = owner;
            BounceCount = 0;
            Health = 5; // placeholder default (design doc: 3~5, 미정); callers can override via initializer
            Level = 1;  // design doc 3.7: level starts at 1, caps at 5
            Xp = 0;
            Trait = StoneTrait.None;
            SniperArmed = false;
        }

        public bool IsAtRest => Velocity == Vector2.zero;
    }
}
