using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

[RequireComponent(typeof(LineRenderer))]
public class GuardRibbonLineXR : MonoBehaviour
{
    [Header("References")]
    public Transform rightHandTransform;
    public RibbonSegment segmentPrefab;

    [Header("Ribbon Visual")]
    public float widthMeters = 0.08f;
    public float pointSpacingMeters = 0.04f;

    [Tooltip("Makes corners look smoother (visual only).")]
    public int cornerVertices = 4;

    [Tooltip("Makes ends look smoother (visual only).")]
    public int endCapVertices = 4;

    [Header("Collision")]
    public float collisionThicknessMeters = 0.08f;
    public float segmentOverlapMeters = 0.02f;
    public int maxSegments = 64;
    public int poolSize = 64;

    [Header("Lifetime While Held")]
    public float activeSeconds = 0.25f;

    [Header("Freeze On Release")]
    public bool freezeOnRelease = true;
    public float freezeSeconds = 0.35f; // 0 = forever

    [Header("Stability Safety")]
    public float maxTeleportDistanceMeters = 0.75f;

    [Header("Stabilization (anti-jitter)")]
    [Tooltip("0 = no smoothing. Higher = smoother but slightly more lag.")]
    public float stabilization = 12f;

    [Tooltip("If the hand jumps farther than this, snap instantly (prevents weird long pulls).")]
    public float snapDistanceMeters = 0.35f;

    [Header("XR Input")]
    public GuardInputMode inputMode = GuardInputMode.Trigger;
    [Range(0f, 1f)] public float pressedThreshold = 0.1f;
    public enum GuardInputMode { Trigger, Grip }

    // XR
    InputDevice rightHandDevice;
    bool deviceValid;

    // Visual + data
    LineRenderer line;
    readonly List<Vector3> points = new();
    readonly List<RibbonSegment> activeSegments = new();
    SegmentPool pool;

    // State
    bool drawingActive;
    bool frozenActive;
    bool lockoutUntilRelease;

    // Timers
    float disableAtTime;
    float frozenDisableAtTime;

    // Teleport detection
    Vector3 lastFixedRawHandPos;
    bool hasLastFixedRawHandPos;

    // Stabilization state
    Vector3 smoothedHandPos;
    bool hasSmoothedHandPos;

    void Awake()
    {
        line = GetComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.enabled = false;
        ApplyLineSettings();

        pool = new SegmentPool(segmentPrefab, poolSize, null);
        TryFindRightHandDevice();
    }

    void OnEnable()
    {
        InputDevices.deviceConnected += OnDeviceChanged;
        InputDevices.deviceDisconnected += OnDeviceChanged;
        TryFindRightHandDevice();
    }

    void OnDisable()
    {
        InputDevices.deviceConnected -= OnDeviceChanged;
        InputDevices.deviceDisconnected -= OnDeviceChanged;
    }

    void OnValidate()
    {
        if (line != null) ApplyLineSettings();
    }

    void ApplyLineSettings()
    {
        line.startWidth = widthMeters;
        line.endWidth = widthMeters;
        line.numCornerVertices = Mathf.Max(0, cornerVertices);
        line.numCapVertices = Mathf.Max(0, endCapVertices);
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
        // Frozen: don't modify points/segments; optionally expire it
        if (frozenActive)
        {
            if (freezeSeconds > 0f && Time.time >= frozenDisableAtTime)
                EndFrozen();
            return;
        }

        if (!drawingActive || rightHandTransform == null)
            return;

        // Auto-end while held
        if (Time.time >= disableAtTime)
        {
            AutoEndWhileHeld();
            return;
        }

        Vector3 rawHandPos = rightHandTransform.position;

        // Teleport safeguard uses RAW tracking to catch real jumps immediately
        if (hasLastFixedRawHandPos && Vector3.Distance(rawHandPos, lastFixedRawHandPos) > maxTeleportDistanceMeters)
        {
            BreakRibbonAt(rawHandPos);
            lastFixedRawHandPos = rawHandPos;
            return;
        }

        // Stabilize for smooth “Procreate brush” feel (visual + collision use the same stabilized pos)
        Vector3 handPos = GetStabilizedHandPos(rawHandPos);

        TryAddPoint(handPos);

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

    // -------------------- Stabilization --------------------

    Vector3 GetStabilizedHandPos(Vector3 rawPos)
    {
        // If stabilization is 0, behave exactly like before
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

        // If the hand “jumps”, snap so we don’t drag a huge curve through space
        if (Vector3.Distance(rawPos, smoothedHandPos) > snapDistanceMeters)
        {
            smoothedHandPos = rawPos;
            return smoothedHandPos;
        }

        // Exponential smoothing (stable across frame rates)
        float t = 1f - Mathf.Exp(-stabilization * Time.fixedDeltaTime);
        smoothedHandPos = Vector3.Lerp(smoothedHandPos, rawPos, t);
        return smoothedHandPos;
    }

    void ResetStabilization()
    {
        hasSmoothedHandPos = false;
    }

    // -------------------- Lifecycle --------------------

    void BeginDraw()
    {
        if (rightHandTransform == null) return;

        drawingActive = true;
        frozenActive = false;
        hasLastFixedRawHandPos = false;
        ResetStabilization();

        disableAtTime = Time.time + activeSeconds;

        ClearAll();
        SetRibbonEnabled(true);

        // Start from the current stabilized position
        AddPoint(GetStabilizedHandPos(rightHandTransform.position));
    }

    void ReleaseDraw()
    {
        drawingActive = false;
        hasLastFixedRawHandPos = false;
        ResetStabilization();

        if (!freezeOnRelease)
        {
            ClearAll();
            SetRibbonEnabled(false);
            return;
        }

        frozenActive = true;
        if (freezeSeconds > 0f)
            frozenDisableAtTime = Time.time + freezeSeconds;
    }

    void AutoEndWhileHeld()
    {
        drawingActive = false;
        lockoutUntilRelease = true;
        hasLastFixedRawHandPos = false;
        ResetStabilization();

        ClearAll();
        SetRibbonEnabled(false);
    }

    void EndFrozen()
    {
        frozenActive = false;
        ResetStabilization();
        ClearAll();
        SetRibbonEnabled(false);
    }

    // -------------------- Ribbon + Collision --------------------

    void SetRibbonEnabled(bool enabled)
    {
        line.enabled = enabled;
        if (!enabled) line.positionCount = 0;
    }

    void BreakRibbonAt(Vector3 handPos)
    {
        ResetStabilization();
        ClearAll();
        SetRibbonEnabled(true);
        AddPoint(handPos);
    }

    void TryAddPoint(Vector3 handPos)
    {
        if (points.Count == 0)
        {
            AddPoint(handPos);
            return;
        }

        Vector3 last = points[points.Count - 1];
        if (Vector3.Distance(last, handPos) < pointSpacingMeters)
            return;

        AddPoint(handPos);
        AddSegment(last, handPos);
        TrimIfNeeded();
    }

    void AddPoint(Vector3 p)
    {
        points.Add(p);
        line.positionCount = points.Count;
        line.SetPosition(points.Count - 1, p);
    }

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

            // refresh line positions (no allocations)
            line.positionCount = points.Count;
            for (int i = 0; i < points.Count; i++)
                line.SetPosition(i, points[i]);
        }
    }

    void ClearAll()
    {
        points.Clear();
        line.positionCount = 0;

        for (int i = 0; i < activeSegments.Count; i++)
            pool.Release(activeSegments[i]);

        activeSegments.Clear();
    }
}