using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using TMPro;

public class Bind : MonoBehaviour, IPointerClickHandler
{
    public InputAction value;
    public int bindingIndex;
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
        if (value == null)
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

        if (value.bindings.Count == 0)
        {
            value.AddBinding();
        }

        rebindOperation = value.PerformInteractiveRebinding(ResolveBindingIndex())
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
        if (value == null || string.IsNullOrEmpty(pendingBindingPath))
            return;

        if (value.bindings.Count == 0)
        {
            value.AddBinding();
        }

        value.ApplyBindingOverride(ResolveBindingIndex(), pendingBindingPath);
    }

    public void ApplySeedBinding(string bindingPath)
    {
        if (value == null || string.IsNullOrEmpty(bindingPath))
            return;

        if (rebindOperation != null)
        {
            rebindOperation.Cancel();
            rebindOperation.Dispose();
            rebindOperation = null;
        }

        if (value.bindings.Count == 0)
        {
            value.AddBinding();
        }

        pendingBindingPath = bindingPath;
        pendingBindingDisplay = bindingPath;
        value.ApplyBindingOverride(ResolveBindingIndex(), bindingPath);
        SetBindingText(GetCurrentBindingDisplay());
    }

    private int ResolveBindingIndex()
    {
        if (value == null || bindingIndex < 0 || bindingIndex >= value.bindings.Count)
            return 0;

        return bindingIndex;
    }

    private void SetBindingText(string text)
    {
        if (tmpText != null)
            tmpText.text = text;
    }

    private string GetCurrentBindingDisplay()
    {
        if (value == null)
            return string.Empty;

        var action = value;
        if (action.bindings.Count == 0)
            return string.Empty;

        return action.bindings[ResolveBindingIndex()].ToDisplayString();
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
