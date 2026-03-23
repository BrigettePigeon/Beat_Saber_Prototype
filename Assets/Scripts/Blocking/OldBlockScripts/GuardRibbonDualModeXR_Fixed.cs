using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

[RequireComponent(typeof(LineRenderer))]
public class GuardRibbonDualModeXR_Fixed : MonoBehaviour
{
    [Header("References")]
    public Transform rightHandTransform;
    public RibbonSegment segmentPrefab;

    [Header("Guard Input (Grip)")]
    [Range(0f, 1f)] public float gripThreshold = 0.1f;

    [Header("Idle (always on, NON-blocking)")]
    public float idleWidth = 0.03f;
    public float idlePointSpacing = 0.02f;
    public float idleDelaySeconds = 0.06f;
    public float idleTrailLifetimeSeconds = 0.20f;
    public Gradient idleGradient;

    [Header("Guard (blocking)")]
    public float guardWidth = 0.08f;
    public float guardPointSpacing = 0.04f;
    public float guardDelaySeconds = 0.08f;
    public float freezeSeconds = 0.50f;
    public Gradient guardGradient;

    [Header("Collision (guard only)")]
    public float collisionThicknessMeters = 0.08f;
    public float segmentOverlapMeters = 0.02f;
    public int maxSegments = 64;
    public int poolSize = 64;

    [Header("Smoothing / Safety")]
    public float stabilization = 12f;
    public float snapDistanceMeters = 0.35f;
    public float maxTeleportDistanceMeters = 0.75f;

    [Header("History")]
    public float historyKeepSeconds = 0.6f;

    // XR
    InputDevice rightDevice; bool deviceValid;

    // Visual
    LineRenderer line;

    // Shared path
    readonly List<Vector3> points = new();
    readonly List<float> pointTimes = new();

    // Collision (kept during freeze)
    SegmentPool pool;
    readonly List<RibbonSegment> activeSegments = new();

    // Delay history
    readonly List<Vector3> handPosHistory = new();
    readonly List<float> handTimeHistory = new();

    // State
    bool guardHeld;
    bool frozen;
    float frozenEndTime;

    // Teleport/smoothing
    Vector3 lastRawHandPos; bool hasLastRaw;
    Vector3 smoothedPos; bool hasSmoothed;

    void Awake()
    {
        line = GetComponent<LineRenderer>();

        // IMPORTANT: force LineRenderer on so we can't "see nothing" due to it being disabled
        line.enabled = true;
        line.useWorldSpace = true;
        line.positionCount = 0;

        pool = new SegmentPool(segmentPrefab, poolSize, null);
        TryFindRightDevice();
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

    void TryFindRightDevice()
    {
        rightDevice = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        deviceValid = rightDevice.isValid;
    }

    void Update()
    {
        if (!deviceValid) TryFindRightDevice();
        if (rightHandTransform == null) return;

        // If frozen, we ignore input until freeze finishes
        if (frozen) return;

        bool grip = ReadGripHeld();

        if (grip && !guardHeld) EnterGuardMode();
        else if (!grip && guardHeld) BeginFreeze();
    }

    void FixedUpdate()
    {
        if (rightHandTransform == null) return;

        // Frozen: keep showing the ribbon where it is, then clear and return to idle
        if (frozen)
        {
            if (Time.time >= frozenEndTime)
            {
                ClearAll();
                ExitToIdle();
            }
            return;
        }

        Vector3 raw = rightHandTransform.position;

        // Teleport safeguard
        if (hasLastRaw && Vector3.Distance(raw, lastRawHandPos) > maxTeleportDistanceMeters)
        {
            BreakRibbonAt(raw);
            lastRawHandPos = raw;
            return;
        }

        AddHistorySample(raw);

        float delay = guardHeld ? guardDelaySeconds : idleDelaySeconds;
        Vector3 delayed = GetDelayedHandPos(delay);
        Vector3 drawPos = Stabilize(delayed);

        float spacing = guardHeld ? guardPointSpacing : idlePointSpacing;
        TryAddPoint(drawPos, spacing);

        if (!guardHeld) PruneIdlePoints();

        lastRawHandPos = raw; hasLastRaw = true;
    }

    bool ReadGripHeld()
    {
        if (!deviceValid) return false;

        // Most common: grip axis 0..1
        if (rightDevice.TryGetFeatureValue(CommonUsages.grip, out float g))
            return g >= gripThreshold;

        // Fallback: grip as a bool button
        if (rightDevice.TryGetFeatureValue(CommonUsages.gripButton, out bool gb))
            return gb;

        return false;
    }

    // ---------- Mode ----------
    void EnterGuardMode()
    {
        guardHeld = true;
        frozen = false;
        ResetSmoothing();
        ClearHistory();

        // Start guard clean (no leftover idle points)
        ClearAll();
        ApplyGuardLook();
    }

    void BeginFreeze()
    {
        guardHeld = false;
        frozen = true;
        frozenEndTime = Time.time + Mathf.Max(0f, freezeSeconds);
        // Keep current points + collision segments frozen in the world
    }

    void ExitToIdle()
    {
        guardHeld = false;
        frozen = false;
        ResetSmoothing();
        ClearHistory();
        ApplyIdleLook();
    }

    void ApplyIdleLook()
    {
        line.startWidth = idleWidth;
        line.endWidth = idleWidth;
        if (idleGradient != null) line.colorGradient = idleGradient;

        // Idle should never block
        ReleaseAllSegments();
    }

    void ApplyGuardLook()
    {
        line.startWidth = guardWidth;
        line.endWidth = guardWidth;
        if (guardGradient != null) line.colorGradient = guardGradient;
    }

    // ---------- Points ----------
    void TryAddPoint(Vector3 p, float spacing)
    {
        // LineRenderer needs 2 points to show a segment, so we add the first point immediately
        if (points.Count == 0) { AddPoint(p); return; }

        if (Vector3.Distance(points[points.Count - 1], p) < spacing) return;

        Vector3 a = points[points.Count - 1];
        AddPoint(p);

        if (guardHeld)
        {
            AddSegment(a, p);
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
        for (int i = 0; i < points.Count; i++) line.SetPosition(i, points[i]);
    }

    // ---------- Collision ----------
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

            if (points.Count > 0) points.RemoveAt(0);
            if (pointTimes.Count > 0) pointTimes.RemoveAt(0);

            line.positionCount = points.Count;
            for (int i = 0; i < points.Count; i++) line.SetPosition(i, points[i]);
        }
    }

    void ReleaseAllSegments()
    {
        for (int i = 0; i < activeSegments.Count; i++) pool.Release(activeSegments[i]);
        activeSegments.Clear();
    }

    // ---------- History / Delay ----------
    void ClearHistory() { handPosHistory.Clear(); handTimeHistory.Clear(); }

    void AddHistorySample(Vector3 raw)
    {
        float now = Time.time;
        handPosHistory.Add(raw); handTimeHistory.Add(now);

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