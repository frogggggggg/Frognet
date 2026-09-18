using UnityEngine;
using UnityEngine.U2D;


public class IK : MonoBehaviour
{
    public Transform main;
    public AnimationCurve shape;
    public class IKJoint
    {
        public Transform joint;
        public Vector3? target = null; 
        //target is based on a local offset from main, null means no target is set
        //target will determine the desired position of the joint relative to main
        public int priority = 0;
        //priority determines the priority of the target and how it affects the ik compared to other joints
    }

    public class IKLeg
    {
        public IKJoint[] joints;
        public bool isPlanted = false;
    }

    public Vector3 GetPositionBelowJoint(IKJoint joint)
    {
        if (main == null) return Vector3.zero;
        RaycastHit hit;
        if (Physics.Raycast(joint.joint.position, Vector3.down, out hit))
        {
            return hit.point;
        }
        return Vector3.zero;
    }
}
