using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class GuardRibbonBonesXR : MonoBehaviour
{
    [Header("Required")]
    public Transform handTransform;   // RibbonAnchor_R (controller child)
    public Transform rootBone;        // first joint in the ribbon chain (wrist)

    [Header("Stabilization (anti-jitter)")]
    [Tooltip("0 = off. Higher = smoother but more lag. Try 10–20.")]
    public float stabilization = 14f;

    [Tooltip("If tracking jumps more than this, snap instantly (meters). Try 0.2–0.5")]
    public float snapDistanceMeters = 0.35f;

    [Header("Rope Physics")]
    [Tooltip("Earth is 9.81. Try 2–5 for floaty, 6–10 for heavier.")]
    public float gravity = 4.0f;

    [Tooltip("Base damping (air resistance). Try 0.10–0.20.")]
    [Range(0f, 0.4f)]
    public float damping = 0.14f;

    [Tooltip("Extra damping when nearly still (stops flapping). Try 0.08–0.20.")]
    [Range(0f, 0.4f)]
    public float idleExtraDamping = 0.12f;

    [Tooltip("Below this speed, idle damping kicks in (m/s). Try 0.08–0.15.")]
    public float idleSpeedThreshold = 0.10f;

    [Tooltip("More iterations = less stretch. Try 4–7.")]
    [Range(1, 12)]
    public int constraintIterations = 6;

    [Header("Bend Limit (optional, helps prevent folding)")]
    [Tooltip("0 = off. Try 15–35.")]
    [Range(0f, 60f)]
    public float maxBendDegrees = 25f;

    [Header("Gravity reduces when moving fast (optional)")]
    public bool reduceGravityWhenMovingFast = true;

    [Tooltip("Speed where gravity starts reducing (m/s). Try 2–4.")]
    public float reduceGravitySpeed = 3.0f;

    [Tooltip("Minimum gravity multiplier at high speed.")]
    [Range(0f, 1f)]
    public float minGravityMultiplier = 0.25f;

    [Header("Attachment")]
    [Tooltip("Keep the exact offset you positioned in-editor between handTransform and root bone.")]
    public bool keepEditorOffset = true;

    [Header("Rotation (no camera facing)")]
    [Tooltip("World up keeps the ribbon from rolling randomly.")]
    public Vector3 worldUp = new Vector3(0, 1, 0);

    [Tooltip("If the ribbon looks rotated 90°, try 90 or -90.")]
    public float rollDegrees = 0f;

    [Header("Safety")]
    public float maxTeleportDistanceMeters = 0.75f;

    // bones
    private readonly List<Transform> _bones = new();
    private float[] _segLen;

    // verlet points
    private Vector3[] _pPrev;
    private Vector3[] _pNow;

    // bind alignment correction
    private Quaternion[] _rotCorrection;

    // hand offset + tracking cache
    private Vector3 _rootOffsetLocal;
    private Vector3 _cachedRawHandPos;
    private float _cachedSpeedMps;
    private bool _hasCached;
    private Vector3 _prevUpdatePos;
    private float _prevUpdateTime;

    // fixed-step state
    private Vector3 _lastFixedRawHandPos;
    private bool _hasLastFixedRawHandPos;

    // stabilization state
    private Vector3 _smoothedHandPos;
    private bool _hasSmoothedHandPos;

    private bool _inited;

    void Awake() => Init();
    void OnEnable() { _hasCached = false; Init(); }

    void Init()
    {
        if (handTransform == null || rootBone == null) return;

        BuildBoneChain(rootBone);
        if (_bones.Count < 2)
        {
            Debug.LogError("GuardRibbonBonesXR: bone chain too short.");
            enabled = false;
            return;
        }

        int n = _bones.Count;

        _segLen = new float[n - 1];
        _pPrev = new Vector3[n];
        _pNow = new Vector3[n];
        _rotCorrection = new Quaternion[n];

        for (int i = 0; i < n - 1; i++)
            _segLen[i] = Vector3.Distance(_bones[i].position, _bones[i + 1].position);

        _rootOffsetLocal = keepEditorOffset
            ? handTransform.InverseTransformPoint(_bones[0].position)
            : Vector3.zero;

        for (int i = 0; i < n; i++)
        {
            Vector3 p = _bones[i].position;
            _pNow[i] = _pPrev[i] = p;
        }

        // align rotations to your bind pose
        Vector3 up = (worldUp.sqrMagnitude < 1e-6f) ? Vector3.up : worldUp.normalized;
        for (int i = 0; i < n; i++)
        {
            Vector3 dir = (i < n - 1) ? (_pNow[i + 1] - _pNow[i]).normalized
                                      : (_pNow[i] - _pNow[i - 1]).normalized;
            if (dir.sqrMagnitude < 1e-6f) dir = handTransform.forward;

            Quaternion look = Quaternion.LookRotation(dir, up);
            _rotCorrection[i] = Quaternion.Inverse(look) * _bones[i].rotation;
        }

        _hasLastFixedRawHandPos = false;
        _hasSmoothedHandPos = false;
        _inited = true;
    }

    void BuildBoneChain(Transform root)
    {
        _bones.Clear();
        Transform t = root;
        _bones.Add(t);
        while (t.childCount > 0)
        {
            t = t.GetChild(0);
            _bones.Add(t);
            if (_bones.Count > 128) break;
        }
    }

    void Update()
    {
        if (!_inited || handTransform == null) return;

        float now = Time.unscaledTime;
        _cachedRawHandPos = GetRootWorldRaw();

        if (!_hasCached)
        {
            _hasCached = true;
            _prevUpdatePos = _cachedRawHandPos;
            _prevUpdateTime = now;
            _cachedSpeedMps = 0f;
            return;
        }

        float dt = now - _prevUpdateTime;
        if (dt > 0.0001f)
            _cachedSpeedMps = Vector3.Distance(_cachedRawHandPos, _prevUpdatePos) / dt;

        _prevUpdatePos = _cachedRawHandPos;
        _prevUpdateTime = now;
    }

    Vector3 GetRootWorldRaw()
    {
        return handTransform.TransformPoint(_rootOffsetLocal);
    }

    Vector3 GetStabilizedHandPos(Vector3 rawPos)
    {
        if (stabilization <= 0f)
        {
            _smoothedHandPos = rawPos;
            _hasSmoothedHandPos = true;
            return rawPos;
        }

        if (!_hasSmoothedHandPos)
        {
            _smoothedHandPos = rawPos;
            _hasSmoothedHandPos = true;
            return _smoothedHandPos;
        }

        if (Vector3.Distance(rawPos, _smoothedHandPos) > snapDistanceMeters)
        {
            _smoothedHandPos = rawPos;
            return _smoothedHandPos;
        }

        float t = 1f - Mathf.Exp(-stabilization * Time.fixedDeltaTime);
        _smoothedHandPos = Vector3.Lerp(_smoothedHandPos, rawPos, t);
        return _smoothedHandPos;
    }

    void FixedUpdate()
    {
        if (!_inited || !_hasCached) return;

        Vector3 rawHandPos = _cachedRawHandPos;

        // teleport safeguard
        if (_hasLastFixedRawHandPos && Vector3.Distance(rawHandPos, _lastFixedRawHandPos) > maxTeleportDistanceMeters)
        {
            ResetChain(rawHandPos);
            _lastFixedRawHandPos = rawHandPos;
            _hasLastFixedRawHandPos = true;
            return;
        }

        Vector3 root = GetStabilizedHandPos(rawHandPos);

        float dt = Time.fixedDeltaTime;
        float speed = _cachedSpeedMps;

        // gravity scaling when moving fast
        float gMul = 1f;
        if (reduceGravityWhenMovingFast && reduceGravitySpeed > 1e-5f)
        {
            float t = Mathf.Clamp01(speed / reduceGravitySpeed);
            gMul = Mathf.Lerp(1f, minGravityMultiplier, t);
        }

        // damping (more when idle)
        float d = damping;
        if (speed < idleSpeedThreshold)
            d = Mathf.Clamp01(damping + idleExtraDamping);

        Vector3 accel = Vector3.down * (gravity * gMul);

        // pin root
        _pNow[0] = root;

        // verlet integrate
        for (int i = 1; i < _pNow.Length; i++)
        {
            Vector3 v = (_pNow[i] - _pPrev[i]) * (1f - d);
            _pPrev[i] = _pNow[i];
            _pNow[i] = _pNow[i] + v + accel * (dt * dt);
        }

        // constraints (length)
        for (int iter = 0; iter < constraintIterations; iter++)
        {
            _pNow[0] = root;

            for (int i = 0; i < _pNow.Length - 1; i++)
            {
                Vector3 a = _pNow[i];
                Vector3 b = _pNow[i + 1];

                Vector3 delta = b - a;
                float dist = delta.magnitude;
                if (dist < 1e-6f)
                {
                    delta = (i == 0) ? handTransform.forward : (_pNow[i] - _pNow[i - 1]);
                    dist = Mathf.Max(delta.magnitude, 1e-6f);
                }

                Vector3 dir = delta / dist;
                float target = _segLen[i];

                if (i == 0)
                {
                    _pNow[i + 1] = a + dir * target;
                }
                else
                {
                    Vector3 mid = (a + b) * 0.5f;
                    _pNow[i] = mid - dir * (target * 0.5f);
                    _pNow[i + 1] = mid + dir * (target * 0.5f);
                }
            }

            // optional bend limit (prevents folding)
            if (maxBendDegrees > 0.01f)
            {
                for (int i = 2; i < _pNow.Length; i++)
                {
                    Vector3 p0 = _pNow[i - 2];
                    Vector3 p1 = _pNow[i - 1];
                    Vector3 p2 = _pNow[i];

                    Vector3 d1 = (p1 - p0).normalized;
                    Vector3 d2 = (p2 - p1).normalized;
                    if (d1.sqrMagnitude < 1e-6f || d2.sqrMagnitude < 1e-6f) continue;

                    float ang = Vector3.Angle(d1, d2);
                    if (ang > maxBendDegrees)
                    {
                        Vector3 axis = Vector3.Cross(d1, d2);
                        if (axis.sqrMagnitude < 1e-6f) axis = Vector3.up;

                        Quaternion q = Quaternion.AngleAxis(maxBendDegrees, axis.normalized);
                        Vector3 limitedDir = (q * d1).normalized;
                        _pNow[i] = p1 + limitedDir * _segLen[i - 1];
                    }
                }
            }
        }

        _lastFixedRawHandPos = rawHandPos;
        _hasLastFixedRawHandPos = true;
    }

    void LateUpdate()
    {
        if (!_inited) return;

        Vector3 upCarry = (worldUp.sqrMagnitude < 1e-6f) ? Vector3.up : worldUp.normalized;
        Quaternion roll = Quaternion.AngleAxis(rollDegrees, Vector3.forward);

        for (int i = 0; i < _bones.Count; i++)
        {
            _bones[i].position = _pNow[i];

            Vector3 dir;
            if (i < _bones.Count - 1) dir = (_pNow[i + 1] - _pNow[i]).normalized;
            else dir = (_pNow[i] - _pNow[i - 1]).normalized;

            if (dir.sqrMagnitude < 1e-6f) dir = handTransform.forward;

            upCarry = Vector3.ProjectOnPlane(upCarry, dir).normalized;
            if (upCarry.sqrMagnitude < 1e-6f) upCarry = ((worldUp.sqrMagnitude < 1e-6f) ? Vector3.up : worldUp.normalized);

            Quaternion look = Quaternion.LookRotation(dir, upCarry);
            _bones[i].rotation = look * roll * _rotCorrection[i];
        }
    }

    void ResetChain(Vector3 root)
    {
        for (int i = 0; i < _pNow.Length; i++)
            _pNow[i] = _pPrev[i] = root;

        _hasSmoothedHandPos = false;
    }
}