using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;
using UnityEngine.UI;
using TMPro;

public class Bind : MonoBehaviour
{
    private const string UserBindingTag = "FrognetUserBind";
    private const string ScrollUpPath = "<Mouse>/scroll/up";
    private const string ScrollDownPath = "<Mouse>/scroll/down";

    [Serializable]
    public class BindEntry
    {
        public bool authored;
        public int bindingIndex;
        public string bindingName;
        public string partName;
        [NonSerialized] public GameObject instance;
    }

    public InputActionAsset actionAsset;
    public string actionMapName;
    public string actionName;
    public List<BindEntry> value = new List<BindEntry>();
    public Transform bindHolder;
    public Button addButton;
    public GameObject bindPrefab;
    public string pendingLabel = "-";

    private static readonly HashSet<InputAction> preparedActions = new HashSet<InputAction>();
    private static int userBindingCounter;

    private GameObject pendingInstance;
    private IDisposable pressListener;
    private InputAction resolvedAction;

    /// <summary>Raised after a binding is added or removed, so a saver can react without polling.</summary>
    public event Action Changed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetSessionState()
    {
        preparedActions.Clear();
        userBindingCounter = 0;
    }

    private void Awake()
    {
        EnsureInitialized();
        PrepareAction(GetAction());

        if (addButton != null)
        {
            addButton.onClick.RemoveListener(StartAddBinding);
            addButton.onClick.AddListener(StartAddBinding);
        }
    }

    private void Start()
    {
        Rebuild();
    }

    /// <summary>
    /// The wheel is an axis, so it never reaches onAnyButtonPress. It is polled by hand for as long
    /// as a row is waiting for input.
    /// </summary>
    private void Update()
    {
        if (pressListener == null || Mouse.current == null)
            return;

        float scroll = Mouse.current.scroll.ReadValue().y;

        if (Mathf.Abs(scroll) < 0.01f)
            return;

        pressListener.Dispose();
        pressListener = null;

        CompleteAdd(GetAction(), scroll > 0f ? ScrollUpPath : ScrollDownPath);
    }

    private void OnDisable()
    {
        StopListening();
    }

    public InputAction GetAction()
    {
        if (resolvedAction != null)
            return resolvedAction;

        if (actionAsset == null || string.IsNullOrEmpty(actionName))
            return null;

        var actionMap = string.IsNullOrEmpty(actionMapName) ? null : actionAsset.FindActionMap(actionMapName, false);
        resolvedAction = actionMap != null
            ? actionMap.FindAction(actionName, false)
            : actionAsset.FindAction(actionName, false);

        return resolvedAction;
    }

    public void StartAddBinding()
    {
        if (pressListener != null)
            return;

        InputAction action = GetAction();
        if (action == null)
        {
            Debug.LogWarning($"Bind: could not resolve action '{actionName}'.");
            return;
        }

        pendingInstance = SpawnRow(pendingLabel, CancelAddBinding);

        pressListener = InputSystem.onAnyButtonPress.CallOnce(control =>
        {
            pressListener = null;
            CompleteAdd(action, ToBindingPath(control));
        });
    }

    /// <summary>Hands the waiting row over to the binding that was just captured.</summary>
    private void CompleteAdd(InputAction action, string path)
    {
        BindEntry entry = action != null ? AddUserBinding(action, path) : null;

        if (entry != null && pendingInstance != null)
        {
            entry.instance = pendingInstance;
            pendingInstance = null;
            WireRow(entry.instance, () => RemoveEntry(entry));
        }
        else
        {
            ClearPending();
        }

        Rebuild();
        Changed?.Invoke();
    }

    public void CancelAddBinding()
    {
        StopListening();
    }

    public void RemoveEntry(BindEntry entry)
    {
        InputAction action = GetAction();
        if (entry == null || action == null)
            return;

        bool authored = IsAuthored(entry);
        int index = authored ? ResolveIndex(action, entry) : ResolveTagIndex(action, entry);

        if (index >= 0)
        {
            bool wasEnabled = DisableForEdit(action);

            if (authored)
                action.ApplyBindingOverride(index, string.Empty);
            else
                action.ChangeBinding(index).Erase();

            RestoreAfterEdit(action, wasEnabled);
        }

        DestroyRow(entry);

        if (!authored)
            value.Remove(entry);

        Rebuild();
        Changed?.Invoke();
    }

