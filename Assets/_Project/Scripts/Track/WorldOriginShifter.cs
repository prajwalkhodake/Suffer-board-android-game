using System.Collections.Generic;
using Skyrift.Core.Events;
using UnityEngine;

namespace Skyrift.Track
{
    /// <summary>
    /// Notified when the world is re-based (TECH_DOC §4.6). Anything that stores an absolute position
    /// (trails, decals, particle simulation space, audio occlusion) must subtract the delta.
    /// </summary>
    public interface IOriginShiftListener
    {
        void OnOriginShifted(double deltaMetres);
    }

    /// <summary>
    /// Keeps world coordinates small enough for float precision on a run that never ends
    /// (TECH_DOC §4.6, ADR-0007). Run distance itself is a scalar and is never affected by a shift.
    /// </summary>
    public interface IWorldOriginShifter
    {
        /// <summary>Metres the world has been translated back so far. Always a multiple of the threshold.</summary>
        double OriginOffset { get; }

        /// <summary>Shifts by exactly one threshold once the player has passed it. Call once per frame.</summary>
        void ShiftIfNeeded(float playerDistance);

        /// <summary>A root that must move with the world (player rig, chaser rig, VFX root).</summary>
        void RegisterRoot(Transform root);

        void UnregisterRoot(Transform root);

        void RegisterListener(IOriginShiftListener listener);
        void UnregisterListener(IOriginShiftListener listener);

        /// <summary>Restores every registered root and clears the offset. Call before a new run starts.</summary>
        void ResetForRun();
    }

    /// <inheritdoc cref="IWorldOriginShifter"/>
    public sealed class WorldOriginShifter : IWorldOriginShifter
    {
        private readonly List<Transform> _roots = new List<Transform>(8);
        private readonly List<IOriginShiftListener> _listeners = new List<IOriginShiftListener>(8);
        private readonly IEventBus _bus;

        private float _threshold = 2048f;
        private float _lastShiftDistance;

        public double OriginOffset { get; private set; }

        public WorldOriginShifter(IEventBus bus)
        {
            _bus = bus;
        }

        /// <summary>
        /// The threshold should be a power of two so repeated subtraction is exact in binary floating
        /// point; the authored default is 2048 m. Composition-time only.
        /// </summary>
        public void Configure(float thresholdMetres)
        {
            _threshold = thresholdMetres > 0f ? thresholdMetres : 2048f;
        }

        public void RegisterRoot(Transform root)
        {
            if (root != null && !_roots.Contains(root)) _roots.Add(root);
        }

        public void UnregisterRoot(Transform root)
        {
            if (root != null) _roots.Remove(root);
        }

        public void RegisterListener(IOriginShiftListener listener)
        {
            if (listener != null && !_listeners.Contains(listener)) _listeners.Add(listener);
        }

        public void UnregisterListener(IOriginShiftListener listener)
        {
            if (listener != null) _listeners.Remove(listener);
        }

        public void ShiftIfNeeded(float playerDistance)
        {
            if (playerDistance - _lastShiftDistance < _threshold) return;
            ApplyShift(_threshold);
            _lastShiftDistance += _threshold;
        }

        public void ResetForRun()
        {
            if (OriginOffset != 0d) ApplyShift(-OriginOffset);
            OriginOffset = 0d;
            _lastShiftDistance = 0f;
        }

        private void ApplyShift(double delta)
        {
            // One Vector3 for the whole pass: no per-root allocation, and roots are top-level so
            // world position is the correct space to translate.
            var translation = new Vector3(0f, 0f, (float)delta);

            for (int i = 0; i < _roots.Count; i++)
            {
                Transform root = _roots[i];
                if (root != null) root.position -= translation;
            }

            OriginOffset += delta;

            for (int i = 0; i < _listeners.Count; i++) _listeners[i].OnOriginShifted(delta);

            _bus?.Publish(new OriginShiftedEvent(OriginOffset, delta));
        }
    }
}