using System;
using Skyrift.Audio;
using Skyrift.Controls;
using Skyrift.Core.Diagnostics;
using Skyrift.Core.Events;
using Skyrift.Core.Flow;
using Skyrift.Core.Ids;
using Skyrift.Core.Pooling;
using Skyrift.Core.Services;
using Skyrift.Core.Time;
using Skyrift.Gameplay;
using Skyrift.Track;
using UnityEngine;

namespace Skyrift.Bootstrap
{
    /// <summary>
    /// Development overlay and run controls for M1, which has no UI module yet.
    ///
    /// <b>Placement: Bootstrap, not UI.</b> It reads <see cref="ITrackGenerator.Debug"/> and
    /// <see cref="IInputSource.Debug"/>, and the dependency DAG (§3.3) sanctions neither <c>UI → Track</c>
    /// nor <c>UI → Controls</c>. Bootstrap is the composition root and may reference everything, so a
    /// diagnostic that spans the whole graph belongs here. The shipped HUD in M3 lives in UI and reads only
    /// Gameplay contracts and events.
    ///
    /// <b>Deliberately outside the gameplay GC budget.</b> IMGUI allocates on every repaint and the readout
    /// is a formatted string, so this component can never meet the 0 B/frame target in §11.1. It is therefore
    /// dev-only: both <c>Update</c> and <c>OnGUI</c> compile to empty bodies without <c>DEV</c> or
    /// <c>UNITY_EDITOR</c>, UiRootStep refuses to activate the object in a non-DEV build, and
    /// <c>SettingsEnforcer</c> fails a release build while <c>DEV</c> is still defined. The authoritative
    /// allocation measurement is the on-device probe, not this overlay. The text is rebuilt at
    /// <see cref="refreshHz"/>, not per frame, and the heap figure below includes the overlay's own strings —
    /// read it as a leak witness across runs, not as a per-frame budget.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DebugHud : MonoBehaviour
    {
        [SerializeField] private int fontSize = 16;
        [SerializeField] private float panelWidth = 430f;

        [Tooltip("Readout rebuild rate. The overlay allocates when it rebuilds, so this is a cost dial.")]
        [Range(1f, 30f)] [SerializeField] private float refreshHz = 4f;

        private IRunController _run;
        private IGameFlow _flow;
        private IPlayerMotor _motor;
        private IChaserSystem _chaser;
        private ITrackGenerator _generator;
        private ObstacleChunkPopulator _populator;
        private ObstacleContactIndex _obstacles;
        private ICollisionResolver _resolver;
        private IScoreCalculator _score;
        private IPoolRegistry _pools;
        private IInputSource _input;
        private IInputCommandBuffer _buffer;
        private IMusicIntensityDriver _music;
        private IEventBus _bus;
        private IClock _clock;
        private ILog _log;

        private GUIStyle _boxStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _buttonStyle;
        private GUIContent _content;

        private string _text = "booting…";
        private float _refreshTimer;
        private bool _stylesReady;

        // Frame timing is sampled every frame, never on the rebuild cadence: the §11.1 budget is about the
        // single bad frame, and a 4 Hz readout averaging four samples would hide it. Unscaled, so a paused
        // or frozen screen still reports real device frame time.
        private float _frameMsAverage;
        private float _frameMsWorst;
        private const float FrameSmoothing = 8f;

        /// <summary>
        /// Composition-time injection. Resolving here is exactly what the container is for (§4.0): the
        /// readout set can grow without changing this signature, and a missing registration is a boot
        /// failure naming the service rather than a null-reference on the first repaint.
        /// </summary>
        public void Construct(IServiceContainer services, ILog log)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));

            _run = services.Resolve<IRunController>();
            _flow = services.Resolve<IGameFlow>();
            _motor = services.Resolve<IPlayerMotor>();
            _chaser = services.Resolve<IChaserSystem>();
            _generator = services.Resolve<ITrackGenerator>();
            _populator = services.Resolve<ObstacleChunkPopulator>();
            _obstacles = services.Resolve<ObstacleContactIndex>();
            _resolver = services.Resolve<ICollisionResolver>();
            _score = services.Resolve<IScoreCalculator>();
            _pools = services.Resolve<IPoolRegistry>();
            _input = services.Resolve<IInputSource>();
            _buffer = services.Resolve<IInputCommandBuffer>();
            _music = services.Resolve<IMusicIntensityDriver>();
            _bus = services.Resolve<IEventBus>();
            _clock = services.Resolve<IClock>();
            _log = log;

            _content = new GUIContent();
            _refreshTimer = 0f;
        }

        private void Update()
        {
#if DEV || UNITY_EDITOR
            if (_run == null) return;

            float frameMs = _clock.UnscaledDeltaTime * 1000f;
            _frameMsWorst = Mathf.Max(_frameMsWorst, frameMs);
            _frameMsAverage = _frameMsAverage <= 0f
                ? frameMs
                : _frameMsAverage + (frameMs - _frameMsAverage) * (1f / FrameSmoothing);

            _refreshTimer -= _clock.UnscaledDeltaTime;
            if (_refreshTimer > 0f) return;

            _refreshTimer = 1f / Mathf.Max(1f, refreshHz);
            _text = BuildText();
#endif
        }

        private void OnGUI()
        {
#if DEV || UNITY_EDITOR
            if (_run == null) return;

            EnsureStyles();

            float width = Mathf.Min(panelWidth, Screen.width - 16f);
            GUILayout.BeginArea(new Rect(8f, 8f, width, Screen.height - 16f));
            GUILayout.BeginVertical(_boxStyle);

            _content.text = _text;
            GUILayout.Label(_content, _labelStyle);

            GUILayout.Space(6f);
            DrawControls();

            GUILayout.EndVertical();
            GUILayout.EndArea();
#endif
        }

        private void DrawControls()
        {
            RunState state = _run.State;

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Restart", _buttonStyle)) Restart();

            // One button, labelled with what it will do. Pause and Resume are the same flow edge in
            // opposite directions, and a single toggle cannot leave the run in a state the label misreports.
            bool canPause = state == RunState.Running;
            bool canResume = state == RunState.Paused;
            GUI.enabled = canPause || canResume;
            if (GUILayout.Button(canResume ? "Resume" : "Pause", _buttonStyle))
            {
                if (canResume) _run.Resume();
                else _run.Pause();
            }
            GUI.enabled = true;

            GUI.enabled = state == RunState.Running || state == RunState.Paused || state == RunState.ContinueOffer;
            if (GUILayout.Button("Quit", _buttonStyle)) _run.EndRun(EndRunReason.PlayerQuit);
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUI.enabled = state == RunState.ContinueOffer;
            if (GUILayout.Button($"Continue ({_run.ContinueSecondsRemaining:F1}s)", _buttonStyle))
            {
                // The same event the M2 offer screen will publish, so the debug path exercises the real
                // controller route rather than calling a private method.
                _bus.Publish(new ContinueRequestedEvent(ContinueSource.DebugFree));
            }

            if (GUILayout.Button("Decline", _buttonStyle)) _run.EndRun(EndRunReason.ContinueDeclined);
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        private void Restart()
        {
            // StartRunAsync refuses while a run is active, so ending first is what makes Restart work from
            // any state. EndRun guards Idle/Ended, so this is a no-op when there is nothing to end.
            _run.EndRun(EndRunReason.PlayerQuit);

            uint seed = unchecked((uint)DateTime.UtcNow.Ticks);
            _log?.Info(LogChannel.UI, $"Debug restart requested. seed={seed}");

            _bus.Publish(new RunStartRequestedEvent(
                new RunStartContext(seed, StableId.None, StableId.None, StableId.None)));
        }

        private string BuildText()
        {
            TrackDebugState track = _generator.Debug;
            InputDebugState input = _input.Debug;
            ScoreBreakdown score = _score.Breakdown;

            // Heap growth across runs is the §11.1 leak metric. GetTotalMemory(false) does not force a
            // collection, so this is the live managed heap, and it includes this overlay's own strings.
            long heap = GC.GetTotalMemory(false);

            // The worst-of-window is reported and then cleared, so each rebuild shows the worst frame since
            // the last one rather than the worst frame of the run — the number a profiler session starts from.
            float worstFrameMs = _frameMsWorst;
            _frameMsWorst = 0f;
            float fps = _frameMsAverage > 0.0001f ? 1000f / _frameMsAverage : 0f;

            return
                $"SKYRIFT M1 — dev overlay (not shipped)\n" +
                $"fps {fps:F0}   frame {_frameMsAverage:F2} ms   worst {worstFrameMs:F1} ms\n" +
                $"flow {_flow.Previous} → {_flow.Current}   run {_run.State}\n" +
                $"dist {_motor.DistanceRun:F1} m   speed {_motor.CurrentSpeed:F2} m/s\n" +
                $"lane {_motor.Lane}   x {_motor.LateralPosition:F2}   z {_motor.ForwardOffset:F1}   {_motor.VerticalState}\n" +
                $"y {_motor.HeightAboveGround:F2}   ground {_motor.GroundHeight:F2}   h {_motor.CurrentHeight:F2}   " +
                $"invuln {(_motor.IsInvulnerable ? "YES" : "no")}   recover {(_motor.IsRecovering ? "YES" : "no")}\n" +
                $"stumbles {_resolver.StumbleCount}   chaser {_chaser.Proximity:F2}\n" +
                $"score {score.Total}   x{score.Multiplier:F2}   combo {_score.Combo} (best {score.BestCombo})\n" +
                $"─ track ─\n" +
                $"live {track.LiveChunks}   gen {track.GeneratedCount}   recyc {track.RecycledCount}   " +
                $"fallback {track.FallbackCount}\n" +
                $"difficulty {track.TargetDifficulty:F2}   head {track.HeadDistance:F0}   last {track.LastChunkId.Value}\n" +
                $"obstacles {_obstacles.Count}   moving {_populator.MovingCount}\n" +
                $"─ systems ─\n" +
                $"pools {_pools.Keys.Count}   overflow {_pools.OverflowCount}\n" +
                $"input {_input.GetType().Name}   enabled {_input.IsEnabled}   pending {_buffer.PendingCount}\n" +
                $"gesture {(input.IsTracking ? "tracking" : "idle")} {input.GestureElapsed:F2}s   last {input.LastIntent}\n" +
                $"music intensity {_music.CurrentIntensity:F2}\n" +
                $"heap {heap / 1048576f:F1} MB   gen0 {GC.CollectionCount(0)}";
        }

        private void EnsureStyles()
        {
            if (_stylesReady) return;
            _stylesReady = true;

            // Built once. GUIStyle construction allocates, and OnGUI runs several times per frame.
            _boxStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(8, 8, 8, 8),
                alignment = TextAnchor.UpperLeft
            };

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                wordWrap = false,
                richText = false,
                alignment = TextAnchor.UpperLeft
            };
            _labelStyle.normal.textColor = Color.white;

            _buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = Mathf.Max(12, fontSize - 2),
                fixedHeight = fontSize + 14f
            };
        }
    }
}