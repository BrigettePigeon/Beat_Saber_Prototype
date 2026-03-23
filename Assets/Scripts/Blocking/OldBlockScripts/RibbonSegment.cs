using UnityEngine;

/// <summary>
/// Put this on the SEGMENT PREFAB.
/// It caches components and has one job: configure the segment collider
/// to span between two world points.
/// </summary>
[RequireComponent(typeof(BoxCollider))]
[RequireComponent(typeof(Rigidbody))]
public class RibbonSegment : MonoBehaviour
{
    [HideInInspector] public BoxCollider boxCollider;
    [HideInInspector] public Rigidbody rb;

    void Awake()
    {
        boxCollider = GetComponent<BoxCollider>();
        rb = GetComponent<Rigidbody>();

        // Trigger collider + kinematic rigidbody gives reliable trigger events.
        boxCollider.isTrigger = true;
        rb.useGravity = false;
        rb.isKinematic = true;
    }

    /// <summary>
    /// Positions and orients this segment so it spans between a and b.
    /// Thickness is X/Y. Length is Z. Overlap slightly to avoid tiny gaps.
    /// </summary>
    public void Configure(Vector3 a, Vector3 b, float thickness, float overlap)
    {
        Vector3 dir = b - a;
        float length = dir.magnitude;

        if (length < 0.001f)
        {
            length = 0.001f;
            dir = Vector3.forward * length;
        }

        Vector3 forward = dir / length;
        Vector3 mid = (a + b) * 0.5f;

        transform.position = mid;
        transform.rotation = Quaternion.LookRotation(forward, Vector3.up);

        // Keep object scale at 1 so collider math stays simple
        transform.localScale = Vector3.one;

        if (boxCollider != null)
        {
            boxCollider.center = Vector3.zero;
            boxCollider.size = new Vector3(thickness, thickness, length + overlap);
            boxCollider.isTrigger = true;
        }

        gameObject.SetActive(true);
    }
}