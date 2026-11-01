namespace Skyrift.Track
{
    /// <summary>
    /// Per-frame driver for the track (TECH_DOC §4.5). A plain class rather than a MonoBehaviour so the
    /// run controller owns the tick order explicitly: coverage → recycle → origin shift. That ordering
    /// matters — recycling first means fewer live chunks to re-base, and shifting last means a chunk
    /// spawned this frame is positioned with the offset it will actually keep.
    /// </summary>
    public sealed class TrackStreamer
    {
        private ITrackGenerator _generator;
        private IWorldOriginShifter _origin;

        public void Construct(ITrackGenerator generator, IWorldOriginShifter origin)
        {
            _generator = generator;
            _origin = origin;
        }

        /// <summary>Called once per simulated frame by the run controller. Allocation-free.</summary>
        public void Tick(float playerDistance)
        {
            if (_generator == null) return;

            _generator.EnsureCoverage(playerDistance);
            _generator.Recycle(playerDistance);
            _origin?.ShiftIfNeeded(playerDistance);
        }

        /// <summary>Pushes the current speed so the difficulty model can use it (TECH_DOC §4.5 step 1).</summary>
        public void SetPlayerSpeed(float metresPerSecond)
        {
            _generator?.SetPlayerSpeed(metresPerSecond);
        }

        public void Shutdown()
        {
            _generator?.Shutdown();
        }
    }
}