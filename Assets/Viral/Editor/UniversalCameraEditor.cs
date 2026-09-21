using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

[CustomPropertyDrawer(typeof(UniversalCamera.AxisMask))]
public class UniversalCameraAxisMaskDrawer : PropertyDrawer
{
    public override void OnGUI(
        Rect position,
        SerializedProperty property,
        GUIContent label)
    {
        SerializedProperty x =
            property.FindPropertyRelative("x");

        SerializedProperty y =
            property.FindPropertyRelative("y");

        SerializedProperty z =
            property.FindPropertyRelative("z");

        EditorGUI.BeginProperty(
            position,
            label,
            property);

        Rect labelRect =
            new Rect(
                position.x,
                position.y,
                EditorGUIUtility.labelWidth,
                position.height);

        EditorGUI.LabelField(
            labelRect,
            label);

        float start =
            position.x +
            EditorGUIUtility.labelWidth;

        float width =
            Mathf.Max(
                42f,
                (position.xMax - start) /
                3f);

        Rect xRect =
            new Rect(
                start,
                position.y,
                width,
                position.height);

        Rect yRect =
            new Rect(
                start + width,
                position.y,
                width,
                position.height);

        Rect zRect =
            new Rect(
                start + width * 2f,
                position.y,
                width,
                position.height);

        x.boolValue =
            EditorGUI.ToggleLeft(
                xRect,
                "X",
                x.boolValue);

        y.boolValue =
            EditorGUI.ToggleLeft(
                yRect,
                "Y",
                y.boolValue);

        z.boolValue =
            EditorGUI.ToggleLeft(
                zRect,
                "Z",
                z.boolValue);

        EditorGUI.EndProperty();
    }
}

[CustomEditor(typeof(UniversalCamera))]
public class UniversalCameraEditor : Editor
{
    static readonly Dictionary<string, bool> AdvancedOpen =
        new Dictionary<string, bool>();

    static readonly Type[] BehaviourOrder =
    {
        typeof(UniversalCamera.TargetPosition),
        typeof(UniversalCamera.AimAtTarget),
        typeof(UniversalCamera.MouseRotation),
        typeof(UniversalCamera.AddPosition),
        typeof(UniversalCamera.RotationOffset),
        typeof(UniversalCamera.CameraCollision),
        typeof(UniversalCamera.FieldOfView),
        typeof(UniversalCamera.SpeedFOV),
        typeof(UniversalCamera.Projection)
    };

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        DrawPropertiesExcluding(
            serializedObject,
            "m_Script",
            "modes",
            "activeMode",
            "phase",
            "_modePhaseMigrated");

        UniversalCamera rig =
            (UniversalCamera)target;

        SerializedProperty modes =
            serializedObject.FindProperty(
                "modes");

        EditorGUILayout.Space();
        DrawModeSelector(rig);

        int removeMode =
            -1;

        for (int i = 0;
             i < modes.arraySize;
             i++)
        {
            if (DrawMode(
                rig,
                modes,
                i))
            {
                removeMode =
                    i;
            }
        }

        EditorGUILayout.Space();

        if (GUILayout.Button(
            "Add Mode",
            GUILayout.Height(22)))
        {
            AddMode(
                modes);
        }

        DrawWarnings(rig);

        if (removeMode >= 0)
        {
            modes.DeleteArrayElementAtIndex(
                removeMode);

            SerializedProperty active =
                serializedObject.FindProperty(
                    "activeMode");

            if (active.intValue >=
                modes.arraySize)
            {
                active.intValue =
                    Mathf.Max(
                        0,
                        modes.arraySize - 1);
            }
        }

