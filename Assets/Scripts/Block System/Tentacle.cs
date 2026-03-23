using System.Collections.Generic;
using UnityEngine;

public class Tentacle : MonoBehaviour
{
    [Header("References")]
    public Transform handTransform;
    public LineRenderer lineRend;

    [Header("Ribbon")]
    public int length = 24;
    public float segmentSpacing = 0.04f;
    public float smoothTime = 0.06f;

    [Header("Float Feel")]
    public float sagPerSegment = 0.003f;      // keep very small — just a whisper of gravity
    public float historySampleDist = 0.01f;   // how often we record a new hand position

    private Vector3[] positions;
    private Vector3[] velocities;

    private readonly List<Vector3> history = new();

    private void Start()
    {
        if (lineRend == null) lineRend = GetComponent<LineRenderer>();
        lineRend.positionCount = length;
        lineRend.useWorldSpace = true;

        positions = new Vector3[length];
        velocities = new Vector3[length];

        Vector3 start = handTransform ? handTransform.position : transform.position;
        for (int i = 0; i < length; i++) positions[i] = start;
        history.Add(start);

        lineRend.SetPositions(positions);
    }

    private void LateUpdate()
    {
        if (!handTransform || !lineRend) return;

        Vector3 tip = handTransform.position;

        // Record path history
        if (Vector3.Distance(history[0], tip) >= historySampleDist)
            history.Insert(0, tip);
        else
            history[0] = tip;

        // Trim history we'll never need
        float maxLen = length * segmentSpacing + 0.5f;
        TrimHistory(maxLen);

        // Anchor tip to hand
        positions[0] = tip;

        // Each segment follows the hand's PATH, not the previous segment
        for (int i = 1; i < length; i++)
        {
            Vector3 target = SampleHistory(i * segmentSpacing)
                           + Vector3.down * (sagPerSegment * i);

            positions[i] = Vector3.SmoothDamp(
                positions[i], target, ref velocities[i], smoothTime);
        }

        lineRend.SetPositions(positions);
    }

    // Walk along history to find the point N metres back
    private Vector3 SampleHistory(float distBack)
    {
        float remaining = distBack;
        for (int i = 0; i < history.Count - 1; i++)
        {
            float seg = Vector3.Distance(history[i], history[i + 1]);
            if (seg <= 0.00001f) continue;
            if (remaining <= seg)
                return Vector3.Lerp(history[i], history[i + 1], remaining / seg);
            remaining -= seg;
        }
        return history[history.Count - 1];
    }

    private void TrimHistory(float maxLen)
    {
        float walked = 0f;
        for (int i = 0; i < history.Count - 1; i++)
        {
            walked += Vector3.Distance(history[i], history[i + 1]);
            if (walked > maxLen)
            {
                history.RemoveRange(i + 2, history.Count - i - 2);
                return;
            }
        }
    }
}
