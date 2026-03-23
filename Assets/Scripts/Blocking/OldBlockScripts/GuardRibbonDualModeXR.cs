using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

[RequireComponent(typeof(LineRenderer))]
public class GuardRibbonDualModeXR : MonoBehaviour
{
    [Header("References")]
    public Transform rightHandTransform;
    public RibbonSegment segmentPrefab;

    [Header("Input")]
    [Range(0f, 1f)] public float gripThreshold = 0.1f;   // hold grip to enter guard mode

    [Header("Idle Look (non-blocking)")]
    public float idleWidth = 0.03f;
    public Gradient idleGradient;
    public float idleTrailLifetimeSeconds = 0.20f;       // how long idle points stay
    public float idlePointSpacing = 0.03f;               // spacing between points
    public float idleDelaySeconds = 0.06f;               // trail lag behind hand

    [Header("Guard Look (blocking)")]
    public float guardWidth = 0.08f;
    public Gradient guardGradient;
    public float guardPointSpacing = 0.04f;
    public float guardDelaySeconds = 0.08f;
    public float freezeSeconds = 0.50f;                  // how long it stays after release

    [Header("Collision (only in guard mode)")]
    public float collisionThicknessMeters = 0.08f;
    public float segmentOverlapMeters = 0.02f;
    public int maxSegments = 64;
    public int poolSize = 64;

    [Header("Smoothing / Safety")]
    public float stabilization = 12f;
    public float snapDistanceMeters = 0.35f;
    public float maxTeleportDistanceMeters = 0.75f;

    // XR
    InputDevice rightDevice; bool deviceValid;

    // Visual
    LineRenderer line;

    // Points (shared for both modes)
    readonly List<Vector3> points = new();
    readonly List<float> pointTimes = new();   // used for idle lifetime pruning

    // Collision segments (guard only)
    SegmentPool pool;
    readonly List<RibbonSegment> activeSegments = new();

    // History for trail delay
    readonly List<Vector3> handPosHistory = new();
    readonly List<float> handTimeHistory = new();
    public float historyKeepSeconds = 0.6f;

    // State
    bool guardHeld;
    bool frozen;            // guard released and frozen on screen
    float frozenEndTime;

    // Teleport + smoothing
    Vector3 lastRawHandPos; bool hasLastRaw;
    Vector3 smoothedPos; bool hasSmoothed;

    void Awake()
    {
        line = GetComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.positionCount = 0;

        pool = new SegmentPool(segmentPrefab, poolSize, null);
        TryFindRightDevice();

        // If gradients are empty, Unity will draw white. That's fine for testing.
        ApplyIdleLook();
    }

    void OnEnable()
    {
        InputDevices.deviceConnected += OnDeviceChanged;
        InputDevices.deviceDisconnected += OnDeviceChanged;
        TryFindRightDevice();
    }

    void OnDisable()
    {
        InputDevices.deviceConnected -= OnDeviceChanged;
        InputDevices.deviceDisconnected -= OnDeviceChanged;
    }

    void OnDeviceChanged(InputDevice _) => TryFindRightDevice();
    void TryFindRightDevice() { rightDevice = InputDevices.GetDeviceAtXRNode(XRNode.RightHand); deviceValid = rightDevice.isValid; }

    void Update()
    {
        if (!deviceValid) TryFindRightDevice();

        bool grip = ReadGripHeld();

        // If we are frozen, we ignore grip until freeze time ends
        if (frozen) return;

        // Transition: idle -> guard
        if (grip && !guardHeld)
            EnterGuardMode();

        // Transition: guard -> freeze
        if (!grip && guardHeld)
            BeginFreeze();
    }

    void FixedUpdate()
    {
        if (rightHandTransform == null) return;

        // If frozen, just wait until it expires, then return to idle
        if (frozen)
        {
            if (Time.time >= frozenEndTime)
            {
                ClearAll();
                ExitGuardModeToIdle();
            }
            return;
        }

        Vector3 raw = rightHandTransform.position;

        // Teleport safeguard (raw)
        if (hasLastRaw && Vector3.Distance(raw, lastRawHandPos) > maxTeleportDistanceMeters)
        {
            BreakRibbonAt(raw);
            lastRawHandPos = raw;
            return;
        }

        AddHistorySample(raw);

        // Use delay depending on mode
        float delay = guardHeld ? guardDelaySeconds : idleDelaySeconds;
        Vector3 delayed = GetDelayedHandPos(delay);

        // Smooth after delay so trail feels nice
        Vector3 p = Stabilize(delayed);

        // Use spacing depending on mode
        float spacing = guardHeld ? guardPointSpacing : idlePointSpacing;
        TryAddPoint(p, spacing);

        // Idle: prune old points so it looks like a short trail
        if (!guardHeld)
            PruneIdlePoints();

        lastRawHandPos = raw; hasLastRaw = true;
    }

    // ---------- Input ----------
    bool ReadGripHeld()
    {
        if (!deviceValid) return false;
        if (!rightDevice.TryGetFeatureValue(CommonUsages.grip, out float g)) return false;
        return g >= gripThreshold;
    }

    // ---------- Mode changes ----------
    void EnterGuardMode()
    {
        guardHeld = true;
        frozen = false;
        ResetSmoothing();
        ClearHistory();

        // Keep the existing trail and “promote” it, OR start fresh.
        // Starting fresh is usually clearer for gameplay:
        ClearAll();

        ApplyGuardLook();
    }

    void BeginFreeze()
    {
        guardHeld = false;
        frozen = true;
        frozenEndTime = Time.time + Mathf.Max(0f, freezeSeconds);

        // Stop adding points now; keep line + segments where they are.
        // (Segments are already placed; we just stop updating.)
    }

