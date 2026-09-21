using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedural fluid-leg walker for VirusMovement.
///
/// SETUP
/// -----
/// Assign ONE Leg Parent and choose Leg Count.
///
/// Virus
/// └── LegParent          <- assign this once
///
/// The script creates the leg roots itself in an even ring around the body.
/// There are NO manually-created leg children, NO leg bones, and NO End transforms.
///
/// The script generates a rounded tube mesh from each root to its procedural foot
/// target. Ground walking, flying spin, landing preparation, and landing-pose
/// preservation all drive those same targets.
/// </summary>
[DefaultExecutionOrder(100)]
public class SpiderLegWalker : MonoBehaviour
{
    class RuntimeLeg
    {
        public Transform root;
        public GameObject meshObject;
        public MeshFilter meshFilter;
        public MeshRenderer meshRenderer;
        public Mesh mesh;

        // Cached mesh buffers. Rebuilding these arrays every frame for every
        // leg was a constant source of garbage collection spikes.
        public Vector3[] vertices;
        public Vector3[] normals;
        public Vector2[] uvs;
        public int[] triangles;
        public int builtRings;
        public int builtSides;

        public float phase;
        public float angleDegrees;
        public float distanceScale;
        public float durationScale;
        public float heightScale;

        // Persistent visual frame for the fluid tube. Keeping this between
        // frames prevents the leg from suddenly bowing to the opposite side
        // when its tangent crosses an orientation singularity.
        public bool bendFrameInitialized;
        public Vector3 bendSide;
        public Vector3 bendUp;

        // Smooth per-leg airborne rearrangement. These offsets are driven by
        // maneuver intensity instead of being re-randomized every frame.
        public float maneuverAngleOffset;
        public float maneuverAngleVelocity;
        public float maneuverRadiusOffset;
        public float maneuverRadiusVelocity;
        public float maneuverAxialOffset;
        public float maneuverAxialVelocity;

        // Independent grounded turn offsets. These keep a turn from rotating
        // all legs around the body like one rigid wheel.
        public float groundTurnAngleOffset;
        public float groundTurnAngleVelocity;
        public float groundTurnRadiusOffset;
        public float groundTurnRadiusVelocity;

        // Ground gait timing. Used to stop the same leg from immediately
        // stepping again and to keep the gait irregular without becoming random.
        public float lastStepEndTime;

        // Captured at the exact moment Grounded -> Flying begins, stored in the
        // BODY's local frame. A world-space capture made the takeoff blend drag
        // the foot back toward the launch spot while the virus flew away.
        public Vector3 flightStartLocalPoint;

        public bool planted;
        public bool stepping;
        public float stepTimer;

        public Vector3 plantedPoint;
        public Vector3 targetPoint;
        public bool targetInitialized;

        // Rendering safety net. A transient bad/uninitialized target must NEVER
        // collapse the tube back to the leg root for one frame.
        public Vector3 lastSafeTargetPoint;
        public bool lastSafeTargetInitialized;

        // Separate DISPLAY endpoint. Logical gait targets are allowed to change,
        // but the rendered tube will not follow a one-frame impossible jump.
        // The held value is kept as an offset from the body so that rejecting a
        // frame never leaves the leg stretched back to a stale world position.
        public Vector3 renderTargetPoint;
        public Vector3 renderLocalOffset;
        public bool renderTargetInitialized;

        public Vector3 pendingRenderTarget;
        public bool pendingRenderTargetInitialized;
        public int pendingRenderTargetFrames;

        // Used whenever no locomotion state is driving the feet (unknown state,
        // or flying with animation disabled). Keeps the legs attached instead of
        // leaving a stale world-space target behind.
        public Vector3 holdLocalPoint;

        public Vector3 stepStart;
        public Vector3 stepEnd;
        public Vector3 stepNormal;

        // Ground support is authoritative while planted. A planted foot is
        // stored as a LOCAL point on this transform, exactly as if it were a
        // child of the surface.
        public Transform support;
        public Vector3 supportLocalPoint;

        // Body motion measured RELATIVE to the support. World motion of a
        // moving platform must not look like the virus walking across it.
        public Vector3 supportLastBodyLocalPoint;
        public bool supportBodyLocalInitialized;
        public Vector3 supportRelativeBodyVelocity;

        // While a foot is swinging, BOTH ends of the step can remain in the
        // support's local frame. Therefore a translating/rotating Rigidbody
        // carries the entire gait arc smoothly instead of leaving it behind in
        // world space for a frame.
        public Transform stepStartSupport;
        public Vector3 stepStartSupportLocalPoint;
        public bool stepStartSupportLocalValid;

        public Transform stepEndSupport;
        public Vector3 stepEndSupportLocalPoint;
        public Vector3 stepEndSupportLocalNormal;
        public bool stepEndSupportLocalValid;
    }

    [Header("References")]
    [Tooltip("Root/body of the virus. Leave empty to use this transform.")]
    public Transform body;

    [Tooltip("Assign VirusMovement so this script knows Grounded/Flying, surface normal, and movement speed.")]
    public VirusMovement movement;

    [Tooltip("Assign ONE empty parent. The script generates all leg roots and meshes beneath it.")]
    public Transform legParent;

    [Min(1)]
    [Tooltip("How many fluid legs to generate.")]
    public int legCount = 6;

    [Header("Generated Leg Roots")]
    [Min(0f)]
    [Tooltip("Distance from the body center to each generated leg root.")]
    public float legRootRadius = 0.45f;

    [Tooltip("Local Y offset of the generated leg-root ring.")]
    public float legRootHeight = 0f;

    [Tooltip("Rotates the whole generated leg pattern around the body's local Y axis.")]
    public float legRootAngleOffset = 0f;

    [Tooltip("Rotate each generated root so its local forward points outward from the body.")]
    public bool orientRootsOutward = true;

    [Tooltip("Material used by every generated fluid leg.")]
    public Material legMaterial;

    [Header("Fluid Leg Shape")]
    [Range(3, 24)]
    [Tooltip("Number of sections along the length of each leg.")]
    public int lengthSegments = 12;

    [Range(3, 16)]
    [Tooltip("How round each leg is. 8 is usually plenty.")]
    public int radialSegments = 8;

    [Min(0.001f)]
    public float baseRadius = 0.10f;

    [Min(0.001f)]
    public float tipRadius = 0.055f;

    [Min(0f)]
    [Tooltip("How strongly the middle of the leg bows away from the surface.")]
    public float curveHeight = 0.24f;

    [Range(0f, 1f)]
    [Tooltip("How much the curve leans out of the root before bending toward the foot.")]
    public float curveBias = 0.35f;

    [Range(0f, 0.5f)]
    [Tooltip("Subtle animated side-to-side motion in the fluid tube.")]
    public float fluidWobble = 0.06f;

    [Min(0f)]
    public float fluidWobbleSpeed = 3.5f;

    [Range(0f, 0.5f)]
    [Tooltip("Makes a stretched leg slightly thinner and a compressed leg slightly fatter.")]
    public float stretchThicknessResponse = 0.16f;

    [Header("Foot Placement")]
    [Min(0.01f)]
    [Tooltip("Resting distance from the virus BODY ORIGIN to each foot.")]
    public float footDistance = 1.5f;

    [Range(0f, 0.4f)]
    [Tooltip("Permanent small distance variation from leg to leg.")]
    public float footDistanceVariation = 0.06f;

    [Header("Ground")]
    [Tooltip("Surfaces the feet may plant on. Exclude the virus/player layer.")]
    public LayerMask groundMask = ~0;

    [Min(0.01f)]
    public float probeHeight = 2f;

    [Min(0.01f)]
    public float probeDistance = 5f;

    [Tooltip("Extra clearance between the outside of the rounded leg and the walking surface.")]
    public float footSurfaceOffset = 0.02f;

    [Tooltip("Keep the WHOLE rounded tube above the walking surface, not just its center line.")]
    public bool keepTubeAboveGround = true;

    [Min(0f)]
    [Tooltip("Additional clearance used when keeping the tube above ground.")]
    public float tubeGroundClearance = 0.015f;

    [Range(1, 4)]
    [Tooltip("Only probe the ground every Nth tube section. 1 is the most accurate and the most expensive; 2 halves the raycast count with no visible difference on normal terrain.")]
    public int tubeGroundSampleStride = 2;

    [Header("Ground Walk")]
    [Min(0.01f)]
    [Tooltip("HARD minimum distance, in world units, between a planted foot and its current desired foot position before that leg is allowed to lift. " +
             "Larger = longer planted strides / fewer steps. Smaller = shorter strides / more frequent steps.")]
    public float stepDistance = 0.40f;

    [Min(0.01f)]
    public float stepDuration = 0.14f;

    [Min(0f)]
    public float stepHeight = 0.22f;

    [Min(0f)]
    [Tooltip("Seconds of body movement used to predict where the foot should land.")]
    public float velocityLeadTime = 0.18f;

    [Range(0f, 1f)]
    [Tooltip("Higher values make trailing feet pick themselves up earlier.")]
    public float dragPrevention = 0.85f;

    public bool retargetSwingFeet = true;

    [Min(0.01f)]
    public float swingRetargetSharpness = 14f;

    [Range(0f, 0.8f)]
    [Tooltip("How much a planted foot creeps with the body during stance. " +
             "A small amount stops every leg from sweeping backward together.")]
    public float plantedFootFollow = 0.28f;

    [Min(0.01f)]
    [Tooltip("How quickly the grounded leg layout follows a new travel direction. " +
             "Lower values make turning happen through individual steps rather than a rigid pivot.")]
    public float groundFrameFollowSharpness = 2.0f;

    [Header("Ground Turn Scramble")]
    [Range(0f, 1f)]
    public float groundTurnScramble = 0.72f;

    [Min(0f)]
    public float groundTurnAngleRange = 38f;

    [Range(0f, 0.5f)]
    public float groundTurnRadiusRange = 0.16f;

    [Min(1f)]
    public float groundFullTurnRate = 140f;

    [Min(0.01f)]
    public float groundTurnResponse = 7f;

    [Min(0.01f)]
    public float groundTurnSettleSpeed = 3f;

    [Header("Movement Scaling")]
    [Tooltip("Scale ground stepping and fluid motion with the virus's actual ground speed.")]
    public bool scaleGroundMotionWithSpeed = true;

    [Min(0.01f)]
    [Tooltip("Ground speed that corresponds to the authored/default gait timing. " +
             "4 matches the original VirusMovement walk speed. If the virus moves at 8, gait runs about twice as fast.")]
    public float gaitReferenceGroundSpeed = 4f;

    [Range(0.1f, 1f)]
    [Tooltip("Minimum gait-rate multiplier when barely moving or turning in place.")]
    public float minimumGroundGaitRate = 0.35f;

    [Range(1f, 5f)]
    [Tooltip("Maximum gait-rate multiplier at very high movement speeds.")]
    public float maximumGroundGaitRate = 3f;

    [Range(0f, 0.75f)]
    [Tooltip("How much faster movement increases the height/energy of a step. Cadence scales much more strongly than height.")]
    public float stepHeightSpeedInfluence = 0.18f;

    [Header("Organic / Chaotic Walk")]
    [Range(0f, 1f)]
    public float chaos = 0.58f;

    [Range(0f, 0.75f)]
    public float timingVariation = 0.28f;

    [Range(0f, 0.75f)]
    public float heightVariation = 0.24f;

    [Min(0f)]
    public float targetWander = 0.10f;

    [Min(0f)]
    public float wanderSpeed = 1.35f;

    [Range(1, 4)]
    [Tooltip("Maximum feet that may be off the ground at once. Two works well for six legs.")]
    public int maxSimultaneousSteps = 2;

    [Range(1, 4)]
    [Tooltip("Minimum number of leg slots between two airborne legs. With six legs, 2 prevents neighboring legs from lifting together.")]
    public int minimumAirborneLegSpacing = 2;

    [Min(0f)]
    [Tooltip("Minimum delay between starting one leg step and starting another. This creates a flowing cascade instead of multiple feet popping up on the same frame.")]
    public float minimumStepStartInterval = 0.055f;

    [Min(0f)]
    [Tooltip("Minimum time a foot stays planted after completing a step before it may step again.")]
    public float minimumLegRestTime = 0.06f;

    [Tooltip("Prevent two clearly same-side legs from being airborne together.")]
    public bool avoidSameSideAirborne = true;

    [Tooltip("Gives alternating legs a slight preference without forcing rigid tripods.")]
    public bool looseTripodBias = true;

    [Header("Flying")]
    public bool animateWhileFlying = true;

    [Min(0f)]
    public float flyingSpinMin = 120f;

    [Min(0f)]
    public float flyingSpinMax = 850f;

    [Min(0.01f)]
    public float flyingReachMultiplier = 0.85f;

    [Range(0f, 0.5f)]
    public float flyingWobble = 0.10f;

    [Tooltip("Spin the feet around the actual flight direction.")]
    public bool spinAroundTravelDirection = true;

    public Vector3 flyingSpinAxisLocal = Vector3.forward;

    [Min(0.01f)]
    [Tooltip("How quickly the flying orbit axis follows a changing flight direction.")]
    public float flyingAxisFollowSharpness = 10f;

    [Min(0f)]
    [Tooltip("Below this flight speed, keep the last valid flight direction instead of switching back to Body.forward. This prevents the legs from flipping when you stop in midair.")]
    public float flyingDirectionHoldSpeed = 0.18f;

    [Min(0.01f)]
    [Tooltip("How quickly the persistent flight direction follows a new velocity direction.")]
    public float flyingDirectionFollowSharpness = 9f;

    [Tooltip("Fade leg spin to zero when the virus is nearly stationary in the air.")]
    public bool stopSpinWhenIdle = true;

    [Min(0.01f)]
    [Tooltip("Speed at which airborne leg spin becomes fully active.")]
    public float flyingSpinActivationSpeed = 1.0f;

    [Min(0.01f)]
    [Tooltip("Time used to blend from the current grounded foot positions into the airborne orbit. Prevents the one-frame takeoff teleport.")]
    public float takeoffLegBlendDuration = 0.24f;

    [Header("Air Maneuver Chaos")]
    [Range(0f, 1f)]
    [Tooltip("How strongly hard turns and direction changes make the legs independently rearrange.")]
    public float maneuverChaos = 0.90f;

    [Min(0f)]
    [Tooltip("Maximum temporary angular rearrangement per leg during a hard maneuver.")]
    public float maneuverAngleRange = 80f;

    [Range(0f, 0.75f)]
    [Tooltip("Maximum temporary radial expansion/compression during maneuvers.")]
    public float maneuverRadiusRange = 0.34f;

    [Min(0f)]
    [Tooltip("How far individual legs can scramble forward/back along the flight axis during maneuvers.")]
    public float maneuverAxialRange = 0.55f;

    [Min(0f)]
    [Tooltip("Extra flowing wave through the BODY of each fluid leg during hard maneuvers.")]
    public float maneuverTubeWave = 0.18f;

    [Min(0f)]
    [Tooltip("Speed of the maneuver-induced fluid writhing.")]
    public float maneuverTubeWaveSpeed = 6.5f;

