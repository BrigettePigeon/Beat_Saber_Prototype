using UnityEngine;

public class RibbonBoneGravity : MonoBehaviour
{
    [Header("Bones (top/root to tip)")]
    public Transform[] bones;

    [Header("Controller motion -> ribbon")]
    [Tooltip("How much controller translation gets carried into the ribbon chain.")]
    [Range(0f, 2f)] public float motionCarryAmount = 0.85f;

    [Tooltip("How much controller acceleration snaps the ribbon.")]
    [Range(0f, 2f)] public float accelerationInfluence = 0.35f;

    [Tooltip("How much motion each bone receives from root to tip.")]
    public AnimationCurve motionByBone = new AnimationCurve(
        new Keyframe(0f, 0.15f),
        new Keyframe(0.35f, 0.45f),
        new Keyframe(0.7f, 0.8f),
        new Keyframe(1f, 1f)
    );

    [Header("Spring follow")]
    [Tooltip("How strongly each joint tries to follow a nice chain shape.")]
    public float springStrength = 45f;

    [Tooltip("Higher = less floppy, lower = more laggy.")]
    public float velocityDamping = 7f;

    [Tooltip("How many length/bend solve passes to do each frame.")]
    [Range(1, 8)] public int constraintIterations = 3;

    [Header("Gravity")]
    [Tooltip("Overall fake gravity strength.")]
    [Range(0f, 2f)] public float gravityAmount = 0.75f;

    [Tooltip("Gravity strength from root to tip.")]
    public AnimationCurve gravityByBone = new AnimationCurve(
        new Keyframe(0f, 0f),
        new Keyframe(0.3f, 0.15f),
        new Keyframe(0.7f, 0.55f),
        new Keyframe(1f, 1f)
    );

    [Tooltip("World gravity direction.")]
    public Vector3 gravityDirection = Vector3.down;

    [Header("Bend limits")]
    [Tooltip("Max angle between neighboring segments.")]
    [Range(5f, 179f)] public float maxBendDegrees = 65f;

    [Header("Bone rotation")]
    [Tooltip("How quickly the actual bones rotate to match the simulated chain.")]
    public float rotationSharpness = 18f;

    [Tooltip("Turn this on if the rig bends the wrong direction.")]
    public bool invertBoneAxis = false;

    [Tooltip("Usually keep this on so the first bone stays stable at the hand.")]
    public bool keepRootBoneAtRestRotation = true;

    private Quaternion[] restLocalRotations;
    private Vector3[] restLocalAxes;
    private float[] segmentLengths;

    // simulated world positions of each joint
    private Vector3[] simPositions;
    private Vector3[] simVelocities;

    private Vector3 lastRootPos;
    private Vector3 lastRootVel;
    private bool hasRootHistory;

    void Awake()
    {
        CacheRigData();
        InitializeSimulationFromCurrentPose();
    }

    void OnEnable()
    {
        CacheRigData();
        InitializeSimulationFromCurrentPose();
    }

    [ContextMenu("Rebuild Ribbon Simulation")]
    public void RebuildRibbonSimulation()
    {
        CacheRigData();
        InitializeSimulationFromCurrentPose();
    }

