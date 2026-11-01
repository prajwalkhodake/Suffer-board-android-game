using System.Collections.Generic;
using Skyrift.Core.Pooling;
using Skyrift.Data;
using UnityEngine;

namespace Skyrift.Gameplay
{
    /// <summary>
    /// Collider-instanceID → obstacle lookup (TECH_DOC §4.7, §11.2 rule 4). Trigger callbacks hand back a
    /// <see cref="Collider"/>; resolving it through this index is an O(1) dictionary hit with no
    /// <c>GetComponent</c> and no allocation, which is what keeps contacts off the GC path.
    /// Entries are added on bind and removed on despawn, so a recycled collider can never resolve to a
    /// stale obstacle.
    /// </summary>
    public sealed class ObstacleContactIndex
    {
        private readonly Dictionary<int, ObstacleInstance> _byCollider = new Dictionary<int, ObstacleInstance>(64);

        public int Count => _byCollider.Count;

        public void Register(Collider collider, ObstacleInstance obstacle)
        {
            if (collider == null || obstacle == null) return;
            _byCollider[collider.GetInstanceID()] = obstacle;
        }

        public void Unregister(Collider collider)
        {
            if (collider != null) _byCollider.Remove(collider.GetInstanceID());
        }

        public bool TryGet(int colliderInstanceId, out ObstacleInstance obstacle)
        {
            return _byCollider.TryGetValue(colliderInstanceId, out obstacle);
        }

        public void Clear() => _byCollider.Clear();
    }

    /// <summary>
    /// A pooled obstacle (TECH_DOC §4.7). Holds its definition, the behaviour selected by
    /// <c>behaviourId</c>, and the authored motion. It never decides an outcome itself — it only supplies
    /// the data the contact sensor puts into a <see cref="ContactContext"/>.
    /// </summary>
    public sealed class ObstacleInstance : PooledEntity
    {
        private ObstacleContactIndex _index;
        private Collider _collider;

        private ObstacleDefinition _definition;
        private IObstacleBehaviour _behaviour;
        private Vector3 _baseLocalPosition;
        private float _motionTime;
        private int _lane;
        private float _forwardSpeed;

        public ObstacleDefinition Definition => _definition;
        public IObstacleBehaviour Behaviour => _behaviour;
        public int Lane => _lane;
        public Collider CachedCollider => _collider;

        /// <summary>Speed along the travel axis produced by authored motion; 0 for a static obstacle.</summary>
        public float ForwardSpeed => _forwardSpeed;

        /// <summary>Height of the top surface above the ground plane, from the authored footprint.</summary>
        public float ObjectHeight => _definition != null ? _definition.footprintMetres.y : 0f;

        protected override void Awake()
        {
            base.Awake();
            // Once per created instance, never per contact: the collider is cached for the object's lifetime.
            _collider = GetComponent<Collider>();
        }

        /// <summary>Composition-time injection of the shared contact index.</summary>
        public void Construct(ObstacleContactIndex index)
        {
            _index = index;
            if (_collider == null) _collider = GetComponent<Collider>();
        }

        /// <summary>
        /// Completes a spawn. Called by the populator immediately after <c>pool.Get()</c>, because
        /// <c>OnSpawned</c> runs before the definition is known and must not register anything.
        /// </summary>
        public void Bind(ObstacleDefinition definition, IObstacleBehaviour behaviour, int lane,
            Vector3 localPosition, float motionStartTime)
        {
            _definition = definition;
            _behaviour = behaviour;
            _lane = lane;
            _baseLocalPosition = localPosition;
            _motionTime = motionStartTime;
            _forwardSpeed = 0f;

            CachedTransform.localPosition = localPosition;
            CachedTransform.localRotation = Quaternion.identity;

            if (_index != null && _collider != null) _index.Register(_collider, this);

            // Apply the authored phase immediately so the first visible frame is already correct.
            if (definition != null && definition.isMoving) TickMotion(0f);
        }

        /// <summary>Advances authored motion. Called by the populator's tick, not by a per-object Update.</summary>
        public void TickMotion(float dt)
        {
            if (_definition == null || !_definition.isMoving) return;

            ObstacleMotion motion = _definition.motion;
            if (motion.period <= 0f) return;

            _motionTime += dt;

            float angular = 2f * Mathf.PI / motion.period;
            float phase = (_motionTime * angular) + (motion.phase * 2f * Mathf.PI);
            float offset = Mathf.Sin(phase) * motion.amplitude;

            Vector3 p = _baseLocalPosition;
            switch (motion.axis)
            {
                case ObstacleMotion.MotionAxis.Lateral:
                    p.x += offset;
                    _forwardSpeed = 0f;
                    break;
                case ObstacleMotion.MotionAxis.Longitudinal:
                    p.z += offset;
                    // Derivative of the authored sine: closing speed the resolver compares against.
                    _forwardSpeed = Mathf.Cos(phase) * motion.amplitude * angular;
                    break;
                case ObstacleMotion.MotionAxis.Vertical:
                    p.y += offset;
                    _forwardSpeed = 0f;
                    break;
            }

            CachedTransform.localPosition = p;
        }

        public override void OnDespawned()
        {
            if (_index != null && _collider != null) _index.Unregister(_collider);

            _definition = null;
            _behaviour = null;
            _lane = 0;
            _forwardSpeed = 0f;
            _motionTime = 0f;
            _baseLocalPosition = Vector3.zero;
        }
    }
}