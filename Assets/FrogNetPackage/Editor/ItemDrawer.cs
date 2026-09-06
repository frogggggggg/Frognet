using System.Collections.Generic;
using Frognet.Data;
using UnityEditor;
using UnityEngine;

/// <summary>
/// One line for the item and its count, with whatever the copy has overridden shown underneath
/// when the row is expanded.
/// </summary>
/// <remarks>
/// Overrides are read only here on purpose: what a copy starts with belongs in the item's
/// <c>modified_data</c> block, and everything after that is a runtime decision.
/// </remarks>
[CustomPropertyDrawer(typeof(Item))]
public class ItemDrawer : PropertyDrawer
{
    private const float Pad = 4f;

    private static string[] names;
    private static int[] ids;
    private static uint builtFor;
    private static bool built;

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        float line = EditorGUIUtility.singleLineHeight;

        if (!property.isExpanded)
            return line;

        int rows = Mathf.Max(1, Values(property).arraySize);
        return line + (line + EditorGUIUtility.standardVerticalSpacing) * rows;
    }

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        Build();

        var line = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);

        float labelWidth = EditorGUIUtility.labelWidth;
        property.isExpanded = EditorGUI.Foldout(
            new Rect(line.x, line.y, labelWidth, line.height), property.isExpanded, label, true);

        float rest = line.width - labelWidth;
        var idRect = new Rect(line.x + labelWidth, line.y, rest * 0.65f - Pad, line.height);
        var countRect = new Rect(idRect.xMax + Pad, line.y, rest * 0.35f, line.height);

        SerializedProperty id = property.FindPropertyRelative("id");
        int current = Mathf.Max(0, System.Array.IndexOf(ids, id.intValue));

        EditorGUI.BeginChangeCheck();
        int picked = EditorGUI.Popup(idRect, current, names);

        if (EditorGUI.EndChangeCheck())
            id.intValue = ids[picked];

        EditorGUI.PropertyField(countRect, property.FindPropertyRelative("quantity"), GUIContent.none);

        if (!property.isExpanded)
            return;

        SerializedProperty values = Values(property);
        float y = line.yMax + EditorGUIUtility.standardVerticalSpacing;

        EditorGUI.indentLevel++;

        if (values.arraySize == 0)
        {
            var empty = new Rect(position.x, y, position.width, line.height);
            EditorGUI.LabelField(EditorGUI.IndentedRect(empty), "No overrides on this copy.");
        }

        for (int i = 0; i < values.arraySize; i++)
        {
            var row = new Rect(position.x, y, position.width, line.height);
            EditorGUI.LabelField(EditorGUI.IndentedRect(row), Describe(values.GetArrayElementAtIndex(i)));
            y += line.height + EditorGUIUtility.standardVerticalSpacing;
        }

        EditorGUI.indentLevel--;
    }

    private static SerializedProperty Values(SerializedProperty property)
    {
        return property.FindPropertyRelative("overrides").FindPropertyRelative("values");
    }

    private static string Describe(SerializedProperty element)
    {
        var value = new DataValue
        {
            leaf = element.FindPropertyRelative("leaf").intValue,
            x = element.FindPropertyRelative("x").floatValue,
            y = element.FindPropertyRelative("y").floatValue,
            z = element.FindPropertyRelative("z").floatValue,
            w = element.FindPropertyRelative("w").floatValue,
            text = element.FindPropertyRelative("text").stringValue
        };

        return value.Describe(ItemRegistry.Schema);
    }

    private static void Build()
    {
        if (built && builtFor == ItemRegistry.Hash)
            return;

        built = true;
        builtFor = ItemRegistry.Hash;

        var labels = new List<string> { "(none)" };
        var values = new List<int> { 0 };

        foreach (Definition def in ItemRegistry.All)
        {
            labels.Add(def.name);
            values.Add(def.id);
        }

        names = labels.ToArray();
        ids = values.ToArray();
    }

    [InitializeOnLoadMethod]
    private static void Invalidate()
    {
        DataRegistry.onReloaded += () => built = false;
    }
}
