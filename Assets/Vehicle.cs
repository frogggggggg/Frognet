using UnityEngine;
using UnityEngine.InputSystem;

public class Vehicle : MonoBehaviour
{
    public Rigidbody rb;
    public float maxSpeed = 10f;
    public float acceleration = 5f;
    public float deceleration = 5f;


    void FixedUpdate()
    {

        Vector2 movement = InputSystem.actions["movement"].ReadValue<Vector2>();

        UpdateVelocity(rb, new Vector3(0f, 0f, movement.y), Time.fixedDeltaTime);
    }

    private void UpdateVelocity(
        Rigidbody body,
        Vector3 direction,
        float delta)
    {
        Vector3 currentPlanarVelocity = new Vector3(
            body.linearVelocity.x,
            0f,
            body.linearVelocity.z);

        Vector3 targetPlanarVelocity =
            direction * maxSpeed;

        float changeRate =
            direction.sqrMagnitude > 0.0001f
                ? acceleration
                : deceleration;

        Vector3 nextPlanarVelocity;

        if (changeRate <= 0f)
        {
            nextPlanarVelocity = targetPlanarVelocity;
        }
        else
        {
            nextPlanarVelocity = Vector3.MoveTowards(
                currentPlanarVelocity,
                targetPlanarVelocity,
                changeRate * delta);
        }

        Vector3 velocityChange =
            nextPlanarVelocity - currentPlanarVelocity;

        body.AddForce(
            velocityChange,
            ForceMode.VelocityChange);
    }
}
