using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[CustomEditor(typeof(SettingDesignerApplier))]
public class SettingDesignerApplierEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField("Designer Applier", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Assign a Setting Designer asset, prefabs, and content roots, then generate the settings UI into the scene.", EditorStyles.wordWrappedMiniLabel);
        EditorGUILayout.EndVertical();

        DrawPropertiesExcluding(serializedObject, "m_Script");

        EditorGUILayout.Space(8);
        using (new EditorGUI.DisabledScope(Application.isPlaying))
        {
            if (GUILayout.Button("Build Settings UI", GUILayout.Height(28)))
            {
                var applier = (SettingDesignerApplier)target;
                applier.CreateSettings();
                EditorSceneManager.MarkSceneDirty(applier.gameObject.scene);
            }
        }

        serializedObject.ApplyModifiedProperties();
    }
}