    /// <summary>
    /// Drops every binding the player added and every override on the authored ones, leaving the
    /// action exactly as the input asset declares it.
    /// </summary>
    public void ResetToDefault()
    {
        InputAction action = GetAction();

        if (action == null)
            return;

        bool wasEnabled = DisableForEdit(action);

        for (int i = value.Count - 1; i >= 0; i--)
        {
            BindEntry entry = value[i];

            if (IsAuthored(entry))
            {
                int authoredIndex = ResolveIndex(action, entry);

                if (authoredIndex >= 0)
                    action.RemoveBindingOverride(authoredIndex);

                continue;
            }

            int tagIndex = ResolveTagIndex(action, entry);

            if (tagIndex >= 0)
                action.ChangeBinding(tagIndex).Erase();

            DestroyRow(entry);
            value.RemoveAt(i);
        }

        RestoreAfterEdit(action, wasEnabled);

        Rebuild();
        Changed?.Invoke();
    }

    public void SeedBinding(InputActionAsset asset, string mapName, string action, int bindingIndex)
    {
        actionAsset = asset;
        actionMapName = mapName;
        actionName = action;
        resolvedAction = null;

        ClearRows();
        value.Clear();
        value.Add(new BindEntry { authored = true, bindingIndex = bindingIndex });
        Rebuild();
    }

    public string GetActionName()
    {
        return actionName;
    }

    public List<string> GetBindingPaths()
    {
        var paths = new List<string>();
        InputAction action = GetAction();
        if (action == null)
            return paths;

        foreach (var entry in value)
        {
            int index = ResolveIndex(action, entry);
            paths.Add(index >= 0 ? action.bindings[index].effectivePath : string.Empty);
        }

        return paths;
    }

    public void RestoreBindings(List<string> paths)
    {
        if (paths == null)
            return;

        InputAction action = GetAction();
        if (action == null)
            return;

        PrepareAction(action);

        bool wasEnabled = DisableForEdit(action);

        for (int i = value.Count - 1; i >= 0; i--)
        {
            BindEntry entry = value[i];
            if (IsAuthored(entry))
                continue;

            int userIndex = ResolveTagIndex(action, entry);
            if (userIndex >= 0)
                action.ChangeBinding(userIndex).Erase();

            DestroyRow(entry);
            value.RemoveAt(i);
        }

        int nextPath = 0;

        foreach (var entry in value)
        {
            int authoredIndex = ResolveIndex(action, entry);
            if (authoredIndex >= 0)
                action.ApplyBindingOverride(authoredIndex, nextPath < paths.Count ? paths[nextPath] : string.Empty);

            nextPath++;
        }

        RestoreAfterEdit(action, wasEnabled);

        for (int i = nextPath; i < paths.Count; i++)
        {
            if (!string.IsNullOrEmpty(paths[i]))
                AddUserBinding(action, paths[i]);
        }

        Rebuild();
    }

    public void Rebuild()
    {
        EnsureInitialized();

        InputAction action = GetAction();
        if (!Application.isPlaying || bindHolder == null || bindPrefab == null || action == null)
            return;

        foreach (var entry in value)
        {
            int index = ResolveIndex(action, entry);

            if (index < 0 || string.IsNullOrEmpty(action.bindings[index].effectivePath))
            {
                DestroyRow(entry);
                continue;
            }

            string text = Label(action.bindings[index]);

            if (entry.instance == null)
            {
                BindEntry captured = entry;
                entry.instance = SpawnRow(text, () => RemoveEntry(captured));
            }
            else
            {
                SetLabel(entry.instance, text);
            }
        }

        if (addButton != null)
            addButton.transform.SetAsLastSibling();
    }

    private GameObject SpawnRow(string text, UnityEngine.Events.UnityAction onClick)
    {
        if (!Application.isPlaying || bindHolder == null || bindPrefab == null)
            return null;

        GameObject instance = Instantiate(bindPrefab, bindHolder);

        SetLabel(instance, text);
        WireRow(instance, onClick);

        if (addButton != null)
            addButton.transform.SetAsLastSibling();

        return instance;
    }

