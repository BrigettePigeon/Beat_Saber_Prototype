using UnityEngine;
using UnityEngine.XR;

/// GuardBarControllerXR
/// A VR "guard bar" that stretches a cube outward from a start point.
/// - Hold right trigger (or grip) to enable guard.
/// - The bar grows from ONE side (from the anchor).
/// - On release, the bar can freeze in place (stops updating) and remain visible.

public class GuardBarControllerXR : MonoBehaviour
{
    [Header("Scene References")]
    [Tooltip("Drag the Transform that follows your RIGHT controller/hand.")]
    public Transform rightHandTransform;

    [Tooltip("Drag the child cube named 'Visual'.")]
    public Transform visual;

    [Tooltip("Drag the BoxCollider on the GuardBar root (IsTrigger = true).")]
    public BoxCollider boxCollider;

    [Header("Guard Shape")]
    [Tooltip("Maximum bar length (meters).")]
    public float maxLengthMeters = 1.0f;

    [Tooltip("Bar thickness (meters). This becomes the cube's X and Y size.")]
    public float thicknessMeters = 0.08f;

    [Header("Stability Safety")]
    [Tooltip("If tracking jumps farther than this in one physics step, reset the anchor (prevents teleport bars).")]
    public float maxTeleportDistanceMeters = 0.75f;

    [Header("Lifetime While Held")]
    [Tooltip("How long the guard bar stays active after it turns on (seconds) even if you keep holding.")]
    public float activeSeconds = 0.25f;

    [Header("Freeze On Release")]
    [Tooltip("If true: when you release the trigger, the bar stops updating and stays where it was drawn.")]
    public bool freezeOnRelease = true;

    [Tooltip("How long the frozen bar stays visible after release (seconds). Set to 0 to keep forever.")]
    public float freezeSeconds = 0.35f;

    [Header("XR Input")]
    public GuardInputMode inputMode = GuardInputMode.Trigger;

    [Tooltip("How far the trigger/grip must be pressed (0..1) to count as held.")]
    [Range(0f, 1f)]
    public float pressedThreshold = 0.1f;

    public enum GuardInputMode { Trigger, Grip }

    // XR device
    private InputDevice rightHandDevice;
    private bool deviceValid;

    // Guard state
    private bool guardActive;            // actively updating while held
    private bool frozenActive;           // frozen in place after release
    private Vector3 anchorStartPos;

    // Timers
    private float disableAtTime;         // auto-end while held
    private float frozenDisableAtTime;   // auto-hide after release (if freezeSeconds > 0)
    private bool autoEndedWhileHeld;     // lockout until release if auto-ended

    // Used for teleport-jump detection in FixedUpdate
    private Vector3 lastFixedHandPos;
    private bool hasLastFixedHandPos;

    void Awake()
    {
        if (boxCollider == null) boxCollider = GetComponent<BoxCollider>();
        SetEnabled(false);
    }

    void OnEnable()
    {
        TryFindRightHandDevice();
        InputDevices.deviceConnected += OnDeviceChanged;
        InputDevices.deviceDisconnected += OnDeviceChanged;
    }

    void OnDisable()
    {
        InputDevices.deviceConnected -= OnDeviceChanged;
        InputDevices.deviceDisconnected -= OnDeviceChanged;
    }

    void OnDeviceChanged(InputDevice _)
    {
        TryFindRightHandDevice();
    }

    void Update()
    {
        if (!deviceValid)
            TryFindRightHandDevice();

        bool pressed = ReadGuardPressed();

        // If user releases, clear the auto-end lockout
        if (!pressed)
            autoEndedWhileHeld = false;

        // Start guard only if pressed and not already active/frozen and not locked out
        if (pressed && !guardActive && !frozenActive && !autoEndedWhileHeld)
        {
            BeginGuard();
        }
        // On release, stop updating. Either freeze or fully disable.
        else if (!pressed && guardActive)
        {
            ReleaseGuard();
        }
    }

