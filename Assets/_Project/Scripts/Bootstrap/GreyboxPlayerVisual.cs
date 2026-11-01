using Skyrift.Gameplay;
using UnityEngine;

namespace Skyrift.Bootstrap
{
    /// <summary>
    /// M1 greybox locomotion presentation (TECH_DOC §3.5). Squashes the player's visual while rolling so the
    /// greybox is actually readable during a collision test — without it a roll looks exactly like a run and
    /// the two-hit stumble model cannot be evaluated by eye. M6 replaces this with an animator.
    ///
    /// <b>Why it lives in Bootstrap.</b> Same reason as <see cref="RunCameraRig"/>: it is scene presentation
    /// wired by the composition root, and §3.3 gives UI no arrow to Gameplay. Keeping it here means the real
    /// character controller can drop it in M6 without any module gaining a reference.
    ///
    /// <b>Why the collider never changes size.</b> Contact classification is analytic (§4.7): the capsule only
    /// decides <i>whether</i> an overlap happens, and <c>IPlayerMotor.CurrentHeight</c> decides the outcome.
    /// Shrinking the trigger while rolling would therefore change nothing about the physics and would cost a
    /// broadphase update every roll. Only the visual moves.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GreyboxPlayerVisual : MonoBehaviour
    {
        [Tooltip("Visual child to squash. Left empty, this component's own transform is used.")]
        [SerializeField] private Transform visual;

        [Tooltip("Standing height the visual was authored at. The scene builder copies BalanceConfig.playerStandHeight " +
                 "here so the squash ratio is measured against the same number the collision model uses.")]
        [SerializeField] private float standHeight = 1.8f;

        [Tooltip("How much wider the visual gets at a full squash, so a roll reads as a roll rather than a shrink.")]
        [Range(1f, 2f)]
        [SerializeField] private float squashWiden = 1.35f;

        private PlayerMotor _motor;
        private Transform _cached;
        private Vector3 _baseScale;
        private Vector3 _basePosition;
        private float _lastHeight = -1f;

        private void Awake()
        {
            // Once per instance, never per frame: the motor is on the same GameObject by construction, so
            // this needs no injection and no BootstrapConfig field (§11.2 rule 2).
            _motor = GetComponent<PlayerMotor>();
            _cached = visual != null ? visual : transform;
            _baseScale = _cached.localScale;
            _basePosition = _cached.localPosition;
        }

        private void LateUpdate()
        {
            // LateUpdate, not Update: GameRoot ticks the simulation in Update, so CurrentHeight is already
            // this frame's value. Reading it in Update would lag the visual by one frame.
            if (_motor == null) return;

            float height = _motor.CurrentHeight;
            if (Mathf.Approximately(height, _lastHeight)) return;
            _lastHeight = height;

            float ratio = standHeight > 0.01f ? Mathf.Clamp(height / standHeight, 0.2f, 1f) : 1f;
            float widen = Mathf.Lerp(1f, squashWiden, 1f - ratio);

            Vector3 scale = _baseScale;
            scale.x = _baseScale.x * widen;
            scale.y = _baseScale.y * ratio;
            scale.z = _baseScale.z * widen;
            _cached.localScale = scale;

            // Keep the feet planted: the visual is centred on half its own height, so squashing about the
            // origin would sink it into the ground slab.
            Vector3 position = _basePosition;
            position.y = height * 0.5f;
            _cached.localPosition = position;
        }
    }
}