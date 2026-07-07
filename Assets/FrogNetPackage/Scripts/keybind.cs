using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
public class keybind : MonoBehaviour
{
    public string actionName;
    public GameObject enableObject;
    public bool enableMouse = false;

    void Update()
    {
        if (InputSystem.actions[actionName].triggered)
        {
            if (enableObject != null)
            {
                enableObject.SetActive(!enableObject.activeSelf);
                if (enableMouse) PlayerCamera.cursorLocked = !enableObject.activeSelf;
            }
        }
    }
}
