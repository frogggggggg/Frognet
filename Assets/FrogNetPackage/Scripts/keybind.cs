using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
public class keybind : MonoBehaviour
{
    public string actionName;
    public GameObject enableObject;
    public bool enableMouse = false;
    public static bool disableMovement = false;

    void Update()
    {
        if (InputSystem.actions[actionName].triggered)
        {
            if (enableObject != null)
            {
                DoAction();
            }
        }
    }

    public void DoAction()
    {
        enableObject.SetActive(!enableObject.activeSelf);
        if (enableMouse) {
            PlayerCamera.cursorLocked = !enableObject.activeSelf;
            disableMovement = enableObject.activeSelf;
        }
    }
}
