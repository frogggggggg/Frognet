using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using TMPro;

public class Bind : MonoBehaviour, IPointerClickHandler
{
    public InputAction Value;
    public TMP_Text tmpText;
    public string pendingBindingPath;
    public string pendingBindingDisplay;

    private InputActionRebindingExtensions.RebindingOperation rebindOperation;

    private void Awake()
    {
        if (tmpText == null)
            tmpText = GetComponentInChildren<TMP_Text>(true);
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        StartRebinding();
    }

    public void StartRebinding()
    {
        if (Value == null)
        {
            Debug.LogWarning("Bind: InputAction is not assigned.");
            return;
        }

        if (rebindOperation != null)
        {
            rebindOperation.Cancel();
            rebindOperation.Dispose();
            rebindOperation = null;
        }

        SetBindingText("-");

        if (Value.bindings.Count == 0)
        {
            Value.AddBinding();
        }

        rebindOperation = Value.PerformInteractiveRebinding()
            .WithControlsExcluding("<Pointer>/position")
            .WithControlsExcluding("<Pointer>/delta")
            .OnMatchWaitForAnother(0.1f)
            .OnComplete(OnRebindComplete)
            .OnCancel(OnRebindCanceled);

        rebindOperation.Start();
    }

    private void OnRebindComplete(InputActionRebindingExtensions.RebindingOperation operation)
    {
        if (operation.selectedControl != null)
        {
            pendingBindingPath = operation.selectedControl.path;
            pendingBindingDisplay = operation.selectedControl.displayName;
            SetBindingText(pendingBindingDisplay);
        }
        else
        {
            pendingBindingPath = null;
            pendingBindingDisplay = GetCurrentBindingDisplay();
            SetBindingText(pendingBindingDisplay);
        }

        CleanUp(operation);
    }

    private void OnRebindCanceled(InputActionRebindingExtensions.RebindingOperation operation)
    {
        pendingBindingPath = null;
        pendingBindingDisplay = GetCurrentBindingDisplay();
        SetBindingText(pendingBindingDisplay);
        CleanUp(operation);
    }

    private void CleanUp(InputActionRebindingExtensions.RebindingOperation operation)
    {
        operation.Dispose();
        rebindOperation = null;
    }

    public void CommitPendingBinding()
    {
        if (Value == null || string.IsNullOrEmpty(pendingBindingPath))
            return;

        if (Value.bindings.Count == 0)
        {
            Value.AddBinding();
        }

        Value.ApplyBindingOverride(0, pendingBindingPath);
    }

    private void SetBindingText(string text)
    {
        if (tmpText != null)
            tmpText.text = text;
    }

    private string GetCurrentBindingDisplay()
    {
        if (Value == null)
            return string.Empty;

        var action = Value;
        if (action.bindings.Count == 0)
            return string.Empty;

        return action.bindings[0].ToDisplayString();
    }

    private void OnDisable()
    {
        if (rebindOperation != null)
        {
            rebindOperation.Cancel();
            rebindOperation.Dispose();
            rebindOperation = null;
        }
    }
}