    [Min(0.01f)]
    [Tooltip("How quickly the maneuver offsets chase their changing targets.")]
    public float maneuverResponse = 6f;

    [Min(0.01f)]
    [Tooltip("How quickly the legs settle back into their normal evenly spaced orbit.")]
    public float maneuverSettleSpeed = 2.5f;

    [Min(1f)]
    [Tooltip("Degrees per second of flight-direction change treated as a full-strength maneuver.")]
    public float maneuverFullTurnRate = 150f;

    [Header("Landing Preparation")]
    [Min(0.01f)]
    public float landingPrepareDistance = 3f;

    [Min(0f)]
    public float landingPredictionTime = 0.12f;

    [Min(0f)]
    public float landingProbeRadius = 0.25f;

    [Range(0f, 1f)]
    public float landingSpinMultiplier = 0.06f;

    [Min(0.01f)]
    public float landingReachMultiplier = 1.20f;

    [Min(0.01f)]
    public float landingPrepareSharpness = 10f;

    [Min(0.01f)]
    public float landingFootCaptureDistance = 1.5f;

    public bool preserveAirPoseOnLanding = true;

    [Header("Mesh")]
    public bool recalculateNormals = true;
    public bool castShadows = true;
    public bool receiveShadows = true;

    [Header("Endpoint Teleport Guard")]
    [Tooltip("Prevents a transient bad calculation from visually snapping a fluid leg endpoint to its root/body/origin.")]
    public bool preventEndpointTeleports = true;

    [Min(0f)]
    [Tooltip("Maximum one-frame endpoint change accepted immediately, ON TOP of however far the body itself moved or rotated this frame. 0 = automatic from Foot Distance.")]
    public float endpointJumpGuardDistance = 0f;

    [Range(2, 10)]
    [Tooltip("A large endpoint jump must persist for this many rendered frames before it is accepted as intentional.")]
    public int endpointJumpConfirmationFrames = 3;

    [Header("Debug")]
    public bool drawDebug = false;

    [Tooltip("Runtime only: largest current planted-foot to desired-foot distance. Compare this directly with Step Distance.")]
    [SerializeField] float debugLargestGroundFootError;

    [Tooltip("Runtime diagnostic: number of suspicious endpoint jumps rejected by the visual guard.")]
    [SerializeField] int debugRejectedEndpointJumps;

    [Tooltip("Runtime diagnostic: size of the most recent rejected endpoint jump.")]
    [SerializeField] float debugLastRejectedEndpointJump;

    [Tooltip("Runtime diagnostic: number of degenerate/invalid physics hits discarded while preparing to land.")]
    [SerializeField] int debugRejectedLandingHits;

    readonly List<RuntimeLeg> _legs = new List<RuntimeLeg>();

    Vector3 _lastBodyPosition;
    Quaternion _lastBodyRotation;
    Vector3 _bodyVelocity;
    float _bodyAngularSpeedDegrees;
    bool _velocitySeeded;

    int _preferredParity;

    bool _warnedMissingLegParent;

    Rigidbody _movementRigidbody;
    VirusMovement _movementRigidbodySource;
    bool _movementRigidbodyResolved;

    // Persistent tangent-space frame for grounded legs.
    bool _groundFrameInitialized;
    Vector3 _groundFrameNormal;
    Vector3 _groundFrameRight;
    Vector3 _groundFrameForward;

    // The gait frame itself is also stored relative to the support. This is
    // important for rotating platforms: rotation around the surface normal
    // does not change the normal, so normal-only transport cannot detect it.
    Transform _groundFrameSupport;
    Vector3 _groundFrameSupportLocalNormal;
    Vector3 _groundFrameSupportLocalRight;
    Vector3 _groundFrameSupportLocalForward;

    Vector3 _previousGroundFacing;
    bool _groundFacingSeeded;
    Transform _groundFacingSupport;
    float _groundTurnIntensity;

    Vector3 _lastGroundBodyPosition;
    bool _groundBodyPositionSeeded;

    float _lastGroundStepStartTime = -999f;
    float _groundTurnSignedIntensity;

    float _flyingSpinAngle;
    float _landingPrepare;
    bool _wasFlying;
    bool _hasLandingHit;

    // Only ever written from a VALIDATED hit. Passing this field straight into
    // Physics.SphereCast as the out parameter meant every missed cast zeroed it,
    // which is how feet ended up reaching for the world origin.
    Vector3 _landingHitPoint;
    Vector3 _landingHitNormal;

    bool _holdPoseCaptured;

    // Persistent airborne orbit frame. Recomputing a basis from Body.forward
    // every frame can flip 180 degrees when vectors become nearly parallel,
    // which looks like the legs teleporting around the virus.
    bool _flyingFrameInitialized;
    Vector3 _flyingOrbitAxis;
    Vector3 _flyingOrbitRight;
    Vector3 _flyingOrbitForward;

    // One continuous direction reference for the entire airborne state.
    // It follows real velocity while moving and freezes when nearly stopped,
    // so stopping never swaps the leg-bend frame back to Body.forward.
    bool _stableFlightDirectionInitialized;
    Vector3 _stableFlightDirection;

    Vector3 _previousFlightDirection;
    bool _flightDirectionSeeded;
    float _maneuverIntensity;
    float _takeoffLegBlend;

    Transform Body => body ? body : transform;

    void Awake()
    {
        RebuildLegs();
    }

    void OnEnable()
    {
        _velocitySeeded = false;
        _landingPrepare = 0f;
        _hasLandingHit = false;
        _holdPoseCaptured = false;
        _wasFlying = IsFlying();

        if (_legs.Count == 0)
            RebuildLegs();

        if (_wasFlying)
            EnterFlyingLegMode();
        else
            SnapFeetToGround();
    }

    void OnDestroy()
    {
        ClearGeneratedLegs();
        ClearGeneratedRoots();
    }

    void OnValidate()
    {
        legCount = Mathf.Max(1, legCount);
        legRootRadius = Mathf.Max(0f, legRootRadius);

        lengthSegments = Mathf.Clamp(lengthSegments, 3, 24);
        radialSegments = Mathf.Clamp(radialSegments, 3, 16);
        tubeGroundSampleStride = Mathf.Clamp(tubeGroundSampleStride, 1, 4);

        baseRadius = Mathf.Max(0.001f, baseRadius);
        tipRadius = Mathf.Max(0.001f, tipRadius);

        footDistance = Mathf.Max(0.01f, footDistance);
        footSurfaceOffset = Mathf.Max(0f, footSurfaceOffset);
        tubeGroundClearance = Mathf.Max(0f, tubeGroundClearance);

        probeHeight = Mathf.Max(0.01f, probeHeight);
        probeDistance = Mathf.Max(0.01f, probeDistance);

        stepDistance = Mathf.Max(0.01f, stepDistance);
        stepDuration = Mathf.Max(0.01f, stepDuration);
        swingRetargetSharpness = Mathf.Max(0.01f, swingRetargetSharpness);

        groundFrameFollowSharpness = Mathf.Max(0.01f, groundFrameFollowSharpness);
        groundTurnAngleRange = Mathf.Max(0f, groundTurnAngleRange);
        groundTurnRadiusRange = Mathf.Max(0f, groundTurnRadiusRange);
        groundFullTurnRate = Mathf.Max(1f, groundFullTurnRate);
        groundTurnResponse = Mathf.Max(0.01f, groundTurnResponse);
        groundTurnSettleSpeed = Mathf.Max(0.01f, groundTurnSettleSpeed);

        gaitReferenceGroundSpeed = Mathf.Max(0.01f, gaitReferenceGroundSpeed);
        minimumGroundGaitRate = Mathf.Clamp(minimumGroundGaitRate, 0.1f, 1f);
        maximumGroundGaitRate = Mathf.Max(1f, maximumGroundGaitRate);

        endpointJumpGuardDistance = Mathf.Max(0f, endpointJumpGuardDistance);
        endpointJumpConfirmationFrames = Mathf.Clamp(endpointJumpConfirmationFrames, 2, 10);

        maxSimultaneousSteps = Mathf.Clamp(maxSimultaneousSteps, 1, 4);
        minimumAirborneLegSpacing = Mathf.Clamp(minimumAirborneLegSpacing, 1, 4);
        minimumStepStartInterval = Mathf.Max(0f, minimumStepStartInterval);
        minimumLegRestTime = Mathf.Max(0f, minimumLegRestTime);

        flyingSpinMin = Mathf.Max(0f, flyingSpinMin);
        flyingSpinMax = Mathf.Max(flyingSpinMin, flyingSpinMax);
        flyingReachMultiplier = Mathf.Max(0.01f, flyingReachMultiplier);
        flyingAxisFollowSharpness = Mathf.Max(0.01f, flyingAxisFollowSharpness);
        flyingDirectionHoldSpeed = Mathf.Max(0f, flyingDirectionHoldSpeed);
        flyingDirectionFollowSharpness = Mathf.Max(0.01f, flyingDirectionFollowSharpness);
        flyingSpinActivationSpeed = Mathf.Max(0.01f, flyingSpinActivationSpeed);
        takeoffLegBlendDuration = Mathf.Max(0.01f, takeoffLegBlendDuration);
        maneuverAngleRange = Mathf.Max(0f, maneuverAngleRange);
        maneuverRadiusRange = Mathf.Max(0f, maneuverRadiusRange);
        maneuverAxialRange = Mathf.Max(0f, maneuverAxialRange);
        maneuverTubeWave = Mathf.Max(0f, maneuverTubeWave);
        maneuverTubeWaveSpeed = Mathf.Max(0f, maneuverTubeWaveSpeed);
        maneuverResponse = Mathf.Max(0.01f, maneuverResponse);
        maneuverSettleSpeed = Mathf.Max(0.01f, maneuverSettleSpeed);
        maneuverFullTurnRate = Mathf.Max(1f, maneuverFullTurnRate);

        landingPrepareDistance = Mathf.Max(0.01f, landingPrepareDistance);
        landingReachMultiplier = Mathf.Max(0.01f, landingReachMultiplier);
        landingPrepareSharpness = Mathf.Max(0.01f, landingPrepareSharpness);
        landingFootCaptureDistance = Mathf.Max(0.01f, landingFootCaptureDistance);
    }

    void SyncLocomotionTransition()
    {
        bool flying = IsFlying();
        bool grounded = IsGrounded();

        if (flying && !_wasFlying)
        {
            // Capture the grounded endpoints immediately, before ANY flying
            // root/orbit work is allowed to run.
            EnterFlyingLegMode();
            _wasFlying = true;
        }
        else if (!flying && _wasFlying && grounded)
        {
            if (preserveAirPoseOnLanding)
                PlantFeetFromAirPose();
            else
                SnapFeetToGround();

            _wasFlying = false;
        }
    }

    void Update()
    {
        // Changing Leg Count during Play Mode rebuilds the generated legs
        // automatically. Without a Leg Parent nothing can be built, so this
        // must not retry (and log) every single frame.
        if (_legs.Count != legCount &&
            legParent)
            RebuildLegs();

        if (_legs.Count == 0)
            return;

        UpdateBodyVelocity();

        SyncLocomotionTransition();

        bool flying = IsFlying();
        bool grounded = IsGrounded();

        if (grounded)
        {
            UpdateGroundSupportRelativeMotion(Time.deltaTime);
            UpdateGroundFrame(Time.deltaTime);
        }

        RefreshLegSlotAngles();
        UpdateGeneratedRootTransforms();

        if (flying)
        {
            if (animateWhileFlying)
            {
                _holdPoseCaptured = false;
                UpdateFlyingFeet(Time.deltaTime);
            }
            else
            {
                // Still keep the feet rigidly attached to the body. Leaving the
                // stale takeoff targets in world space stretched the legs across
                // the level as soon as the virus moved.
                HoldFeetRelativeToBody();
            }

            return;
        }

        if (grounded)
        {
            _holdPoseCaptured = false;
            UpdateGroundFeet(Time.deltaTime);
            return;
        }

        // Neither Grounded nor Flying (for example a transition state added to
        // VirusMovement later). Hold the pose relative to the body instead of
        // leaving world-space targets behind.
        HoldFeetRelativeToBody();
    }

    void LateUpdate()
    {
        if (_legs.Count == 0)
            return;

        // VirusMovement may have changed Grounded/Flying after this component's
        // Update() ran. Catch that transition before drawing even one frame.
        SyncLocomotionTransition();

        // Update once more after body movement so the tube bases are visually
        // glued to the body with no one-frame lag.
        UpdateGeneratedRootTransforms();

        Vector3 curveUp = VisualCurveUp();

        for (int i = 0; i < _legs.Count; i++)
            UpdateFluidMesh(_legs[i], i, curveUp);
    }

    bool IsGrounded()
    {
        // FocusMode is non-interactive Grounded, not an unowned transition
        // state. Keeping this semantic in VirusMovement prevents every support
        // consumer from having to duplicate the state list.
        return !movement || movement.IsSurfaceAttached;
    }

    bool IsFlying()
    {
        return movement && movement.state == VirusMovement.State.Flying;
    }

    Vector3 SurfaceNormal()
    {
        if (movement &&
            movement.IsSurfaceAttached &&
            movement.surfaceNormal.sqrMagnitude > 0.000001f)
            return movement.surfaceNormal.normalized;

        return Body.up.sqrMagnitude > 0.000001f
            ? Body.up.normalized
            : Vector3.up;
    }

    Vector3 VisualCurveUp()
    {
        if (IsGrounded())
            return SurfaceNormal();

        if (IsFlying())
        {
            if (_stableFlightDirectionInitialized &&
                _stableFlightDirection.sqrMagnitude > 0.000001f)
            {
                // One continuous trailing direction for every airborne leg.
                // It does not change just because velocity falls to zero.
                return -_stableFlightDirection;
            }

            if (_flyingFrameInitialized &&
                _flyingOrbitAxis.sqrMagnitude > 0.000001f)
            {
                return -_flyingOrbitAxis;
            }

            return -Body.forward;
        }

        return Body.up;
    }

    // ---------------------------------------------------------------------
    // One-parent setup / generated fluid meshes
    // ---------------------------------------------------------------------

