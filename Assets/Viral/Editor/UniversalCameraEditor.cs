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
/// null elements. This draws the list itself: an Add menu built from every
/// non-abstract CameraBehaviour, plus reorder and remove controls, since order
/// is what defines how the behaviours compose.
/// </summary>
[CustomEditor(typeof(UniversalCamera))]
public class UniversalCameraEditor : Editor
{
    static List<Type> _behaviourTypes;
    static readonly Dictionary<string, bool> AdvancedOpen = new Dictionary<string, bool>();

    SerializedProperty _behaviours;

    void OnEnable()
    {
        _behaviours = serializedObject.FindProperty("behaviours");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        DrawPropertiesExcluding(serializedObject, "m_Script", "behaviours");

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Behaviours", EditorStyles.boldLabel);
        EditorGUILayout.LabelField(
            "Applied top to bottom. Rotation usually belongs above the position " +
            "behaviours that depend on facing.", EditorStyles.wordWrappedMiniLabel);

        int removeAt = -1;
        int moveFrom = -1;
        int moveTo = -1;

        if (_behaviours == null || _behaviours.arraySize == 0)
        {
            EditorGUILayout.HelpBox("No behaviours yet. Use Add Behaviour below.",
                                    MessageType.Info);
        }
        else
        {
            for (int i = 0; i < _behaviours.arraySize; i++)
                DrawElement(i, ref removeAt, ref moveFrom, ref moveTo);
        }

        EditorGUILayout.Space();
        DrawAddMenu();

        DrawWarnings();

        // Deferred on purpose: resizing the list while drawing changes the
        // control count between the Layout and Repaint passes, and IMGUI
        // throws when those two disagree.
        if (moveFrom >= 0)
            _behaviours.MoveArrayElement(moveFrom, moveTo);
        else if (removeAt >= 0)
            RemoveAt(removeAt);

        serializedObject.ApplyModifiedProperties();
    }

    void DrawElement(int index, ref int removeAt, ref int moveFrom, ref int moveTo)
    {
        SerializedProperty element = _behaviours.GetArrayElementAtIndex(index);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();

        SerializedProperty enabled = element.FindPropertyRelative("enabled");
        if (enabled != null)
        {
            enabled.boolValue = EditorGUILayout.Toggle(
                enabled.boolValue, GUILayout.Width(16));
        }

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

        using (new EditorGUI.DisabledScope(index == _behaviours.arraySize - 1))
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

        // Always drawn, regardless of a pending mutation, so the control
        // count stays identical across both IMGUI passes.
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
        Type type = property.managedReferenceValue?.GetType();
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
        open = EditorGUILayout.Foldout(open, $"Advanced ({advanced.Count})", true);
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

    void RemoveAt(int index)
    {
        int size = _behaviours.arraySize;
        _behaviours.DeleteArrayElementAtIndex(index);

        // On some Unity versions the first delete only nulls the managed
        // reference rather than shortening the array.
        if (_behaviours.arraySize == size)
            _behaviours.DeleteArrayElementAtIndex(index);
    }

    /// <summary>
    /// Flag the combinations that visibly fight each other. These are all
    /// legal, so they are warnings rather than errors.
    /// </summary>
    void DrawWarnings()
    {
        var rig = (UniversalCamera)target;
        if (rig.behaviours == null) return;

        bool freeLook = false, mouseLook = false, lookAt = false;
        int rotationWriters = 0;
        UniversalCamera.DistanceFromTarget boom = null;

        foreach (UniversalCamera.CameraBehaviour behaviour in rig.behaviours)
        {
            if (behaviour == null || !behaviour.enabled) continue;

            switch (behaviour)
            {
                case UniversalCamera.FreeLook:       freeLook = true;  rotationWriters++; break;
                case UniversalCamera.MouseLook:      mouseLook = true; rotationWriters++; break;
                case UniversalCamera.LookAtTarget:   lookAt = true;    rotationWriters++; break;
                case UniversalCamera.ConstantRotate:                   rotationWriters++; break;
                case UniversalCamera.DistanceFromTarget distance:      boom = distance;   break;
            }
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
                $"{rotationWriters} behaviours write rotation. Unless they are " +
                "restricted to different axes, only the last one's result " +
                "survives.", MessageType.Info);
        }
    }

    void DrawAddMenu()
    {
        if (!GUILayout.Button("Add Behaviour", GUILayout.Height(22)))
            return;

        var menu = new GenericMenu();

        foreach (Type type in BehaviourTypes())
        {
            Type captured = type;
            menu.AddItem(new GUIContent(ObjectNames.NicifyVariableName(captured.Name)),
                         false, () => Append(captured));
        }

        if (menu.GetItemCount() == 0)
            menu.AddDisabledItem(new GUIContent("No CameraBehaviour types found"));

        menu.ShowAsContext();
    }

    void Append(Type type)
    {
        serializedObject.Update();

        int index = _behaviours.arraySize;
        _behaviours.InsertArrayElementAtIndex(index);

        SerializedProperty element = _behaviours.GetArrayElementAtIndex(index);
        element.managedReferenceValue = Activator.CreateInstance(type);
        element.isExpanded = true;

        serializedObject.ApplyModifiedProperties();
    }

    static IEnumerable<Type> BehaviourTypes()
    {
        // TypeCache is prebuilt by the editor, so this costs nothing to query.
        _behaviourTypes ??= TypeCache
            .GetTypesDerivedFrom<UniversalCamera.CameraBehaviour>()
            .Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition)
            .OrderBy(t => t.Name)
            .ToList();

        return _behaviourTypes;
    }

    static string DisplayName(SerializedProperty element)
    {
        string full = element.managedReferenceFullTypename;
        if (string.IsNullOrEmpty(full))
            return "(unassigned)";

        // Format is "<assembly> <Namespace.Outer/Nested>".
        int space = full.IndexOf(' ');
        string typeName = space >= 0 ? full.Substring(space + 1) : full;

        int cut = typeName.LastIndexOfAny(new[] { '/', '+', '.' });
        if (cut >= 0)
            typeName = typeName.Substring(cut + 1);

        return ObjectNames.NicifyVariableName(typeName);
    }
}
