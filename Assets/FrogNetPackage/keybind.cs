using UnityEngine;
using System;
using System.Collections.Generic;
using UnityEngine.Events;
using UnityEngine.InputSystem;
public class keybind : MonoBehaviour
{
    [Serializable]
    public class KeyEvent
    {
        public string actionName;
        public UnityEvent On;
        public UnityEvent Off;
        public bool enableMouse = false;
        public bool toggle = false;
    }
    public static bool disableMovement = false;

    public List<KeyEvent> events = new List<KeyEvent>();

    void Update()
    {
        foreach(KeyEvent keyEvent in events)
        {
            if (InputSystem.actions[keyEvent.actionName].triggered)
            {
                keyEvent.toggle = !keyEvent.toggle;
                if (keyEvent.enableMouse) {
                    PlayerCamera.cursorLocked = !keyEvent.toggle;
                    disableMovement = keyEvent.toggle;
                }
                if(keyEvent.toggle) {
                    keyEvent.On.Invoke();
                } else {
                    keyEvent.Off.Invoke();
                }
            }
        }
    }
}