    [ContextMenu("Rebuild Legs")]
    public void RebuildLegs()
    {
        ClearGeneratedLegs();
        ClearGeneratedRoots();

        if (!legParent)
        {
            if (!_warnedMissingLegParent)
            {
                _warnedMissingLegParent = true;

                Debug.LogWarning(
                    $"{nameof(SpiderLegWalker)} on '{name}': assign Leg Parent.", this);
            }

            return;
        }

        _warnedMissingLegParent = false;

        if (!legMaterial)
        {
            Debug.LogWarning(
                $"{nameof(SpiderLegWalker)} on '{name}': no Leg Material assigned. " +
                "The generated legs will render with Unity's missing-material shader.", this);
        }

        for (int i = 0; i < legCount; i++)
        {
            Transform root = CreateGeneratedRoot(i);

            RuntimeLeg leg = new RuntimeLeg();
            leg.root = root;

            float r1 = Hash01(i * 17 + 3);
            float r2 = Hash01(i * 31 + 7);
            float r3 = Hash01(i * 47 + 11);

            leg.phase = Hash01(i * 61 + 13) * Mathf.PI * 2f;
            leg.angleDegrees = BaseSlotAngle(i);

            leg.distanceScale =
                Mathf.Lerp(1f - footDistanceVariation, 1f + footDistanceVariation, r1);
            leg.durationScale =
                Mathf.Lerp(1f - timingVariation, 1f + timingVariation, r2);
            leg.heightScale =
                Mathf.Lerp(1f - heightVariation, 1f + heightVariation, r3);

            leg.bendFrameInitialized = false;
            leg.maneuverAngleOffset = 0f;
            leg.maneuverAngleVelocity = 0f;
            leg.maneuverRadiusOffset = 0f;
            leg.maneuverRadiusVelocity = 0f;
            leg.maneuverAxialOffset = 0f;
            leg.maneuverAxialVelocity = 0f;
            leg.groundTurnAngleOffset = 0f;
            leg.groundTurnAngleVelocity = 0f;
            leg.groundTurnRadiusOffset = 0f;
            leg.groundTurnRadiusVelocity = 0f;
            leg.lastStepEndTime = -999f;

            // Never leave a generated leg with Vector3.zero as an implicit
            // endpoint. That value is a perfectly finite vector and previously
            // could be mistaken for a legitimate world-space foot target.
            leg.targetPoint = root.position;
            leg.targetInitialized = true;
            leg.lastSafeTargetPoint = root.position;
            leg.lastSafeTargetInitialized = true;

            leg.renderTargetInitialized = false;
            leg.pendingRenderTargetInitialized = false;
            leg.pendingRenderTargetFrames = 0;

            leg.flightStartLocalPoint = ToBodyLocal(root.position);
            leg.holdLocalPoint = leg.flightStartLocalPoint;

            CreateGeneratedMesh(leg, i);
            _legs.Add(leg);
        }

        if (Application.isPlaying)
        {
            if (IsFlying())
                EnterFlyingLegMode();
            else
                SnapFeetToGround();
        }
    }

    float BaseSlotAngle(int index)
    {
        return
            legRootAngleOffset +
            (360f / Mathf.Max(1, legCount)) * index;
    }

    void RefreshLegSlotAngles()
    {
        // Lets Leg Root Angle Offset be tweaked in the inspector while playing
        // without needing a full rebuild.
        for (int i = 0; i < _legs.Count; i++)
            _legs[i].angleDegrees = BaseSlotAngle(i);
    }

    Transform CreateGeneratedRoot(int index)
    {
        GameObject go =
            new GameObject($"__GeneratedLegRoot_{index + 1}");

        Transform root = go.transform;
        root.SetParent(legParent, true);
        root.localScale = Vector3.one;

        // Give it an initial valid position. Every frame it is updated from
        // the leg's fixed angular slot so roots cannot collapse when the
        // walking surface normal changes.
        Vector3 normal =
            IsGrounded()
                ? SurfaceNormal()
                : Body.up;

        PositionGeneratedRoot(
            root,
            BaseSlotAngle(index),
            normal);

        return root;
    }

    void PositionGeneratedRoot(
        Transform root,
        float angleDegrees,
        Vector3 planeNormal)
    {
        planeNormal = SafeNormal(planeNormal);

        GetLegSlotBasis(
            planeNormal,
            out Vector3 right,
            out Vector3 forward);

        float radians =
            angleDegrees * Mathf.Deg2Rad;

        Vector3 radial =
            right * Mathf.Cos(radians) +
            forward * Mathf.Sin(radians);

        if (radial.sqrMagnitude < 0.000001f)
            radial = AnyPerpendicular(planeNormal);

        radial.Normalize();

        root.position =
            Body.position +
            radial * legRootRadius +
            planeNormal * legRootHeight;

        if (orientRootsOutward)
        {
            root.rotation =
                Quaternion.LookRotation(
                    radial,
                    planeNormal);
        }
        else
        {
            root.rotation = Body.rotation;
        }
    }

    Transform PrimaryGroundSupport()
    {
        for (int i = 0; i < _legs.Count; i++)
        {
            RuntimeLeg leg = _legs[i];

            if (leg.planted &&
                leg.support)
            {
                return leg.support;
            }
        }

        return null;
    }

    void CaptureGroundFrameOnSupport(
        Transform support)
    {
        _groundFrameSupport = support;

        if (!support)
            return;

        _groundFrameSupportLocalNormal =
            SafeNormal(
                support.InverseTransformDirection(
                    _groundFrameNormal));

        _groundFrameSupportLocalRight =
            SafeNormal(
                support.InverseTransformDirection(
                    _groundFrameRight));

        _groundFrameSupportLocalForward =
            SafeNormal(
                support.InverseTransformDirection(
                    _groundFrameForward));
    }

    void InitializeGroundFrame(Vector3 normal)
    {
        normal = SafeNormal(normal);

        Vector3 forward =
            Vector3.ProjectOnPlane(
                Body.forward,
                normal);

        if (forward.sqrMagnitude < 0.000001f)
            forward =
                Vector3.ProjectOnPlane(
                    Body.right,
                    normal);

        if (forward.sqrMagnitude < 0.000001f)
            forward = AnyPerpendicular(normal);

        forward.Normalize();

        Vector3 right =
            Vector3.Cross(
                normal,
                forward);

        if (right.sqrMagnitude < 0.000001f)
            right = AnyPerpendicular(normal);

        right.Normalize();

        forward =
            Vector3.Cross(
                right,
                normal).normalized;

        _groundFrameNormal = normal;
        _groundFrameRight = right;
        _groundFrameForward = forward;
        _groundFrameInitialized = true;

        CaptureGroundFrameOnSupport(
            PrimaryGroundSupport());

        _groundFacingSeeded = false;
        _groundFacingSupport = null;
        _groundTurnIntensity = 0f;

        _lastGroundBodyPosition = Body.position;
        _groundBodyPositionSeeded = true;
    }

    void UpdateGroundSupportRelativeMotion(float dt)
    {
        dt = Mathf.Max(dt, 0.00001f);

        for (int i = 0; i < _legs.Count; i++)
        {
            RuntimeLeg leg = _legs[i];

            if (!leg.planted)
                continue;

            if (leg.support)
            {
                Vector3 bodyLocal =
                    leg.support.InverseTransformPoint(
                        Body.position);

                if (!leg.supportBodyLocalInitialized)
                {
                    leg.supportLastBodyLocalPoint = bodyLocal;
                    leg.supportBodyLocalInitialized = true;
                    leg.supportRelativeBodyVelocity = Vector3.zero;
                    continue;
                }

                Vector3 localDelta =
                    bodyLocal -
                    leg.supportLastBodyLocalPoint;

                leg.supportLastBodyLocalPoint =
                    bodyLocal;

                // TransformVector turns a delta in support-local coordinates
                // back into the support's current world frame. Pure movement
                // of the support itself therefore contributes ZERO.
                leg.supportRelativeBodyVelocity =
                    leg.support.TransformVector(
                        localDelta) /
                    dt;
            }
            else
            {
                leg.supportBodyLocalInitialized = false;
                leg.supportRelativeBodyVelocity = _bodyVelocity;
            }
        }
    }

    Vector3 GroundPlanarVelocity(
        RuntimeLeg leg,
        Vector3 normal)
    {
        Vector3 velocity =
            leg != null &&
            leg.planted &&
            leg.support
                ? leg.supportRelativeBodyVelocity
                : _bodyVelocity;

        return
            Vector3.ProjectOnPlane(
                velocity,
                normal);
    }

    Vector3 GroundPlanarVelocity(Vector3 normal)
    {
        for (int i = 0; i < _legs.Count; i++)
        {
            RuntimeLeg leg = _legs[i];

            if (leg.planted &&
                leg.support)
            {
                return
                    Vector3.ProjectOnPlane(
                        leg.supportRelativeBodyVelocity,
                        normal);
            }
        }

        return
            Vector3.ProjectOnPlane(
                _bodyVelocity,
                normal);
    }

    void UpdateGroundFrame(float dt)
    {
        Vector3 normal = SurfaceNormal();

        if (!_groundFrameInitialized)
        {
            InitializeGroundFrame(normal);
            return;
        }

        Transform support =
            PrimaryGroundSupport();

        Vector3 previousNormal =
            _groundFrameNormal;

        Vector3 previousRight =
            _groundFrameRight;

        // Reconstruct last frame's basis from the support's CURRENT transform.
        // This is the equivalent of parenting the gait frame to the platform.
        if (support)
        {
            if (_groundFrameSupport != support)
            {
                CaptureGroundFrameOnSupport(support);
            }
            else
            {
                previousNormal =
                    SafeNormal(
                        support.TransformDirection(
                            _groundFrameSupportLocalNormal));

                previousRight =
                    SafeNormal(
                        support.TransformDirection(
                            _groundFrameSupportLocalRight));
            }
        }
        else
        {
            _groundFrameSupport = null;
        }

        Quaternion transport =
            Quaternion.FromToRotation(
                previousNormal,
                normal);

        Vector3 right =
            transport *
            previousRight;

        right =
            Vector3.ProjectOnPlane(
                right,
                normal);

        if (right.sqrMagnitude < 0.000001f)
            right = AnyPerpendicular(normal);

        right.Normalize();

        Vector3 forward =
            Vector3.Cross(
                right,
                normal).normalized;

        // Follow motion RELATIVE TO the surface. A moving platform by itself
        // no longer rotates/re-aims the gait.
        Vector3 planarVelocity =
            GroundPlanarVelocity(normal);

        if (planarVelocity.sqrMagnitude > 0.01f)
        {
            Vector3 desiredForward =
                planarVelocity.normalized;

            // Avoid a 180-degree basis flip while reversing.
            if (Vector3.Dot(
                    desiredForward,
                    forward) < 0f)
            {
                desiredForward =
                    -desiredForward;
            }

            float follow =
                1f -
                Mathf.Exp(
                    -groundFrameFollowSharpness *
                    dt);

            forward =
                Vector3.Slerp(
                    forward,
                    desiredForward,
                    follow).normalized;

            right =
                Vector3.Cross(
                    normal,
                    forward).normalized;

            forward =
                Vector3.Cross(
                    right,
                    normal).normalized;
        }

        _groundFrameNormal = normal;
        _groundFrameRight = right;
        _groundFrameForward = forward;

        CaptureGroundFrameOnSupport(support);

        UpdateGroundTurnScramble(
            normal,
            dt);
    }

    void UpdateGroundTurnScramble(
        Vector3 normal,
        float dt)
    {
        Transform support =
            PrimaryGroundSupport();

        Vector3 facing;
        Vector3 turnAxis;

        if (support)
        {
            if (_groundFacingSupport != support)
            {
                _groundFacingSeeded = false;
                _groundFacingSupport = support;
            }

            turnAxis =
                SafeNormal(
                    support.InverseTransformDirection(
                        normal));

            facing =
                Vector3.ProjectOnPlane(
                    support.InverseTransformDirection(
                        Body.forward),
                    turnAxis);
        }
        else
        {
            if (_groundFacingSupport)
                _groundFacingSeeded = false;

            _groundFacingSupport = null;
            turnAxis = normal;

            facing =
                Vector3.ProjectOnPlane(
                    Body.forward,
                    normal);
        }

        float signedTurnRate = 0f;

        if (facing.sqrMagnitude > 0.000001f)
        {
            facing.Normalize();

            if (_groundFacingSeeded)
            {
                float signed =
                    Vector3.SignedAngle(
                        _previousGroundFacing,
                        facing,
                        turnAxis);

                signedTurnRate =
                    signed /
                    Mathf.Max(
                        dt,
                        0.00001f);
            }
            else
            {
                _groundFacingSeeded = true;
            }

            _previousGroundFacing = facing;
        }

        float desiredSigned =
            Mathf.Clamp(
                signedTurnRate /
                Mathf.Max(
                    groundFullTurnRate,
                    1f),
                -1f,
                1f);

        float desiredIntensity =
            Mathf.Abs(desiredSigned);

        float speed =
            desiredIntensity >
            _groundTurnIntensity
                ? groundTurnResponse
                : groundTurnSettleSpeed;

        float gaitRate =
            GroundGaitRate();

        float response =
            1f -
            Mathf.Exp(
                -speed *
                gaitRate *
                dt);

        _groundTurnIntensity =
            Mathf.Lerp(
                _groundTurnIntensity,
                desiredIntensity,
                response);

        _groundTurnSignedIntensity =
            Mathf.Lerp(
                _groundTurnSignedIntensity,
                desiredSigned,
                response);

        float turnSign =
            Mathf.Abs(_groundTurnSignedIntensity) > 0.001f
                ? Mathf.Sign(_groundTurnSignedIntensity)
                : 0f;

        for (int i = 0; i < _legs.Count; i++)
        {
            RuntimeLeg leg = _legs[i];

            float active =
                _groundTurnIntensity *
                groundTurnScramble;

            float radians =
                leg.angleDegrees *
                Mathf.Deg2Rad;

            // In the persistent leg frame:
            // cos(angle) = right/left side
            // sin(angle) = front/back position
            float side =
                Mathf.Cos(radians);

            float foreAft =
                Mathf.Sin(radians);

            // During a turn the OUTSIDE legs reach farther while the inside
            // legs tuck slightly. This is deterministic body mechanics, not
            // random noise.
            float outside =
                Mathf.Clamp01(
                    -turnSign *
                    side);

            float inside =
                Mathf.Clamp01(
                    turnSign *
                    side);

            float targetRadius =
                (outside -
                 inside * 0.65f) *
                groundTurnRadiusRange *
                active;

            // Quadrants shift by different amounts, causing a re-layout around
            // the curve without rotating all six preferred targets as one ring.
            float targetAngle =
                (-turnSign *
                 side *
                 foreAft) *
                groundTurnAngleRange *
                active;

            float smoothTime =
                1f /
                Mathf.Max(
                    (active > 0.01f
                        ? groundTurnResponse
                        : groundTurnSettleSpeed) *
                    gaitRate,
                    0.01f);

            leg.groundTurnAngleOffset =
                Mathf.SmoothDamp(
                    leg.groundTurnAngleOffset,
                    targetAngle,
                    ref leg.groundTurnAngleVelocity,
                    smoothTime,
                    Mathf.Infinity,
                    dt);

            leg.groundTurnRadiusOffset =
                Mathf.SmoothDamp(
                    leg.groundTurnRadiusOffset,
                    targetRadius,
                    ref leg.groundTurnRadiusVelocity,
                    smoothTime,
                    Mathf.Infinity,
                    dt);
        }
    }