    private static void WireRow(GameObject instance, UnityEngine.Events.UnityAction onClick)
    {
        if (instance == null || onClick == null)
            return;

        var button = instance.GetComponentInChildren<Button>(true);
        if (button == null)
            return;

        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(onClick);
    }

    /// <summary>
    /// Adds one more binding for this action. Composite actions get a full clone of the
    /// composite with the target part swapped, because a lone control cannot feed a Vector2.
    /// </summary>
    private BindEntry AddUserBinding(InputAction action, string path)
    {
        int template = FindCompositePart(action);
        if (template >= 0)
            return CloneComposite(action, template, path);

        if (!AcceptsStandalone(action))
        {
            Debug.LogWarning($"Bind: action '{actionName}' expects '{action.expectedControlType}' and has no composite to clone.", this);
            return null;
        }

        return AppendBinding(action, path);
    }

    private BindEntry AppendBinding(InputAction action, string path)
    {
        string bindingName = $"{UserBindingTag}:{userBindingCounter++}";

        bool wasEnabled = DisableForEdit(action);
        action.AddBinding(path).WithName(bindingName);
        RestoreAfterEdit(action, wasEnabled);

        var entry = new BindEntry { authored = false, bindingName = bindingName };
        value.Add(entry);
        return entry;
    }

    /// <summary>
    /// Duplicates the composite that owns <paramref name="templatePartIndex"/>, binding only the
    /// matching part to <paramref name="path"/> and leaving the other parts empty. Unbound parts
    /// contribute nothing, so the clone adds an alternate for this row alone and cannot leak into
    /// rows that share the original composite.
    /// </summary>
    private BindEntry CloneComposite(InputAction action, int templatePartIndex, string path)
    {
        int header = FindCompositeHeader(action, templatePartIndex);
        if (header < 0)
            return null;

        string compositePath = action.bindings[header].path;
        string targetPart = action.bindings[templatePartIndex].name;

        var partNames = new List<string>();
        for (int i = header + 1; i < action.bindings.Count && action.bindings[i].isPartOfComposite; i++)
            partNames.Add(action.bindings[i].name);

        string bindingName = $"{UserBindingTag}:{userBindingCounter++}";

        bool wasEnabled = DisableForEdit(action);

        var composite = action.AddCompositeBinding(compositePath);
        foreach (string part in partNames)
            composite.With(part, part == targetPart ? path : string.Empty);

        action.ChangeBinding(composite.bindingIndex).WithName(bindingName);

        RestoreAfterEdit(action, wasEnabled);

        var entry = new BindEntry { authored = false, bindingName = bindingName, partName = targetPart };
        value.Add(entry);
        return entry;
    }

    /// <summary>
    /// The binding index of any existing entry that sits inside a composite, used as the clone template.
    /// </summary>
    private int FindCompositePart(InputAction action)
    {
        foreach (var entry in value)
        {
            int index = ResolveIndex(action, entry);
            if (index >= 0 && action.bindings[index].isPartOfComposite)
                return index;
        }

        return -1;
    }

    private static int FindCompositeHeader(InputAction action, int partIndex)
    {
        for (int i = partIndex; i >= 0; i--)
        {
            if (action.bindings[i].isComposite)
                return i;

            if (!action.bindings[i].isPartOfComposite)
                return -1;
        }

        return -1;
    }

    private static bool AcceptsStandalone(InputAction action)
    {
        string expected = action.expectedControlType;
        return string.IsNullOrEmpty(expected) || expected == "Button" || expected == "Axis";
    }

    private static string ToBindingPath(InputControl control)
    {
        string devicePath = control.device.path;
        string controlPath = control.path;

        if (controlPath.Length > devicePath.Length + 1 && controlPath.StartsWith(devicePath))
            return $"<{control.device.layout}>/{controlPath.Substring(devicePath.Length + 1)}";

        return controlPath;
    }

