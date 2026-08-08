using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(ItemData))]
public class ItemDataEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.PropertyField(serializedObject.FindProperty("itemName"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("description"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("maxStack"));

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Defaults", EditorStyles.boldLabel);

        SerializedProperty mods = serializedObject.FindProperty("mods");
        Rect rect = EditorGUILayout.GetControlRect(false, ItemModsGUI.Height(mods));
        ItemModsGUI.Draw(rect, mods);

        serializedObject.ApplyModifiedProperties();
    }
}
