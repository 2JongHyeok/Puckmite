using Vector2 = UnityEngine.Vector2;

namespace Puckmite.Sim
{
    public enum PuckOwner
    {
        Player,
        Enemy,
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
        }

        public bool IsAtRest => Velocity == Vector2.zero;
    }
}
