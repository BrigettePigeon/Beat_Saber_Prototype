using UnityEngine;

/// <summary>
/// Attach to your BLOCK prefab.
/// It checks the block's swept movement (prev->current) against the active math ribbon.
/// If it intersects, it triggers the block's normal "hit/break" behavior.
/// </summary>
public class BlockRibbonMathHit : MonoBehaviour
{
    [Tooltip("Approx radius of the block for collision math (meters).")]
    public float blockRadiusMeters = 0.08f;

    Vector3 prevPos;
    bool hasPrev;

    void OnEnable()
    {
        hasPrev = false;
    }

    void FixedUpdate()
    {
        Vector3 currPos = transform.position;

        if (!hasPrev)
        {
            prevPos = currPos;
            hasPrev = true;
            return;
        }

        var ribbon = GuardRibbonMathXR.Active;
        if (ribbon != null && ribbon.SweepHitsRibbon(prevPos, currPos, blockRadiusMeters))
        {
            TriggerBlockBreak();
            return;
        }

        prevPos = currPos;
    }

    void TriggerBlockBreak()
    {
        // Best case: your existing Block script has a method we can call.
        // If it's not public, SendMessage still works in Unity (even if the method is private).
        // Change "Hit" to whatever your break function is called.
        SendMessage("Hit", SendMessageOptions.DontRequireReceiver);

        // Fallback if nothing receives "Hit":
        // Destroy(gameObject);
    }
}