    /// <summary>The wheel axes display as bare "Up"/"Down", which reads as nothing on a key row.</summary>
    private static string Label(InputBinding binding)
    {
        if (binding.effectivePath == ScrollUpPath)
            return "Scroll Up";

        if (binding.effectivePath == ScrollDownPath)
            return "Scroll Down";

        return binding.ToDisplayString();
    }

    private static void PrepareAction(InputAction action)
    {
        if (action == null || !preparedActions.Add(action))
            return;

        bool wasEnabled = DisableForEdit(action);

        for (int i = action.bindings.Count - 1; i >= 0; i--)
        {
            string name = action.bindings[i].name;
            if (!string.IsNullOrEmpty(name) && name.StartsWith(UserBindingTag, StringComparison.Ordinal))
                action.ChangeBinding(i).Erase();
        }

        action.RemoveAllBindingOverrides();
        RestoreAfterEdit(action, wasEnabled);
    }

    private static bool IsAuthored(BindEntry entry)
    {
        return entry != null && (entry.authored || string.IsNullOrEmpty(entry.bindingName));
    }

    /// <summary>
    /// The index of the tagged binding itself. For a cloned composite this is the composite header,
    /// which is what has to be erased to take the whole clone away.
    /// </summary>
    private static int ResolveTagIndex(InputAction action, BindEntry entry)
    {
        if (action == null || entry == null || string.IsNullOrEmpty(entry.bindingName))
            return -1;

        for (int i = 0; i < action.bindings.Count; i++)
        {
            if (action.bindings[i].name == entry.bindingName)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// The index of the binding this entry displays and overrides. For a cloned composite that is
    /// the part the row represents, not the header.
    /// </summary>
    private static int ResolveIndex(InputAction action, BindEntry entry)
    {
        if (action == null || entry == null)
            return -1;

        if (IsAuthored(entry))
            return entry.bindingIndex >= 0 && entry.bindingIndex < action.bindings.Count ? entry.bindingIndex : -1;

        int tagIndex = ResolveTagIndex(action, entry);
        if (tagIndex < 0 || string.IsNullOrEmpty(entry.partName))
            return tagIndex;

        for (int i = tagIndex + 1; i < action.bindings.Count && action.bindings[i].isPartOfComposite; i++)
        {
            if (action.bindings[i].name == entry.partName)
                return i;
        }

        return -1;
    }

    private static bool DisableForEdit(InputAction action)
    {
        if (action.actionMap != null)
        {
            bool mapWasEnabled = action.actionMap.enabled;
            action.actionMap.Disable();
            return mapWasEnabled;
        }

        bool wasEnabled = action.enabled;
        action.Disable();
        return wasEnabled;
    }

    private static void RestoreAfterEdit(InputAction action, bool wasEnabled)
    {
        if (!wasEnabled)
            return;

        if (action.actionMap != null)
            action.actionMap.Enable();
        else
            action.Enable();
    }

    private void EnsureInitialized()
    {
        if (bindHolder == null && transform.childCount > 0)
            bindHolder = transform.GetChild(0);

        if (addButton == null && bindHolder != null)
            addButton = bindHolder.GetComponentInChildren<Button>(true);
    }

    private static void SetLabel(GameObject instance, string text)
    {
        if (instance == null)
            return;

        var label = instance.GetComponentInChildren<TMP_Text>(true);
        if (label != null)
            label.text = text;
    }

    private void DestroyRow(BindEntry entry)
    {
        if (entry == null || entry.instance == null)
            return;

        DestroyInstance(entry.instance);
        entry.instance = null;
    }

    private void ClearRows()
    {
        foreach (var entry in value)
            DestroyRow(entry);
    }

    private static void DestroyInstance(GameObject instance)
    {
        if (instance == null)
            return;

        if (Application.isPlaying)
            Destroy(instance);
        else
            DestroyImmediate(instance);
    }

    private void ClearPending()
    {
        if (pendingInstance == null)
            return;

        DestroyInstance(pendingInstance);
        pendingInstance = null;
    }

    private void StopListening()
    {
        ClearPending();

        if (pressListener == null)
            return;

        pressListener.Dispose();
        pressListener = null;
    }
}