    void ApplyPlantedFootFollow(
        Vector3 normal,
        float dt)
    {
        if (!_groundBodyPositionSeeded)
        {
            _lastGroundBodyPosition = Body.position;
            _groundBodyPositionSeeded = true;
        }

        _lastGroundBodyPosition =
            Body.position;

        if (plantedFootFollow <= 0f)
            return;

        dt = Mathf.Max(dt, 0.00001f);

        for (int i = 0; i < _legs.Count; i++)
        {
            RuntimeLeg leg = _legs[i];

            if (!leg.planted ||
                leg.stepping)
                continue;

            Vector3 current =
                CurrentPlantedPoint(leg);

            // This velocity has already had the platform's own translation and
            // rotation removed. Only motion of the VIRUS across the platform
            // may cause planted-foot creep.
            Vector3 relativeDelta =
                GroundPlanarVelocity(
                    leg,
                    normal) *
                dt;

            if (relativeDelta.sqrMagnitude <
                0.0000001f)
            {
                AssignTarget(
                    leg,
                    current);

                continue;
            }

            Vector3 moved =
                current +
                relativeDelta *
                plantedFootFollow;

            leg.plantedPoint = moved;

            if (leg.support)
            {
                leg.supportLocalPoint =
                    leg.support.InverseTransformPoint(
                        moved);
            }

            AssignTarget(
                leg,
                moved);
        }
    }

    void UpdateGeneratedRootTransforms()
    {
        if (_legs.Count == 0)
            return;

        Vector3 normal;

        if (IsGrounded())
        {
            normal = SurfaceNormal();
        }
        else
        {
            // In the air the attachment ring stays attached to the body's own
            // orientation. The FOOT targets are free to spin independently.
            normal =
                Body.up.sqrMagnitude > 0.000001f
                    ? Body.up.normalized
                    : Vector3.up;
        }

        for (int i = 0; i < _legs.Count; i++)
        {
            RuntimeLeg leg = _legs[i];

            if (leg.root)
            {
                PositionGeneratedRoot(
                    leg.root,
                    leg.angleDegrees,
                    normal);
            }
        }
    }

    void ClearGeneratedRoots()
    {
        if (!legParent)
            return;

        for (int i = legParent.childCount - 1; i >= 0; i--)
        {
            Transform child = legParent.GetChild(i);

            if (!child.name.StartsWith("__GeneratedLegRoot_"))
                continue;

            if (Application.isPlaying)
                Destroy(child.gameObject);
            else
                DestroyImmediate(child.gameObject);
        }
    }

