using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(SettingSetter))]
public class SettingSetterEditor : Editor
{
    private const BindingFlags MemberFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.PropertyField(serializedObject.FindProperty("applier"));

        SerializedProperty links = serializedObject.FindProperty("links");

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Links", EditorStyles.boldLabel);

        int removeIndex = -1;

        for (int i = 0; i < links.arraySize; i++)
        {
            SerializedProperty link = links.GetArrayElementAtIndex(i);
            SerializedProperty targetProp = link.FindPropertyRelative("target");
            SerializedProperty variableProp = link.FindPropertyRelative("variableName");
            SerializedProperty keyProp = link.FindPropertyRelative("settingKey");

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(Describe(targetProp, variableProp, keyProp), EditorStyles.miniBoldLabel);
                if (GUILayout.Button("Remove", GUILayout.Width(64)))
                    removeIndex = i;
            }

            DrawTargetPicker(targetProp);
            DrawVariablePopup(targetProp, variableProp);
            DrawKeyPopup(keyProp);

            EditorGUILayout.EndVertical();
        }

        if (links.arraySize == 0)
            EditorGUILayout.LabelField("No links yet.", EditorStyles.centeredGreyMiniLabel);

        if (GUILayout.Button("Add Link"))
            links.InsertArrayElementAtIndex(links.arraySize);

        if (removeIndex >= 0)
            links.DeleteArrayElementAtIndex(removeIndex);

        serializedObject.ApplyModifiedProperties();
    }

    private static string Describe(SerializedProperty targetProp, SerializedProperty variableProp, SerializedProperty keyProp)
    {
        string key = string.IsNullOrEmpty(keyProp.stringValue) ? "?" : keyProp.stringValue;
        Component target = targetProp.objectReferenceValue as Component;

        if (target == null || string.IsNullOrEmpty(variableProp.stringValue))
            return $"{key} \u2192 ?";

        return $"{key} \u2192 {target.GetType().Name}.{variableProp.stringValue}";
    }

    private static void DrawTargetPicker(SerializedProperty targetProp)
    {
        Component current = targetProp.objectReferenceValue as Component;
        GameObject owner = current != null ? current.gameObject : null;

        GameObject picked = (GameObject)EditorGUILayout.ObjectField("Target Object", owner, typeof(GameObject), true);
        if (picked != owner)
        {
            targetProp.objectReferenceValue = picked != null ? DefaultComponent(picked) : null;
            current = targetProp.objectReferenceValue as Component;
            owner = picked;
        }

        if (owner == null)
            return;

        List<Component> components = new List<Component>();
        foreach (Component component in owner.GetComponents<Component>())
        {
            if (component != null)
                components.Add(component);
        }

        string[] names = new string[components.Count];
        for (int i = 0; i < components.Count; i++)
            names[i] = components[i].GetType().Name;

        int index = components.IndexOf(current);
        int chosen = EditorGUILayout.Popup("Component", index, names);

        if (chosen != index && chosen >= 0)
            targetProp.objectReferenceValue = components[chosen];
    }

    private static void DrawVariablePopup(SerializedProperty targetProp, SerializedProperty variableProp)
    {
        Component target = targetProp.objectReferenceValue as Component;

        if (target == null)
        {
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.Popup("Variable", 0, new[] { "(no component)" });
            return;
        }

        List<string> values = CollectMembers(target.GetType());

        if (values.Count == 0)
        {
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.Popup("Variable", 0, new[] { "(no settable variables)" });
            return;
        }

        DrawStringPopup("Variable", variableProp, values);
    }

    private void DrawKeyPopup(SerializedProperty keyProp)
    {
        SettingSetter setter = (SettingSetter)target;
        SettingDesigner designer = setter.applier != null ? setter.applier.designer : null;

        if (designer == null)
        {
            EditorGUILayout.PropertyField(keyProp, new GUIContent("Setting Key"));
            return;
        }

        List<string> keys = designer.GetKeys();

        if (keys.Count == 0)
        {
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.Popup("Setting Key", 0, new[] { "(no keys defined)" });
            return;
        }

        DrawStringPopup("Setting Key", keyProp, keys);
    }

    /// <summary>
    /// Popup over a string property. A value that is no longer in the list stays selected and is marked missing.
    /// </summary>
    private static void DrawStringPopup(string label, SerializedProperty stringProp, List<string> values)
    {
        List<string> display = new List<string>(values);
        int index = values.IndexOf(stringProp.stringValue);

        if (index < 0)
        {
            values.Insert(0, stringProp.stringValue);
            display.Insert(0, string.IsNullOrEmpty(stringProp.stringValue)
                ? "(none)"
                : $"{stringProp.stringValue} (missing)");
            index = 0;
        }

        int chosen = EditorGUILayout.Popup(label, index, display.ToArray());
        if (chosen != index)
            stringProp.stringValue = values[chosen];
    }

    private static Component DefaultComponent(GameObject owner)
    {
        MonoBehaviour script = owner.GetComponent<MonoBehaviour>();
        return script != null ? script : owner.transform;
    }

    /// <summary>
    /// Every instance field and writable property on the type that a setting can actually be written into.
    /// Vector members are listed as their individual components.
    /// </summary>
    private static List<string> CollectMembers(Type type)
    {
        var names = new List<string>();

        foreach (FieldInfo field in type.GetFields(MemberFlags))
        {
            if (field.IsInitOnly || field.Name.Contains("<") || IsEngineNoise(field.DeclaringType))
                continue;

            if (field.IsPublic || field.IsDefined(typeof(SerializeField), true))
                AddMember(names, field.Name, field.FieldType);
        }

        foreach (PropertyInfo property in type.GetProperties(MemberFlags))
        {
            if (!property.CanWrite || property.GetIndexParameters().Length > 0 || IsEngineNoise(property.DeclaringType))
                continue;

            AddMember(names, property.Name, property.PropertyType);
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static void AddMember(List<string> names, string memberName, Type type)
    {
        string axes = VectorAxes(type);

        if (axes == null)
        {
            if (IsSupported(type))
                names.Add(memberName);

            return;
        }

        foreach (char axis in axes)
            names.Add($"{memberName}.{axis}");
    }

    private static string VectorAxes(Type type)
    {
        if (type == typeof(Vector2))
            return "xy";

        if (type == typeof(Vector3))
            return "xyz";

        return type == typeof(Vector4) ? "xyzw" : null;
    }

    private static bool IsSupported(Type type)
    {
        return type.IsEnum
            || type == typeof(float)
            || type == typeof(int)
            || type == typeof(bool)
            || type == typeof(string);
    }

    private static bool IsEngineNoise(Type declaringType)
    {
        return declaringType == typeof(MonoBehaviour)
            || declaringType == typeof(Component)
            || declaringType == typeof(UnityEngine.Object);
    }
}
