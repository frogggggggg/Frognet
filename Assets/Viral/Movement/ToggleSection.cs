using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Base for an optional feature block. Derive a [Serializable] class from it
/// and it shows in the inspector as a foldout with an enable checkbox; the
/// fields only show while enabled. Nests (a section inside a section works).
/// Reusable anywhere.
/// </summary>
[Serializable]
public abstract class ToggleSection
{
    public bool enabled = true;
}

#if UNITY_EDITOR
[CustomPropertyDrawer(typeof(ToggleSection), true)]
class ToggleSectionDrawer : PropertyDrawer
{
    static float Line => EditorGUIUtility.singleLineHeight;
    static float Gap => EditorGUIUtility.standardVerticalSpacing;

    static bool Open(SerializedProperty p) =>
        p.isExpanded && p.FindPropertyRelative("enabled").boolValue;

    static IEnumerable<SerializedProperty> Children(SerializedProperty p)
    {
        SerializedProperty it = p.Copy(), end = p.GetEndProperty();
        if (!it.NextVisible(true)) yield break;

        while (!SerializedProperty.EqualContents(it, end))
        {
            if (it.name != "enabled") yield return it;
            if (!it.NextVisible(false)) yield break;
        }
    }

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        float h = Line;
        if (!Open(property)) return h;

        foreach (SerializedProperty c in Children(property))
        {
            float ch = EditorGUI.GetPropertyHeight(c, true);
            if (ch > 0f) h += ch + Gap; // settingless effects take no space
        }

        return h;
    }

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        label = EditorGUI.BeginProperty(position, label, property);

        SerializedProperty on = property.FindPropertyRelative("enabled");
        Rect row = EditorGUI.IndentedRect(new Rect(position.x, position.y, position.width, Line));

        int indent = EditorGUI.indentLevel;
        EditorGUI.indentLevel = 0;
        property.isExpanded = EditorGUI.Foldout(new Rect(row.x, row.y, 14f, Line), property.isExpanded, GUIContent.none, true);
        on.boolValue = EditorGUI.ToggleLeft(new Rect(row.x + 14f, row.y, row.width - 14f, Line), label, on.boolValue, EditorStyles.boldLabel);
        EditorGUI.indentLevel = indent;

        if (Open(property))
        {
            EditorGUI.indentLevel++;
            float y = row.yMax + Gap;

            foreach (SerializedProperty c in Children(property))
            {
                float h = EditorGUI.GetPropertyHeight(c, true);
                if (h <= 0f) continue;
                EditorGUI.PropertyField(new Rect(position.x, y, position.width, h), c, true);
                y += h + Gap;
            }

            EditorGUI.indentLevel--;
        }

        EditorGUI.EndProperty();
    }
}
#endif