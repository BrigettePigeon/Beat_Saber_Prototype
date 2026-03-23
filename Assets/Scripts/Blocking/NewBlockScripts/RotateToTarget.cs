using UnityEngine;

public class RotateToTarget : MonoBehaviour
{
    [Header("References")]
    public Transform rightHandTransform;

    [Header("Stabilized Follow")]
    public float stabilization = 12f;
    public float snapDistanceMeters = 0.35f;

    [Header("Rotation From Movement")]
    public float rotationSpeed = 18f;
    public float minMoveDistance = 0.001f;
    public Vector3 modelAimAxis = Vector3.right; // try right / forward / up depending on your head object

    private Vector3 smoothedHandPos;
    private bool hasSmoothedHandPos;

    private Vector3 lastStablePos;
    private Vector3 lastMoveDir = Vector3.forward;
    private bool initialized;

    private void Start()
    {
        if (rightHandTransform == null) return;

        Vector3 startPos = rightHandTransform.position;
        smoothedHandPos = startPos;
        lastStablePos = startPos;
        hasSmoothedHandPos = true;
        initialized = true;

        transform.position = startPos;
    }

    private void LateUpdate()
    {
        if (rightHandTransform == null) return;

        Vector3 rawHandPos = rightHandTransform.position;
        Vector3 stableHandPos = GetStabilizedHandPos(rawHandPos);

        transform.position = stableHandPos;

        if (!initialized)
        {
            lastStablePos = stableHandPos;
            initialized = true;
        }

        Vector3 moveDelta = stableHandPos - lastStablePos;

        if (moveDelta.sqrMagnitude >= minMoveDistance * minMoveDistance)
            lastMoveDir = moveDelta.normalized;

        if (lastMoveDir.sqrMagnitude > 0.0001f)
        {
            Quaternion faceMovement = Quaternion.LookRotation(lastMoveDir, Vector3.up);
            Quaternion axisCorrection = Quaternion.FromToRotation(modelAimAxis.normalized, Vector3.forward);
            Quaternion targetRotation = faceMovement * axisCorrection;

            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                targetRotation,
                rotationSpeed * Time.deltaTime
            );
        }

        lastStablePos = stableHandPos;
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