    void CreateGeneratedMesh(RuntimeLeg leg, int index)
    {
        GameObject go = new GameObject($"__FluidLeg_{index + 1}");
        go.transform.SetParent(leg.root, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

        leg.meshObject = go;
        leg.meshFilter = go.AddComponent<MeshFilter>();
        leg.meshRenderer = go.AddComponent<MeshRenderer>();

        leg.mesh = new Mesh
        {
            name = $"FluidLegMesh_{index + 1}"
        };
        leg.mesh.MarkDynamic();

        leg.meshFilter.sharedMesh = leg.mesh;

        if (legMaterial)
            leg.meshRenderer.sharedMaterial = legMaterial;

        leg.meshRenderer.shadowCastingMode =
            castShadows
                ? UnityEngine.Rendering.ShadowCastingMode.On
                : UnityEngine.Rendering.ShadowCastingMode.Off;

        leg.meshRenderer.receiveShadows = receiveShadows;
    }

    void ClearGeneratedLegs()
    {
        for (int i = 0; i < _legs.Count; i++)
        {
            RuntimeLeg leg = _legs[i];

            if (leg.mesh)
            {
                if (Application.isPlaying) Destroy(leg.mesh);
                else DestroyImmediate(leg.mesh);
            }

            if (leg.meshObject)
            {
                if (Application.isPlaying) Destroy(leg.meshObject);
                else DestroyImmediate(leg.meshObject);
            }
        }

        _legs.Clear();
    }

    // ---------------------------------------------------------------------
    // Target assignment / validation
    // ---------------------------------------------------------------------

    Vector3 ToBodyLocal(Vector3 worldPoint)
    {
        return
            Quaternion.Inverse(Body.rotation) *
            (worldPoint - Body.position);
    }

    Vector3 FromBodyLocal(Vector3 localPoint)
    {
        return
            Body.position +
            Body.rotation * localPoint;
    }

    /// <summary>
    /// Single funnel for every foot target. Anything that is not finite, or
    /// that is nowhere near the virus, is discarded here rather than being
    /// allowed to reach the renderer.
    /// </summary>
    void AssignTarget(
        RuntimeLeg leg,
        Vector3 candidate)
    {
        if (!IsUsableLegTarget(candidate))
        {
            if (leg.lastSafeTargetInitialized)
                leg.targetPoint = leg.lastSafeTargetPoint;

            leg.targetInitialized = true;
            return;
        }

        leg.targetPoint = candidate;
        leg.targetInitialized = true;
        leg.lastSafeTargetPoint = candidate;
        leg.lastSafeTargetInitialized = true;
    }

    void HoldFeetRelativeToBody()
    {
        if (!_holdPoseCaptured)
        {
            for (int i = 0; i < _legs.Count; i++)
            {
                RuntimeLeg leg = _legs[i];

                Vector3 point =
                    leg.targetInitialized &&
                    IsFinite(leg.targetPoint)
                        ? leg.targetPoint
                        : leg.root.position;

                leg.holdLocalPoint = ToBodyLocal(point);
            }

            _holdPoseCaptured = true;
        }

        for (int i = 0; i < _legs.Count; i++)
        {
            RuntimeLeg leg = _legs[i];
            AssignTarget(leg, FromBodyLocal(leg.holdLocalPoint));
        }
    }

    // ---------------------------------------------------------------------
    // Ground gait
    // ---------------------------------------------------------------------

    float CurrentGroundMovementSpeed()
    {
        if (movement &&
            movement.IsSurfaceAttached)
        {
            return
                Mathf.Max(
                    0f,
                    movement.speed);
        }

        Vector3 normal =
            SurfaceNormal();

        return
            Vector3.ProjectOnPlane(
                _bodyVelocity,
                normal).magnitude;
    }

    float GroundMovement01()
    {
        if (movement &&
            movement.IsSurfaceAttached)
        {
            return
                Mathf.Clamp01(
                    movement.normalizedSpeed);
        }

        return
            Mathf.Clamp01(
                CurrentGroundMovementSpeed() /
                Mathf.Max(
                    gaitReferenceGroundSpeed,
                    0.01f));
    }

    float GroundGaitRate()
    {
        if (!scaleGroundMotionWithSpeed)
            return 1f;

        float movementRate =
            CurrentGroundMovementSpeed() /
            Mathf.Max(
                gaitReferenceGroundSpeed,
                0.01f);

        // Turning in place still needs usable foot movement even with almost
        // zero translational speed.
        float turnRate =
            _groundTurnIntensity;

        float rate =
            Mathf.Max(
                movementRate,
                Mathf.Lerp(
                    minimumGroundGaitRate,
                    1f,
                    turnRate));

        return
            Mathf.Clamp(
                rate,
                minimumGroundGaitRate,
                maximumGroundGaitRate);
    }

    bool CanStartGroundStep(
        int candidateIndex,
        Vector3 normal)
    {
        RuntimeLeg candidate =
            _legs[candidateIndex];

        float gaitRate =
            GroundGaitRate();

        if (Time.time -
            candidate.lastStepEndTime <
            minimumLegRestTime /
            Mathf.Max(
                gaitRate,
                0.01f))
            return false;

        for (int i = 0; i < _legs.Count; i++)
        {
            if (i == candidateIndex)
                continue;

            RuntimeLeg other =
                _legs[i];

            if (!other.stepping)
                continue;

            int separation =
                CircularLegDistance(
                    candidateIndex,
                    i,
                    _legs.Count);

            if (separation <
                minimumAirborneLegSpacing)
                return false;

            if (avoidSameSideAirborne)
            {
                float candidateSide =
                    GroundLegSide(
                        candidate,
                        normal);

                float otherSide =
                    GroundLegSide(
                        other,
                        normal);

                if (Mathf.Abs(candidateSide) > 0.30f &&
                    Mathf.Abs(otherSide) > 0.30f &&
                    Mathf.Sign(candidateSide) ==
                    Mathf.Sign(otherSide))
                    return false;
            }
        }

        return true;
    }

    static int CircularLegDistance(
        int a,
        int b,
        int count)
    {
        if (count <= 0)
            return 0;

        int direct =
            Mathf.Abs(a - b);

        return
            Mathf.Min(
                direct,
                count - direct);
    }

    float GroundLegSide(
        RuntimeLeg leg,
        Vector3 normal)
    {
        if (!_groundFrameInitialized)
            InitializeGroundFrame(normal);

        Vector3 radial =
            Vector3.ProjectOnPlane(
                leg.root.position -
                Body.position,
                normal);

        if (radial.sqrMagnitude < 0.000001f)
            return 0f;

        radial.Normalize();

        return
            Vector3.Dot(
                radial,
                _groundFrameRight);
    }

    void UpdateGroundFeet(float dt)
    {
        Vector3 normal = SurfaceNormal();

        ApplyPlantedFootFollow(normal, dt);

        debugLargestGroundFootError = 0f;

        int steppingCount = 0;

        for (int i = 0; i < _legs.Count; i++)
        {
            RuntimeLeg leg = _legs[i];

            if (!leg.planted)
                continue;

            if (leg.stepping)
            {
                AdvanceStep(leg, i, dt);
                if (leg.stepping)
                    steppingCount++;
            }
            else
            {
                AssignTarget(leg, CurrentPlantedPoint(leg));
            }
        }

        if (steppingCount >=
            maxSimultaneousSteps)
            return;

        // Never pop two new feet off the floor on the same frame. A short
        // start interval gives the gait a fluid cascade while allowing two
        // well-separated feet to overlap in the air.
        float gaitRate =
            GroundGaitRate();

        if (Time.time -
            _lastGroundStepStartTime <
            minimumStepStartInterval /
            Mathf.Max(
                gaitRate,
                0.01f))
            return;

        int bestIndex = -1;
        float bestScore = float.NegativeInfinity;

        for (int i = 0; i < _legs.Count; i++)
        {
            RuntimeLeg leg =
                _legs[i];

            if (!leg.planted ||
                leg.stepping ||
                !CanStartGroundStep(
                    i,
                    normal))
                continue;

            if (!TryDesiredGroundPoint(
                leg,
                i,
                normal,
                out Vector3 desired,
                out _,
                out _))
                continue;

            Vector3 planted =
                CurrentPlantedPoint(leg);

            Vector3 delta =
                desired -
                planted;

            float distance =
                delta.magnitude;

            debugLargestGroundFootError =
                Mathf.Max(
                    debugLargestGroundFootError,
                    distance);

            // STEP DISTANCE IS A HARD GATE.
            // No urgency, turning, parity, or drag-prevention score is allowed
            // to make this leg lift before the planted foot is at least this far
            // from the desired point.
            if (distance < stepDistance)
                continue;

            Vector3 planarVelocity =
                GroundPlanarVelocity(
                    leg,
                    normal);

            // Once the hard distance requirement is met, scoring decides WHICH
            // eligible leg should move first.
            float score =
                distance /
                Mathf.Max(
                    stepDistance,
                    0.0001f);

            if (planarVelocity.sqrMagnitude > 0.0001f &&
                delta.sqrMagnitude > 0.000001f)
            {
                float trailing =
                    Mathf.Clamp01(
                        Vector3.Dot(
                            delta.normalized,
                            planarVelocity.normalized));

                // Drag prevention now raises priority only. It no longer shrinks
                // Step Distance behind the scenes.
                score +=
                    trailing *
                    dragPrevention *
                    0.85f;
            }

            if (planarVelocity.sqrMagnitude > 0.0001f)
            {
                Vector3 rootToFoot =
                    planted -
                    leg.root.position;

                float behind =
                    Mathf.Max(
                        0f,
                        -Vector3.Dot(
                            rootToFoot,
                            planarVelocity.normalized));

                score +=
                    behind /
                    Mathf.Max(
                        stepDistance,
                        0.01f) *
                    dragPrevention *
                    0.75f;
            }

            // During a turn, favor legs whose coordinated turn target has
            // actually moved the most. No random "this leg feels like lifting"
            // term is added.
            score +=
                (Mathf.Abs(
                    leg.groundTurnAngleOffset) /
                 Mathf.Max(
                    groundTurnAngleRange,
                    1f)) *
                _groundTurnIntensity *
                0.30f;

            score +=
                Mathf.Abs(
                    leg.groundTurnRadiusOffset) *
                _groundTurnIntensity *
                0.45f;

            if (looseTripodBias &&
                ((i & 1) ==
                 _preferredParity))
            {
                score +=
                    0.06f *
                    (1f -
                     chaos);
            }

            if (score >
                bestScore)
            {
                bestScore =
                    score;

                bestIndex =
                    i;
            }
        }

        if (bestIndex < 0)
            return;

        RuntimeLeg selected =
            _legs[bestIndex];

        if (TryDesiredGroundPoint(
            selected,
            bestIndex,
            normal,
            out Vector3 target,
            out Vector3 targetNormal,
            out Transform support))
        {
            BeginStep(
                selected,
                target,
                targetNormal,
                support);

            _lastGroundStepStartTime =
                Time.time;

            _preferredParity =
                1 -
                (bestIndex & 1);
        }
    }

    bool TryDesiredGroundPoint(
        RuntimeLeg leg,
        int index,
        Vector3 surfaceNormal,
        out Vector3 point,
        out Vector3 hitNormal,
        out Transform support)
    {
        Vector3 radial = RootRadialDirection(leg, surfaceNormal);

        float distance =
            footDistance *
            leg.distanceScale *
            Mathf.Max(
                0.35f,
                1f +
                leg.groundTurnRadiusOffset);

        Vector3 restPoint =
            Body.position + radial * distance;

        Vector3 planarVelocity =
            GroundPlanarVelocity(
                leg,
                surfaceNormal);

        GetLegSlotBasis(
            surfaceNormal,
            out Vector3 tangentA,
            out Vector3 tangentB);

        float gaitRate =
            GroundGaitRate();

        float time =
            Time.time *
            wanderSpeed *
            gaitRate;

        float movementAmount =
            movement
                ? Mathf.Clamp01(movement.normalizedSpeed)
                : Mathf.Clamp01(
                    planarVelocity.magnitude /
                    Mathf.Max(
                        gaitReferenceGroundSpeed,
                        0.01f));

        float organicActivity =
            Mathf.Clamp01(
                movementAmount +
                _groundTurnIntensity *
                0.5f);

        Vector3 wander =
            (tangentA *
                Mathf.Sin(time + leg.phase) +
             tangentB *
                Mathf.Cos(time * 0.73f + leg.phase * 1.31f)) *
            (targetWander *
             chaos *
             0.35f *
             organicActivity);

        Vector3 ideal =
            restPoint +
            planarVelocity * velocityLeadTime +
            wander;

        return ProjectToGround(
            ideal,
            surfaceNormal,
            out point,
            out hitNormal,
            out support);
    }

    Vector3 RootRadialDirection(
        RuntimeLeg leg,
        Vector3 planeNormal)
    {
        planeNormal = SafeNormal(planeNormal);

        GetLegSlotBasis(
            planeNormal,
            out Vector3 right,
            out Vector3 forward);

        float radians =
            (leg.angleDegrees +
             leg.groundTurnAngleOffset) *
            Mathf.Deg2Rad;

        Vector3 radial =
            right * Mathf.Cos(radians) +
            forward * Mathf.Sin(radians);

        if (radial.sqrMagnitude < 0.000001f)
            radial = AnyPerpendicular(planeNormal);

        return radial.normalized;
    }

    void GetLegSlotBasis(
        Vector3 planeNormal,
        out Vector3 right,
        out Vector3 forward)
    {
        planeNormal = SafeNormal(planeNormal);

        if (IsGrounded())
        {
            if (!_groundFrameInitialized)
                InitializeGroundFrame(planeNormal);

            Quaternion transport =
                Quaternion.FromToRotation(
                    _groundFrameNormal,
                    planeNormal);

            right =
                transport *
                _groundFrameRight;

            right =
                Vector3.ProjectOnPlane(
                    right,
                    planeNormal);

            if (right.sqrMagnitude < 0.000001f)
                right = AnyPerpendicular(planeNormal);

            right.Normalize();

            forward =
                Vector3.Cross(
                    right,
                    planeNormal).normalized;

            return;
        }

        forward =
            Vector3.ProjectOnPlane(
                Body.forward,
                planeNormal);

        if (forward.sqrMagnitude < 0.000001f)
            forward =
                Vector3.ProjectOnPlane(
                    Body.right,
                    planeNormal);

        if (forward.sqrMagnitude < 0.000001f)
            forward = AnyPerpendicular(planeNormal);

        forward.Normalize();

        right =
            Vector3.Cross(
                planeNormal,
                forward).normalized;

        forward =
            Vector3.Cross(
                right,
                planeNormal).normalized;
    }

    bool ProjectToGround(
        Vector3 expected,
        Vector3 normal,
        out Vector3 point,
        out Vector3 hitNormal,
        out Transform support)
    {
        normal = SafeNormal(normal);

        Vector3 origin =
            expected + normal * probeHeight;

        if (Physics.Raycast(
            origin,
            -normal,
            out RaycastHit hit,
            probeHeight + probeDistance,
            groundMask,
            QueryTriggerInteraction.Ignore) &&
            IsUsableHit(hit))
        {
            hitNormal = SafeNormal(hit.normal);
            point =
                hit.point +
                hitNormal * FootCenterClearance();
            support = ResolveHitSupport(hit);
            return true;
        }

        point = expected;
        hitNormal = normal;
        support = null;
        return false;
    }

    void SetStepEnd(
        RuntimeLeg leg,
        Vector3 worldPoint,
        Vector3 worldNormal,
        Transform support)
    {
        leg.stepEnd = worldPoint;
        leg.stepNormal = SafeNormal(worldNormal);
        leg.stepEndSupport = support;
        leg.stepEndSupportLocalValid = false;

        if (support)
        {
            leg.stepEndSupportLocalPoint =
                support.InverseTransformPoint(
                    worldPoint);

            leg.stepEndSupportLocalNormal =
                SafeNormal(
                    support.InverseTransformDirection(
                        worldNormal));

            leg.stepEndSupportLocalValid = true;
        }
    }

    Vector3 CurrentStepStartPoint(
        RuntimeLeg leg)
    {
        if (leg.stepStartSupport &&
            leg.stepStartSupportLocalValid)
        {
            leg.stepStart =
                leg.stepStartSupport.TransformPoint(
                    leg.stepStartSupportLocalPoint);
        }

        return leg.stepStart;
    }

    Vector3 CurrentStepEndPoint(
        RuntimeLeg leg)
    {
        if (leg.stepEndSupport &&
            leg.stepEndSupportLocalValid)
        {
            leg.stepEnd =
                leg.stepEndSupport.TransformPoint(
                    leg.stepEndSupportLocalPoint);
        }
        else if (!leg.stepEndSupport)
        {
            leg.stepEndSupportLocalValid = false;
        }

        return leg.stepEnd;
    }

    Vector3 CurrentStepNormal(
        RuntimeLeg leg)
    {
        if (leg.stepEndSupport &&
            leg.stepEndSupportLocalValid)
        {
            leg.stepNormal =
                SafeNormal(
                    leg.stepEndSupport.TransformDirection(
                        leg.stepEndSupportLocalNormal));
        }

        return SafeNormal(
            leg.stepNormal);
    }

    float CurrentStepNormalizedTime(
        RuntimeLeg leg)
    {
        float gaitRate =
            GroundGaitRate();

        float duration =
            stepDuration *
            Mathf.Lerp(
                1f,
                leg.durationScale,
                chaos) /
            Mathf.Max(
                gaitRate,
                0.01f);

        return
            Mathf.Clamp01(
                leg.stepTimer /
                Mathf.Max(
                    duration,
                    0.01f));
    }

    Vector3 CurrentSwingPoint(
        RuntimeLeg leg)
    {
        float t =
            CurrentStepNormalizedTime(
                leg);

        float smooth =
            t * t *
            (3f - 2f * t);

        smooth =
            Mathf.Lerp(
                smooth,
                1f -
                    Mathf.Pow(
                        1f - t,
                        2.25f),
                0.25f * chaos);

        Vector3 basePoint =
            Vector3.LerpUnclamped(
                CurrentStepStartPoint(leg),
                CurrentStepEndPoint(leg),
                smooth);

        float speed01 =
            GroundMovement01();

        float height =
            stepHeight *
            Mathf.Lerp(
                1f,
                leg.heightScale,
                chaos) *
            Mathf.Lerp(
                1f,
                1f +
                    stepHeightSpeedInfluence,
                speed01);

        float lift =
            Mathf.Sin(
                t * Mathf.PI) *
            height;

        return
            basePoint +
            CurrentStepNormal(leg) *
            lift;
    }

    void BeginStep(
        RuntimeLeg leg,
        Vector3 target,
        Vector3 normal,
        Transform support)
    {
        leg.stepping = true;
        leg.stepTimer = 0f;

        leg.stepStart =
            CurrentPlantedPoint(
                leg);

        leg.stepStartSupport =
            leg.support;

        leg.stepStartSupportLocalValid =
            false;

        if (leg.stepStartSupport)
        {
            leg.stepStartSupportLocalPoint =
                leg.stepStartSupport.InverseTransformPoint(
                    leg.stepStart);

            leg.stepStartSupportLocalValid =
                true;
        }

        SetStepEnd(
            leg,
            target,
            normal,
            support);
    }

    void AdvanceStep(
        RuntimeLeg leg,
        int index,
        float dt)
    {
        leg.stepTimer += dt;

        // Refresh the support-anchored endpoint BEFORE retargeting. If the
        // platform moved since last frame, the old local point has already moved
        // with it exactly like a child transform.
        Vector3 currentEnd =
            CurrentStepEndPoint(
                leg);

        Vector3 currentNormal =
            CurrentStepNormal(
                leg);

        if (retargetSwingFeet)
        {
            Vector3 normal =
                SurfaceNormal();

            if (TryDesiredGroundPoint(
                leg,
                index,
                normal,
                out Vector3 newest,
                out Vector3 newestNormal,
                out Transform newestSupport))
            {
                float response =
                    1f -
                    Mathf.Exp(
                        -swingRetargetSharpness *
                        dt);

                SetStepEnd(
                    leg,
                    Vector3.Lerp(
                        currentEnd,
                        newest,
                        response),
                    Vector3.Slerp(
                        currentNormal,
                        newestNormal,
                        response).normalized,
                    newestSupport);
            }
        }

        AssignTarget(
            leg,
            CurrentSwingPoint(
                leg));

        if (CurrentStepNormalizedTime(leg) < 1f)
            return;

        Vector3 finalPoint =
            CurrentStepEndPoint(
                leg);

        Transform finalSupport =
            leg.stepEndSupport;

        leg.stepping = false;
        leg.lastStepEndTime = Time.time;

        Plant(
            leg,
            finalPoint,
            finalSupport);

        AssignTarget(
            leg,
            CurrentPlantedPoint(
                leg));
    }

    // ---------------------------------------------------------------------
    // Flying / landing preparation
    // ---------------------------------------------------------------------

    void EnterFlyingLegMode()
    {
        _landingPrepare = 0f;
        _hasLandingHit = false;
        _flyingFrameInitialized = false;
        _flightDirectionSeeded = false;
        _maneuverIntensity = 0f;
        _takeoffLegBlend = 0f;
        _stableFlightDirectionInitialized = false;
        _holdPoseCaptured = false;

        for (int i = 0; i < _legs.Count; i++)
        {
            RuntimeLeg leg = _legs[i];

            leg.bendFrameInitialized = false;
            leg.maneuverAngleOffset = 0f;
            leg.maneuverAngleVelocity = 0f;
            leg.maneuverRadiusOffset = 0f;
            leg.maneuverRadiusVelocity = 0f;
            leg.maneuverAxialOffset = 0f;
            leg.maneuverAxialVelocity = 0f;

            // Capture the exact current grounded pose BEFORE the flying orbit
            // is calculated. A planted foot is authoritative. If the leg is
            // mid-step, preserve its visible swing target instead.
            Vector3 startPoint;

            if (leg.stepping &&
                leg.targetInitialized &&
                IsUsableLegTarget(leg.targetPoint))
            {
                startPoint = leg.targetPoint;
            }
            else if (leg.planted &&
                     IsUsableLegTarget(CurrentPlantedPoint(leg)))
            {
                startPoint = CurrentPlantedPoint(leg);
            }
            else if (leg.targetInitialized &&
                     IsUsableLegTarget(leg.targetPoint))
            {
                startPoint = leg.targetPoint;
            }
            else
            {
                startPoint = leg.root.position;
            }

            // Stored in the body's frame. The takeoff blend then travels with
            // the virus instead of reaching back toward the launch spot.
            leg.flightStartLocalPoint = ToBodyLocal(startPoint);
            leg.holdLocalPoint = leg.flightStartLocalPoint;

            // Make the current target equal the captured point immediately.
            // There is therefore no intermediate frame where the renderer can
            // see an old/default/zero target.
            leg.stepping = false;
            leg.support = null;
            leg.supportBodyLocalInitialized = false;
            leg.supportRelativeBodyVelocity = Vector3.zero;

            leg.stepStartSupport = null;
            leg.stepStartSupportLocalValid = false;
            leg.stepEndSupport = null;
            leg.stepEndSupportLocalValid = false;

            leg.planted = true;

            AssignTarget(leg, startPoint);
        }

        Vector3 velocity = CurrentFlightVelocity();

        Vector3 initialDirection;

        if (velocity.magnitude > flyingDirectionHoldSpeed)
        {
            initialDirection = velocity.normalized;
        }
        else if (_groundFrameInitialized &&
                 _groundFrameForward.sqrMagnitude > 0.000001f)
        {
            initialDirection = _groundFrameForward.normalized;
        }
        else
        {
            initialDirection = Body.forward;
        }

        _stableFlightDirection =
            SafeNormal(initialDirection);

        _stableFlightDirectionInitialized = true;

        InitializeFlyingOrbitFrame(
            FlyingSpinAxis(_stableFlightDirection));
    }

    Vector3 UpdateStableFlightDirection(
        Vector3 velocity,
        float dt)
    {
        float speed =
            velocity.magnitude;

        if (!_stableFlightDirectionInitialized)
        {
            Vector3 seed =
                speed > flyingDirectionHoldSpeed
                    ? velocity.normalized
                    : (_groundFrameInitialized
                        ? _groundFrameForward
                        : Body.forward);

            _stableFlightDirection =
                SafeNormal(seed);

            _stableFlightDirectionInitialized =
                true;

            return _stableFlightDirection;
        }

        // Only update direction from velocity when there is enough velocity to
        // define a meaningful direction. At low/zero speed, HOLD the last one.
        if (speed > flyingDirectionHoldSpeed)
        {
            Vector3 desired =
                velocity.normalized;

            float response =
                1f -
                Mathf.Exp(
                    -flyingDirectionFollowSharpness *
                    dt);

            // Special-case nearly opposite directions so Slerp never gets an
            // arbitrary hemisphere choice.
            float dot =
                Vector3.Dot(
                    _stableFlightDirection,
                    desired);

            if (dot < -0.995f)
            {
                Vector3 turnAxis =
                    _flyingFrameInitialized
                        ? _flyingOrbitRight
                        : Body.up;

                if (turnAxis.sqrMagnitude < 0.000001f)
                    turnAxis = AnyPerpendicular(_stableFlightDirection);

                Quaternion rotation =
                    Quaternion.AngleAxis(
                        180f * response,
                        turnAxis.normalized);

                _stableFlightDirection =
                    (rotation *
                     _stableFlightDirection).normalized;
            }
            else
            {
                _stableFlightDirection =
                    Vector3.Slerp(
                        _stableFlightDirection,
                        desired,
                        response).normalized;
            }
        }

        return _stableFlightDirection;
    }

    void UpdateFlyingFeet(float dt)
    {
        Vector3 velocity =
            CurrentFlightVelocity();

        float speed = velocity.magnitude;

        Vector3 travelDirection =
            UpdateStableFlightDirection(
                velocity,
                dt);

        UpdateAirManeuverChaos(
            travelDirection,
            dt);

        UpdateLandingPreparation(
            travelDirection,
            speed,
            dt);

        _takeoffLegBlend =
            Mathf.MoveTowards(
                _takeoffLegBlend,
                1f,
                dt / Mathf.Max(takeoffLegBlendDuration, 0.01f));

        float takeoffBlendSmooth =
            _takeoffLegBlend *
            _takeoffLegBlend *
            (3f - 2f * _takeoffLegBlend);

        float speed01 =
            movement
                ? Mathf.Clamp01(movement.normalizedSpeed)
                : Mathf.Clamp01(speed / 10f);

        float spinSpeed =
            Mathf.Lerp(
                flyingSpinMin,
                flyingSpinMax,
                speed01);

        if (stopSpinWhenIdle)
        {
            float spinActivity =
                Mathf.SmoothStep(
                    0f,
                    1f,
                    Mathf.Clamp01(
                        speed /
                        Mathf.Max(
                            flyingSpinActivationSpeed,
                            0.01f)));

            spinSpeed *=
                spinActivity;
        }

        spinSpeed *=
            Mathf.Lerp(
                1f,
                landingSpinMultiplier,
                _landingPrepare);

        _flyingSpinAngle =
            Mathf.Repeat(
                _flyingSpinAngle +
                spinSpeed * dt,
                360f);

        Vector3 desiredSpinAxis =
            FlyingSpinAxis(travelDirection);

        UpdateFlyingOrbitFrame(
            desiredSpinAxis,
            dt);

        for (int i = 0; i < _legs.Count; i++)
        {
            RuntimeLeg leg = _legs[i];

            Vector3 airTarget =
                FlyingTargetForLeg(
                    leg,
                    i);

            Vector3 desiredTarget =
                airTarget;

            if (_hasLandingHit &&
                _landingPrepare > 0.001f)
            {
                Vector3 landingTarget =
                    LandingTargetForLeg(
                        leg,
                        _landingHitPoint,
                        _landingHitNormal);

                float landingBlend =
                    _landingPrepare *
                    _landingPrepare;

                desiredTarget =
                    Vector3.LerpUnclamped(
                        airTarget,
                        landingTarget,
                        landingBlend);
            }

            // Do not jump directly from a planted ground foot to a rotating
            // air orbit. Blend the exact old target into the new orbit, with
            // the old pose carried along by the body.
            Vector3 blended =
                Vector3.LerpUnclamped(
                    FromBodyLocal(leg.flightStartLocalPoint),
                    desiredTarget,
                    takeoffBlendSmooth);

            AssignTarget(leg, blended);

            leg.holdLocalPoint = ToBodyLocal(leg.targetPoint);
        }
    }

    Vector3 CurrentFlightVelocity()
    {
        // Cached: GetComponent every frame for every velocity query was pure
        // overhead. Re-resolved if the movement reference is swapped at runtime.
        if (!_movementRigidbodyResolved ||
            _movementRigidbodySource != movement)
        {
            _movementRigidbody =
                movement
                    ? movement.GetComponent<Rigidbody>()
                    : null;

            _movementRigidbodySource = movement;
            _movementRigidbodyResolved = true;
        }

        if (_movementRigidbody)
            return _movementRigidbody.linearVelocity;

        return _bodyVelocity;
    }

    void UpdateAirManeuverChaos(
        Vector3 travelDirection,
        float dt)
    {
        travelDirection = SafeNormal(travelDirection);

        float turnRate = 0f;

        if (_flightDirectionSeeded)
        {
            float angle =
                Vector3.Angle(
                    _previousFlightDirection,
                    travelDirection);

            turnRate =
                angle /
                Mathf.Max(dt, 0.00001f);
        }
        else
        {
            _flightDirectionSeeded = true;
        }

        _previousFlightDirection =
            travelDirection;

        float targetIntensity =
            Mathf.Clamp01(
                turnRate /
                Mathf.Max(
                    maneuverFullTurnRate,
                    1f));

        // Slightly favor quick engagement and slower release so the chaotic
        // rearrangement hangs around for a moment after a sharp maneuver.
        float rise =
            1f -
            Mathf.Exp(
                -maneuverResponse *
                dt);

        float fall =
            1f -
            Mathf.Exp(
                -maneuverSettleSpeed *
                dt);

        _maneuverIntensity =
            Mathf.Lerp(
                _maneuverIntensity,
                targetIntensity,
                targetIntensity >
                _maneuverIntensity
                    ? rise
                    : fall);

        float time =
            Time.time;

        for (int i = 0; i < _legs.Count; i++)
        {
            RuntimeLeg leg = _legs[i];

            // Smooth changing pseudo-random targets. Different frequencies and
            // phases make the legs rearrange independently instead of forming
            // another synchronized pattern.
            float angleNoise =
                Mathf.Sin(
                    time *
                        (1.75f +
                         i * 0.11f) +
                    leg.phase) *
                0.65f +
                Mathf.Sin(
                    time *
                        (0.83f +
                         i * 0.07f) +
                    leg.phase *
                        2.17f) *
                0.35f;

            float radiusNoise =
                Mathf.Sin(
                    time *
                        (1.21f +
                         i * 0.09f) +
                    leg.phase *
                        1.43f);

            float active =
                _maneuverIntensity *
                maneuverChaos;

            float targetAngle =
                angleNoise *
                maneuverAngleRange *
                active;

            float targetRadius =
                radiusNoise *
                maneuverRadiusRange *
                active;

            float axialNoise =
                Mathf.Sin(
                    time *
                        (2.37f +
                         i * 0.13f) +
                    leg.phase *
                        0.71f) *
                0.6f +
                Mathf.Cos(
                    time *
                        (1.09f +
                         i * 0.05f) +
                    leg.phase *
                        2.61f) *
                0.4f;

            float targetAxial =
                axialNoise *
                maneuverAxialRange *
                active;

            float response =
                active > 0.01f
                    ? maneuverResponse
                    : maneuverSettleSpeed;

            float smoothTime =
                1f /
                Mathf.Max(
                    response,
                    0.01f);

            leg.maneuverAngleOffset =
                Mathf.SmoothDamp(
                    leg.maneuverAngleOffset,
                    targetAngle,
                    ref leg.maneuverAngleVelocity,
                    smoothTime,
                    Mathf.Infinity,
                    dt);

            leg.maneuverRadiusOffset =
                Mathf.SmoothDamp(
                    leg.maneuverRadiusOffset,
                    targetRadius,
                    ref leg.maneuverRadiusVelocity,
                    smoothTime,
                    Mathf.Infinity,
                    dt);

            leg.maneuverAxialOffset =
                Mathf.SmoothDamp(
                    leg.maneuverAxialOffset,
                    targetAxial,
                    ref leg.maneuverAxialVelocity,
                    smoothTime,
                    Mathf.Infinity,
                    dt);
        }
    }

    Vector3 FlyingSpinAxis(
        Vector3 travelDirection)
    {
        if (spinAroundTravelDirection)
        {
            if (_stableFlightDirectionInitialized &&
                _stableFlightDirection.sqrMagnitude > 0.000001f)
            {
                return
                    _stableFlightDirection.normalized;
            }

            if (travelDirection.sqrMagnitude > 0.000001f)
                return travelDirection.normalized;

            if (_flyingFrameInitialized &&
                _flyingOrbitAxis.sqrMagnitude > 0.000001f)
            {
                return
                    _flyingOrbitAxis.normalized;
            }
        }

        Vector3 axis =
            Body.TransformDirection(
                flyingSpinAxisLocal);

        return axis.sqrMagnitude > 0.000001f
            ? axis.normalized
            : Body.forward;
    }

    void InitializeFlyingOrbitFrame(Vector3 axis)
    {
        axis = SafeNormal(axis);

        Vector3 seedForward =
            _groundFrameInitialized
                ? _groundFrameForward
                : Body.forward;

        Vector3 forward =
            Vector3.ProjectOnPlane(
                seedForward,
                axis);

        if (forward.sqrMagnitude < 0.000001f)
        {
            Vector3 seedRight =
                _groundFrameInitialized
                    ? _groundFrameRight
                    : Body.up;

            forward =
                Vector3.ProjectOnPlane(
                    seedRight,
                    axis);
        }

        if (forward.sqrMagnitude < 0.000001f)
            forward = AnyPerpendicular(axis);

        forward.Normalize();

        Vector3 right =
            Vector3.Cross(
                axis,
                forward);

        if (right.sqrMagnitude < 0.000001f)
            right = AnyPerpendicular(axis);

        right.Normalize();

        forward =
            Vector3.Cross(
                right,
                axis).normalized;

        _flyingOrbitAxis = axis;
        _flyingOrbitRight = right;
        _flyingOrbitForward = forward;
        _flyingFrameInitialized = true;
    }

    void UpdateFlyingOrbitFrame(
        Vector3 desiredAxis,
        float dt)
    {
        desiredAxis = SafeNormal(desiredAxis);

        if (!_flyingFrameInitialized)
        {
            InitializeFlyingOrbitFrame(
                desiredAxis);
            return;
        }

        float response =
            1f -
            Mathf.Exp(
                -flyingAxisFollowSharpness *
                dt);

        Vector3 newAxis =
            Vector3.Slerp(
                _flyingOrbitAxis,
                desiredAxis,
                response);

        if (newAxis.sqrMagnitude < 0.000001f)
            newAxis = desiredAxis;

        newAxis.Normalize();

        // Parallel-transport the old ring orientation onto the new axis.
        // This preserves angular continuity instead of choosing a brand-new
        // "forward" vector that may suddenly flip signs.
        Quaternion transport =
            Quaternion.FromToRotation(
                _flyingOrbitAxis,
                newAxis);

        Vector3 newRight =
            transport *
            _flyingOrbitRight;

        newRight =
            Vector3.ProjectOnPlane(
                newRight,
                newAxis);

        if (newRight.sqrMagnitude < 0.000001f)
        {
            newRight =
                transport *
                _flyingOrbitForward;

            newRight =
                Vector3.ProjectOnPlane(
                    newRight,
                    newAxis);
        }

        if (newRight.sqrMagnitude < 0.000001f)
            newRight = AnyPerpendicular(newAxis);

        newRight.Normalize();

        Vector3 newForward =
            Vector3.Cross(
                newRight,
                newAxis).normalized;

        // Keep the new forward direction on the same hemisphere as the
        // transported old forward. This removes rare 180-degree sign flips.
        Vector3 transportedForward =
            transport *
            _flyingOrbitForward;

        if (Vector3.Dot(
                newForward,
                transportedForward) < 0f)
        {
            newRight = -newRight;
            newForward = -newForward;
        }

        _flyingOrbitAxis = newAxis;
        _flyingOrbitRight = newRight;
        _flyingOrbitForward = newForward;
    }

    Vector3 FlyingTargetForLeg(
        RuntimeLeg leg,
        int index)
    {
        if (!_flyingFrameInitialized)
        {
            InitializeFlyingOrbitFrame(
                Body.forward);
        }

        // Use one continuous angular value:
        // permanent leg slot + accumulated spin.
        float angle =
            leg.angleDegrees +
            _flyingSpinAngle +
            leg.maneuverAngleOffset;

        float radians =
            angle *
            Mathf.Deg2Rad;

        Vector3 radial =
            _flyingOrbitRight *
                Mathf.Cos(radians) +
            _flyingOrbitForward *
                Mathf.Sin(radians);

        if (radial.sqrMagnitude < 0.000001f)
            radial = AnyPerpendicular(_flyingOrbitAxis);

        radial.Normalize();

        float radius =
            footDistance *
            leg.distanceScale *
            flyingReachMultiplier *
            Mathf.Max(
                0.25f,
                1f +
                leg.maneuverRadiusOffset);

        radius *=
            1f +
            Mathf.Sin(
                Time.time *
                    (2f + index * 0.17f) +
                leg.phase) *
            flyingWobble *
            chaos;

        return
            Body.position +
            radial * radius +
            _flyingOrbitAxis *
                leg.maneuverAxialOffset;
    }

    void UpdateLandingPreparation(
        Vector3 travelDirection,
        float speed,
        float dt)
    {
        travelDirection = SafeNormal(travelDirection);

        float castDistance =
            Mathf.Max(
                0.01f,
                landingPrepareDistance +
                speed * landingPredictionTime);

        // IMPORTANT: cast into a LOCAL hit. Passing the persistent field
        // straight in meant every missed cast overwrote it with a zeroed
        // struct, and a zeroed struct's point is the world origin - which is
        // exactly how the legs ended up reaching for (0,0,0).
        RaycastHit hit;
        bool hasHit;

        if (landingProbeRadius > 0.001f)
        {
            hasHit =
                Physics.SphereCast(
                    Body.position,
                    landingProbeRadius,
                    travelDirection,
                    out hit,
                    castDistance,
                    groundMask,
                    QueryTriggerInteraction.Ignore);
        }
        else
        {
            hasHit =
                Physics.Raycast(
                    Body.position,
                    travelDirection,
                    out hit,
                    castDistance,
                    groundMask,
                    QueryTriggerInteraction.Ignore);
        }

        // A sphere cast that STARTS already overlapping a collider reports a
        // hit with distance 0, a zero normal and point (0,0,0). Treat that as
        // no usable landing information rather than as a landing site.
        if (hasHit &&
            !IsUsableHit(hit))
        {
            hasHit = false;
            debugRejectedLandingHits++;
        }

        float desired = 0f;

        if (hasHit)
        {
            _landingHitPoint = hit.point;
            _landingHitNormal = SafeNormal(hit.normal);
            _hasLandingHit = true;

            desired =
                1f -
                Mathf.Clamp01(
                    hit.distance /
                    castDistance);
        }

        float response =
            1f -
            Mathf.Exp(
                -landingPrepareSharpness *
                dt);

        _landingPrepare =
            Mathf.Lerp(
                _landingPrepare,
                desired,
                response);

        // Keep the most recent valid landing hit while the blend fades.
        // Otherwise a single missed SphereCast frame can make all legs snap
        // instantly back to the orbit target.
        if (!hasHit &&
            _landingPrepare <= 0.001f)
        {
            _landingPrepare = 0f;
            _hasLandingHit = false;
        }
    }

    Vector3 LandingTargetForLeg(
        RuntimeLeg leg,
        Vector3 hitPoint,
        Vector3 hitNormal)
    {
        Vector3 normal =
            SafeNormal(hitNormal);

        Vector3 radial =
            RootRadialDirection(
                leg,
                normal);

        Vector3 expected =
            hitPoint +
            radial *
                (footDistance *
                 leg.distanceScale *
                 landingReachMultiplier) +
            normal *
                FootCenterClearance();

        if (ProjectToGround(
            expected,
            normal,
            out Vector3 point,
            out _,
            out _))
            return point;

        return expected;
    }

    void PlantFeetFromAirPose()
    {
        Vector3 normal =
            SurfaceNormal();

        InitializeGroundFrame(normal);
        _groundFrameSupport = null;
        _groundFacingSupport = null;
        _lastGroundStepStartTime = -999f;
        _holdPoseCaptured = false;

        for (int i = 0; i < _legs.Count; i++)
        {
            RuntimeLeg leg = _legs[i];

            Vector3 airPoint;

            if (leg.targetInitialized &&
                IsUsableLegTarget(leg.targetPoint))
            {
                airPoint = leg.targetPoint;
            }
            else if (leg.lastSafeTargetInitialized &&
                     IsUsableLegTarget(leg.lastSafeTargetPoint))
            {
                airPoint = leg.lastSafeTargetPoint;
            }
            else
            {
                airPoint = leg.root.position;
            }

            if (CaptureFootOnSurface(
                airPoint,
                normal,
                out Vector3 point,
                out Transform support))
            {
                Plant(leg, point, support);
            }
            else
            {
                Plant(leg, airPoint, null);
            }

            leg.stepping = false;
            leg.stepTimer = 0f;
            leg.lastStepEndTime = Time.time;

            AssignTarget(
                leg,
                CurrentPlantedPoint(leg));
        }
    }

    bool CaptureFootOnSurface(
        Vector3 pointInAir,
        Vector3 normal,
        out Vector3 point,
        out Transform support)
    {
        normal = SafeNormal(normal);

        float half =
            landingFootCaptureDistance *
            0.5f;

        Vector3 origin =
            pointInAir +
            normal * half;

        if (Physics.Raycast(
            origin,
            -normal,
            out RaycastHit hit,
            landingFootCaptureDistance,
            groundMask,
            QueryTriggerInteraction.Ignore) &&
            IsUsableHit(hit))
        {
            Vector3 n =
                SafeNormal(hit.normal);

            point =
                hit.point +
                n * FootCenterClearance();

            support = ResolveHitSupport(hit);
            return true;
        }

        origin =
            pointInAir -
            normal * half;

        if (Physics.Raycast(
            origin,
            normal,
            out hit,
            landingFootCaptureDistance,
            groundMask,
            QueryTriggerInteraction.Ignore) &&
            IsUsableHit(hit))
        {
            Vector3 n =
                SafeNormal(hit.normal);

            point =
                hit.point +
                n * FootCenterClearance();

            support = ResolveHitSupport(hit);
            return true;
        }

        point = pointInAir;
        support = null;
        return false;
    }

    // ---------------------------------------------------------------------
    // Planting / world support
    // ---------------------------------------------------------------------

    float FootCenterClearance()
    {
        // The target is the CENTER of the round tube tip. Therefore it must sit
        // at least one tip radius above the surface or half of the mesh will be
        // underground even though the center line is technically above it.
        return
            tipRadius +
            footSurfaceOffset +
            tubeGroundClearance;
    }

    void Plant(
        RuntimeLeg leg,
        Vector3 worldPoint,
        Transform support)
    {
        leg.planted = true;
        leg.plantedPoint = worldPoint;
        leg.support = support;

        leg.supportBodyLocalInitialized = false;
        leg.supportRelativeBodyVelocity = Vector3.zero;

        if (support)
        {
            leg.supportLocalPoint =
                support.InverseTransformPoint(
                    worldPoint);

            leg.supportLastBodyLocalPoint =
                support.InverseTransformPoint(
                    Body.position);

            leg.supportBodyLocalInitialized = true;
        }

        // A completed plant owns the foot again; old swing anchors must not
        // survive into the next step.
        leg.stepStartSupport = null;
        leg.stepStartSupportLocalValid = false;
        leg.stepEndSupport = null;
        leg.stepEndSupportLocalValid = false;
    }

    Vector3 CurrentPlantedPoint(
        RuntimeLeg leg)
    {
        // The support-local point is authoritative. Reconstructing it every
        // frame means translation, rotation and Rigidbody interpolation carry
        // the foot exactly as though the foot transform were parented there.
        if (leg.support)
        {
            Vector3 worldPoint =
                leg.support.TransformPoint(
                    leg.supportLocalPoint);

            // Keep the fallback current in case the support is destroyed
            // between frames.
            leg.plantedPoint =
                worldPoint;

            return worldPoint;
        }

        leg.support = null;
        leg.supportBodyLocalInitialized = false;
        leg.supportRelativeBodyVelocity = Vector3.zero;

        return leg.plantedPoint;
    }

    [ContextMenu("Snap Feet To Ground")]
    public void SnapFeetToGround()
    {
        Vector3 normal =
            SurfaceNormal();

        InitializeGroundFrame(normal);
        _groundFrameSupport = null;
        _groundFacingSupport = null;
        _lastGroundStepStartTime = -999f;
        _holdPoseCaptured = false;

        for (int i = 0; i < _legs.Count; i++)
        {
            RuntimeLeg leg = _legs[i];

            Vector3 radial =
                RootRadialDirection(
                    leg,
                    normal);

            Vector3 expected =
                Body.position +
                radial *
                (footDistance *
                 leg.distanceScale);

            if (ProjectToGround(
                expected,
                normal,
                out Vector3 point,
                out _,
                out Transform support))
            {
                Plant(
                    leg,
                    point,
                    support);
            }
            else
            {
                Plant(
                    leg,
                    expected,
                    null);
            }

            leg.stepping = false;

            AssignTarget(
                leg,
                CurrentPlantedPoint(leg));
        }
    }

    /// <summary>
    /// How far the rendered endpoint is allowed to move in one frame. This has
    /// to include everything the BODY did this frame, otherwise flying fast or
    /// spinning quickly is mistaken for a teleport and the guard itself becomes
    /// the glitch by holding a stale world position.
    /// </summary>
    float AllowedEndpointJumpThisFrame()
    {
        float dt =
            Mathf.Max(
                Time.deltaTime,
                0.0001f);

        float reach =
            footDistance *
            Mathf.Max(
                1f,
                Mathf.Max(
                    flyingReachMultiplier,
                    landingReachMultiplier));

        float baseAllowance =
            endpointJumpGuardDistance > 0f
                ? endpointJumpGuardDistance
                : Mathf.Max(
                    0.12f,
                    footDistance * 0.45f);

        // Pure body translation.
        float translation =
            Mathf.Max(
                _bodyVelocity.magnitude,
                CurrentFlightVelocity().magnitude) *
            dt *
            3f;

        // Body rotation swings the whole leg ring.
        float rotationArc =
            _bodyAngularSpeedDegrees *
            Mathf.Deg2Rad *
            reach *
            dt *
            3f;

        // Airborne orbit spin.
        float spinArc =
            IsFlying()
                ? flyingSpinMax *
                  Mathf.Deg2Rad *
                  reach *
                  dt *
                  3f
                : 0f;

        return
            baseAllowance +
            translation +
            rotationArc +
            spinArc;
    }

    Vector3 GuardRenderedEndpoint(
        RuntimeLeg leg,
        Vector3 candidate)
    {
        // Anything non-finite, or absurdly far from the virus, is never shown.
        if (!IsUsableLegTarget(candidate))
        {
            if (leg.renderTargetInitialized)
                return HoldRenderedEndpoint(leg);

            if (leg.lastSafeTargetInitialized &&
                IsUsableLegTarget(leg.lastSafeTargetPoint))
                candidate = leg.lastSafeTargetPoint;
            else
                candidate = leg.root.position;
        }

        if (!preventEndpointTeleports ||
            !leg.renderTargetInitialized)
        {
            AcceptRenderedEndpoint(leg, candidate);
            return candidate;
        }

        float jump =
            Vector3.Distance(
                leg.renderTargetPoint,
                candidate);

        float allowed =
            AllowedEndpointJumpThisFrame();

        if (jump <= allowed)
        {
            AcceptRenderedEndpoint(leg, candidate);
            return candidate;
        }

        // Suspicious large change. Do not render it immediately. Require the
        // new location to remain coherent for several consecutive frames.
        debugRejectedEndpointJumps++;
        debugLastRejectedEndpointJump = jump;

        float pendingTolerance =
            Mathf.Max(
                allowed * 0.5f,
                0.05f);

        if (leg.pendingRenderTargetInitialized &&
            Vector3.Distance(
                leg.pendingRenderTarget,
                candidate) <=
            pendingTolerance)
        {
            leg.pendingRenderTargetFrames++;
        }
        else
        {
            leg.pendingRenderTarget = candidate;
            leg.pendingRenderTargetInitialized = true;
            leg.pendingRenderTargetFrames = 1;
        }

        if (leg.pendingRenderTargetFrames >=
            endpointJumpConfirmationFrames)
        {
            // Persistent target: it was intentional, not a one-frame glitch.
            AcceptRenderedEndpoint(leg, candidate);
            return candidate;
        }

        return HoldRenderedEndpoint(leg);
    }

    void AcceptRenderedEndpoint(
        RuntimeLeg leg,
        Vector3 point)
    {
        leg.renderTargetPoint = point;
        leg.renderLocalOffset = ToBodyLocal(point);
        leg.renderTargetInitialized = true;
        leg.pendingRenderTargetInitialized = false;
        leg.pendingRenderTargetFrames = 0;
    }

    Vector3 HoldRenderedEndpoint(
        RuntimeLeg leg)
    {
        // Hold the pose RELATIVE TO THE BODY. Freezing a world position while
        // the virus keeps moving was itself a visible glitch: the leg stretched
        // back toward wherever it had last been accepted.
        Vector3 held =
            FromBodyLocal(leg.renderLocalOffset);

        leg.renderTargetPoint = held;
        return held;
    }

    // ---------------------------------------------------------------------
    // Fluid mesh
    // ---------------------------------------------------------------------

    void UpdateLegBendFrame(
        RuntimeLeg leg,
        Vector3 direction,
        Vector3 preferredUp,
        out Vector3 side,
        out Vector3 up)
    {
        direction = SafeNormal(direction);

        // The preferred direction has an actual meaning:
        // Grounded = away from the surface.
        // Flying  = backwards along travel.
        //
        // Use that semantic direction directly whenever possible. Preserving
        // each leg's own hemisphere instead made some legs appear to "face" the
        // opposite direction even in straight flight.
        Vector3 semanticUp =
            Vector3.ProjectOnPlane(
                preferredUp,
                direction);

        if (semanticUp.sqrMagnitude > 0.00001f)
        {
            semanticUp.Normalize();

            up = semanticUp;

            side =
                Vector3.Cross(
                    direction,
                    up);

            if (side.sqrMagnitude < 0.000001f)
                side = AnyPerpendicular(direction);

            side.Normalize();

            up =
                Vector3.Cross(
                    side,
                    direction).normalized;

            leg.bendSide = side;
            leg.bendUp = up;
            leg.bendFrameInitialized = true;
            return;
        }

        // Rare near-parallel case: transport the last valid frame instead of
        // inventing a new perpendicular axis and causing a visible flip.
        if (leg.bendFrameInitialized)
        {
            side =
                Vector3.ProjectOnPlane(
                    leg.bendSide,
                    direction);

            if (side.sqrMagnitude < 0.000001f)
            {
                side =
                    Vector3.ProjectOnPlane(
                        leg.bendUp,
                        direction);
            }

            if (side.sqrMagnitude < 0.000001f)
                side = AnyPerpendicular(direction);

            side.Normalize();

            up =
                Vector3.Cross(
                    side,
                    direction).normalized;

            leg.bendSide = side;
            leg.bendUp = up;
            return;
        }

        side = AnyPerpendicular(direction);
        up =
            Vector3.Cross(
                side,
                direction).normalized;

        leg.bendSide = side;
        leg.bendUp = up;
        leg.bendFrameInitialized = true;
    }

    void EnsureMeshBuffers(
        RuntimeLeg leg,
        int rings,
        int sides,
        out bool topologyChanged)
    {
        topologyChanged =
            leg.vertices == null ||
            leg.builtRings != rings ||
            leg.builtSides != sides;

        if (!topologyChanged)
            return;

        // Two extra vertices close the tube: one fan center at the root ring
        // and one at the tip ring. Without them the tube is an open pipe and
        // you can see straight down the inside of the foot.
        int vertexCount = rings * sides + 2;

        leg.vertices = new Vector3[vertexCount];
        leg.normals = new Vector3[vertexCount];
        leg.uvs = new Vector2[vertexCount];
        leg.triangles =
            new int[
                (rings - 1) * sides * 6 +
                sides * 6];

        int tri = 0;

        // WINDING
        // -------
        // Each ring's frame is (ringSide, ringUp, tangent) with
        // Cross(ringSide, ringUp) == tangent, and a vertex sits at
        // center + radius * (cos(a) * ringSide + sin(a) * ringUp).
        // Unity's front face normal is Cross(v1 - v0, v2 - v0), so the quad has
        // to be wound a -> b -> c. The previous a -> c -> b order produced an
        // inward-facing normal and rendered the tube inside out.
        for (int ring = 0; ring < rings - 1; ring++)
        {
            for (int s = 0; s < sides; s++)
            {
                int nextS =
                    (s + 1) %
                    sides;

                int a = ring * sides + s;
                int b = ring * sides + nextS;
                int c = (ring + 1) * sides + s;
                int d = (ring + 1) * sides + nextS;

                leg.triangles[tri++] = a;
                leg.triangles[tri++] = b;
                leg.triangles[tri++] = c;

                leg.triangles[tri++] = b;
                leg.triangles[tri++] = d;
                leg.triangles[tri++] = c;
            }
        }

        int rootCenter = rings * sides;
        int tipCenter = rings * sides + 1;
        int lastRing = (rings - 1) * sides;

        for (int s = 0; s < sides; s++)
        {
            int nextS =
                (s + 1) %
                sides;

            // Root cap faces backwards along the tube (-tangent).
            leg.triangles[tri++] = rootCenter;
            leg.triangles[tri++] = nextS;
            leg.triangles[tri++] = s;

            // Tip cap faces forwards along the tube (+tangent).
            leg.triangles[tri++] = tipCenter;
            leg.triangles[tri++] = lastRing + s;
            leg.triangles[tri++] = lastRing + nextS;
        }

        leg.uvs[rootCenter] = new Vector2(0.5f, 0f);
        leg.uvs[tipCenter] = new Vector2(0.5f, 1f);

        for (int ring = 0; ring < rings; ring++)
        {
            float t =
                ring /
                (float)(rings - 1);

            for (int s = 0; s < sides; s++)
            {
                leg.uvs[ring * sides + s] =
                    new Vector2(
                        s / (float)sides,
                        t);
            }
        }

        leg.builtRings = rings;
        leg.builtSides = sides;
    }

    void UpdateFluidMesh(
        RuntimeLeg leg,
        int legIndex,
        Vector3 curveUp)
    {
        if (!leg.mesh ||
            !leg.meshObject ||
            !leg.root)
            return;

        int rings =
            Mathf.Max(2, lengthSegments + 1);

        int sides =
            Mathf.Max(3, radialSegments);

        EnsureMeshBuffers(
            leg,
            rings,
            sides,
            out bool topologyChanged);

        Vector3 p0 =
            leg.root.position;

        Vector3 candidateP3;
        bool surfaceAnchored = false;

        // LateUpdate may run after Rigidbody interpolation has produced a newer
        // support transform than Update saw. Rebuild the grounded endpoint from
        // support-local coordinates HERE, immediately before drawing the mesh.
        if (IsGrounded() &&
            leg.planted &&
            !leg.stepping &&
            leg.support)
        {
            candidateP3 =
                CurrentPlantedPoint(
                    leg);

            surfaceAnchored = true;
        }
        else if (IsGrounded() &&
                 leg.planted &&
                 leg.stepping &&
                 (leg.stepStartSupport ||
                  leg.stepEndSupport))
        {
            candidateP3 =
                CurrentSwingPoint(
                    leg);

            surfaceAnchored = true;
        }
        else if (leg.targetInitialized &&
                 IsUsableLegTarget(leg.targetPoint))
        {
            candidateP3 =
                leg.targetPoint;
        }
        else if (leg.lastSafeTargetInitialized &&
                 IsUsableLegTarget(leg.lastSafeTargetPoint))
        {
            candidateP3 =
                leg.lastSafeTargetPoint;
        }
        else
        {
            Vector3 fallbackDirection =
                leg.root.position -
                Body.position;

            if (fallbackDirection.sqrMagnitude <
                0.000001f)
            {
                fallbackDirection =
                    Body.right;
            }

            fallbackDirection.Normalize();

            candidateP3 =
                leg.root.position +
                fallbackDirection *
                Mathf.Max(
                    footDistance -
                    legRootRadius,
                    0.05f);
        }

        if (IsUsableLegTarget(candidateP3))
        {
            leg.lastSafeTargetPoint =
                candidateP3;

            leg.lastSafeTargetInitialized =
                true;
        }

        Vector3 p3;

        if (surfaceAnchored)
        {
            // This point came from a known support-local anchor. Its movement is
            // authoritative, even if a fast Rigidbody translated/rotated farther
            // than the generic teleport guard normally permits.
            p3 = candidateP3;
            AcceptRenderedEndpoint(
                leg,
                p3);
        }
        else
        {
            // Free/airborne targets still retain the visual safety guard.
            p3 =
                GuardRenderedEndpoint(
                    leg,
                    candidateP3);
        }

        Vector3 delta =
            p3 - p0;

        float length =
            Mathf.Max(
                delta.magnitude,
                0.0001f);

        Vector3 direction =
            delta.sqrMagnitude > 0.000001f
                ? delta / length
                : (leg.root.position - Body.position).sqrMagnitude > 0.000001f
                    ? (leg.root.position - Body.position).normalized
                    : Body.right;

        UpdateLegBendFrame(
            leg,
            direction,
            curveUp,
            out Vector3 side,
            out Vector3 up);

        float fluidRate =
            IsGrounded()
                ? GroundGaitRate()
                : 1f;

        float wobble =
            Mathf.Sin(
                Time.time *
                    fluidWobbleSpeed *
                    fluidRate +
                leg.phase) *
            fluidWobble;

        Vector3 wobbleVector =
            side * wobble;

        float arch =
            curveHeight;

        if (leg.stepping)
            arch += stepHeight * 0.20f;

        Vector3 p1 =
            p0 +
            direction *
                (length * curveBias) +
            up * arch +
            wobbleVector;

        Vector3 p2 =
            p3 -
            direction *
                (length *
                 (1f - curveBias) *
                 0.55f) +
            up * (arch * 0.55f) -
            wobbleVector * 0.5f;

        float desiredLength =
            Mathf.Max(
                footDistance,
                0.001f);

        float stretch =
            length /
            desiredLength;

        float thicknessScale =
            Mathf.Clamp(
                1f -
                (stretch - 1f) *
                stretchThicknessResponse,
                0.55f,
                1.45f);

        Vector3 previousSide =
            side;

        bool grounded =
            IsGrounded();

        bool flying =
            IsFlying();

        Vector3 groundNormal =
            grounded
                ? SurfaceNormal()
                : Vector3.up;

        Vector3 clearanceOffset =
            Vector3.zero;

        int stride =
            Mathf.Clamp(
                tubeGroundSampleStride,
                1,
                4);

        Transform meshTransform =
            leg.meshObject.transform;

        // Captured from the real ring positions, so the caps follow the same
        // ground clearance and maneuver distortion as the tube itself.
        Vector3 rootCapCenter = p0;
        Vector3 rootCapNormal = -direction;
        Vector3 tipCapCenter = p3;
        Vector3 tipCapNormal = direction;

        for (int ring = 0; ring < rings; ring++)
        {
            float t =
                ring /
                (float)(rings - 1);

            Vector3 center =
                CubicBezier(
                    p0,
                    p1,
                    p2,
                    p3,
                    t);

            // Real fluid scrambling during maneuvers: distort the BODY of the
            // tentacle, not just its endpoint. Envelope keeps both ends fixed.
            if (flying &&
                _maneuverIntensity > 0.001f &&
                maneuverTubeWave > 0f)
            {
                float envelope =
                    Mathf.Sin(t * Mathf.PI);

                float wavePhase =
                    Time.time *
                        maneuverTubeWaveSpeed +
                    leg.phase +
                    t * Mathf.PI * 2.4f;

                float amount =
                    maneuverTubeWave *
                    _maneuverIntensity *
                    maneuverChaos *
                    envelope;

                center +=
                    side *
                        (Mathf.Sin(wavePhase) * amount) +
                    up *
                        (Mathf.Cos(wavePhase * 0.73f) *
                         amount * 0.65f);
            }

            Vector3 tangent =
                CubicBezierTangent(
                    p0,
                    p1,
                    p2,
                    p3,
                    t);

            if (tangent.sqrMagnitude < 0.000001f)
                tangent = direction;

            tangent.Normalize();

            Vector3 ringSide =
                Vector3.ProjectOnPlane(
                    previousSide,
                    tangent);

            if (ringSide.sqrMagnitude < 0.000001f)
                ringSide =
                    AnyPerpendicular(tangent);

            ringSide.Normalize();

            // Keep adjacent cross-sections on the same hemisphere. Without
            // this, a near-degenerate tangent can make one ring rotate 180.
            if (Vector3.Dot(
                    ringSide,
                    previousSide) < 0f)
            {
                ringSide = -ringSide;
            }

            Vector3 ringUp =
                Vector3.Cross(
                    tangent,
                    ringSide).normalized;

            previousSide = ringSide;

            float radius =
                Mathf.Lerp(
                    baseRadius,
                    tipRadius,
                    t) *
                thicknessScale;

            // Slight soft bulge around the middle.
            radius *=
                1f +
                Mathf.Sin(t * Mathf.PI) *
                0.06f *
                chaos;

            if (grounded &&
                keepTubeAboveGround)
            {
                // Sampling every Nth ring keeps this from firing a raycast per
                // ring per leg per frame; the lift barely changes between
                // neighbouring sections.
                if (ring % stride == 0 ||
                    ring == rings - 1)
                {
                    clearanceOffset =
                        GroundClearanceOffset(
                            center,
                            radius,
                            groundNormal);
                }

                center += clearanceOffset;
            }

            if (ring == 0)
            {
                rootCapCenter = center;
                rootCapNormal = -tangent;
            }

            if (ring == rings - 1)
            {
                tipCapCenter = center;
                tipCapNormal = tangent;
            }

            for (int s = 0; s < sides; s++)
            {
                float a =
                    (s / (float)sides) *
                    Mathf.PI *
                    2f;

                Vector3 outward =
                    ringSide *
                        Mathf.Cos(a) +
                    ringUp *
                        Mathf.Sin(a);

                Vector3 world =
                    center +
                    outward * radius;

                int index =
                    ring * sides + s;

                leg.vertices[index] =
                    meshTransform.InverseTransformPoint(world);

                leg.normals[index] =
                    meshTransform
                        .InverseTransformDirection(outward)
                        .normalized;
            }
        }

        int rootCapIndex = rings * sides;
        int tipCapIndex = rootCapIndex + 1;

        leg.vertices[rootCapIndex] =
            meshTransform.InverseTransformPoint(rootCapCenter);

        leg.normals[rootCapIndex] =
            meshTransform
                .InverseTransformDirection(rootCapNormal)
                .normalized;

        leg.vertices[tipCapIndex] =
            meshTransform.InverseTransformPoint(tipCapCenter);

        leg.normals[tipCapIndex] =
            meshTransform
                .InverseTransformDirection(tipCapNormal)
                .normalized;

        if (topologyChanged)
            leg.mesh.Clear();

        leg.mesh.SetVertices(leg.vertices);

        if (topologyChanged)
        {
            leg.mesh.SetUVs(0, leg.uvs);
            leg.mesh.SetTriangles(leg.triangles, 0, false);
        }

        if (recalculateNormals)
            leg.mesh.RecalculateNormals();
        else
            leg.mesh.SetNormals(leg.normals);

        leg.mesh.RecalculateBounds();

        if (leg.meshRenderer &&
            leg.meshRenderer.sharedMaterial !=
            legMaterial)
        {
            leg.meshRenderer.sharedMaterial =
                legMaterial;
        }
    }

    /// <summary>
    /// Displacement needed to keep the OUTSIDE of the tube above the collider
    /// under this section, rather than just its center line.
    /// </summary>
    Vector3 GroundClearanceOffset(
        Vector3 center,
        float radius,
        Vector3 referenceNormal)
    {
        referenceNormal =
            SafeNormal(referenceNormal);

        float castUp =
            Mathf.Max(
                probeHeight,
                radius +
                tubeGroundClearance +
                0.05f);

        Vector3 origin =
            center +
            referenceNormal *
                castUp;

        float castDistance =
            castUp +
            Mathf.Max(
                probeDistance,
                radius * 2f);

        if (!Physics.Raycast(
            origin,
            -referenceNormal,
            out RaycastHit hit,
            castDistance,
            groundMask,
            QueryTriggerInteraction.Ignore) ||
            !IsUsableHit(hit))
            return Vector3.zero;

        Vector3 hitNormal =
            SafeNormal(hit.normal);

        float required =
            radius +
            footSurfaceOffset +
            tubeGroundClearance;

        float currentDistance =
            Vector3.Dot(
                center -
                hit.point,
                hitNormal);

        if (currentDistance < required)
        {
            return
                hitNormal *
                (required -
                 currentDistance);
        }

        return Vector3.zero;
    }

    static Vector3 CubicBezier(
        Vector3 p0,
        Vector3 p1,
        Vector3 p2,
        Vector3 p3,
        float t)
    {
        float u = 1f - t;

        return
            u * u * u * p0 +
            3f * u * u * t * p1 +
            3f * u * t * t * p2 +
            t * t * t * p3;
    }

    static Vector3 CubicBezierTangent(
        Vector3 p0,
        Vector3 p1,
        Vector3 p2,
        Vector3 p3,
        float t)
    {
        float u = 1f - t;

        return
            3f * u * u *
                (p1 - p0) +
            6f * u * t *
                (p2 - p1) +
            3f * t * t *
                (p3 - p2);
    }

    // ---------------------------------------------------------------------
    // Utility
    // ---------------------------------------------------------------------

    void UpdateBodyVelocity()
    {
        if (!_velocitySeeded)
        {
            _lastBodyPosition =
                Body.position;

            _lastBodyRotation =
                Body.rotation;

            _bodyVelocity =
                Vector3.zero;

            _bodyAngularSpeedDegrees = 0f;

            _velocitySeeded = true;
            return;
        }

        float dt =
            Mathf.Max(
                Time.deltaTime,
                0.00001f);

        _bodyVelocity =
            (Body.position -
             _lastBodyPosition) /
            dt;

        _bodyAngularSpeedDegrees =
            Quaternion.Angle(
                _lastBodyRotation,
                Body.rotation) /
            dt;

        _lastBodyPosition =
            Body.position;

        _lastBodyRotation =
            Body.rotation;
    }

    static Vector3 SafeNormal(
        Vector3 value)
    {
        return
            value.sqrMagnitude >
            0.000001f
                ? value.normalized
                : Vector3.up;
    }

    static Vector3 AnyPerpendicular(
        Vector3 normal)
    {
        normal =
            SafeNormal(normal);

        // Pick the reference axis that is LEAST aligned with the normal.
        // Choosing the aligned one produced a near-zero cross product and,
        // after normalizing, a garbage direction.
        Vector3 axis =
            Mathf.Abs(normal.y) < 0.9f
                ? Vector3.up
                : Vector3.right;

        Vector3 result =
            Vector3.Cross(
                normal,
                axis);

        return
            result.sqrMagnitude > 0.000001f
                ? result.normalized
                : Vector3.right;
    }

    static float Hash01(int value)
    {
        uint x = (uint)value;

        x ^= x >> 16;
        x *= 0x7feb352d;
        x ^= x >> 15;
        x *= 0x846ca68b;
        x ^= x >> 16;

        return
            (x & 0x00FFFFFF) /
            16777215f;
    }

    static Transform ResolveHitSupport(RaycastHit hit)
    {
        // A Collider may be a child of a Rigidbody. Anchoring to the Rigidbody
        // transform gives every collider on that moving body one coherent local
        // coordinate frame and follows Rigidbody interpolation naturally.
        Rigidbody attached =
            hit.rigidbody;

        return
            attached
                ? attached.transform
                : hit.transform;
    }

    /// <summary>
    /// Rejects the degenerate hit Unity reports when a cast STARTS inside a
    /// collider: distance 0, zero normal, and point (0,0,0).
    /// </summary>
    static bool IsUsableHit(RaycastHit hit)
    {
        return
            hit.distance > 0.0001f &&
            hit.normal.sqrMagnitude > 0.000001f &&
            IsFinite(hit.point);
    }

    float MaximumReasonableLegDistance()
    {
        float reach =
            footDistance *
            Mathf.Max(
                1f,
                Mathf.Max(
                    landingReachMultiplier,
                    flyingReachMultiplier));

        float speed =
            Mathf.Max(
                _bodyVelocity.magnitude,
                CurrentFlightVelocity().magnitude);

        return
            Mathf.Max(
                2f,
                reach * 3f +
                legRootRadius +
                landingPrepareDistance +
                speed *
                Mathf.Max(
                    landingPredictionTime,
                    Time.deltaTime) +
                1f);
    }

    bool IsUsableLegTarget(
        Vector3 value)
    {
        if (!IsFinite(value))
            return false;

        // A valid procedural foot always stays reasonably close to the virus.
        // This catches accidental world-origin values in scenes where the virus
        // is nowhere near the origin, plus other stale target corruption.
        float maximumReasonableDistance =
            MaximumReasonableLegDistance();

        return
            (value - Body.position).sqrMagnitude <=
            maximumReasonableDistance *
            maximumReasonableDistance;
    }

    static bool IsFinite(
        Vector3 value)
    {
        return
            !float.IsNaN(value.x) &&
            !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) &&
            !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) &&
            !float.IsInfinity(value.z);
    }

    void OnDrawGizmosSelected()
    {
        if (!drawDebug)
            return;

        Transform rootBody =
            body ? body : transform;

        if (!Application.isPlaying)
        {
            if (legParent)
            {
                for (int i = 0; i < legCount; i++)
                {
                    float angle =
                        legRootAngleOffset +
                        (360f / Mathf.Max(1, legCount)) * i;

                    float radians = angle * Mathf.Deg2Rad;

                    Vector3 previewNormal =
                        rootBody.up.sqrMagnitude > 0.000001f
                            ? rootBody.up.normalized
                            : Vector3.up;

                    Vector3 previewForward =
                        Vector3.ProjectOnPlane(
                            rootBody.forward,
                            previewNormal);

                    if (previewForward.sqrMagnitude < 0.000001f)
                        previewForward = AnyPerpendicular(previewNormal);

                    previewForward.Normalize();

                    Vector3 previewRight =
                        Vector3.Cross(
                            previewNormal,
                            previewForward).normalized;

                    Vector3 radial =
                        previewRight * Mathf.Cos(radians) +
                        previewForward * Mathf.Sin(radians);

                    Vector3 world =
                        rootBody.position +
                        radial * legRootRadius +
                        previewNormal * legRootHeight;

                    Gizmos.DrawWireSphere(world, 0.04f);
                    Gizmos.DrawLine(rootBody.position, world);
                }
            }

            return;
        }

        for (int i = 0; i < _legs.Count; i++)
        {
            if (!_legs[i].root)
                continue;

            Gizmos.DrawSphere(
                _legs[i].targetPoint,
                0.035f);

            Gizmos.DrawLine(
                _legs[i].root.position,
                _legs[i].targetPoint);
        }

        if (IsFlying())
        {
            Vector3 velocity =
                CurrentFlightVelocity();

            Vector3 direction =
                velocity.sqrMagnitude >
                0.000001f
                    ? velocity.normalized
                    : Body.forward;

            float distance =
                landingPrepareDistance +
                velocity.magnitude *
                landingPredictionTime;

            Gizmos.DrawLine(
                Body.position,
                Body.position +
                direction * distance);

            if (_hasLandingHit)
            {
                Gizmos.DrawWireSphere(
                    _landingHitPoint,
                    0.08f);
            }
        }
    }
}
