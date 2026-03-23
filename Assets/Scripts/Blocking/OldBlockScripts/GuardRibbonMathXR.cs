using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// GuardRibbonMathXR
/// - Draws a ribbon visually using LineRenderer
/// - Stores ribbon points for a short lifetime
/// - NO COLLIDERS. Collision is done by projectiles using math.
/// </summary>
[RequireComponent(typeof(LineRenderer))]
public class GuardRibbonMathXR : MonoBehaviour
{
    // Easy access for projectiles. (If you later have two hands, you can store two instances.)
    public static GuardRibbonMathXR Active;

    [Header("References")]
    public Transform rightHandTransform;

    [Header("Ribbon Visual")]
    public float widthMeters = 0.08f;
    public float pointSpacingMeters = 0.04f;

    [Header("Ribbon Collision (math)")]
    [Tooltip("How 'thick' the ribbon is for math collision (radius). Example: 0.04 = 8cm wide ribbon.")]
    public float ribbonRadiusMeters = 0.04f;

    [Tooltip("How long points stay alive while drawing/frozen.")]
    public float trailLifetimeSeconds = 0.35f;

    [Header("Freeze On Release")]
    public bool freezeOnRelease = true;
    public float freezeSeconds = 0.35f; // 0 = forever

    [Header("Stability Safety")]
    public float maxTeleportDistanceMeters = 0.75f;

    [Header("Stabilization (anti-jitter)")]
    public float stabilization = 12f;
    public float snapDistanceMeters = 0.35f;

    [Header("XR Input")]
    public GuardInputMode inputMode = GuardInputMode.Trigger;
    [Range(0f, 1f)] public float pressedThreshold = 0.1f;
    public enum GuardInputMode { Trigger, Grip }

    // XR
    private InputDevice rightHandDevice;
    private bool deviceValid;

    // Visual + stored points
    private LineRenderer line;
    private readonly List<Vector3> points = new();
    private readonly List<float> pointTimes = new(); // same length as points

    // State
    private bool drawingActive;
    private bool frozenActive;
    private bool lockoutUntilRelease;
    private float frozenDisableAtTime;

    // Teleport detection
    private Vector3 lastFixedRawHandPos;
    private bool hasLastFixedRawHandPos;

    // Stabilization state
    private Vector3 smoothedHandPos;
    private bool hasSmoothedHandPos;

    void Awake()
    {
        Active = this;

        line = GetComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.enabled = false;
        line.startWidth = widthMeters;
        line.endWidth = widthMeters;

        TryFindRightHandDevice();
    }

    void OnEnable()
    {
        Active = this;
        InputDevices.deviceConnected += OnDeviceChanged;
        InputDevices.deviceDisconnected += OnDeviceChanged;
        TryFindRightHandDevice();
    }

    void OnDisable()
    {
        if (Active == this) Active = null;
        InputDevices.deviceConnected -= OnDeviceChanged;
        InputDevices.deviceDisconnected -= OnDeviceChanged;
    }

    void OnDeviceChanged(InputDevice _) => TryFindRightHandDevice();

    void TryFindRightHandDevice()
    {
        rightHandDevice = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        deviceValid = rightHandDevice.isValid;
    }

    void Update()
    {
        if (!deviceValid) TryFindRightHandDevice();

        bool pressed = ReadGuardPressed();

        // releasing clears lockout
        if (!pressed) lockoutUntilRelease = false;

        if (pressed && !drawingActive && !frozenActive && !lockoutUntilRelease)
            BeginDraw();
        else if (!pressed && drawingActive)
            ReleaseDraw();
    }

    void FixedUpdate()
    {
        // Frozen: don’t add points. Just expire if needed.
        if (frozenActive)
        {
            if (freezeSeconds > 0f && Time.time >= frozenDisableAtTime)
                ClearAndDisable();
            else
                PruneOldPoints(); // still prune while frozen so it fades naturally if lifetime is short
            return;
        }

        if (!drawingActive || rightHandTransform == null)
            return;

        Vector3 rawHandPos = rightHandTransform.position;

        // Teleport safeguard uses raw tracking to catch real jumps immediately
        if (hasLastFixedRawHandPos && Vector3.Distance(rawHandPos, lastFixedRawHandPos) > maxTeleportDistanceMeters)
        {
            BreakRibbonAt(rawHandPos);
            lastFixedRawHandPos = rawHandPos;
            return;
        }

        Vector3 handPos = GetStabilizedHandPos(rawHandPos);

        TryAddPoint(handPos);
        PruneOldPoints();

        lastFixedRawHandPos = rawHandPos;
        hasLastFixedRawHandPos = true;
    }

    bool ReadGuardPressed()
    {
        if (!deviceValid) return false;

        float value = 0f;
        bool got = inputMode == GuardInputMode.Trigger
            ? rightHandDevice.TryGetFeatureValue(CommonUsages.trigger, out value)
            : rightHandDevice.TryGetFeatureValue(CommonUsages.grip, out value);

        return got && value >= pressedThreshold;
    }

    // ---------------- Stabilization ----------------

