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

        // Substepping (design doc 7.4): keep the fastest puck's per-substep move under this fraction of the
        // smallest radius so it cannot pass through another puck. MaxSubsteps bounds work for extreme speeds.
        private const float SubstepMoveFraction = 0.5f;
        private const int MaxSubsteps = 64;

        private readonly List<Puck> _pucks;
        private readonly Vector2 _boardMin;
        private readonly Vector2 _boardMax;
        private readonly float _friction;          // constant deceleration, units/s^2
        private readonly float _restitution;       // puck-to-puck bounciness, [0, 1]
        private readonly float _restThreshold;     // speed (units/s) at or below which a puck snaps to rest
        private readonly float _wallRestitution;   // reflected speed kept after a wall bounce, [0, 1]; 1 = no damping
        private readonly float _collisionSpeedKept; // fraction of both pucks' speed kept after an impact, [0, 1]; 1 = no loss

        // Events produced by the current Step(). Allocated once and cleared each step so headless
        // roll-outs never allocate. The buffer is handed back from Step() and overwritten by the next.
        private readonly List<PuckSimEvent> _events = new List<PuckSimEvent>();

        // Reused index buffer, sorted by ascending puck Id, so collision/wall resolution order does not
        // depend on the (unstable) list order. Rebuilt each Step().
        private readonly List<int> _order = new List<int>();

        // Reused buffer of puck Ids destroyed this step (health <= 0), removed at the end of Step().
        private readonly List<int> _dead = new List<int>();

        public PuckSim(Vector2 boardMin, Vector2 boardMax, PuckSimConfig config)
        {
            _pucks = new List<Puck>();
            _boardMin = boardMin;
            _boardMax = boardMax;
            _friction = config.Friction;
            _restitution = config.Restitution;
            _restThreshold = config.RestThreshold;
            _wallRestitution = config.WallRestitution;
            _collisionSpeedKept = config.CollisionSpeedKept;
        }

        /// <summary>Convenience overload with no wall damping (WallRestitution = 1).</summary>
        public PuckSim(Vector2 boardMin, Vector2 boardMax, float friction, float restitution, float restThreshold)
            : this(boardMin, boardMax, new PuckSimConfig(friction, restitution, restThreshold))
        {
        }

        public IReadOnlyList<Puck> Pucks => _pucks;
        public Vector2 BoardMin => _boardMin;
        public Vector2 BoardMax => _boardMax;
        public float Friction => _friction;
        public float Restitution => _restitution;
        public float RestThreshold => _restThreshold;
        public float WallRestitution => _wallRestitution;
        public float CollisionSpeedKept => _collisionSpeedKept;

        /// <summary>Adds a puck and returns its list index.</summary>
        public int AddPuck(Puck puck)
        {
            _pucks.Add(puck);
            return _pucks.Count - 1;
        }

        /// <summary>Removes the puck with the given Id. Returns false if no puck has that Id.</summary>
        public bool RemovePuck(int id)
        {
            int index = IndexOf(id);
            if (index < 0)
            {
                return false;
            }

            _pucks.RemoveAt(index);
            return true;
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

        /// <summary>Sets the health of the puck with the given Id. Returns false if not found.</summary>
        public bool SetHealth(int id, int health)
        {
            int index = IndexOf(id);
            if (index < 0)
            {
                return false;
            }

            Puck p = _pucks[index];
            p.Health = health;
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

            // Friction once per step so the deceleration model is unchanged; the integration below is
            // split into substeps, but the total displacement over the step is the same as one full move.
            for (int i = 0; i < _pucks.Count; i++)
            {
                Puck p = _pucks[i];
                ApplyFriction(ref p);
                _pucks[i] = p;
            }

            BuildIdOrder();
            int substeps = ComputeSubstepCount();
            float subDt = Dt / substeps;

            for (int s = 0; s < substeps; s++)
            {
                for (int i = 0; i < _pucks.Count; i++)
                {
                    Puck p = _pucks[i];
                    p.Position += p.Velocity * subDt;
                    _pucks[i] = p;
                }

                // Resolve collisions then walls in ascending Id order. List-index order is unstable once
                // pucks are destroyed and recreated, which would make the result depend on layout.
                for (int oi = 0; oi < _order.Count; oi++)
                {
                    for (int oj = oi + 1; oj < _order.Count; oj++)
                    {
                        ResolvePuckPuck(_order[oi], _order[oj]);
                    }
                }

                for (int oi = 0; oi < _order.Count; oi++)
                {
                    int index = _order[oi];
                    Puck p = _pucks[index];
                    ResolveWalls(ref p);
                    _pucks[index] = p;
                }
            }

            RemoveDeadPucks();

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
            PuckSim copy = new PuckSim(_boardMin, _boardMax,
                new PuckSimConfig(_friction, _restitution, _restThreshold, _wallRestitution, _collisionSpeedKept));
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

        // Number of substeps for this step: enough that the fastest puck moves at most a fraction of the
        // smallest radius per substep. Clamped to [1, MaxSubsteps].
        private int ComputeSubstepCount()
        {
            float maxSpeed = 0f;
            float minRadius = float.MaxValue;
            for (int i = 0; i < _pucks.Count; i++)
            {
                float speed = _pucks[i].Velocity.magnitude;
                if (speed > maxSpeed)
                {
                    maxSpeed = speed;
                }

                if (_pucks[i].Radius < minRadius)
                {
                    minRadius = _pucks[i].Radius;
                }
            }

            if (maxSpeed <= 0f || minRadius <= 0f || minRadius == float.MaxValue)
            {
                return 1;
            }

            float moveThisStep = maxSpeed * Dt;
            float safeMovePerSubstep = minRadius * SubstepMoveFraction;
            int count = (int)Math.Ceiling(moveThisStep / safeMovePerSubstep);
            if (count < 1)
            {
                count = 1;
            }
            else if (count > MaxSubsteps)
            {
                count = MaxSubsteps;
            }

            return count;
        }

        // Fills _order with puck list-indices sorted by ascending Id (insertion sort: allocation-free and
        // fast on the small, usually already-sorted puck list).
        private void BuildIdOrder()
        {
            _order.Clear();
            for (int i = 0; i < _pucks.Count; i++)
            {
                _order.Add(i);
            }

            for (int i = 1; i < _order.Count; i++)
            {
                int index = _order[i];
                int id = _pucks[index].Id;
                int j = i - 1;
                while (j >= 0 && _pucks[_order[j]].Id > id)
                {
                    _order[j + 1] = _order[j];
                    j--;
                }

                _order[j + 1] = index;
            }
        }

        // Removes pucks whose health has dropped to 0 or below, emitting a PuckDestroyed event for each.
        // Processed in ascending Id order for determinism. Called after collisions and walls so the fatal
        // hit's physics is already applied this step (design doc 3.3 / 7.2).
        private void RemoveDeadPucks()
        {
            _dead.Clear();
            for (int i = 0; i < _pucks.Count; i++)
            {
                if (_pucks[i].Health <= 0)
                {
                    _dead.Add(_pucks[i].Id);
                }
            }

            if (_dead.Count == 0)
            {
                return;
            }

            _dead.Sort();
            for (int i = 0; i < _dead.Count; i++)
            {
                _events.Add(PuckSimEvent.PuckDestroyed(_dead[i]));
                RemovePuck(_dead[i]);
            }
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
                    p.Velocity = new Vector2(-p.Velocity.x * _wallRestitution, p.Velocity.y);
                    p.BounceCount++;
                    _events.Add(PuckSimEvent.WallBounce(p.Id));
                }
            }
            else if (p.Position.x > maxX)
            {
                p.Position = new Vector2(maxX, p.Position.y);
                if (p.Velocity.x > 0f)
                {
                    p.Velocity = new Vector2(-p.Velocity.x * _wallRestitution, p.Velocity.y);
                    p.BounceCount++;
                    _events.Add(PuckSimEvent.WallBounce(p.Id));
                }
            }

            if (p.Position.y < minY)
            {
                p.Position = new Vector2(p.Position.x, minY);
                if (p.Velocity.y < 0f)
                {
                    p.Velocity = new Vector2(p.Velocity.x, -p.Velocity.y * _wallRestitution);
                    p.BounceCount++;
                    _events.Add(PuckSimEvent.WallBounce(p.Id));
                }
            }
            else if (p.Position.y > maxY)
            {
                p.Position = new Vector2(p.Position.x, maxY);
                if (p.Velocity.y > 0f)
                {
                    p.Velocity = new Vector2(p.Velocity.x, -p.Velocity.y * _wallRestitution);
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

                // Feel knob (not physical): bleed a fraction of both pucks' speed on the impact so a
                // collision always costs energy, even a glancing one. Applied once per impact (this
                // branch only runs when the pucks are approaching).
                a.Velocity *= _collisionSpeedKept;
                b.Velocity *= _collisionSpeedKept;

                // Cross-team impact costs each puck 1 health (design doc 3.3). Same-team collisions deal no
                // damage (they yield XP instead — not yet implemented). Destruction is deferred to the end
                // of the step so this step's collision physics is fully applied first (3.3 / 7.2).
                if (a.Owner != b.Owner)
                {
                    a.Health--;
                    b.Health--;
                }

                _events.Add(PuckSimEvent.PuckCollision(a.Id, b.Id, impulseMagnitude));
            }

            _pucks[i] = a;
            _pucks[j] = b;
        }
    }
}