    void ExitGuardModeToIdle()
    {
        guardHeld = false;
        frozen = false;
        ResetSmoothing();
        ClearHistory();
        ApplyIdleLook();

        // After freeze ends we start drawing idle again automatically on next FixedUpdate.
    }

    void ApplyIdleLook()
    {
        line.startWidth = idleWidth; line.endWidth = idleWidth;
        if (idleGradient != null) line.colorGradient = idleGradient;
        // Important: idle is non-blocking
        ReleaseAllSegments();
    }

    void ApplyGuardLook()
    {
        line.startWidth = guardWidth; line.endWidth = guardWidth;
        if (guardGradient != null) line.colorGradient = guardGradient;
    }

    // ---------- Points ----------
    void TryAddPoint(Vector3 p, float spacing)
    {
        if (points.Count == 0)
        {
            AddPoint(p);
            return;
        }

        Vector3 last = points[points.Count - 1];
        if (Vector3.Distance(last, p) < spacing) return;

        AddPoint(p);

        // Only create collision segments in guard mode (while held)
        if (guardHeld)
        {
            AddSegment(last, p);
            TrimIfNeeded();
        }
    }

    void AddPoint(Vector3 p)
    {
        points.Add(p);
        pointTimes.Add(Time.time);

        line.positionCount = points.Count;
        line.SetPosition(points.Count - 1, p);
    }

    void PruneIdlePoints()
    {
        if (idleTrailLifetimeSeconds <= 0f) return;

        float cutoff = Time.time - idleTrailLifetimeSeconds;

        int removeCount = 0;
        for (int i = 0; i < pointTimes.Count; i++)
        {
            if (pointTimes[i] < cutoff) removeCount++;
            else break;
        }

        if (removeCount <= 0) return;

        points.RemoveRange(0, removeCount);
        pointTimes.RemoveRange(0, removeCount);

        line.positionCount = points.Count;
        for (int i = 0; i < points.Count; i++)
            line.SetPosition(i, points[i]);
    }

    // ---------- Collision (guard only) ----------
    void AddSegment(Vector3 a, Vector3 b)
    {
        if (segmentPrefab == null) return;
        RibbonSegment seg = pool.Get();
        if (seg == null) return;

        activeSegments.Add(seg);
        seg.Configure(a, b, collisionThicknessMeters, segmentOverlapMeters);
    }

    void TrimIfNeeded()
    {
        while (activeSegments.Count > maxSegments)
        {
            pool.Release(activeSegments[0]);
            activeSegments.RemoveAt(0);

            // Keep visuals matched (remove oldest point)
            if (points.Count > 0) points.RemoveAt(0);
            if (pointTimes.Count > 0) pointTimes.RemoveAt(0);

            line.positionCount = points.Count;
            for (int i = 0; i < points.Count; i++)
                line.SetPosition(i, points[i]);
        }
    }

    void ReleaseAllSegments()
    {
        for (int i = 0; i < activeSegments.Count; i++) pool.Release(activeSegments[i]);
        activeSegments.Clear();
    }

    // ---------- History / delay ----------
    void ClearHistory() { handPosHistory.Clear(); handTimeHistory.Clear(); }

    void AddHistorySample(Vector3 rawPos)
    {
        float now = Time.time;
        handPosHistory.Add(rawPos); handTimeHistory.Add(now);

        float cutoff = now - Mathf.Max(0.1f, historyKeepSeconds);
        int removeCount = 0;
        for (int i = 0; i < handTimeHistory.Count; i++) { if (handTimeHistory[i] < cutoff) removeCount++; else break; }
        if (removeCount > 0) { handPosHistory.RemoveRange(0, removeCount); handTimeHistory.RemoveRange(0, removeCount); }
    }

    Vector3 GetDelayedHandPos(float delaySeconds)
    {
        if (handTimeHistory.Count == 0) return rightHandTransform.position;
        if (delaySeconds <= 0f) return handPosHistory[handPosHistory.Count - 1];

        float target = Time.time - delaySeconds;

        if (target <= handTimeHistory[0]) return handPosHistory[0];
        int last = handTimeHistory.Count - 1;
        if (target >= handTimeHistory[last]) return handPosHistory[last];

        for (int i = 1; i < handTimeHistory.Count; i++)
        {
            if (handTimeHistory[i] >= target)
            {
                float u = Mathf.InverseLerp(handTimeHistory[i - 1], handTimeHistory[i], target);
                return Vector3.Lerp(handPosHistory[i - 1], handPosHistory[i], u);
            }
        }
        return handPosHistory[last];
    }

    // ---------- Smoothing ----------
    Vector3 Stabilize(Vector3 raw)
    {
        if (stabilization <= 0f) { smoothedPos = raw; hasSmoothed = true; return raw; }
        if (!hasSmoothed) { smoothedPos = raw; hasSmoothed = true; return smoothedPos; }

        if (Vector3.Distance(raw, smoothedPos) > snapDistanceMeters) { smoothedPos = raw; return smoothedPos; }

        float t = 1f - Mathf.Exp(-stabilization * Time.fixedDeltaTime);
        smoothedPos = Vector3.Lerp(smoothedPos, raw, t);
        return smoothedPos;
    }

    void ResetSmoothing() { hasSmoothed = false; }

    // ---------- Helpers ----------
    void BreakRibbonAt(Vector3 p)
    {
        ResetSmoothing();
        ClearHistory();
        ClearAll();
        AddHistorySample(p);
        AddPoint(p);
    }

    void ClearAll()
    {
        points.Clear(); pointTimes.Clear();
        line.positionCount = 0;
        ReleaseAllSegments();
    }
}
