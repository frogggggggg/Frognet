using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Inspector for <see cref="UniversalCamera"/>.
///
/// Unity serialises [SerializeReference] lists correctly but does not reliably
/// offer a way to choose the concrete type, so entries appear as uneditable
/// null elements. This draws the lists itself, grouped by mode, with an Add
/// menu built from every non-abstract CameraBehaviour.
/// </summary>
[CustomEditor(typeof(UniversalCamera))]
public class UniversalCameraEditor : Editor
{
    static List<Type> _behaviourTypes;
    static readonly Dictionary<string, bool> AdvancedOpen = new Dictionary<string, bool>();

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        DrawPropertiesExcluding(serializedObject, "m_Script", "modes", "activeMode");

        var rig = (UniversalCamera)target;
        SerializedProperty modes = serializedObject.FindProperty("modes");

        EditorGUILayout.Space();
        DrawModeSelector(rig);

        int removeMode = -1;

        for (int m = 0; m < modes.arraySize; m++)
        {
            if (DrawMode(rig, modes, m)) removeMode = m;
        }

        EditorGUILayout.Space();

        if (GUILayout.Button("Add Mode", GUILayout.Height(22)))
        {
            modes.InsertArrayElementAtIndex(modes.arraySize);

            SerializedProperty added = modes.GetArrayElementAtIndex(modes.arraySize - 1);
            added.FindPropertyRelative("name").stringValue = "Mode " + modes.arraySize;
            added.FindPropertyRelative("behaviours").ClearArray();
            added.isExpanded = true;
        }

        DrawWarnings(rig);

        // Deferred for the same reason the behaviour list defers: resizing an
        // array mid-draw desynchronises the Layout and Repaint passes.
        if (removeMode >= 0)
        {
            modes.DeleteArrayElementAtIndex(removeMode);

            SerializedProperty active = serializedObject.FindProperty("activeMode");
            if (active.intValue >= modes.arraySize)
                active.intValue = Mathf.Max(0, modes.arraySize - 1);
        }