    void LateUpdate()
    {
        if (!IsValid())
            return;

        float dt = Mathf.Max(Time.deltaTime, 0.0001f);

        Transform rootBone = bones[0];
        Vector3 rootPos = rootBone.position;

        Vector3 rootDelta = Vector3.zero;
        Vector3 rootVel = Vector3.zero;
        Vector3 rootAccel = Vector3.zero;

        if (hasRootHistory)
        {
            rootDelta = rootPos - lastRootPos;
            rootVel = rootDelta / dt;
            rootAccel = (rootVel - lastRootVel) / dt;
        }

        lastRootPos = rootPos;
        lastRootVel = rootVel;
        hasRootHistory = true;

        Vector3 gravityDir = gravityDirection.sqrMagnitude > 0.0001f
            ? gravityDirection.normalized
            : Vector3.down;

        // keep root pinned to controller-attached position
        simPositions[0] = rootPos;
        simVelocities[0] = Vector3.zero;

        if (keepRootBoneAtRestRotation)
            bones[0].localRotation = restLocalRotations[0];

        Vector3[] oldPositions = new Vector3[simPositions.Length];
        for (int i = 0; i < simPositions.Length; i++)
            oldPositions[i] = simPositions[i];

        // ---------- Free simulation ----------
        for (int i = 1; i < bones.Length; i++)
        {
            float bone01 = (bones.Length <= 1) ? 0f : i / (float)(bones.Length - 1);

            float motionW = Mathf.Clamp01(motionByBone.Evaluate(bone01));
            float gravityW = Mathf.Clamp01(gravityByBone.Evaluate(bone01));

            // carry controller movement into the chain
            simPositions[i] += rootDelta * (motionCarryAmount * motionW);

            // preferred direction = continue parent segment direction
            Vector3 preferredDir = GetPreferredWorldDir(i);

            Vector3 targetPos = simPositions[i - 1] + preferredDir * segmentLengths[i - 1];

            // spring toward target chain shape
            Vector3 accel = (targetPos - simPositions[i]) * springStrength;

            // add controller snap
            accel += rootAccel * (accelerationInfluence * motionW);

            // add gravity
            accel += gravityDir * (gravityAmount * gravityW);

            simVelocities[i] += accel * dt;

            // exponential damping
            float dampT = Mathf.Exp(-velocityDamping * dt);
            simVelocities[i] *= dampT;

            simPositions[i] += simVelocities[i] * dt;
        }

        // ---------- Constraints ----------
        for (int iter = 0; iter < constraintIterations; iter++)
        {
            simPositions[0] = rootPos;

            // keep segment lengths exact
            for (int i = 1; i < bones.Length; i++)
            {
                Vector3 anchor = simPositions[i - 1];
                Vector3 delta = simPositions[i] - anchor;

                float len = delta.magnitude;
                if (len < 0.00001f)
                    delta = GetPreferredWorldDir(i) * segmentLengths[i - 1];
                else
                    delta = delta / len * segmentLengths[i - 1];

                simPositions[i] = anchor + delta;
            }

            // limit sharp folding
            ApplyBendLimits();
        }

        // update simulated velocities after constraints
        for (int i = 1; i < bones.Length; i++)
            simVelocities[i] = (simPositions[i] - oldPositions[i]) / dt;

        // ---------- Drive the actual bones ----------
        DriveBonesFromSimulation(dt);
    }