        serializedObject.ApplyModifiedProperties();
    }

    void AddMode(
        SerializedProperty modes)
    {
        modes.InsertArrayElementAtIndex(
            modes.arraySize);

        SerializedProperty mode =
            modes.GetArrayElementAtIndex(
                modes.arraySize - 1);

        mode.FindPropertyRelative(
            "name").stringValue =
            "Mode " +
            modes.arraySize;

        SerializedProperty phase =
            mode.FindPropertyRelative(
                "phase");

        if (phase != null)
        {
            phase.enumValueIndex =
                (int)UniversalCamera.Phase.LateUpdate;
        }

        SetBool(
            mode,
            "lockCursor",
            true);

        SetBool(
            mode,
            "hideCursor",
            true);

        SetBool(
            mode,
            "centerCursorOnEnter",
            true);

        SetBool(
            mode,
            "transitionOnEnter",
            false);

        SerializedProperty duration =
            mode.FindPropertyRelative(
                "transitionDuration");

        if (duration != null)
        {
            duration.floatValue =
                0.35f;
        }

        SetBool(
            mode,
            "transitionPosition",
            true);

        SetBool(
            mode,
            "transitionRotation",
            true);

        SetBool(
            mode,
            "transitionFieldOfView",
            true);

        SetBool(
            mode,
            "transitionProjection",
            true);

        SerializedProperty curve =
            mode.FindPropertyRelative(
                "transitionCurve");

        if (curve != null)
        {
            curve.animationCurveValue =
                AnimationCurve.EaseInOut(
                    0f,
                    0f,
                    1f,
                    1f);
        }

        mode.FindPropertyRelative(
            "behaviours").ClearArray();

        mode.isExpanded =
            true;
    }

    static void SetBool(
        SerializedProperty parent,
        string name,
        bool value)
    {
        SerializedProperty property =
            parent.FindPropertyRelative(
                name);

        if (property != null)
        {
            property.boolValue =
                value;
        }
    }

    void DrawModeSelector(
        UniversalCamera rig)
    {
        if (rig.modes == null ||
            rig.modes.Count == 0)
        {
            EditorGUILayout.HelpBox(
                "No modes yet. Add one below.",
                MessageType.Info);

            return;
        }

        string[] names =
            new string[rig.modes.Count];

        for (int i = 0;
             i < names.Length;
             i++)
        {
            string n =
                rig.modes[i] != null
                    ? rig.modes[i].name
                    : null;

            names[i] =
                string.IsNullOrEmpty(n)
                    ? "Mode " + (i + 1)
                    : n;
        }

        EditorGUILayout.LabelField(
            "Active Mode",
            EditorStyles.boldLabel);

        SerializedProperty active =
            serializedObject.FindProperty(
                "activeMode");

        int current =
            Mathf.Clamp(
                active.intValue,
                0,
                names.Length - 1);

        int picked =
            GUILayout.Toolbar(
                current,
                names);

        if (picked != current)
        {
            if (Application.isPlaying)
            {
                rig.SetMode(
                    picked);
            }
            else
            {
                active.intValue =
                    picked;
            }
        }

        EditorGUILayout.LabelField(
            "The stack runs top to bottom. Lower rows see the result above them; " +
            "they never rewrite an earlier row on the next frame.",
            EditorStyles.wordWrappedMiniLabel);
    }

    bool DrawMode(
        UniversalCamera rig,
        SerializedProperty modes,
        int index)
    {
        SerializedProperty mode =
            modes.GetArrayElementAtIndex(
                index);

        SerializedProperty name =
            mode.FindPropertyRelative(
                "name");

        SerializedProperty behaviours =
            mode.FindPropertyRelative(
                "behaviours");

        bool active =
            index ==
            rig.activeMode;

        bool remove =
            false;

        EditorGUILayout.Space();
        EditorGUILayout.BeginVertical(
            EditorStyles.helpBox);

        EditorGUILayout.BeginHorizontal();

        string label =
            string.IsNullOrEmpty(
                name.stringValue)
                ? "Mode " +
                  (index + 1)
                : name.stringValue;

        mode.isExpanded =
            EditorGUILayout.Foldout(
                mode.isExpanded,
                active
                    ? label + "  (active)"
                    : label,
                true);

        if (GUILayout.Button(
            "\u2715",
            EditorStyles.miniButton,
            GUILayout.Width(22)))
        {
            remove =
                true;
        }

        EditorGUILayout.EndHorizontal();

        if (mode.isExpanded)
        {
            EditorGUI.indentLevel++;

            EditorGUILayout.PropertyField(
                name);

            EditorGUILayout.Space(2f);

            EditorGUILayout.PropertyField(
                mode.FindPropertyRelative(
                    "phase"));

            EditorGUILayout.Space(3f);
            EditorGUILayout.LabelField(
                "Cursor",
                EditorStyles.boldLabel);

            EditorGUILayout.PropertyField(
                mode.FindPropertyRelative(
                    "lockCursor"));

            EditorGUILayout.PropertyField(
                mode.FindPropertyRelative(
                    "hideCursor"));

            SerializedProperty center =
                mode.FindPropertyRelative(
                    "centerCursorOnEnter");

            SerializedProperty lockCursor =
                mode.FindPropertyRelative(
                    "lockCursor");

            using (new EditorGUI.DisabledScope(
                lockCursor != null &&
                lockCursor.boolValue))
            {
                EditorGUILayout.PropertyField(
                    center);
            }

            EditorGUILayout.Space(3f);
            DrawTransition(mode);

            EditorGUI.indentLevel--;

            EditorGUILayout.Space(5f);

            EditorGUILayout.LabelField(
                "Camera Stack",
                EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "Read it literally from top to bottom. Example: Target Position -> " +
                "Mouse Look -> Position Offset -> Camera Collision.",
                MessageType.None);

            DrawBehaviourList(
                behaviours);
        }

        EditorGUILayout.EndVertical();

        return remove;
    }

    void DrawTransition(
        SerializedProperty mode)
    {
        SerializedProperty enabled =
            mode.FindPropertyRelative(
                "transitionOnEnter");

        EditorGUILayout.LabelField(
            "Transition Into Mode",
            EditorStyles.boldLabel);

        EditorGUILayout.PropertyField(
            enabled);

        using (new EditorGUI.DisabledScope(
            enabled == null ||
            !enabled.boolValue))
        {
            EditorGUI.indentLevel++;

            EditorGUILayout.PropertyField(
                mode.FindPropertyRelative(
                    "transitionDuration"));

            EditorGUILayout.PropertyField(
                mode.FindPropertyRelative(
                    "transitionPosition"));

            EditorGUILayout.PropertyField(
                mode.FindPropertyRelative(
                    "transitionRotation"));

            EditorGUILayout.PropertyField(
                mode.FindPropertyRelative(
                    "transitionFieldOfView"));

            EditorGUILayout.PropertyField(
                mode.FindPropertyRelative(
                    "transitionProjection"));

            EditorGUILayout.PropertyField(
                mode.FindPropertyRelative(
                    "transitionCurve"));

            EditorGUI.indentLevel--;
        }
    }

    void DrawBehaviourList(
        SerializedProperty behaviours)
    {
        int removeAt =
            -1;

        int moveFrom =
            -1;

        int moveTo =
            -1;

        for (int i = 0;
             i < behaviours.arraySize;
             i++)
        {
            DrawElement(
                behaviours,
                i,
                ref removeAt,
                ref moveFrom,
                ref moveTo);
        }

        if (behaviours.arraySize == 0)
        {
            EditorGUILayout.HelpBox(
                "Empty stack.",
                MessageType.Info);
        }

        DrawAddMenu(
            behaviours);

        if (moveFrom >= 0)
        {
            behaviours.MoveArrayElement(
                moveFrom,
                moveTo);
        }
        else if (removeAt >= 0)
        {
            RemoveAt(
                behaviours,
                removeAt);
        }
    }

    void DrawElement(
        SerializedProperty behaviours,
        int index,
        ref int removeAt,
        ref int moveFrom,
        ref int moveTo)
    {
        SerializedProperty element =
            behaviours.GetArrayElementAtIndex(
                index);

        EditorGUILayout.BeginVertical(
            EditorStyles.helpBox);

        EditorGUILayout.BeginHorizontal();

        SerializedProperty enabled =
            element.FindPropertyRelative(
                "enabled");

        if (enabled != null)
        {
            enabled.boolValue =
                EditorGUILayout.Toggle(
                    enabled.boolValue,
                    GUILayout.Width(16));
        }

        element.isExpanded =
            EditorGUILayout.Foldout(
                element.isExpanded,
                (index + 1) +
                ". " +
                DisplayName(element),
                true);

        using (new EditorGUI.DisabledScope(
            index == 0))
        {
            if (GUILayout.Button(
                "\u25B2",
                EditorStyles.miniButtonLeft,
                GUILayout.Width(22)))
            {
                moveFrom =
                    index;

                moveTo =
                    index - 1;
            }
        }

        using (new EditorGUI.DisabledScope(
            index ==
            behaviours.arraySize - 1))
        {
            if (GUILayout.Button(
                "\u25BC",
                EditorStyles.miniButtonMid,
                GUILayout.Width(22)))
            {
                moveFrom =
                    index;

                moveTo =
                    index + 1;
            }
        }

        if (GUILayout.Button(
            "\u2715",
            EditorStyles.miniButtonRight,
            GUILayout.Width(22)))
        {
            removeAt =
                index;
        }

        EditorGUILayout.EndHorizontal();

        if (element.isExpanded)
        {
            EditorGUI.indentLevel++;
            DrawChildren(element);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.EndVertical();
    }

    static void DrawChildren(
        SerializedProperty property)
    {
        Type type =
            property.managedReferenceValue != null
                ? property.managedReferenceValue.GetType()
                : null;

        List<SerializedProperty> advanced =
            new List<SerializedProperty>();

        SerializedProperty end =
            property.GetEndProperty();

        SerializedProperty iterator =
            property.Copy();

        bool enterChildren =
            true;

        while (iterator.NextVisible(
                   enterChildren) &&
               !SerializedProperty.EqualContents(
                   iterator,
                   end))
        {
            enterChildren =
                false;

            if (iterator.name ==
                "enabled")
                continue;

            if (IsAdvanced(
                type,
                iterator.name))
            {
                advanced.Add(
                    iterator.Copy());
            }
            else
            {
                EditorGUILayout.PropertyField(
                    iterator,
                    true);
            }
        }

        if (advanced.Count == 0)
            return;

        string key =
            property.propertyPath;

        AdvancedOpen.TryGetValue(
            key,
            out bool open);

        open =
            EditorGUILayout.Foldout(
                open,
                "Advanced (" +
                advanced.Count +
                ")",
                true);

        AdvancedOpen[key] =
            open;

        if (!open)
            return;

        EditorGUI.indentLevel++;

        foreach (SerializedProperty field
                 in advanced)
        {
            EditorGUILayout.PropertyField(
                field,
                true);
        }

        EditorGUI.indentLevel--;
    }

    static bool IsAdvanced(
        Type type,
        string fieldName)
    {
        FieldInfo field =
            FindField(
                type,
                fieldName);

        return
            field != null &&
            field.GetCustomAttribute<
                UniversalCamera.AdvancedAttribute>() !=
            null;
    }

    static FieldInfo FindField(
        Type type,
        string name)
    {
        const BindingFlags flags =
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.Instance |
            BindingFlags.DeclaredOnly;

        while (type != null)
        {
            FieldInfo field =
                type.GetField(
                    name,
                    flags);

            if (field != null)
                return field;

            type =
                type.BaseType;
        }

        return null;
    }

    void DrawAddMenu(
        SerializedProperty behaviours)
    {
        if (!GUILayout.Button(
            "Add Step",
            GUILayout.Height(22)))
            return;

        SerializedProperty captured =
            behaviours.Copy();

        GenericMenu menu =
            new GenericMenu();

        foreach (Type type
                 in BehaviourOrder)
        {
            Type capturedType =
                type;

            menu.AddItem(
                new GUIContent(
                    ObjectNames.NicifyVariableName(
                        capturedType.Name)),
                false,
                () =>
                    Append(
                        captured,
                        capturedType));
        }

        menu.ShowAsContext();
    }

    void Append(
        SerializedProperty behaviours,
        Type type)
    {
        serializedObject.Update();

        int index =
            behaviours.arraySize;

        behaviours.InsertArrayElementAtIndex(
            index);

        SerializedProperty element =
            behaviours.GetArrayElementAtIndex(
                index);

        element.managedReferenceValue =
            Activator.CreateInstance(
                type);

        element.isExpanded =
            true;

        serializedObject.ApplyModifiedProperties();
    }

    static void RemoveAt(
        SerializedProperty behaviours,
        int index)
    {
        int size =
            behaviours.arraySize;

        behaviours.DeleteArrayElementAtIndex(
            index);

        if (behaviours.arraySize ==
            size)
        {
            behaviours.DeleteArrayElementAtIndex(
                index);
        }
    }

    static string DisplayName(
        SerializedProperty element)
    {
        string full =
            element.managedReferenceFullTypename;

        if (string.IsNullOrEmpty(full))
            return "(missing)";

        int space =
            full.IndexOf(' ');

        string name =
            space >= 0
                ? full.Substring(
                    space + 1)
                : full;

        int cut =
            name.LastIndexOfAny(
                new[]
                {
                    '/',
                    '+',
                    '.'
                });

        if (cut >= 0)
        {
            name =
                name.Substring(
                    cut + 1);
        }

        // New primitive types use intentionally simple user-facing names.
        switch (name)
        {
            case "AimAtTarget":   return "Look At Target";
            case "MouseRotation": return "Mouse Look";
            case "AddPosition":   return "Position Offset";
            case "SpeedFOV":      return "Speed Field Of View";
            case "Projection":    return "Projection";
        }

        string normal =
            ObjectNames.NicifyVariableName(
                name);

        // Anything not offered by the current Add Step menu is a preserved
        // SerializeReference from the pre-simplification camera.
        Type resolved =
            propertyTypeFromName(name);

        bool current =
            BehaviourOrder.Contains(
                resolved);

        return current
            ? normal
            : normal + "  (Legacy)";
    }

    static Type propertyTypeFromName(
        string nestedName)
    {
        return typeof(UniversalCamera)
            .GetNestedTypes(
                BindingFlags.Public |
                BindingFlags.NonPublic)
            .FirstOrDefault(
                t => t.Name == nestedName);
    }

    void DrawWarnings(
        UniversalCamera rig)
    {
        UniversalCamera.CameraMode mode =
            rig.ActiveMode;

        if (mode == null ||
            mode.behaviours == null)
            return;

        bool requiresLockedCursor =
            false;

        bool needsCamera =
            false;

        foreach (UniversalCamera.CameraBehaviour behaviour
                 in mode.behaviours)
        {
            if (behaviour == null ||
                !behaviour.enabled)
                continue;

            if (behaviour is
                UniversalCamera.MouseRotation mouse)
            {
                requiresLockedCursor |=
                    mouse.requireCursorLock;
            }
            else if (behaviour is
                     UniversalCamera.MouseLook legacyMouse)
            {
                requiresLockedCursor |=
                    legacyMouse.requireCursorLock;
            }

            if (behaviour.WritesFieldOfView ||
                behaviour.WritesProjection)
            {
                needsCamera =
                    true;
            }
        }

        if (!mode.lockCursor &&
            requiresLockedCursor)
        {
            EditorGUILayout.HelpBox(
                "Mouse Look requires a locked cursor, but this mode does not lock it.",
                MessageType.Warning);
        }

        if (needsCamera &&
            rig.ResolveCamera() == null)
        {
            EditorGUILayout.HelpBox(
                "This mode changes Camera values, but no Camera is assigned or found below this rig.",
                MessageType.Warning);
        }
    }
}