        serializedObject.ApplyModifiedProperties();
    }

    /// <summary>Row of mode names; clicking one makes it active.</summary>
    void DrawModeSelector(UniversalCamera rig)
    {
        if (rig.modes == null || rig.modes.Count == 0)
        {
            EditorGUILayout.HelpBox(
                "No modes yet. Add one below, then switch between them at " +
                "runtime with SetMode.", MessageType.Info);
            return;
        }

        var names = new string[rig.modes.Count];
        for (int i = 0; i < names.Length; i++)
        {
            string n = rig.modes[i] != null ? rig.modes[i].name : null;
            names[i] = string.IsNullOrEmpty(n) ? "Mode " + (i + 1) : n;
        }

        EditorGUILayout.LabelField("Active Mode", EditorStyles.boldLabel);

        SerializedProperty active = serializedObject.FindProperty("activeMode");
        int current = Mathf.Clamp(active.intValue, 0, names.Length - 1);
        int picked = GUILayout.Toolbar(current, names);

        if (picked != current)
        {
            // Route through SetMode while playing so the incoming behaviours
            // get re-seeded from the current pose, as a script switch would.
            if (Application.isPlaying) rig.SetMode(picked);
            else active.intValue = picked;
        }

        EditorGUILayout.LabelField(
            "Switch from script with SetMode(name) or SetMode(index).",
            EditorStyles.wordWrappedMiniLabel);
    }

    /// <summary>Returns true if this mode was marked for removal.</summary>
    bool DrawMode(UniversalCamera rig, SerializedProperty modes, int index)
    {
        SerializedProperty mode = modes.GetArrayElementAtIndex(index);
        SerializedProperty name = mode.FindPropertyRelative("name");
        SerializedProperty behaviours = mode.FindPropertyRelative("behaviours");

        bool isActive = index == rig.activeMode;
        bool remove = false;

        EditorGUILayout.Space();
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();

        string label = string.IsNullOrEmpty(name.stringValue)
            ? "Mode " + (index + 1)
            : name.stringValue;

        mode.isExpanded = EditorGUILayout.Foldout(
            mode.isExpanded, isActive ? label + "  (active)" : label, true);

        if (GUILayout.Button("\u2715", EditorStyles.miniButton, GUILayout.Width(22)))
            remove = true;

        EditorGUILayout.EndHorizontal();

        if (mode.isExpanded)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(name);
            EditorGUI.indentLevel--;

            EditorGUILayout.LabelField(
                "Applied top to bottom. Rotation usually belongs above the " +
                "position behaviours that depend on facing.",
                EditorStyles.wordWrappedMiniLabel);

            DrawBehaviourList(behaviours);
        }

        EditorGUILayout.EndVertical();
        return remove;
    }

    void DrawBehaviourList(SerializedProperty behaviours)
    {
        int removeAt = -1;
        int moveFrom = -1;
        int moveTo = -1;

        if (behaviours.arraySize == 0)
        {
            EditorGUILayout.HelpBox("No behaviours yet. Use Add Behaviour below.",
                                    MessageType.Info);
        }
        else
        {
            for (int i = 0; i < behaviours.arraySize; i++)
                DrawElement(behaviours, i, ref removeAt, ref moveFrom, ref moveTo);
        }

        DrawAddMenu(behaviours);

        // Same deferral as the mode list above.
        if (moveFrom >= 0)
            behaviours.MoveArrayElement(moveFrom, moveTo);
        else if (removeAt >= 0)
            RemoveAt(behaviours, removeAt);
    }

    void DrawElement(SerializedProperty behaviours, int index,
                     ref int removeAt, ref int moveFrom, ref int moveTo)
    {
        SerializedProperty element = behaviours.GetArrayElementAtIndex(index);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();

        SerializedProperty enabled = element.FindPropertyRelative("enabled");
        if (enabled != null)
            enabled.boolValue = EditorGUILayout.Toggle(enabled.boolValue, GUILayout.Width(16));

        element.isExpanded = EditorGUILayout.Foldout(
            element.isExpanded, DisplayName(element), true);

        using (new EditorGUI.DisabledScope(index == 0))
        {
            if (GUILayout.Button("\u25B2", EditorStyles.miniButtonLeft, GUILayout.Width(22)))
            {
                moveFrom = index;
                moveTo = index - 1;
            }
        }

        using (new EditorGUI.DisabledScope(index == behaviours.arraySize - 1))
        {
            if (GUILayout.Button("\u25BC", EditorStyles.miniButtonMid, GUILayout.Width(22)))
            {
                moveFrom = index;
                moveTo = index + 1;
            }
        }

        if (GUILayout.Button("\u2715", EditorStyles.miniButtonRight, GUILayout.Width(22)))
            removeAt = index;

        EditorGUILayout.EndHorizontal();

        // Always drawn, regardless of a pending mutation, so the control count
        // stays identical across both IMGUI passes.
        if (element.isExpanded)
        {
            EditorGUI.indentLevel++;
            DrawChildren(element);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.EndVertical();
    }

    /// <summary>
    /// Draw a managed reference's fields. Anything marked [Advanced] is held
    /// back into a collapsed foldout so the common settings stay short.
    /// </summary>
    static void DrawChildren(SerializedProperty property)
    {
        Type type = property.managedReferenceValue != null
            ? property.managedReferenceValue.GetType()
            : null;

        var advanced = new List<SerializedProperty>();

        SerializedProperty end = property.GetEndProperty();
        SerializedProperty iterator = property.Copy();
        bool enterChildren = true;

        while (iterator.NextVisible(enterChildren) &&
               !SerializedProperty.EqualContents(iterator, end))
        {
            enterChildren = false;   // PropertyField already drew any children

            // "enabled" is the toggle in the header, so do not draw it twice.
            if (iterator.name == "enabled") continue;

            if (IsAdvanced(type, iterator.name))
                advanced.Add(iterator.Copy());
            else
                EditorGUILayout.PropertyField(iterator, true);
        }

        if (advanced.Count == 0) return;

        string key = property.propertyPath;
        AdvancedOpen.TryGetValue(key, out bool open);
        open = EditorGUILayout.Foldout(open, "Advanced (" + advanced.Count + ")", true);
        AdvancedOpen[key] = open;

        if (!open) return;

        EditorGUI.indentLevel++;
        foreach (SerializedProperty field in advanced)
            EditorGUILayout.PropertyField(field, true);
        EditorGUI.indentLevel--;
    }

    static bool IsAdvanced(Type type, string fieldName)
    {
        FieldInfo field = FindField(type, fieldName);
        return field != null &&
               field.GetCustomAttribute<UniversalCamera.AdvancedAttribute>() != null;
    }

    /// <summary>
    /// GetField cannot see private base-class fields in one call, and the
    /// shared ones live on CameraBehaviour, so walk the chain.
    /// </summary>
    static FieldInfo FindField(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.DeclaredOnly;

        while (type != null)
        {
            FieldInfo field = type.GetField(name, flags);
            if (field != null) return field;
            type = type.BaseType;
        }
        return null;
    }

    static void RemoveAt(SerializedProperty behaviours, int index)
    {
        int size = behaviours.arraySize;
        behaviours.DeleteArrayElementAtIndex(index);

        // On some Unity versions the first delete only nulls the managed
        // reference rather than shortening the array.
        if (behaviours.arraySize == size)
            behaviours.DeleteArrayElementAtIndex(index);
    }

    void DrawAddMenu(SerializedProperty behaviours)
    {
        if (!GUILayout.Button("Add Behaviour", GUILayout.Height(20)))
            return;

        SerializedProperty captured = behaviours.Copy();
        var menu = new GenericMenu();

        foreach (Type type in BehaviourTypes())
        {
            Type capturedType = type;
            menu.AddItem(new GUIContent(ObjectNames.NicifyVariableName(capturedType.Name)),
                         false, () => Append(captured, capturedType));
        }

        if (menu.GetItemCount() == 0)
            menu.AddDisabledItem(new GUIContent("No CameraBehaviour types found"));

        menu.ShowAsContext();
    }

    void Append(SerializedProperty behaviours, Type type)
    {
        serializedObject.Update();

        int index = behaviours.arraySize;
        behaviours.InsertArrayElementAtIndex(index);

        SerializedProperty element = behaviours.GetArrayElementAtIndex(index);
        element.managedReferenceValue = Activator.CreateInstance(type);
        element.isExpanded = true;

        serializedObject.ApplyModifiedProperties();
    }

    static IEnumerable<Type> BehaviourTypes()
    {
        // TypeCache is prebuilt by the editor, so this costs nothing to query.
        if (_behaviourTypes == null)
        {
            _behaviourTypes = TypeCache
                .GetTypesDerivedFrom<UniversalCamera.CameraBehaviour>()
                .Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition)
                .OrderBy(t => t.Name)
                .ToList();
        }

        return _behaviourTypes;
    }

    static string DisplayName(SerializedProperty element)
    {
        string full = element.managedReferenceFullTypename;
        if (string.IsNullOrEmpty(full)) return "(unassigned)";

        // Format is "<assembly> <Namespace.Outer/Nested>".
        int space = full.IndexOf(' ');
        string typeName = space >= 0 ? full.Substring(space + 1) : full;

        int cut = typeName.LastIndexOfAny(new[] { '/', '+', '.' });
        if (cut >= 0) typeName = typeName.Substring(cut + 1);

        return ObjectNames.NicifyVariableName(typeName);
    }

    /// <summary>
    /// Flag combinations in the active mode that visibly fight each other.
    /// These are all legal, so they are warnings rather than errors.
    /// </summary>
    void DrawWarnings(UniversalCamera rig)
    {
        UniversalCamera.CameraMode mode = rig.ActiveMode;
        if (mode == null || mode.behaviours == null) return;

        bool freeLook = false, mouseLook = false, lookAt = false;
        bool wantsFieldOfView = false;
        int rotationWriters = 0;
        UniversalCamera.DistanceFromTarget boom = null;

        foreach (UniversalCamera.CameraBehaviour behaviour in mode.behaviours)
        {
            if (behaviour == null || !behaviour.enabled) continue;

            switch (behaviour)
            {
                case UniversalCamera.FreeLook:       freeLook = true;  rotationWriters++; break;
                case UniversalCamera.MouseLook:      mouseLook = true; rotationWriters++; break;
                case UniversalCamera.LookAtTarget:   lookAt = true;    rotationWriters++; break;
                case UniversalCamera.ConstantRotate:                   rotationWriters++; break;
                case UniversalCamera.SpeedFieldOfView:  wantsFieldOfView = true;   break;
                case UniversalCamera.DistanceFromTarget distance:      boom = distance;   break;
            }
        }

        // The rig only drives a Camera it can actually find, and a missing one
        // is otherwise completely silent -- no error, just nothing happening.
        if (wantsFieldOfView && rig.ResolveCamera() == null)
        {
            EditorGUILayout.HelpBox(
                "Speed Field Of View has nothing to drive: no Camera on this " +
                "object or under it. Assign Target Camera, or move this rig " +
                "onto the object that has the Camera.", MessageType.Warning);
        }

        bool specific = false;

        if (freeLook && mouseLook)
        {
            EditorGUILayout.HelpBox(
                "Free Look and Mouse Look both write rotation. Whichever sits " +
                "lower in the list wins and the other's input is thrown away. " +
                "Use one or the other.", MessageType.Warning);
            specific = true;
        }

        if (lookAt && boom != null &&
            boom.direction == UniversalCamera.DistanceFromTarget.Direction.SelfBackward)
        {
            EditorGUILayout.HelpBox(
                "Feedback loop: Distance From Target places the camera along its " +
                "own facing, while Look At Target derives that facing from the " +
                "camera's position. With smoothing on either, the two chase each " +
                "other and the camera oscillates. Set the distance Direction to " +
                "Target Backward, or remove Look At Target.", MessageType.Warning);
            specific = true;
        }

        if (!specific && rotationWriters > 1)
        {
            EditorGUILayout.HelpBox(
                rotationWriters + " behaviours write rotation. Unless they are " +
                "restricted to different axes, only the last one's result " +
                "survives.", MessageType.Info);
        }
    }
}
