using UnityEngine;
using UnityEngine.InputSystem;
using PurrNet.Prediction;

public class PlayerCamera : MonoBehaviour
{
    public static PlayerCamera Instance { get; private set; }
    public Transform Player;
    public Transform TempFollowTarget;
    public Vector3 offset;
    public Vector2 sensitivity = new Vector2(100f, 100f);
    public Vector2 UpDownClamp = new Vector2(-30, 60);
    public float movementSmoothing = 0;
    public float rotationSmoothing = 0;
    
    public float xRotation = 0f;
    public float yRotation = 0f;

    public static bool cursorLocked = true;

    void Awake()
    {
        Instance = this;
    }

    private void InitializePlayer()
    {
        if (!PlayerManager.TryGetLocal(out var player))
            return;

        var predicted = player.GetComponent<PredictedTransform>();
        Player = predicted && predicted.graphics ? predicted.graphics : player.transform;
    }

    void LateUpdate()
    {
        SetCursorLocked(cursorLocked);
        if(!cursorLocked) return;

        if (Player == null)
        {
            InitializePlayer();
            return;
        }

        Transform target = TempFollowTarget ? TempFollowTarget : Player;

        // Get mouse input from the new Input System
        Vector2 mouseDelta = Vector2.zero;
        if (Mouse.current != null)
        {
            mouseDelta = Mouse.current.delta.ReadValue();
        }

        float mouseX = mouseDelta.x * sensitivity.x;
        float mouseY = mouseDelta.y * sensitivity.y;

        // Update vertical rotation (X axis)
        xRotation -= mouseY;
        xRotation = Mathf.Clamp(xRotation, UpDownClamp.x, UpDownClamp.y);
        yRotation += mouseX;
        yRotation = Mathf.Repeat(yRotation, 360f); // Keep yRotation within 0-360 degrees

        // Apply rotations with angle wrap handling
        float smoothPitch = rotationSmoothing > 0
            ? Mathf.LerpAngle(transform.localEulerAngles.x, xRotation, rotationSmoothing * Time.deltaTime)
            : xRotation;
        float smoothYaw = rotationSmoothing > 0
            ? Mathf.LerpAngle(transform.localEulerAngles.y, yRotation, rotationSmoothing * Time.deltaTime)
            : yRotation;

        transform.localRotation = Quaternion.Euler(smoothPitch, smoothYaw, 0f);

        // Update position
        transform.position = movementSmoothing > 0
            ? Vector3.Lerp(transform.position, target.position, movementSmoothing * Time.deltaTime)
            : target.position;
        transform.GetChild(0).localPosition = offset;
    }

    private void SetCursorLocked(bool locked)
    {
        cursorLocked = locked;
        Cursor.visible = !locked;
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
    }
}