    Vector3 GetStabilizedHandPos(Vector3 rawPos)
    {
        if (stabilization <= 0f)
        {
            smoothedHandPos = rawPos;
            hasSmoothedHandPos = true;
            return rawPos;
        }

        if (!hasSmoothedHandPos)
        {
            smoothedHandPos = rawPos;
            hasSmoothedHandPos = true;
            return smoothedHandPos;
        }

        if (Vector3.Distance(rawPos, smoothedHandPos) > snapDistanceMeters)
        {
            smoothedHandPos = rawPos;
            return smoothedHandPos;
        }

        float t = 1f - Mathf.Exp(-stabilization * Time.fixedDeltaTime);
        smoothedHandPos = Vector3.Lerp(smoothedHandPos, rawPos, t);
        return smoothedHandPos;
    }

    void ResetStabilization() => hasSmoothedHandPos = false;

    // ---------------- Lifecycle ----------------

    void BeginDraw()
    {
        drawingActive = true;
        frozenActive = false;
        hasLastFixedRawHandPos = false;
        ResetStabilization();

        points.Clear();
        pointTimes.Clear();

        line.enabled = true;
        line.positionCount = 0;

        AddPoint(GetStabilizedHandPos(rightHandTransform.position));
    }

    void ReleaseDraw()
    {
        drawingActive = false;
        hasLastFixedRawHandPos = false;
        ResetStabilization();

        if (!freezeOnRelease)
        {
            ClearAndDisable();
            return;
        }

        frozenActive = true;
        if (freezeSeconds > 0f)
            frozenDisableAtTime = Time.time + freezeSeconds;
    }

    void ClearAndDisable()
    {
        drawingActive = false;
        frozenActive = false;
        lockoutUntilRelease = false;

        points.Clear();
        pointTimes.Clear();

        line.positionCount = 0;
        line.enabled = false;
    }

    void BreakRibbonAt(Vector3 handPos)
    {
        ResetStabilization();
        points.Clear();
        pointTimes.Clear();
        line.positionCount = 0;
        line.enabled = true;
        AddPoint(handPos);
    }

    // ---------------- Points ----------------

    void TryAddPoint(Vector3 p)
    {
        if (points.Count == 0)
        {
            AddPoint(p);
            return;
        }

        if (Vector3.Distance(points[points.Count - 1], p) < pointSpacingMeters)
            return;

        AddPoint(p);
    }

    void AddPoint(Vector3 p)
    {
        points.Add(p);
        pointTimes.Add(Time.time);

        line.positionCount = points.Count;
        line.SetPosition(points.Count - 1, p);
    }

    void PruneOldPoints()
    {
        if (trailLifetimeSeconds <= 0f) return;

        float now = Time.time;

        // Remove oldest points that are too old
        int removeCount = 0;
        for (int i = 0; i < pointTimes.Count; i++)
        {
            if (now - pointTimes[i] > trailLifetimeSeconds) removeCount++;
            else break;
        }

        if (removeCount <= 0) return;

        points.RemoveRange(0, removeCount);
        pointTimes.RemoveRange(0, removeCount);

        line.positionCount = points.Count;
        for (int i = 0; i < points.Count; i++)
            line.SetPosition(i, points[i]);
    }

    // ---------------- API for projectiles ----------------

    /// <summary>
    /// Check if a swept projectile segment (from prev->curr) comes within (projectileRadius + ribbonRadius)
    /// of any ribbon segment.
    /// </summary>
    public bool SweepHitsRibbon(Vector3 sweepA, Vector3 sweepB, float projectileRadius)
    {
        if (points.Count < 2) return false;

        float radius = projectileRadius + ribbonRadiusMeters;
        float radiusSq = radius * radius;

        for (int i = 1; i < points.Count; i++)
        {
            Vector3 rA = points[i - 1];
            Vector3 rB = points[i];

            float dSq = SegmentSegmentDistanceSquared(sweepA, sweepB, rA, rB);
            if (dSq <= radiusSq)
                return true;
        }

        return false;
    }

    // Distance between two line segments in 3D (squared), from standard geometry.
    static float SegmentSegmentDistanceSquared(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
    {
        Vector3 d1 = q1 - p1;
        Vector3 d2 = q2 - p2;
        Vector3 r = p1 - p2;

        float a = Vector3.Dot(d1, d1);
        float e = Vector3.Dot(d2, d2);
        float f = Vector3.Dot(d2, r);

        float s, t;

        const float EPS = 1e-6f;

        if (a <= EPS && e <= EPS)
        {
            // both segments are points
            return Vector3.Dot(p1 - p2, p1 - p2);
        }

        if (a <= EPS)
        {
            // first segment is a point
            s = 0f;
            t = Mathf.Clamp01(f / e);
        }
        else
        {
            float c = Vector3.Dot(d1, r);

            if (e <= EPS)
            {
                // second segment is a point
                t = 0f;
                s = Mathf.Clamp01(-c / a);
            }
            else
            {
                float b = Vector3.Dot(d1, d2);
                float denom = a * e - b * b;

                if (denom != 0f)
                    s = Mathf.Clamp01((b * f - c * e) / denom);
                else
                    s = 0f;

                t = (b * s + f) / e;

                if (t < 0f)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else if (t > 1f)
                {
                    t = 1f;
                    s = Mathf.Clamp01((b - c) / a);
                }
            }
        }

        Vector3 c1 = p1 + d1 * s;
        Vector3 c2 = p2 + d2 * t;
        return Vector3.Dot(c1 - c2, c1 - c2);
    }
}
