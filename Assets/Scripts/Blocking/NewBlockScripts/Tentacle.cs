using System.Collections.Generic;
using UnityEngine;

public class Tentacle : MonoBehaviour
{
    [Header("References")]
    public Transform rightHandTransform;
    public LineRenderer lineRend;

    [Header("Ribbon Shape")]
    public int length = 24;
    public float segmentSpacing = 0.04f;
    public float smoothSpeed = 0.03f;

    [Header("Hand Stabilization")]
    public float stabilization = 12f;
    public float snapDistanceMeters = 0.35f;

    [Header("Path Sampling")]
    public float historySampleDistance = 0.01f;
    public int maxHistoryPoints = 128;

    [Header("Idle / Gravity Feel")]
    public float idleSpeedThreshold = 0.05f;
    public float idleBlendSpeed = 10f;
    public float idleSagPerSegment = 0.006f;

    [Header("Age-Based Droop")]
    public float droopStartsAfterSeconds = 0.25f;
    public float droopFullBySeconds = 1.0f;
    public float droopPerSegment = 0.012f;
    [Range(0f, 1f)] public float youngGravityMultiplier = 0.05f;

    [Header("Optional Head Visual")]
    public Transform headVisual;
    public float headRotationSpeed = 20f;
    public Vector3 headAimAxis = Vector3.right;

    public Vector3[] segmentPoses;
    private Vector3[] segmentV;

    private readonly List<Vector3> historyPoints = new();
    private readonly List<float> historyTimes = new();

    private Vector3 smoothedHandPos;
    private bool hasSmoothedHandPos;

    private Vector3 lastStableHandPos;
    private Vector3 moveDir = Vector3.forward;
    private float idleBlend;
    private bool initialized;

    private void Start()
    {
        if (lineRend == null)
            lineRend = GetComponent<LineRenderer>();

        if (length < 2)
            length = 2;

        if (lineRend != null)
        {
            lineRend.positionCount = length;
            lineRend.useWorldSpace = true;
        }

        segmentPoses = new Vector3[length];
        segmentV = new Vector3[length];

        if (rightHandTransform == null)
            return;

        Vector3 startPos = rightHandTransform.position;
        smoothedHandPos = startPos;
        hasSmoothedHandPos = true;
        lastStableHandPos = startPos;
        initialized = true;

        historyPoints.Clear();
        historyTimes.Clear();
        historyPoints.Add(startPos);
        historyTimes.Add(Time.time);

        for (int i = 0; i < length; i++)
        {
            segmentPoses[i] = startPos;
            segmentV[i] = Vector3.zero;
        }

        if (lineRend != null)
            lineRend.SetPositions(segmentPoses);

        if (headVisual != null)
            headVisual.position = startPos;
    }

    private void LateUpdate()
    {
        if (rightHandTransform == null || lineRend == null)
            return;

        Vector3 stableHandPos = GetStabilizedHandPos(rightHandTransform.position);

        if (!initialized)
        {
            initialized = true;
            smoothedHandPos = stableHandPos;
            hasSmoothedHandPos = true;
            lastStableHandPos = stableHandPos;

            historyPoints.Clear();
            historyTimes.Clear();
            historyPoints.Add(stableHandPos);
            historyTimes.Add(Time.time);

            for (int i = 0; i < length; i++)
            {
                segmentPoses[i] = stableHandPos;
                segmentV[i] = Vector3.zero;
            }
        }

        Vector3 handDelta = stableHandPos - lastStableHandPos;
        float dt = Mathf.Max(Time.deltaTime, 0.0001f);
        float handSpeed = handDelta.magnitude / dt;

        if (handSpeed >= idleSpeedThreshold)
            moveDir = handDelta.normalized;

        AddOrUpdateHistoryPoint(stableHandPos);
        TrimHistory();

        float targetIdleBlend = handSpeed < idleSpeedThreshold ? 1f : 0f;
        idleBlend = Mathf.MoveTowards(idleBlend, targetIdleBlend, idleBlendSpeed * Time.deltaTime);

        segmentPoses[0] = stableHandPos;

        for (int i = 1; i < segmentPoses.Length; i++)
        {
            float distBack = i * segmentSpacing;
            float pointAge;
            Vector3 targetPos = GetPointAlongHistory(distBack, out pointAge);

            float age01 = GetAge01(pointAge);
            float gravityMul = Mathf.Lerp(youngGravityMultiplier, 1f, age01);

            float ageSag = droopPerSegment * i * gravityMul;
            float idleSag = idleSagPerSegment * i * idleBlend;

            targetPos += Vector3.down * (ageSag + idleSag);

            segmentPoses[i] = Vector3.SmoothDamp(
                segmentPoses[i],
                targetPos,
                ref segmentV[i],
                smoothSpeed
            );
        }

        lineRend.positionCount = segmentPoses.Length;
        lineRend.SetPositions(segmentPoses);

        if (headVisual != null)
        {
            headVisual.position = segmentPoses[0];

            if (moveDir.sqrMagnitude > 0.0001f)
            {
                Quaternion faceMovement = Quaternion.LookRotation(moveDir, Vector3.up);
                Quaternion axisCorrection = Quaternion.FromToRotation(headAimAxis.normalized, Vector3.forward);
                Quaternion targetRotation = faceMovement * axisCorrection;

                headVisual.rotation = Quaternion.Slerp(
                    headVisual.rotation,
                    targetRotation,
                    headRotationSpeed * Time.deltaTime
                );
            }
        }

        lastStableHandPos = stableHandPos;
    }

