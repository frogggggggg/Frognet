using UnityEditor;
using UnityEngine;

/// <summary>
/// One line for the item and its count, with the per-instance modifications underneath
/// when the row is expanded.
/// </summary>
[CustomPropertyDrawer(typeof(Item))]
public class ItemDrawer : PropertyDrawer
{
    private const float Pad = 4f;

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        float line = EditorGUIUtility.singleLineHeight;

        if (!property.isExpanded)
            return line;

        return line + EditorGUIUtility.standardVerticalSpacing
             + ItemModsGUI.Height(property.FindPropertyRelative("mods"));
    }

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        var line = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);

        float labelWidth = EditorGUIUtility.labelWidth;
        var foldoutRect = new Rect(line.x, line.y, labelWidth, line.height);
        property.isExpanded = EditorGUI.Foldout(foldoutRect, property.isExpanded, label, true);

        float rest = line.width - labelWidth;
        var dataRect = new Rect(line.x + labelWidth, line.y, rest * 0.65f - Pad, line.height);
        var countRect = new Rect(dataRect.xMax + Pad, line.y, rest * 0.35f, line.height);

        EditorGUI.PropertyField(dataRect, property.FindPropertyRelative("data"), GUIContent.none);
        EditorGUI.PropertyField(countRect, property.FindPropertyRelative("quantity"), GUIContent.none);

        if (!property.isExpanded)
            return;

        SerializedProperty mods = property.FindPropertyRelative("mods");
        var modsRect = new Rect(
            position.x,
            line.yMax + EditorGUIUtility.standardVerticalSpacing,
            position.width,
            ItemModsGUI.Height(mods));

        EditorGUI.indentLevel++;
        ItemModsGUI.Draw(EditorGUI.IndentedRect(modsRect), mods);
        EditorGUI.indentLevel--;
    }
}
