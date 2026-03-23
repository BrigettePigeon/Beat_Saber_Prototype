using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public class GuardRibbonTrailXR : MonoBehaviour
{
    [Header("References")]
    public Transform rightHandTransform;
    public RibbonSegment segmentPrefab;

    [Header("Ribbon Visual")]
    public float widthMeters = 0.08f;
    public float pointSpacingMeters = 0.04f;
    public int cornerVertices = 0;
    public int endCapVertices = 0;

    [Header("Sampling (anti-jitter)")]
    [Tooltip("Must move at least this much before we add a new point (prevents jitter sampling).")]
    public float minSampleDistanceMeters = 0.03f;

    [Header("Gap Smoothing")]
    public int maxSubdivisionsPerGap = 8;

    [Header("Trail Delay")]
    public float trailDelaySeconds = 0.08f;
    public float historyKeepSeconds = 0.6f;

    [Header("Always On")]
    public bool alwaysVisible = true;

    [Header("Physics (points actually move)")]
    public float gravity = 8.0f;
    [Range(0f, 0.4f)] public float damping = 0.16f;
    [Range(0f, 0.4f)] public float idleExtraDamping = 0.12f;
    public float idleSpeedThreshold = 0.12f;
    [Range(1, 16)] public int constraintIterations = 8;

    [Tooltip("Keep this at 1 so the base rotates naturally with the ribbon.")]
    [Range(1, 16)] public int pinnedEndPoints = 1;

    [Header("Age-based droop")]
    public float droopStartsAfterSeconds = 0.4f;
    public float droopFullBySeconds = 1.6f;
    [Range(0f, 1f)] public float youngGravityMultiplier = 0.05f;

    [Header("Elastic follow to trail")]
    public float followStrengthMoving = 18f;
    public float followStrengthIdle = 3f;
    public float followFullSpeed = 2.5f;

    [Header("General smoothing")]
    [Range(0f, 0.5f)] public float shapeSmoothing = 0.12f;

    [Header("Near-top stability")]
    [Range(0.7f, 1.0f)] public float nearTopMinSpacing = 0.9f;
    [Range(1, 4)] public int nearTopProtectedSegments = 2;
    public float collapsedPairEpsilon = 0.002f;

    [Header("Recent target turn smoothing")]
    [Range(3, 16)] public int recentTurnPoints = 7;
    [Range(-1f, 1f)] public float targetTurnSmoothingDotThreshold = 0.15f;
    [Range(0f, 1f)] public float recentTargetTurnSmoothing = 0.18f;

    [Header("Bend / curvature constraint")]
    [Range(60f, 170f)] public float maxTurnAngleDegrees = 115f;
    [Range(0f, 1f)] public float bendConstraintStrength = 0.65f;

    [Header("Anti-knot near crossings")]
    [Range(4, 20)] public int knotCheckRecentPoints = 8;
    [Range(0.25f, 2f)] public float knotAvoidanceRadiusMultiplier = 0.8f;
    [Range(0f, 1f)] public float knotAvoidanceStrength = 0.18f;

    [Header("Collision")]
    public float collisionThicknessMeters = 0.08f;
    public float segmentOverlapMeters = 0.02f;
    public int maxSegments = 64;
    public int poolSize = 64;

    [Header("Safety")]
    public float maxTeleportDistanceMeters = 0.75f;

    [Header("Stabilization (anti-jitter)")]
    public float stabilization = 12f;
    public float snapDistanceMeters = 0.35f;

    // Components
    private LineRenderer line;
    private SegmentPool pool;
    private readonly List<RibbonSegment> activeSegments = new();

    // Delay history
    private readonly List<Vector3> handPosHistory = new();
    private readonly List<float> handTimeHistory = new();

    // Target trail + age (oldest -> newest delayed)
    private readonly List<Vector3> targetTrailPoints = new();
    private readonly List<float> targetTrailAges = new();

    // Tracking cache (Update)
    private Vector3 cachedRawHandPos;
    private float cachedSpeedMps;
    private bool hasCached;
    private Vector3 prevUpdatePos;
    private float prevUpdateTime;

    // Stabilization
    private Vector3 smoothedHandPos;
    private bool hasSmoothedHandPos;

    // Teleport
    private Vector3 lastFixedRawHandPos;
    private bool hasLastFixedRawHandPos;

    // Buffers
    private Vector3[] targetBuf;
    private float[] ageBuf;
    private Vector3[] simNow;
    private Vector3[] simPrev;

    // Render interpolation
    private Vector3[] renderPrev;
    private Vector3[] renderNow;
    private Vector3[] renderInterp;
    private float lastFixedTime;

    // Temp
    private Vector3[] smoothTmp;

    void Awake()
    {
        line = GetComponent<LineRenderer>();
        line.useWorldSpace = true;
        ApplyLineSettings();

        pool = new SegmentPool(segmentPrefab, poolSize, null);

        if (historyKeepSeconds < trailDelaySeconds + 0.05f)
            historyKeepSeconds = trailDelaySeconds + 0.05f;

        if (maxSubdivisionsPerGap < 1)
            maxSubdivisionsPerGap = 1;

        line.enabled = alwaysVisible;

        if (alwaysVisible && rightHandTransform != null)
            SeedInitialRibbon(rightHandTransform.position);
    }

    void OnValidate()
    {
        if (line != null)
            ApplyLineSettings();
    }

    void ApplyLineSettings()
    {
        line.startWidth = widthMeters;
        line.endWidth = widthMeters;
        line.numCornerVertices = Mathf.Max(0, cornerVertices);
        line.numCapVertices = Mathf.Max(0, endCapVertices);
        line.textureMode = LineTextureMode.Tile;
        line.alignment = LineAlignment.View;
    }

    void Update()
    {
        if (!alwaysVisible || rightHandTransform == null)
            return;

        float now = Time.unscaledTime;
        cachedRawHandPos = rightHandTransform.position;

        if (!hasCached)
        {
            hasCached = true;
            prevUpdatePos = cachedRawHandPos;
            prevUpdateTime = now;
            cachedSpeedMps = 0f;
            return;
        }

        float dt = now - prevUpdateTime;
        if (dt > 0.0001f)
            cachedSpeedMps = Vector3.Distance(cachedRawHandPos, prevUpdatePos) / dt;

        prevUpdatePos = cachedRawHandPos;
        prevUpdateTime = now;
    }

    void FixedUpdate()
    {
        if (!alwaysVisible || !hasCached || rightHandTransform == null)
            return;

        Vector3 rawHandPos = cachedRawHandPos;

        if (hasLastFixedRawHandPos &&
            Vector3.Distance(rawHandPos, lastFixedRawHandPos) > maxTeleportDistanceMeters)
        {
            SeedInitialRibbon(rawHandPos);
            lastFixedRawHandPos = rawHandPos;
            hasLastFixedRawHandPos = true;
            return;
        }

        AddHistorySample(rawHandPos);

        Vector3 delayed = GetDelayedHandPos();
        Vector3 delayedStable = GetStabilizedHandPos(delayed);

        TryAddTargetPoint(delayedStable);
        SmoothRecentTargetTurns();
        TrimTargetIfNeeded();

        for (int i = 0; i < targetTrailAges.Count; i++)
            targetTrailAges[i] += Time.fixedDeltaTime;

        BuildTargetBuffer(rawHandPos);
        StepVerlet(Time.fixedDeltaTime, cachedSpeedMps);

        EnsureRenderBuffers();
        for (int i = 0; i < simNow.Length; i++)
        {
            renderPrev[i] = renderNow[i];
            renderNow[i] = simNow[i];
        }

        lastFixedTime = Time.time;
        UpdateCollisionFrom(simNow);

        lastFixedRawHandPos = rawHandPos;
        hasLastFixedRawHandPos = true;
    }

    void LateUpdate()
    {
        if (!alwaysVisible || renderNow == null)
            return;

        float alpha = Mathf.Clamp01((Time.time - lastFixedTime) / Mathf.Max(Time.fixedDeltaTime, 1e-6f));
        int n = renderNow.Length;

        for (int i = 0; i < n; i++)
            renderInterp[i] = Vector3.Lerp(renderPrev[i], renderNow[i], alpha);

        line.enabled = true;
        line.positionCount = n;
        line.SetPositions(renderInterp);
    }

    // ---------- Seed ----------
    void SeedInitialRibbon(Vector3 handPos)
    {
        ClearHistory();
        ResetStabilization();
        targetTrailPoints.Clear();
        targetTrailAges.Clear();
        ClearAllSegments();

        int count = Mathf.Max(8, maxSegments);
        for (int i = count; i >= 1; i--)
        {
            targetTrailPoints.Add(handPos + Vector3.down * (i * pointSpacingMeters));
            targetTrailAges.Add(10f);
        }

        BuildTargetBuffer(handPos);
        EnsureSimBuffers();

        for (int i = 0; i < targetBuf.Length; i++)
            simNow[i] = simPrev[i] = targetBuf[i];

        EnsureRenderBuffers();
        for (int i = 0; i < simNow.Length; i++)
            renderPrev[i] = renderNow[i] = simNow[i];

        lastFixedTime = Time.time;
    }

    // ---------- History ----------
    void ClearHistory()
    {
        handPosHistory.Clear();
        handTimeHistory.Clear();
    }

    void AddHistorySample(Vector3 rawPos)
    {
        float now = Time.unscaledTime;

        handPosHistory.Add(rawPos);
        handTimeHistory.Add(now);

        float cutoff = now - Mathf.Max(0.1f, historyKeepSeconds);

        int removeCount = 0;
        for (int i = 0; i < handTimeHistory.Count; i++)
        {
            if (handTimeHistory[i] < cutoff) removeCount++;
            else break;
        }

        if (removeCount > 0)
        {
            handPosHistory.RemoveRange(0, removeCount);
            handTimeHistory.RemoveRange(0, removeCount);
        }
    }

    Vector3 GetDelayedHandPos()
    {
        if (handTimeHistory.Count == 0) return cachedRawHandPos;
        if (trailDelaySeconds <= 0f) return handPosHistory[handPosHistory.Count - 1];

        float targetTime = Time.unscaledTime - trailDelaySeconds;

        if (targetTime <= handTimeHistory[0]) return handPosHistory[0];

        int last = handTimeHistory.Count - 1;
        if (targetTime >= handTimeHistory[last]) return handPosHistory[last];

        for (int i = 1; i < handTimeHistory.Count; i++)
        {
            float t1 = handTimeHistory[i];
            if (t1 >= targetTime)
            {
                float t0 = handTimeHistory[i - 1];
                float u = Mathf.InverseLerp(t0, t1, targetTime);
                return Vector3.Lerp(handPosHistory[i - 1], handPosHistory[i], u);
            }
        }

        return handPosHistory[last];
    }

    // ---------- Stabilization ----------
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

    void ResetStabilization()
    {
        hasSmoothedHandPos = false;
    }

    // ---------- Target trail ----------
    void TryAddTargetPoint(Vector3 p)
    {
        if (targetTrailPoints.Count == 0)
        {
            targetTrailPoints.Add(p);
            targetTrailAges.Add(0f);
            return;
        }

        Vector3 last = targetTrailPoints[targetTrailPoints.Count - 1];
        float dist = Vector3.Distance(last, p);

        if (dist < Mathf.Max(minSampleDistanceMeters, pointSpacingMeters * 0.5f))
            return;

        int steps = Mathf.Clamp(Mathf.CeilToInt(dist / pointSpacingMeters), 1, maxSubdivisionsPerGap);
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            targetTrailPoints.Add(Vector3.Lerp(last, p, t));
            targetTrailAges.Add(0f);
        }
    }

    void SmoothRecentTargetTurns()
    {
        if (recentTargetTurnSmoothing <= 0f || targetTrailPoints.Count < 3)
            return;

        int start = Mathf.Max(1, targetTrailPoints.Count - 1 - recentTurnPoints);
        int end = targetTrailPoints.Count - 1;

        for (int i = start; i < end; i++)
        {
            if (i <= 0 || i >= targetTrailPoints.Count - 1)
                continue;

            Vector3 a = targetTrailPoints[i] - targetTrailPoints[i - 1];
            Vector3 b = targetTrailPoints[i + 1] - targetTrailPoints[i];

            float la = a.magnitude;
            float lb = b.magnitude;

            if (la < 1e-5f || lb < 1e-5f)
                continue;

            float dot = Vector3.Dot(a / la, b / lb);
            if (dot >= targetTurnSmoothingDotThreshold)
                continue;

            Vector3 avg = (targetTrailPoints[i - 1] + targetTrailPoints[i + 1]) * 0.5f;
            targetTrailPoints[i] = Vector3.Lerp(targetTrailPoints[i], avg, recentTargetTurnSmoothing);
        }
    }

    void TrimTargetIfNeeded()
    {
        while (targetTrailPoints.Count > maxSegments)
        {
            targetTrailPoints.RemoveAt(0);
            targetTrailAges.RemoveAt(0);
        }
    }

    // ---------- Buffers ----------
    void EnsureSimBuffers()
    {
        int n = targetTrailPoints.Count + 1;

        if (targetBuf == null || targetBuf.Length != n) targetBuf = new Vector3[n];
        if (ageBuf == null || ageBuf.Length != n) ageBuf = new float[n];
        if (simNow == null || simNow.Length != n) simNow = new Vector3[n];
        if (simPrev == null || simPrev.Length != n) simPrev = new Vector3[n];

        int neededSegs = Mathf.Max(0, n - 1);

        while (activeSegments.Count < neededSegs)
        {
            if (segmentPrefab == null) break;
            var seg = pool.Get();
            if (seg == null) break;
            activeSegments.Add(seg);
        }

        while (activeSegments.Count > neededSegs)
        {
            pool.Release(activeSegments[activeSegments.Count - 1]);
            activeSegments.RemoveAt(activeSegments.Count - 1);
        }
    }

    void EnsureRenderBuffers()
    {
        if (simNow == null) return;

        int n = simNow.Length;
        if (renderPrev == null || renderPrev.Length != n) renderPrev = new Vector3[n];
        if (renderNow == null || renderNow.Length != n) renderNow = new Vector3[n];
        if (renderInterp == null || renderInterp.Length != n) renderInterp = new Vector3[n];
        if (smoothTmp == null || smoothTmp.Length != n) smoothTmp = new Vector3[n];
    }

    void BuildTargetBuffer(Vector3 rawHandPos)
    {
        EnsureSimBuffers();

        int n = targetBuf.Length;
        int trailCount = targetTrailPoints.Count;

        for (int i = 0; i < trailCount; i++)
        {
            targetBuf[i] = targetTrailPoints[i];
            ageBuf[i] = targetTrailAges[i];
        }

        // live hand point
        targetBuf[n - 1] = rawHandPos;
        ageBuf[n - 1] = 0f;

        // Prevent the newest free point from collapsing into the live hand point.
        int topFree = n - 2;
        if (topFree >= 0)
        {
            float minTopSpacing = pointSpacingMeters * nearTopMinSpacing;
            float sqrMinTopSpacing = minTopSpacing * minTopSpacing;

            if ((targetBuf[n - 1] - targetBuf[topFree]).sqrMagnitude < sqrMinTopSpacing)
            {
                Vector3 dir = Vector3.up;

                if (topFree > 0)
                    dir = targetBuf[topFree] - targetBuf[topFree - 1];
                else if (simNow != null && simNow.Length == n)
                    dir = simNow[n - 1] - simNow[topFree];

                if (dir.sqrMagnitude < 1e-8f)
                    dir = Vector3.up;

                dir.Normalize();
                targetBuf[topFree] = rawHandPos - dir * minTopSpacing;
            }
        }
    }

    // ---------- Physics ----------
    float Age01(float age)
    {
        if (droopFullBySeconds <= droopStartsAfterSeconds)
            return age >= droopStartsAfterSeconds ? 1f : 0f;

        float t = Mathf.InverseLerp(droopStartsAfterSeconds, droopFullBySeconds, age);
        return t * t * (3f - 2f * t);
    }

    Vector3 GetStableSegmentDir(int i)
    {
        float epsSqr = collapsedPairEpsilon * collapsedPairEpsilon;

        if (simNow != null && i >= 0 && i + 1 < simNow.Length)
        {
            Vector3 d = simNow[i + 1] - simNow[i];
            if (d.sqrMagnitude > epsSqr)
                return d.normalized;
        }

        if (targetBuf != null && i >= 0 && i + 1 < targetBuf.Length)
        {
            Vector3 d = targetBuf[i + 1] - targetBuf[i];
            if (d.sqrMagnitude > 1e-8f)
                return d.normalized;
        }

        if (simPrev != null && i >= 0 && i + 1 < simPrev.Length)
        {
            Vector3 d = simPrev[i + 1] - simPrev[i];
            if (d.sqrMagnitude > 1e-8f)
                return d.normalized;
        }

        if (i > 0 && simNow != null && i < simNow.Length)
        {
            Vector3 d = simNow[i] - simNow[i - 1];
            if (d.sqrMagnitude > 1e-8f)
                return d.normalized;
        }

        return Vector3.up;
    }

    Vector3 GetStablePointDir(int i)
    {
        if (targetBuf != null && i > 0 && i < targetBuf.Length - 1)
        {
            Vector3 d = targetBuf[i + 1] - targetBuf[i - 1];
            if (d.sqrMagnitude > 1e-8f)
                return d.normalized;
        }

        if (simPrev != null && i > 0 && i < simPrev.Length - 1)
        {
            Vector3 d = simPrev[i + 1] - simPrev[i - 1];
            if (d.sqrMagnitude > 1e-8f)
                return d.normalized;
        }

        if (simNow != null && i > 0 && i < simNow.Length - 1)
        {
            Vector3 d = simNow[i + 1] - simNow[i - 1];
            if (d.sqrMagnitude > 1e-8f)
                return d.normalized;
        }

        return Vector3.up;
    }

    Vector3 GetStableSpanDir(int aIndex, int bIndex, int middleIndex)
    {
        if (simNow != null && aIndex >= 0 && bIndex < simNow.Length)
        {
            Vector3 d = simNow[bIndex] - simNow[aIndex];
            if (d.sqrMagnitude > 1e-8f)
                return d.normalized;
        }

        if (targetBuf != null && aIndex >= 0 && bIndex < targetBuf.Length)
        {
            Vector3 d = targetBuf[bIndex] - targetBuf[aIndex];
            if (d.sqrMagnitude > 1e-8f)
                return d.normalized;
        }

        return GetStablePointDir(middleIndex);
    }

    void SolvePairForward(int i, float desiredDistance)
    {
        Vector3 a = simNow[i];
        Vector3 dir = GetStableSegmentDir(i);
        simNow[i + 1] = a + dir * desiredDistance;
    }

    void SolvePairBackward(int i, float desiredDistance)
    {
        Vector3 b = simNow[i + 1];
        Vector3 dir = GetStableSegmentDir(i);
        simNow[i] = b - dir * desiredDistance;
    }

    void ProtectNearTopSpacing(int firstPinnedIndex)
    {
        if (firstPinnedIndex <= 0) return;

        float minDist = pointSpacingMeters * nearTopMinSpacing;
        int start = Mathf.Max(0, firstPinnedIndex - nearTopProtectedSegments);

        for (int i = firstPinnedIndex - 1; i >= start; i--)
        {
            float dist = Vector3.Distance(simNow[i], simNow[i + 1]);
            if (dist >= minDist) continue;

            Vector3 dir = GetStableSegmentDir(i);
            simNow[i] = simNow[i + 1] - dir * minDist;
            simPrev[i] = simNow[i];
        }
    }

    void ApplyBendConstraints(int firstPinnedIndex)
    {
        if (bendConstraintStrength <= 0f || simNow == null || simNow.Length < 3)
            return;

        float maxTurnRad = maxTurnAngleDegrees * Mathf.Deg2Rad;
        float minChord = 2f * pointSpacingMeters * Mathf.Cos(maxTurnRad * 0.5f);

        if (minChord <= 0f)
            return;

        int n = simNow.Length;

        for (int middle = 1; middle < n - 1; middle++)
        {
            if (middle >= firstPinnedIndex)
                break;

            int aIndex = middle - 1;
            int cIndex = middle + 1;

            bool aPinned = aIndex >= firstPinnedIndex;
            bool cPinned = cIndex >= firstPinnedIndex;

            if (aPinned && cPinned)
                continue;

            Vector3 a = simNow[aIndex];
            Vector3 c = simNow[cIndex];
            Vector3 delta = c - a;
            float dist = delta.magnitude;

            if (dist >= minChord)
                continue;

            Vector3 dir = GetStableSpanDir(aIndex, cIndex, middle);
            float correction = (minChord - dist) * bendConstraintStrength;

            if (!aPinned && !cPinned)
            {
                Vector3 half = dir * (correction * 0.5f);
                simNow[aIndex] -= half;
                simNow[cIndex] += half;
            }
            else if (!aPinned)
            {
                simNow[aIndex] -= dir * correction;
            }
            else if (!cPinned)
            {
                simNow[cIndex] += dir * correction;
            }
        }
    }

    void ApplyRecentPointRepulsion(int firstPinnedIndex)
    {
        if (knotAvoidanceStrength <= 0f || firstPinnedIndex < 4)
            return;

        float minDist = pointSpacingMeters * knotAvoidanceRadiusMultiplier;
        float minDistSqr = minDist * minDist;

        int recentStart = Mathf.Max(0, firstPinnedIndex - knotCheckRecentPoints);

        for (int i = recentStart; i < firstPinnedIndex; i++)
        {
            for (int j = 0; j < i; j++)
            {
                // skip adjacent and second-neighbor relationships
                if (Mathf.Abs(i - j) < 3)
                    continue;

                Vector3 delta = simNow[i] - simNow[j];
                float sqr = delta.sqrMagnitude;

                if (sqr >= minDistSqr)
                    continue;

                float dist = Mathf.Sqrt(Mathf.Max(sqr, 1e-10f));
                Vector3 dir = (dist > 1e-5f) ? (delta / dist) : GetStablePointDir(i);
                float push = (minDist - dist) * knotAvoidanceStrength;

                Vector3 half = dir * (push * 0.5f);
                simNow[j] -= half;
                simNow[i] += half;
            }
        }
    }

    void StepVerlet(float dt, float speedMps)
    {
        int n = simNow.Length;
        int pinned = Mathf.Clamp(pinnedEndPoints, 1, Mathf.Min(16, n));
        int firstPinnedIndex = n - pinned;

        float move01 = (followFullSpeed <= 1e-5f) ? 0f : Mathf.Clamp01(speedMps / followFullSpeed);
        float followStrength = Mathf.Lerp(followStrengthIdle, followStrengthMoving, move01);
        float followTBase = 1f - Mathf.Exp(-followStrength * dt);

        float d = damping;
        if (speedMps < idleSpeedThreshold)
            d = Mathf.Clamp01(damping + idleExtraDamping);

        // Pin the live hand point(s)
        for (int i = firstPinnedIndex; i < n; i++)
            simNow[i] = simPrev[i] = targetBuf[i];

        // Verlet integration
        for (int i = 0; i < firstPinnedIndex; i++)
        {
            float a01 = Age01(ageBuf[i]);
            float gMul = Mathf.Lerp(youngGravityMultiplier, 1f, a01);

            Vector3 accel = Vector3.down * (gravity * gMul);
            Vector3 v = (simNow[i] - simPrev[i]) * (1f - d);

            simPrev[i] = simNow[i];
            simNow[i] = simNow[i] + v + accel * (dt * dt);

            float followMul = 1f - a01;
            simNow[i] = Vector3.Lerp(simNow[i], targetBuf[i], followTBase * followMul);
        }

        // Main solve: distance + bend, repeated
        for (int iter = 0; iter < constraintIterations; iter++)
        {
            for (int i = firstPinnedIndex; i < n; i++)
                simNow[i] = targetBuf[i];

            // forward distance pass
            for (int i = 0; i < n - 1; i++)
            {
                if (i + 1 >= firstPinnedIndex) continue;
                SolvePairForward(i, pointSpacingMeters);
            }

            // backward distance pass
            for (int i = n - 2; i >= 0; i--)
            {
                if (i >= firstPinnedIndex) continue;
                SolvePairBackward(i, pointSpacingMeters);
            }

            ProtectNearTopSpacing(firstPinnedIndex);
            ApplyBendConstraints(firstPinnedIndex);
        }

        // Light overall shape smoothing
        if (shapeSmoothing > 0.0001f && firstPinnedIndex > 2)
        {
            for (int i = 0; i < n; i++)
                smoothTmp[i] = simNow[i];

            for (int i = 1; i < firstPinnedIndex - 1; i++)
            {
                Vector3 avg = (smoothTmp[i - 1] + smoothTmp[i] + smoothTmp[i + 1]) / 3f;
                simNow[i] = Vector3.Lerp(simNow[i], avg, shapeSmoothing);
            }
        }

        // Extra anti-knot pass for recent region near crossings
        ApplyRecentPointRepulsion(firstPinnedIndex);

        // Short re-solve after smoothing / repulsion
        for (int iter = 0; iter < 3; iter++)
        {
            for (int i = firstPinnedIndex; i < n; i++)
                simNow[i] = targetBuf[i];

            for (int i = 0; i < n - 1; i++)
            {
                if (i + 1 >= firstPinnedIndex) continue;
                SolvePairForward(i, pointSpacingMeters);
            }

            for (int i = n - 2; i >= 0; i--)
            {
                if (i >= firstPinnedIndex) continue;
                SolvePairBackward(i, pointSpacingMeters);
            }

            ProtectNearTopSpacing(firstPinnedIndex);
            ApplyBendConstraints(firstPinnedIndex);
        }

        ProtectNearTopSpacing(firstPinnedIndex);
    }

    // ---------- Collision ----------
    void UpdateCollisionFrom(Vector3[] pts)
    {
        if (segmentPrefab == null) return;

        int needed = pts.Length - 1;
        for (int i = 0; i < needed && i < activeSegments.Count; i++)
            activeSegments[i].Configure(pts[i], pts[i + 1], collisionThicknessMeters, segmentOverlapMeters);
    }

    void ClearAllSegments()
    {
        for (int i = 0; i < activeSegments.Count; i++)
            pool.Release(activeSegments[i]);

        activeSegments.Clear();
    }
}