    void FixedUpdate()
    {
        // If we're frozen, we do NOT update the bar shape anymore.
        // We only count down until it's time to hide (if freezeSeconds > 0).
        if (frozenActive)
        {
            if (freezeSeconds > 0f && Time.time >= frozenDisableAtTime)
            {
                frozenActive = false;
                SetEnabled(false);
            }
            return;
        }

        if (!guardActive) return;
        if (rightHandTransform == null) return;

        // Auto-disable while held after activeSeconds
        if (Time.time >= disableAtTime)
        {
            AutoEndGuardWhileHeld();
            return;
        }

        Vector3 handPos = rightHandTransform.position;

        // Teleport safeguard
        if (hasLastFixedHandPos)
        {
            float jump = Vector3.Distance(handPos, lastFixedHandPos);
            if (jump > maxTeleportDistanceMeters)
                anchorStartPos = handPos;
        }

        UpdateBarOneSided(anchorStartPos, handPos);

        lastFixedHandPos = handPos;
        hasLastFixedHandPos = true;
    }

    // ----------------------------
    // Guard lifecycle
    // ----------------------------

    void BeginGuard()
    {
        if (rightHandTransform == null) return;

        guardActive = true;
        frozenActive = false;
        hasLastFixedHandPos = false;
        autoEndedWhileHeld = false;

        anchorStartPos = rightHandTransform.position;
        disableAtTime = Time.time + activeSeconds;

        SetEnabled(true);
        UpdateBarOneSided(anchorStartPos, rightHandTransform.position);
    }

    // Called when trigger is released while guardActive is true
    void ReleaseGuard()
    {
        guardActive = false;
        hasLastFixedHandPos = false;

        if (freezeOnRelease)
        {
            // Freeze the current bar in place by leaving it enabled and switching to frozen state.
            frozenActive = true;

            // If freezeSeconds is 0, it stays forever. Otherwise it hides after freezeSeconds.
            if (freezeSeconds > 0f)
                frozenDisableAtTime = Time.time + freezeSeconds;
        }
        else
        {
            SetEnabled(false);
        }
    }

    void AutoEndGuardWhileHeld()
    {
        // Turns off while still holding; requires release before re-arming.
        guardActive = false;
        autoEndedWhileHeld = true;

        if (freezeOnRelease)
        {
            // If it auto-ends while held, we do NOT freeze (prevents weird "stuck" bars).
            // We simply turn it off. User must release to clear lockout.
            SetEnabled(false);
        }
        else
        {
            SetEnabled(false);
        }
    }

    void SetEnabled(bool enabled)
    {
        if (visual != null) visual.gameObject.SetActive(enabled);
        if (boxCollider != null) boxCollider.enabled = enabled;
    }

    // ----------------------------
    // XR input
    // ----------------------------

    void TryFindRightHandDevice()
    {
        rightHandDevice = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        deviceValid = rightHandDevice.isValid;
    }

    bool ReadGuardPressed()
    {
        if (!deviceValid) return false;

        float value;
        bool gotValue = false;

        if (inputMode == GuardInputMode.Trigger)
            gotValue = rightHandDevice.TryGetFeatureValue(CommonUsages.trigger, out value);
        else
            gotValue = rightHandDevice.TryGetFeatureValue(CommonUsages.grip, out value);

        if (!gotValue) return false;
        return value >= pressedThreshold;
    }

    // ----------------------------
    // Bar math (ONE-SIDED growth)
    // ----------------------------

    void UpdateBarOneSided(Vector3 start, Vector3 end)
    {
        Vector3 dir = end - start;
        float dist = dir.magnitude;

        if (dist < 0.001f)
        {
            dist = 0.001f;
            dir = Vector3.forward * dist;
        }

        float clampedDist = Mathf.Min(dist, maxLengthMeters);
        Vector3 dirNorm = dir / dist;

        transform.position = start;
        transform.rotation = Quaternion.LookRotation(dirNorm, Vector3.up);

        float halfLen = clampedDist * 0.5f;

        if (visual != null)
        {
            visual.localScale = new Vector3(thicknessMeters, thicknessMeters, clampedDist);
            visual.localPosition = new Vector3(0f, 0f, halfLen);
            visual.localRotation = Quaternion.identity;
        }

        if (boxCollider != null)
        {
            boxCollider.size = new Vector3(thicknessMeters, thicknessMeters, clampedDist);
            boxCollider.center = new Vector3(0f, 0f, halfLen);
        }
    }
}