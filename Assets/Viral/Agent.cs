using UnityEngine;

public class Agent : MonoBehaviour
{
    public Rigidbody rb;
    public Transform target;
    public float speed;
    int myId, victimId;
    public float control;

    void Start()
    {
        myId     = PathManager.Id(this);
        victimId = PathManager.Id(target);
    }

    void FixedUpdate()
    {
        Vector3 dir = PathManager.I.GetDirection(transform.position, target.position, myId, victimId);
        Vector3 targetVelocity = dir * speed;
        rb.AddForce((targetVelocity-rb.linearVelocity)*control, ForceMode.VelocityChange);
    }
}
