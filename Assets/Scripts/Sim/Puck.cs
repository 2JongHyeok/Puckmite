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

        public Puck(int id, Vector2 position, float radius, float mass, PuckOwner owner)
        {
            Id = id;
            Position = position;
            Velocity = Vector2.zero;
            Radius = radius;
            Mass = mass;
            Owner = owner;
            BounceCount = 0;
        }

        public bool IsAtRest => Velocity == Vector2.zero;
    }
}