    void DriveBonesFromSimulation(float dt)
    {
        float t = 1f - Mathf.Exp(-rotationSharpness * dt);

        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] == null)
                continue;

            if (i == 0 && keepRootBoneAtRestRotation)
                continue;

            Vector3 desiredWorldDir;

            if (i < bones.Length - 1)
            {
                Vector3 d = simPositions[i + 1] - bones[i].position;
                if (d.sqrMagnitude < 0.000001f)
                    d = GetPreferredWorldDir(i + 1);

                desiredWorldDir = d.normalized;
            }
            else
            {
                Vector3 d = simPositions[i] - simPositions[i - 1];
                if (d.sqrMagnitude < 0.000001f)
                    d = bones[i - 1].TransformDirection(restLocalAxes[Mathf.Max(0, i - 1)]);

                desiredWorldDir = d.normalized;
            }

            Transform parent = bones[i].parent;
            if (parent == null || desiredWorldDir.sqrMagnitude < 0.000001f)
                continue;

            Vector3 desiredLocalDir = parent.InverseTransformDirection(desiredWorldDir).normalized;
            Quaternion deltaRot = Quaternion.FromToRotation(restLocalAxes[i], desiredLocalDir);
            Quaternion targetLocalRot = deltaRot * restLocalRotations[i];

            bones[i].localRotation = Quaternion.Slerp(bones[i].localRotation, targetLocalRot, t);
        }
    }

    void ApplyBendLimits()
    {
        if (bones.Length < 3)
            return;

        float maxRad = maxBendDegrees * Mathf.Deg2Rad;

        for (int i = 2; i < bones.Length; i++)
        {
            Vector3 a = simPositions[i - 2];
            Vector3 b = simPositions[i - 1];
            Vector3 c = simPositions[i];

            Vector3 prevDir = (b - a);
            Vector3 currDir = (c - b);

            float prevLen = prevDir.magnitude;
            float currLen = currDir.magnitude;

            if (prevLen < 0.00001f || currLen < 0.00001f)
                continue;

            prevDir /= prevLen;
            currDir /= currLen;

            float angle = Vector3.Angle(prevDir, currDir);
            if (angle <= maxBendDegrees)
                continue;

            Vector3 clampedDir = Vector3.RotateTowards(prevDir, currDir, maxRad, 0f).normalized;
            simPositions[i] = b + clampedDir * segmentLengths[i - 1];
        }
    }

    Vector3 GetPreferredWorldDir(int boneIndex)
    {
        // boneIndex is the joint being solved, so segment is boneIndex-1 -> boneIndex

        // for the first free segment, use the root bone's current axis
        if (boneIndex == 1)
        {
            Vector3 d = bones[0].TransformDirection(restLocalAxes[0]);
            if (d.sqrMagnitude > 0.000001f)
                return d.normalized;
        }

        // otherwise continue the previous simulated segment direction
        if (boneIndex >= 2)
        {
            Vector3 d = simPositions[boneIndex - 1] - simPositions[boneIndex - 2];
            if (d.sqrMagnitude > 0.000001f)
                return d.normalized;
        }

        // fallback
        Transform parent = bones[Mathf.Max(0, boneIndex - 1)].parent;
        if (parent != null)
        {
            Vector3 d = parent.TransformDirection(restLocalAxes[Mathf.Max(0, boneIndex - 1)]);
            if (d.sqrMagnitude > 0.000001f)
                return d.normalized;
        }

        return Vector3.down;
    }

    void CacheRigData()
    {
        if (bones == null || bones.Length < 2)
            return;

        restLocalRotations = new Quaternion[bones.Length];
        restLocalAxes = new Vector3[bones.Length];
        segmentLengths = new float[bones.Length - 1];
        simPositions = new Vector3[bones.Length];
        simVelocities = new Vector3[bones.Length];

        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] == null)
                continue;

            restLocalRotations[i] = bones[i].localRotation;
        }

        for (int i = 0; i < bones.Length - 1; i++)
        {
            if (bones[i] == null || bones[i + 1] == null)
                continue;

            Vector3 localToChild = bones[i + 1].localPosition;
            float len = localToChild.magnitude;

            if (len < 0.00001f)
            {
                localToChild = Vector3.up;
                len = 0.01f;
            }

            Vector3 axis = localToChild.normalized;
            if (invertBoneAxis)
                axis = -axis;

            restLocalAxes[i] = axis;
            segmentLengths[i] = len;
        }

        // tip bone axis fallback
        if (bones.Length >= 2)
            restLocalAxes[bones.Length - 1] = restLocalAxes[bones.Length - 2];
    }

    void InitializeSimulationFromCurrentPose()
    {
        if (!IsValid())
            return;

        for (int i = 0; i < bones.Length; i++)
        {
            simPositions[i] = bones[i].position;
            simVelocities[i] = Vector3.zero;
        }

        lastRootPos = bones[0].position;
        lastRootVel = Vector3.zero;
        hasRootHistory = false;
    }

    bool IsValid()
    {
        if (bones == null || bones.Length < 2)
            return false;

        if (restLocalRotations == null || restLocalAxes == null || segmentLengths == null ||
            simPositions == null || simVelocities == null)
            return false;

        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] == null)
                return false;
        }

        return true;
    }
}