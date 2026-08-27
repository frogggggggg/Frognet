using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

[CustomPropertyDrawer(typeof(Movement.Binding))]
public sealed class MovementBindingDrawer : PropertyDrawer
{
    private static string[] actionPaths;
    private static bool[] actionIsVector2;
    private static string[] actionOptions;

    private static readonly string[] AnalogNames =
        { "Up", "Down", "Left", "Right", "Horizontal", "Vertical" };

    private static readonly Movement.BindComponent[] AnalogValues =
    {
        Movement.BindComponent.Up,
        Movement.BindComponent.Down,
        Movement.BindComponent.Left,
        Movement.BindComponent.Right,
        Movement.BindComponent.Horizontal,
        Movement.BindComponent.Vertical
    };

    private static InputActionAsset FindAsset()
    {
        InputActionAsset asset = InputSystem.actions;

        if (asset != null)
            return asset;

        string[] guids = AssetDatabase.FindAssets("t:InputActionAsset");

        return guids.Length == 0
            ? null
            : AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetDatabase.GUIDToAssetPath(guids[0]));
    }

    private static void EnsureActions()
    {
        if (actionPaths != null)
            return;

        var paths = new List<string>();
        var isVector2 = new List<bool>();

        InputActionAsset asset = FindAsset();

        if (asset != null)
        {
            foreach (InputActionMap map in asset.actionMaps)
            {
                foreach (InputAction action in map.actions)
                {
                    paths.Add($"{map.name}/{action.name}");
                    isVector2.Add(action.expectedControlType == "Vector2");
                }
            }
        }

        actionPaths = paths.ToArray();
        actionIsVector2 = isVector2.ToArray();

        actionOptions = new string[actionPaths.Length + 1];
        actionOptions[0] = "(none)";
        Array.Copy(actionPaths, 0, actionOptions, 1, actionPaths.Length);
    }

    private static bool IsAnalogAction(string path)
    {
        EnsureActions();
        int index = Array.IndexOf(actionPaths, path);
        return index >= 0 && actionIsVector2[index];
    }

    private static string[] FieldsFor(Movement.BindingType type)
    {
        return type switch
        {
            Movement.BindingType.Linear =>
                new[] { "direction", "space", "maxSpeed", "acceleration", "deceleration" },
            Movement.BindingType.Angular =>
                new[] { "direction", "maxSpeed", "acceleration", "deceleration" },
            Movement.BindingType.Multiplier =>
                new[] { "multiplier" },
            _ =>
                new[] { "direction", "space", "force", "requireGrounded" }
        };
    }

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        float line = EditorGUIUtility.singleLineHeight;
        float spacing = EditorGUIUtility.standardVerticalSpacing;
        float height = line + spacing;

        if (!property.isExpanded)
            return height;

        var type = (Movement.BindingType)property.FindPropertyRelative("type").enumValueIndex;
        bool analog = IsAnalogAction(property.FindPropertyRelative("action").stringValue);

        // name, type, action, and the component picker when the action is a Vector2.
        height += (line + spacing) * (analog ? 4 : 3);

        foreach (string field in FieldsFor(type))
            height += EditorGUI.GetPropertyHeight(property.FindPropertyRelative(field)) + spacing;

        return height + spacing;
    }

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        EnsureActions();
        EditorGUI.BeginProperty(position, label, property);

        float spacing = EditorGUIUtility.standardVerticalSpacing;
        var row = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);

        SerializedProperty nameProperty = property.FindPropertyRelative("name");
        SerializedProperty typeProperty = property.FindPropertyRelative("type");
        SerializedProperty actionProperty = property.FindPropertyRelative("action");
        SerializedProperty componentProperty = property.FindPropertyRelative("component");

        var type = (Movement.BindingType)typeProperty.enumValueIndex;
        string title = nameProperty.stringValue;

        property.isExpanded = EditorGUI.Foldout(
            row,
            property.isExpanded,
            string.IsNullOrEmpty(title) ? label.text : $"{title}  ({type})",
            true);

        if (!property.isExpanded)
        {
            EditorGUI.EndProperty();
            return;
        }

        EditorGUI.indentLevel++;
        row.y += row.height + spacing;

        DrawField(ref row, nameProperty, spacing);
        DrawField(ref row, typeProperty, spacing);
        DrawAction(ref row, actionProperty, componentProperty, spacing);

        if (IsAnalogAction(actionProperty.stringValue))
            DrawComponent(ref row, componentProperty, spacing);

        foreach (string field in FieldsFor(type))
            DrawField(ref row, property.FindPropertyRelative(field), spacing);

        EditorGUI.indentLevel--;
        EditorGUI.EndProperty();
    }

    private static void DrawAction(
        ref Rect row,
        SerializedProperty actionProperty,
        SerializedProperty componentProperty,
        float spacing)
    {
        row.height = EditorGUIUtility.singleLineHeight;

        string current = actionProperty.stringValue;
        int index = Array.IndexOf(actionPaths, current);

        string[] options = actionOptions;

        if (index < 0 && !string.IsNullOrEmpty(current))
        {
            options = (string[])actionOptions.Clone();
            options[0] = $"(missing) {current}";
        }

        int selected = EditorGUI.Popup(row, "Action", index + 1, options);

        if (selected != index + 1)
        {
            actionProperty.stringValue = selected == 0 ? string.Empty : actionPaths[selected - 1];

            bool analog = selected > 0 && actionIsVector2[selected - 1];
            var component = (Movement.BindComponent)componentProperty.enumValueIndex;

            if (analog && component == Movement.BindComponent.Pressed)
                componentProperty.enumValueIndex = (int)Movement.BindComponent.Horizontal;
            else if (!analog)
                componentProperty.enumValueIndex = (int)Movement.BindComponent.Pressed;
        }

        row.y += row.height + spacing;
    }

    private static void DrawComponent(ref Rect row, SerializedProperty componentProperty, float spacing)
    {
        row.height = EditorGUIUtility.singleLineHeight;

        int index = Math.Max(0, Array.IndexOf(AnalogValues, (Movement.BindComponent)componentProperty.enumValueIndex));
        int selected = EditorGUI.Popup(row, "Component", index, AnalogNames);

        if (selected != index)
            componentProperty.enumValueIndex = (int)AnalogValues[selected];

        row.y += row.height + spacing;
    }

    private static void DrawField(ref Rect row, SerializedProperty child, float spacing)
    {
        row.height = EditorGUI.GetPropertyHeight(child);
        EditorGUI.PropertyField(row, child, true);
        row.y += row.height + spacing;
    }
}
