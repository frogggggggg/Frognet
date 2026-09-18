using UnityEngine;
using UnityEngine.InputSystem;

public class Vehicle : MonoBehaviour
{
    [Header("References")]
    public Rigidbody rb;
    public Transform turn;

    [Header("Speed Tilt")]
    public float maxSpeed = 10f;
    public float maxTilt = 30f;
    public AnimationCurve tiltCurve = AnimationCurve.Linear(0, 0, 1, 1);

    [Header("Spring Feel")]
    [Tooltip("Spring stiffness for the tilt. Higher = snappier response.")]
    public float tiltStiffness = 120f;
    [Tooltip("1 = settles with no overshoot. Below 1 = bouncy/springy overshoot.")]
    [Range(0.05f, 2f)] public float tiltDamping = 0.4f;

    [Header("Acceleration Lean (rover dive/squat)")]
    [Tooltip("Extra pitch per unit of forward acceleration, on top of the speed tilt.")]
    public float accelLeanAmount = 3f;
    [Tooltip("Extra roll per unit of lateral acceleration, banking into turns.")]
    public float bankAmount = 6f;

    [Header("Suspension Bounce")]
    [Tooltip("Max vertical bob offset from sudden acceleration changes and landings.")]
    public float bounceAmount = 0.15f;
    public float bounceStiffness = 250f;
    [Range(0.05f, 2f)] public float bounceDamping = 0.3f;
    [Tooltip("How hard a landing impact compresses the suspension.")]
    public float landingImpulseScale = 0.05f;

    Vector3 turnBasePos;
    Vector3 prevLocalVel;

    float pitch, pitchVel;
    float roll, rollVel;
    float bob, bobVel;

    void Start()
    {
        if (turn != null) turnBasePos = turn.localPosition;
    }

    void Update()
    {
        if (rb == null || turn == null) return; // Safety check

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        Vector3 worldVel = rb.linearVelocity;

        // Convert to local space
        Vector3 localVel = transform.InverseTransformDirection(worldVel);
        Vector3 localAccel = (localVel - prevLocalVel) / dt;
        prevLocalVel = localVel;

        // Get horizontal velocity (ignore Y)
        Vector3 speed = new Vector3(localVel.z, 0f, localVel.x);

        // Base tilt from current speed, plus an acceleration-driven lean/bank for a lively rover feel
        float targetPitch = tiltCurve.Evaluate(Mathf.Clamp01(Mathf.Abs(speed.x) / maxSpeed)) * maxTilt * Mathf.Sign(speed.x)
            - localAccel.z * accelLeanAmount;
        float targetRoll = tiltCurve.Evaluate(Mathf.Clamp01(Mathf.Abs(speed.z) / maxSpeed)) * maxTilt * Mathf.Sign(speed.z)
            + localAccel.x * bankAmount;

        pitch = SpringDamp(pitch, targetPitch, ref pitchVel, tiltStiffness, tiltDamping, dt);
        roll = SpringDamp(roll, targetRoll, ref rollVel, tiltStiffness, tiltDamping, dt);

        // Suspension bob reacts to how hard the car is accelerating, so bumps and launches feel springy
        float targetBob = -Mathf.Clamp01(localAccel.magnitude / 50f) * bounceAmount;
        bob = SpringDamp(bob, targetBob, ref bobVel, bounceStiffness, bounceDamping, dt);

        turn.localRotation = Quaternion.Euler(pitch, 0f, roll);
        turn.localPosition = turnBasePos + Vector3.up * bob;
    }

    void OnCollisionEnter(Collision collision)
    {
        // Kick the suspension spring on impact/landing instead of just snapping to rest
        bobVel -= Mathf.Abs(collision.relativeVelocity.y) * landingImpulseScale;
    }

    // Semi-implicit spring-damper: damping < 1 overshoots and settles, giving a springy feel.
    static float SpringDamp(float current, float target, ref float velocity, float stiffness, float damping, float dt)
    {
        float omega = Mathf.Sqrt(stiffness);
        float accel = (target - current) * stiffness - velocity * (2f * omega * damping);
        velocity += accel * dt;
        return current + velocity * dt;
    }


    // public float maxSpeed = 10f;
    // public float acceleration = 5f;
    // public float deceleration = 5f;


    // void FixedUpdate()
    // {

    //     Vector2 movement = InputSystem.actions["movement"].ReadValue<Vector2>();

    //     UpdateVelocity(rb, new Vector3(0f, 0f, movement.y), Time.fixedDeltaTime);
    // }

    // private void UpdateVelocity(
    //     Rigidbody body,
    //     Vector3 direction,
    //     float delta)
    // {
    //     Vector3 currentPlanarVelocity = new Vector3(
    //         body.linearVelocity.x,
    //         0f,
    //         body.linearVelocity.z);

    //     Vector3 targetPlanarVelocity =
    //         direction * maxSpeed;

    //     float changeRate =
    //         direction.sqrMagnitude > 0.0001f
    //             ? acceleration
    //             : deceleration;

    //     Vector3 nextPlanarVelocity;

    //     if (changeRate <= 0f)
    //     {
    //         nextPlanarVelocity = targetPlanarVelocity;
    //     }
    //     else
    //     {
    //         nextPlanarVelocity = Vector3.MoveTowards(
    //             currentPlanarVelocity,
    //             targetPlanarVelocity,
    //             changeRate * delta);
    //     }

    //     Vector3 velocityChange =
    //         nextPlanarVelocity - currentPlanarVelocity;

    //     body.AddForce(
    //         velocityChange,
    //         ForceMode.VelocityChange);
    // }
}
