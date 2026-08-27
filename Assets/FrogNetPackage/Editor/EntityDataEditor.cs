using System;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(EntityData))]
public class EntityDataEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.PropertyField(serializedObject.FindProperty("entityName"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("description"));

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("States", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("states"), true);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Stats", EditorStyles.boldLabel);
        DrawStats();

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawStats()
    {
        SerializedProperty stats = serializedObject.FindProperty("stats");

        for (int i = 0; i < stats.arraySize; i++)
        {
            SerializedProperty element = stats.GetArrayElementAtIndex(i);
            string label = element.managedReferenceValue != null
                ? element.managedReferenceValue.GetType().Name
                : "(null)";

            EditorGUILayout.PropertyField(element, new GUIContent(label), true);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Remove", EditorStyles.miniButton, GUILayout.Width(60f)))
                {
                    stats.DeleteArrayElementAtIndex(i);
                    break;
                }
            }

            EditorGUILayout.Space(2f);
        }

        if (GUILayout.Button("Add Stat", GUILayout.Width(90f)))
            ShowAddStatMenu(stats);
    }

    private void ShowAddStatMenu(SerializedProperty stats)
    {
        var menu = new GenericMenu();

        foreach (Type type in TypeCache.GetTypesDerivedFrom<Stat>())
        {
            if (type.IsAbstract)
                continue;

            menu.AddItem(new GUIContent(type.Name), false, () =>
            {
                stats.InsertArrayElementAtIndex(stats.arraySize);
                SerializedProperty element = stats.GetArrayElementAtIndex(stats.arraySize - 1);
                element.managedReferenceValue = Activator.CreateInstance(type);
                serializedObject.ApplyModifiedProperties();
            });
        }

        menu.ShowAsContext();
    }
}
