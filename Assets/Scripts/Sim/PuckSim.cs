using System;
using System.Collections.Generic;
using Vector2 = UnityEngine.Vector2;

namespace Puckmite.Sim
{
    /// <summary>
    /// Deterministic, headless top-down puck simulation. Physics is hand-rolled (no Rigidbody2D /
    /// Collider2D), advances on a fixed timestep, and never uses randomness, so the same input always
    /// produces the same output. Rendering reads this; it must not depend on rendering.
    /// </summary>
    public sealed class PuckSim
    {
        /// <summary>Fixed simulation timestep, in seconds.</summary>
        public const float Dt = 1f / 120f;

        /// <summary>Safety cap so <see cref="RunToRest"/> cannot spin forever on a degenerate (e.g. frictionless) setup.</summary>
        public const int DefaultMaxSteps = 100000;

        private readonly List<Puck> _pucks;
        private readonly Vector2 _boardMin;
        private readonly Vector2 _boardMax;
        private readonly float _friction;      // constant deceleration, units/s^2
        private readonly float _restitution;   // puck-to-puck bounciness, [0, 1]
        private readonly float _restThreshold; // speed (units/s) at or below which a puck snaps to rest

        // Events produced by the current Step(). Allocated once and cleared each step so headless
        // roll-outs never allocate. The buffer is handed back from Step() and overwritten by the next.
        private readonly List<PuckSimEvent> _events = new List<PuckSimEvent>();

        public PuckSim(Vector2 boardMin, Vector2 boardMax, float friction, float restitution, float restThreshold)
        {
            _pucks = new List<Puck>();
            _boardMin = boardMin;
            _boardMax = boardMax;
            _friction = friction;
            _restitution = restitution;
            _restThreshold = restThreshold;
        }

        public IReadOnlyList<Puck> Pucks => _pucks;
        public Vector2 BoardMin => _boardMin;
        public Vector2 BoardMax => _boardMax;
        public float Friction => _friction;
        public float Restitution => _restitution;
        public float RestThreshold => _restThreshold;

        /// <summary>Adds a puck and returns its list index.</summary>
        public int AddPuck(Puck puck)
        {
            _pucks.Add(puck);
            return _pucks.Count - 1;
        }

        /// <summary>Finds a puck by Id. Returns false if no puck has that Id.</summary>
        public bool TryGetPuck(int id, out Puck puck)
        {
            int index = IndexOf(id);
            if (index < 0)
            {
                puck = default;
                return false;
            }

            puck = _pucks[index];
            return true;
        }

        /// <summary>Sets the velocity of the puck with the given Id. Returns false if not found.</summary>
        public bool SetVelocity(int id, Vector2 velocity)
        {
            int index = IndexOf(id);
            if (index < 0)
            {
                return false;
            }

            Puck p = _pucks[index];
            p.Velocity = velocity;
            _pucks[index] = p;
            return true;
        }

        /// <summary>
        /// Advances the whole simulation by one fixed timestep and returns the events that occurred
        /// during it (wall bounces, puck-to-puck impacts). The returned list is a buffer reused by the
        /// next <see cref="Step"/> call, so read it before stepping again.
        /// </summary>
        public IReadOnlyList<PuckSimEvent> Step()
        {
            _events.Clear();

            // Friction, then integrate position.
            for (int i = 0; i < _pucks.Count; i++)
            {
                Puck p = _pucks[i];
                ApplyFriction(ref p);
                p.Position += p.Velocity * Dt;
                _pucks[i] = p;
            }

            // Puck-to-puck collisions, one deterministic pass in fixed (i < j) order.
            for (int i = 0; i < _pucks.Count; i++)
            {
                for (int j = i + 1; j < _pucks.Count; j++)
                {
                    ResolvePuckPuck(i, j);
                }
            }

            // Wall reflections last, so every puck ends the step inside the board.
            for (int i = 0; i < _pucks.Count; i++)
            {
                Puck p = _pucks[i];
                ResolveWalls(ref p);
                _pucks[i] = p;
            }

            return _events;
        }

