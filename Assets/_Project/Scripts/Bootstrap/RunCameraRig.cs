using UnityEngine;

namespace Skyrift.Bootstrap
{
    /// <summary>
    /// M1 greybox chase camera (TECH_DOC §3.5). A pure transform follower: it reads the player rig's world
    /// position and nothing else, so it needs no service injection and no tick ownership.
    ///
    /// <b>Why it lives in Bootstrap.</b> A camera is scene presentation wired by the composition root, and
    /// §3.3 gives UI no arrow to Track or Controls. Keeping the greybox rig here means M6 can replace it
    /// with the real camera (shake, boost FOV, chaser framing) without any module gaining a reference.
    ///
    /// <b>Why the follow is rigid.</b> The motor already eases lane changes through
    /// <c>BalanceConfig.laneChangeCurve</c>, so smoothing here would only add lag. It also survives a
    /// floating-origin shift for free: the player rig and every live chunk are re-based by the same
    /// delta inside one simulation tick (§4.6), so the post-shift world position this reads in LateUpdate
    /// keeps exactly the same framing. There is no pop at 2048 m.
    ///
    /// Deliberately <b>not</b> registered with <c>IWorldOriginShifter</c>: it re-derives its position from
    /// the target every frame, so shifting it as well would move it twice.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RunCameraRig : MonoBehaviour
    {
        [Tooltip("The player rig — the transform PlayerMotor writes, not its organisational parent.")]
        [SerializeField] private Transform target;

        [Tooltip("Camera position relative to the target. Behind and above, portrait framing.")]
        [SerializeField] private Vector3 followOffset = new Vector3(0f, 5.5f, -14f);

        [Tooltip("Point relative to the target the camera looks at. Ahead of the player so the next obstacle is readable.")]
        [SerializeField] private Vector3 lookAtOffset = new Vector3(0f, 1f, 8f);

        [Range(20f, 90f)]
        [SerializeField] private float fieldOfView = 60f;

        [Tooltip("Authored for a portrait runner. M6 adds boost FOV on top of this baseline.")]
        [SerializeField] private float nearClip = 0.3f;

        [SerializeField] private float farClip = 260f;

        [Tooltip("Solid clear colour. The scene builder copies WorldDefinition.fogColor here so the greybox " +
                 "horizon matches the authored world palette; M6 replaces this with a skybox material.")]
        [SerializeField] private Color backgroundColor = new Color(0.05f, 0.07f, 0.12f);

        private Camera _camera;
        private Transform _cached;

        /// <summary>Composition-time wiring. The scene builder assigns the rig; tests may retarget it.</summary>
        public void Construct(Transform followTarget)
        {
            target = followTarget;
        }

        private void Awake()
        {
            _cached = transform;
            _camera = GetComponent<Camera>();
            if (_camera == null) _camera = gameObject.AddComponent<Camera>();

            _camera.fieldOfView = fieldOfView;
            _camera.nearClipPlane = nearClip;
            _camera.farClipPlane = farClip;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = backgroundColor;
        }

        private void LateUpdate()
        {
            // LateUpdate, not Update: GameRoot ticks the simulation in Update, so the motor has already
            // written this frame's rig position. Reading it in Update would lag the camera by one frame.
            if (target == null) return;

            Vector3 position = target.position;
            _cached.position = position + followOffset;
            _cached.rotation = Quaternion.LookRotation(position + lookAtOffset - _cached.position, Vector3.up);
        }
    }
}