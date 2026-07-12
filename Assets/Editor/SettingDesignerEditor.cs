using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[CustomEditor(typeof(SettingDesigner))]
public class SettingDesignerEditor : Editor
{
    private SerializedProperty tabsProp;

    private void OnEnable()
    {
        tabsProp = serializedObject.FindProperty("tabs");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        DrawTopLevelProperties();

        EditorGUILayout.Space();
        DrawTabsList();

        EditorGUILayout.Space();
        if (!Application.isPlaying)
        {
            if (GUILayout.Button("Create Settings"))
            {
                var designer = (SettingDesigner)target;
                designer.CreateSettings();
                EditorSceneManager.MarkSceneDirty(designer.gameObject.scene);
            }
        }
        else
        {
            using (new EditorGUI.DisabledScope(true))
            {
                GUILayout.Button("Create Settings (play mode disabled)", GUILayout.Height(22));
            }
        }

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawTopLevelProperties()
    {
        SerializedProperty property = serializedObject.GetIterator();
        bool enterChildren = true;
        while (property.NextVisible(enterChildren))
        {
            if (property.name == "tabs")
            {
                enterChildren = false;
                continue;
            }

            EditorGUILayout.PropertyField(property, true);
            enterChildren = false;
        }
    }

    private void DrawStartValueForOutputType(SerializedProperty settingProp)
    {
        var outputType = (SettingDesigner.OutputType)settingProp.FindPropertyRelative("output").enumValueIndex;

        switch (outputType)
        {
            case SettingDesigner.OutputType.Audio:
                EditorGUILayout.PropertyField(settingProp.FindPropertyRelative("audioGroup"), new GUIContent("Start Value"));
                break;
            case SettingDesigner.OutputType.InputAction:
                EditorGUILayout.PropertyField(settingProp.FindPropertyRelative("inputActionReference"), new GUIContent("Start Value"));
                break;
            case SettingDesigner.OutputType.CameraSetting:
                EditorGUILayout.PropertyField(settingProp.FindPropertyRelative("cameraSetting"), new GUIContent("Start Value"));
                break;
            case SettingDesigner.OutputType.UI:
                EditorGUILayout.PropertyField(settingProp.FindPropertyRelative("uiSetting"), new GUIContent("Start Value"));
                break;
            case SettingDesigner.OutputType.Gameplay:
                EditorGUILayout.PropertyField(settingProp.FindPropertyRelative("gameplaySetting"), new GUIContent("Start Value"));
                break;
        }
    }

    private void DrawTabsList()
    {
        if (tabsProp == null)
        {
            EditorGUILayout.HelpBox("Unable to find tabs property.", MessageType.Error);
            return;
        }

        EditorGUILayout.LabelField("Tabs", EditorStyles.boldLabel);

        for (int i = 0; i < tabsProp.arraySize; i++)
        {
            SerializedProperty tabProp = tabsProp.GetArrayElementAtIndex(i);
            SerializedProperty tabNameProp = tabProp.FindPropertyRelative("name");
            SerializedProperty settingsProp = tabProp.FindPropertyRelative("settings");

            string tabLabel = string.IsNullOrEmpty(tabNameProp.stringValue) ? $"Tab {i}" : tabNameProp.stringValue;
            tabProp.isExpanded = EditorGUILayout.Foldout(tabProp.isExpanded, tabLabel, true);
            if (!tabProp.isExpanded)
                continue;

            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(tabNameProp);
            EditorGUILayout.Space();

            for (int j = 0; j < settingsProp.arraySize; j++)
            {
                SerializedProperty settingProp = settingsProp.GetArrayElementAtIndex(j);
                SerializedProperty settingNameProp = settingProp.FindPropertyRelative("name");
                SerializedProperty typeProp = settingProp.FindPropertyRelative("type");

                string settingLabel = string.IsNullOrEmpty(settingNameProp.stringValue) ? $"Setting {j}" : settingNameProp.stringValue;
                settingProp.isExpanded = EditorGUILayout.Foldout(settingProp.isExpanded, settingLabel, true);
                if (settingProp.isExpanded)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(settingNameProp);
                    EditorGUILayout.PropertyField(typeProp);
                    EditorGUILayout.PropertyField(settingProp.FindPropertyRelative("output"));
                    DrawStartValueForOutputType(settingProp);

                    var selectedType = (SettingDesigner.SettingType)typeProp.enumValueIndex;
                    switch (selectedType)
                    {
                        case SettingDesigner.SettingType.Slider:
                            EditorGUILayout.PropertyField(settingProp.FindPropertyRelative("sliderStart"), new GUIContent("Start Value"));
                            EditorGUILayout.PropertyField(settingProp.FindPropertyRelative("sliderMin"), new GUIContent("Min Value"));
                            EditorGUILayout.PropertyField(settingProp.FindPropertyRelative("sliderMax"), new GUIContent("Max Value"));
                            break;
                        case SettingDesigner.SettingType.Dropdown:
                            EditorGUILayout.PropertyField(settingProp.FindPropertyRelative("dropdownOptions"), new GUIContent("Options"), true);
                            EditorGUILayout.PropertyField(settingProp.FindPropertyRelative("dropdownStartIndex"), new GUIContent("Start Index"));
                            break;
                        case SettingDesigner.SettingType.Toggle:
                            EditorGUILayout.PropertyField(settingProp.FindPropertyRelative("toggleStart"), new GUIContent("Start Value"));
                            break;
                        case SettingDesigner.SettingType.Rebind:
                            EditorGUILayout.PropertyField(settingProp.FindPropertyRelative("rebindStart"), new GUIContent("Start Binding"));
                            break;
                    }

                    var outputType = (SettingDesigner.OutputType)settingProp.FindPropertyRelative("output").enumValueIndex;
                    switch (outputType)
                    {
                        case SettingDesigner.OutputType.Audio:
                            EditorGUILayout.PropertyField(settingProp.FindPropertyRelative("audioGroup"), new GUIContent("Audio Group"));
                            break;
                        case SettingDesigner.OutputType.InputAction:
                            EditorGUILayout.PropertyField(settingProp.FindPropertyRelative("inputActionReference"), new GUIContent("Input Action"));
                            break;
                        case SettingDesigner.OutputType.CameraSetting:
                            EditorGUILayout.PropertyField(settingProp.FindPropertyRelative("cameraSetting"), new GUIContent("Camera Setting"));
                            break;
                        case SettingDesigner.OutputType.UI:
                            EditorGUILayout.PropertyField(settingProp.FindPropertyRelative("uiSetting"), new GUIContent("UI Setting"));
                            break;
                        case SettingDesigner.OutputType.Gameplay:
                            EditorGUILayout.PropertyField(settingProp.FindPropertyRelative("gameplaySetting"), new GUIContent("Gameplay Setting"));
                            break;
                    }

                    if (GUILayout.Button("Remove Setting", GUILayout.MaxWidth(120)))
                    {
                        settingsProp.DeleteArrayElementAtIndex(j);
                        break;
                    }
                    EditorGUI.indentLevel--;
                    EditorGUILayout.Space();
                }
            }

            if (GUILayout.Button("Add Setting", GUILayout.MaxWidth(120)))
            {
                settingsProp.arraySize++;
                SerializedProperty newSetting = settingsProp.GetArrayElementAtIndex(settingsProp.arraySize - 1);
                newSetting.FindPropertyRelative("name").stringValue = string.Empty;
                newSetting.FindPropertyRelative("type").enumValueIndex = 0;
                newSetting.FindPropertyRelative("output").enumValueIndex = 0;
                newSetting.FindPropertyRelative("sliderStart").floatValue = 0f;
                newSetting.FindPropertyRelative("sliderMin").floatValue = 0f;
                newSetting.FindPropertyRelative("sliderMax").floatValue = 1f;
                newSetting.FindPropertyRelative("dropdownOptions").ClearArray();
                newSetting.FindPropertyRelative("dropdownStartIndex").intValue = 0;
                newSetting.FindPropertyRelative("toggleStart").floatValue = 0f;
                newSetting.FindPropertyRelative("audioGroup").objectReferenceValue = null;
                newSetting.FindPropertyRelative("inputActionReference").objectReferenceValue = null;
                newSetting.FindPropertyRelative("cameraSetting").stringValue = string.Empty;
                newSetting.FindPropertyRelative("uiSetting").stringValue = string.Empty;
                newSetting.FindPropertyRelative("gameplaySetting").stringValue = string.Empty;
                newSetting.FindPropertyRelative("rebindStart").stringValue = string.Empty;
            }

            if (GUILayout.Button("Remove Tab", GUILayout.MaxWidth(120)))
            {
                tabsProp.DeleteArrayElementAtIndex(i);
                break;
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.Space();
        }

        if (GUILayout.Button("Add Tab", GUILayout.MaxWidth(120)))
        {
            tabsProp.arraySize++;
            SerializedProperty newTab = tabsProp.GetArrayElementAtIndex(tabsProp.arraySize - 1);
            newTab.FindPropertyRelative("name").stringValue = string.Empty;
            newTab.FindPropertyRelative("settings").ClearArray();
        }
    }
}