        /// <summary>True when every puck has exactly zero velocity.</summary>
        public bool AllAtRest()
        {
            for (int i = 0; i < _pucks.Count; i++)
            {
                if (_pucks[i].Velocity != Vector2.zero)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Steps until all pucks are at rest, or until maxSteps is reached (degenerate setups).
        /// Returns the number of steps taken.
        /// </summary>
        public int RunToRest(int maxSteps = DefaultMaxSteps)
        {
            int steps = 0;
            while (steps < maxSteps && !AllAtRest())
            {
                Step();
                steps++;
            }

            return steps;
        }

        /// <summary>
        /// Headless roll-out: clones the state, launches the puck with the given Id at the given
        /// velocity, runs until everything is at rest, and returns the final state. The receiver is
        /// left untouched, so landing previews and enemy AI can call this repeatedly.
        /// </summary>
        public PuckSim Simulate(int puckId, Vector2 initialVelocity)
        {
            PuckSim copy = Clone();
            if (!copy.SetVelocity(puckId, initialVelocity))
            {
                throw new ArgumentException($"No puck with Id {puckId}.", nameof(puckId));
            }

            copy.RunToRest();
            return copy;
        }

        /// <summary>Deep copy of the entire simulation state.</summary>
        public PuckSim Clone()
        {
            PuckSim copy = new PuckSim(_boardMin, _boardMax, _friction, _restitution, _restThreshold);
            copy._pucks.AddRange(_pucks); // Puck is a value type, so this copies every field of every puck.
            return copy;
        }

        private int IndexOf(int id)
        {
            for (int i = 0; i < _pucks.Count; i++)
            {
                if (_pucks[i].Id == id)
                {
                    return i;
                }
            }

            return -1;
        }

        // Constant deceleration: cut a fixed amount off the speed each step, then snap to rest once
        // the speed drops to the threshold. Direction is preserved.
        private void ApplyFriction(ref Puck p)
        {
            float speed = p.Velocity.magnitude;
            if (speed <= 0f)
            {
                p.Velocity = Vector2.zero;
                return;
            }

            float newSpeed = speed - _friction * Dt;
            if (newSpeed <= _restThreshold)
            {
                p.Velocity = Vector2.zero;
            }
            else
            {
                p.Velocity *= newSpeed / speed;
            }
        }

        // Reflect off the rectangular board, clamping the puck back inside and flipping the inward
        // velocity component. Each wall that actually reflects counts as one bounce.
        private void ResolveWalls(ref Puck p)
        {
            float minX = _boardMin.x + p.Radius;
            float maxX = _boardMax.x - p.Radius;
            float minY = _boardMin.y + p.Radius;
            float maxY = _boardMax.y - p.Radius;

            if (p.Position.x < minX)
            {
                p.Position = new Vector2(minX, p.Position.y);
                if (p.Velocity.x < 0f)
                {
                    p.Velocity = new Vector2(-p.Velocity.x, p.Velocity.y);
                    p.BounceCount++;
                    _events.Add(PuckSimEvent.WallBounce(p.Id));
                }
            }
            else if (p.Position.x > maxX)
            {
                p.Position = new Vector2(maxX, p.Position.y);
                if (p.Velocity.x > 0f)
                {
                    p.Velocity = new Vector2(-p.Velocity.x, p.Velocity.y);
                    p.BounceCount++;
                    _events.Add(PuckSimEvent.WallBounce(p.Id));
                }
            }

            if (p.Position.y < minY)
            {
                p.Position = new Vector2(p.Position.x, minY);
                if (p.Velocity.y < 0f)
                {
                    p.Velocity = new Vector2(p.Velocity.x, -p.Velocity.y);
                    p.BounceCount++;
                    _events.Add(PuckSimEvent.WallBounce(p.Id));
                }
            }
            else if (p.Position.y > maxY)
            {
                p.Position = new Vector2(p.Position.x, maxY);
                if (p.Velocity.y > 0f)
                {
                    p.Velocity = new Vector2(p.Velocity.x, -p.Velocity.y);
                    p.BounceCount++;
                    _events.Add(PuckSimEvent.WallBounce(p.Id));
                }
            }
        }

        // Circle-circle resolution: separate overlapping pucks along the contact normal (split by
        // inverse mass), then apply a normal impulse with restitution when they are approaching.
        private void ResolvePuckPuck(int i, int j)
        {
            Puck a = _pucks[i];
            Puck b = _pucks[j];

            Vector2 delta = b.Position - a.Position;
            float distance = delta.magnitude;
            float minDistance = a.Radius + b.Radius;
            if (distance >= minDistance)
            {
                return; // not touching
            }

            float invMassA = a.Mass > 0f ? 1f / a.Mass : 0f;
            float invMassB = b.Mass > 0f ? 1f / b.Mass : 0f;
            float invMassSum = invMassA + invMassB;
            if (invMassSum <= 0f)
            {
                return; // both immovable
            }

            // Deterministic normal even in the degenerate case where the two centres coincide.
            Vector2 normal = distance > 0f ? delta / distance : new Vector2(1f, 0f);

            // Positional correction: push the pucks apart so they just touch, split by inverse mass.
            float penetration = minDistance - distance;
            Vector2 correction = normal * (penetration / invMassSum);
            a.Position -= correction * invMassA;
            b.Position += correction * invMassB;

            // Normal impulse with restitution, only when the pucks are moving toward each other. The
            // collision event fires here (not on mere overlap) so resting contact — corrected every
            // step but not approaching — does not spam a hit every frame.
            Vector2 relativeVelocity = b.Velocity - a.Velocity;
            float velocityAlongNormal = Vector2.Dot(relativeVelocity, normal);
            if (velocityAlongNormal < 0f)
            {
                float impulseMagnitude = -(1f + _restitution) * velocityAlongNormal / invMassSum;
                Vector2 impulse = normal * impulseMagnitude;
                a.Velocity -= impulse * invMassA;
                b.Velocity += impulse * invMassB;
                _events.Add(PuckSimEvent.PuckCollision(a.Id, b.Id, impulseMagnitude));
            }

            _pucks[i] = a;
            _pucks[j] = b;
        }
    }
}
