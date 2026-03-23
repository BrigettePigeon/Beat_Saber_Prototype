using UnityEngine;

/// <summary>
/// Attach to a projectile that moves with a Rigidbody.
/// It checks the projectile's swept path (prev->curr) against the active ribbon.
/// If it hits, it calls OnRibbonHit().
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class RibbonSweepHit : MonoBehaviour
{
    [Tooltip("Projectile radius in meters for collision math.")]
    public float projectileRadius = 0.05f;

    private Rigidbody rb;
    private Vector3 prevPos;
    private bool hasPrev;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    void FixedUpdate()
    {
        Vector3 currPos = rb.position;

        if (!hasPrev)
        {
            prevPos = currPos;
            hasPrev = true;
            return;
        }

        GuardRibbonMathXR ribbon = GuardRibbonMathXR.Active;
        if (ribbon != null)
        {
            if (ribbon.SweepHitsRibbon(prevPos, currPos, projectileRadius))
            {
                OnRibbonHit();
                return;
            }
        }

        prevPos = currPos;
    }

    void OnRibbonHit()
    {
        // TODO: replace this with your real deflect/destroy logic.
        // For now, we just destroy the projectile so you can confirm it works.
        Destroy(gameObject);
    }
}