    private void AddOrUpdateHistoryPoint(Vector3 pos)
    {
        float now = Time.time;

        if (historyPoints.Count == 0)
        {
            historyPoints.Add(pos);
            historyTimes.Add(now);
            return;
        }

        if (Vector3.Distance(historyPoints[0], pos) >= historySampleDistance)
        {
            historyPoints.Insert(0, pos);
            historyTimes.Insert(0, now);
        }
        else
        {
            historyPoints[0] = pos;
            historyTimes[0] = now;
        }

        if (historyPoints.Count > maxHistoryPoints)
        {
            int removeCount = historyPoints.Count - maxHistoryPoints;
            historyPoints.RemoveRange(maxHistoryPoints, removeCount);
            historyTimes.RemoveRange(maxHistoryPoints, removeCount);
        }
    }

    private void TrimHistory()
    {
        float maxNeededLength = (length + 2) * segmentSpacing + 0.5f;
        float walked = 0f;

        for (int i = 0; i < historyPoints.Count - 1; i++)
        {
            walked += Vector3.Distance(historyPoints[i], historyPoints[i + 1]);

            if (walked > maxNeededLength)
            {
                int keepCount = i + 2;
                if (keepCount < historyPoints.Count)
                {
                    historyPoints.RemoveRange(keepCount, historyPoints.Count - keepCount);
                    historyTimes.RemoveRange(keepCount, historyTimes.Count - keepCount);
                }
                return;
            }
        }
    }

    private Vector3 GetPointAlongHistory(float distanceBack, out float ageSeconds)
    {
        float now = Time.time;
        ageSeconds = 0f;

        if (historyPoints.Count == 0)
            return transform.position;

        if (historyPoints.Count == 1)
        {
            ageSeconds = now - historyTimes[0];
            return historyPoints[0];
        }

        float remaining = distanceBack;

        for (int i = 0; i < historyPoints.Count - 1; i++)
        {
            Vector3 a = historyPoints[i];
            Vector3 b = historyPoints[i + 1];
            float segLen = Vector3.Distance(a, b);

            if (segLen <= 0.0001f)
                continue;

            if (remaining <= segLen)
            {
                float t = remaining / segLen;
                ageSeconds = Mathf.Lerp(now - historyTimes[i], now - historyTimes[i + 1], t);
                return Vector3.Lerp(a, b, t);
            }

            remaining -= segLen;
        }

        ageSeconds = now - historyTimes[historyTimes.Count - 1];
        return historyPoints[historyPoints.Count - 1];
    }

    private float GetAge01(float ageSeconds)
    {
        if (droopFullBySeconds <= droopStartsAfterSeconds)
            return ageSeconds >= droopStartsAfterSeconds ? 1f : 0f;

        float t = Mathf.InverseLerp(droopStartsAfterSeconds, droopFullBySeconds, ageSeconds);
        return t * t * (3f - 2f * t);
    }

    private Vector3 GetStabilizedHandPos(Vector3 rawPos)
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

        float t = 1f - Mathf.Exp(-stabilization * Time.deltaTime);
        smoothedHandPos = Vector3.Lerp(smoothedHandPos, rawPos, t);
        return smoothedHandPos;
    }
}