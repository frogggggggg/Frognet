using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

[CustomEditor(typeof(SettingDesigner))]
public class SettingDesignerEditor : Editor
{
    private SerializedProperty tabsProp;
    private GUIStyle headerStyle;
    private GUIStyle sectionStyle;

    private void OnEnable()
    {
        tabsProp = serializedObject.FindProperty("tabs");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        EnsureStyles();

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField("Setting Designer", headerStyle);
        EditorGUILayout.LabelField("Asset-based configuration for tabs and their settings. Use a SettingDesignerApplier in the scene to build the UI.", EditorStyles.wordWrappedMiniLabel);
        EditorGUILayout.EndVertical();

        EditorGUILayout.Space(6);

        DrawTabsSection();

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawTabsSection()
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Tabs", sectionStyle);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Add Tab", EditorStyles.miniButton, GUILayout.Width(80)))
        {
            AddTab();
        }
        EditorGUILayout.EndHorizontal();

        if (tabsProp == null)
        {
            EditorGUILayout.HelpBox("Unable to find tabs property.", MessageType.Error);
            EditorGUILayout.EndVertical();
            return;
        }

        for (int i = 0; i < tabsProp.arraySize; i++)
        {
            DrawTabCard(tabsProp.GetArrayElementAtIndex(i), i);
        }

        EditorGUILayout.EndVertical();
    }

    private void DrawTabCard(SerializedProperty tabProp, int tabIndex)
    {
        SerializedProperty tabNameProp = tabProp.FindPropertyRelative("name");
        SerializedProperty settingsProp = tabProp.FindPropertyRelative("settings");

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();
        tabProp.isExpanded = EditorGUILayout.Foldout(tabProp.isExpanded, string.IsNullOrEmpty(tabNameProp.stringValue) ? $"Tab {tabIndex + 1}" : tabNameProp.stringValue, true);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Remove", EditorStyles.miniButton, GUILayout.Width(70)))
        {
            tabsProp.DeleteArrayElementAtIndex(tabIndex);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
            return;
        }
        EditorGUILayout.EndHorizontal();

        if (tabProp.isExpanded)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(tabNameProp, new GUIContent("Tab Name"));
            EditorGUILayout.Space(4);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Settings", sectionStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Add Setting", EditorStyles.miniButton, GUILayout.Width(90)))
            {
                AddSetting(settingsProp);
            }
            EditorGUILayout.EndHorizontal();

            for (int j = 0; j < settingsProp.arraySize; j++)
            {
                DrawSettingCard(settingsProp.GetArrayElementAtIndex(j), settingsProp, j);
            }

            EditorGUILayout.EndVertical();
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.EndVertical();
    }

    private void DrawSettingCard(SerializedProperty settingProp, SerializedProperty settingsProp, int settingIndex)
    {
        SerializedProperty settingNameProp = settingProp.FindPropertyRelative("name");
        SerializedProperty typeProp = settingProp.FindPropertyRelative("type");
        SerializedProperty outputProp = settingProp.FindPropertyRelative("output");

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();
        settingProp.isExpanded = EditorGUILayout.Foldout(settingProp.isExpanded, string.IsNullOrEmpty(settingNameProp.stringValue) ? $"Setting {settingIndex + 1}" : settingNameProp.stringValue, true);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Remove", EditorStyles.miniButton, GUILayout.Width(70)))
        {
            settingsProp.DeleteArrayElementAtIndex(settingIndex);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
            return;
        }
        EditorGUILayout.EndHorizontal();

        if (settingProp.isExpanded)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(settingNameProp, new GUIContent("Setting Name"));
            EditorGUILayout.PropertyField(typeProp);
            EditorGUILayout.PropertyField(outputProp);
            EditorGUILayout.Space(4);
            DrawTypeSpecificFields(settingProp);
            DrawOutputFields(settingProp);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.EndVertical();
    }

    private static void DrawTypeSpecificFields(SerializedProperty settingProp)
    {
        var selectedType = (SettingDesigner.SettingType)settingProp.FindPropertyRelative("type").enumValueIndex;

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
                DrawRebindBindingPicker(settingProp);
                break;
        }
    }

    private static void DrawRebindBindingPicker(SerializedProperty settingProp)
    {
        SerializedProperty inputActionAssetProp = settingProp.FindPropertyRelative("inputActionAsset");
        SerializedProperty inputActionMapNameProp = settingProp.FindPropertyRelative("inputActionMapName");
        SerializedProperty inputActionNameProp = settingProp.FindPropertyRelative("inputActionName");
        SerializedProperty bindingIndexProp = settingProp.FindPropertyRelative("rebindStartBindingIndex");

        EditorGUILayout.PropertyField(inputActionAssetProp, new GUIContent("Input Action Asset"));

        InputActionAsset actionAsset = inputActionAssetProp.objectReferenceValue as InputActionAsset;
        if (actionAsset == null)
        {
            EditorGUILayout.HelpBox("Assign an Input Action Asset to choose a map, action, and binding.", MessageType.Info);
            return;
        }

        if (actionAsset.actionMaps.Count == 0)
        {
            EditorGUILayout.HelpBox("This Input Action Asset has no action maps.", MessageType.Warning);
            return;
        }

        string[] mapNames = new string[actionAsset.actionMaps.Count];
        int mapIndex = 0;
        for (int i = 0; i < actionAsset.actionMaps.Count; i++)
        {
            mapNames[i] = actionAsset.actionMaps[i].name;
            if (mapNames[i] == inputActionMapNameProp.stringValue)
                mapIndex = i;
        }

        mapIndex = EditorGUILayout.Popup(new GUIContent("Action Map"), mapIndex, mapNames);
        inputActionMapNameProp.stringValue = mapNames[mapIndex];

        var actionMap = actionAsset.actionMaps[mapIndex];
        if (actionMap.actions.Count == 0)
        {
            EditorGUILayout.HelpBox("The selected action map has no actions.", MessageType.Warning);
            return;
        }

        string[] actionNames = new string[actionMap.actions.Count];
        int actionIndex = 0;
        for (int i = 0; i < actionMap.actions.Count; i++)
        {
            actionNames[i] = actionMap.actions[i].name;
            if (actionNames[i] == inputActionNameProp.stringValue)
                actionIndex = i;
        }

        actionIndex = EditorGUILayout.Popup(new GUIContent("Action"), actionIndex, actionNames);
        inputActionNameProp.stringValue = actionNames[actionIndex];

        InputAction action = actionMap.actions[actionIndex];

        if (action == null)
        {
            EditorGUILayout.HelpBox("Assign an action to choose a default binding.", MessageType.Info);
            return;
        }

        DrawBindingIndexPopup(action, bindingIndexProp, "Default Binding");

        EditorGUILayout.HelpBox("Composite parts such as WASD are listed individually, so each direction can be seeded and rebound on its own.", MessageType.Info);
    }

    private static void DrawBindingIndexPopup(InputAction action, SerializedProperty bindingIndexProp, string label)
    {
        List<string> bindingLabels = new List<string>();
        List<int> selectableIndices = new List<int>();
        string compositeName = null;

        for (int i = 0; i < action.bindings.Count; i++)
        {
            var binding = action.bindings[i];

            if (binding.isComposite)
            {
                compositeName = string.IsNullOrWhiteSpace(binding.name) ? binding.path : binding.name;
                continue;
            }

            string entry = DescribeBinding(binding);

            if (binding.isPartOfComposite)
            {
                string partName = string.IsNullOrWhiteSpace(binding.name) ? $"Part {i}" : binding.name;
                string owner = string.IsNullOrWhiteSpace(compositeName) ? "Composite" : compositeName;
                entry = $"{owner}/{partName}: {entry}";
            }

            bindingLabels.Add(entry);
            selectableIndices.Add(i);
        }

        if (bindingLabels.Count == 0)
        {
            EditorGUILayout.HelpBox("This action has no selectable bindings.", MessageType.Warning);
            return;
        }

        int currentListIndex = selectableIndices.IndexOf(bindingIndexProp.intValue);
        if (currentListIndex < 0)
            currentListIndex = 0;

        int nextListIndex = EditorGUILayout.Popup(new GUIContent(label), currentListIndex, bindingLabels.ToArray());
        bindingIndexProp.intValue = selectableIndices[nextListIndex];
    }

    private static string DescribeBinding(InputBinding binding)
    {
        string label = binding.ToDisplayString();
        if (string.IsNullOrWhiteSpace(label))
            label = string.IsNullOrWhiteSpace(binding.path) ? binding.name : binding.path;

        if (!string.IsNullOrWhiteSpace(binding.groups))
        {
            string groups = binding.groups.Replace(InputBinding.Separator.ToString(), ", ");
            label = $"{label} [{groups}]";
        }

        return label;
    }

    private static void DrawOutputFields(SerializedProperty settingProp)
    {
        var outputType = (SettingDesigner.OutputType)settingProp.FindPropertyRelative("output").enumValueIndex;

        EditorGUILayout.Space(2);
        EditorGUILayout.LabelField("Output Target", EditorStyles.miniBoldLabel);

        switch (outputType)
        {
            case SettingDesigner.OutputType.Audio:
                EditorGUILayout.PropertyField(settingProp.FindPropertyRelative("audioGroup"), new GUIContent("Audio Group"));
                break;
            case SettingDesigner.OutputType.InputAction:
                DrawOutputInputActionPicker(settingProp);
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
    }

    private static void DrawOutputInputActionPicker(SerializedProperty settingProp)
    {
        SerializedProperty inputActionAssetProp = settingProp.FindPropertyRelative("outputInputActionAsset");
        SerializedProperty inputActionMapNameProp = settingProp.FindPropertyRelative("outputInputActionMapName");
        SerializedProperty inputActionNameProp = settingProp.FindPropertyRelative("outputInputActionName");
        SerializedProperty bindingIndexProp = settingProp.FindPropertyRelative("outputInputActionBindingIndex");

        EditorGUILayout.PropertyField(inputActionAssetProp, new GUIContent("Input Action Asset"));

        InputActionAsset actionAsset = inputActionAssetProp.objectReferenceValue as InputActionAsset;
        if (actionAsset == null)
        {
            EditorGUILayout.HelpBox("Assign an Input Action Asset to pick an output action.", MessageType.Info);
            return;
        }

        if (actionAsset.actionMaps.Count == 0)
        {
            EditorGUILayout.HelpBox("This Input Action Asset has no action maps.", MessageType.Warning);
            return;
        }

        string[] mapNames = new string[actionAsset.actionMaps.Count];
        int mapIndex = 0;
        for (int i = 0; i < actionAsset.actionMaps.Count; i++)
        {
            mapNames[i] = actionAsset.actionMaps[i].name;
            if (mapNames[i] == inputActionMapNameProp.stringValue)
                mapIndex = i;
        }

        mapIndex = EditorGUILayout.Popup(new GUIContent("Action Map"), mapIndex, mapNames);
        inputActionMapNameProp.stringValue = mapNames[mapIndex];

        var actionMap = actionAsset.actionMaps[mapIndex];
        if (actionMap.actions.Count == 0)
        {
            EditorGUILayout.HelpBox("The selected action map has no actions.", MessageType.Warning);
            return;
        }

        string[] actionNames = new string[actionMap.actions.Count];
        int actionIndex = 0;
        for (int i = 0; i < actionMap.actions.Count; i++)
        {
            actionNames[i] = actionMap.actions[i].name;
            if (actionNames[i] == inputActionNameProp.stringValue)
                actionIndex = i;
        }

        actionIndex = EditorGUILayout.Popup(new GUIContent("Action"), actionIndex, actionNames);
        inputActionNameProp.stringValue = actionNames[actionIndex];

        InputAction outputAction = actionMap.actions[actionIndex];
        if (outputAction == null)
            return;

        DrawBindingIndexPopup(outputAction, bindingIndexProp, "Binding");

        EditorGUILayout.HelpBox("This output writes to the selected binding on that action, matching the binding-level granularity of the rebind input.", MessageType.Info);
    }

    private void AddTab()
    {
        tabsProp.arraySize++;
        SerializedProperty newTab = tabsProp.GetArrayElementAtIndex(tabsProp.arraySize - 1);
        newTab.FindPropertyRelative("name").stringValue = "New Tab";
        newTab.FindPropertyRelative("settings").ClearArray();
        newTab.isExpanded = true;
    }

    private static void AddSetting(SerializedProperty settingsProp)
    {
        settingsProp.arraySize++;
        SerializedProperty newSetting = settingsProp.GetArrayElementAtIndex(settingsProp.arraySize - 1);
        newSetting.FindPropertyRelative("name").stringValue = "New Setting";
        newSetting.FindPropertyRelative("type").enumValueIndex = 0;
        newSetting.FindPropertyRelative("output").enumValueIndex = 0;
        newSetting.FindPropertyRelative("sliderStart").floatValue = 0f;
        newSetting.FindPropertyRelative("sliderMin").floatValue = 0f;
        newSetting.FindPropertyRelative("sliderMax").floatValue = 1f;
        newSetting.FindPropertyRelative("dropdownOptions").ClearArray();
        newSetting.FindPropertyRelative("dropdownStartIndex").intValue = 0;
        newSetting.FindPropertyRelative("toggleStart").boolValue = false;
        newSetting.FindPropertyRelative("rebindStartBindingIndex").intValue = 0;
        newSetting.FindPropertyRelative("audioGroup").objectReferenceValue = null;
        newSetting.FindPropertyRelative("outputInputActionAsset").objectReferenceValue = null;
        newSetting.FindPropertyRelative("outputInputActionMapName").stringValue = string.Empty;
        newSetting.FindPropertyRelative("outputInputActionName").stringValue = string.Empty;
        newSetting.FindPropertyRelative("outputInputActionBindingIndex").intValue = 0;
        newSetting.FindPropertyRelative("inputActionAsset").objectReferenceValue = null;
        newSetting.FindPropertyRelative("inputActionMapName").stringValue = string.Empty;
        newSetting.FindPropertyRelative("inputActionName").stringValue = string.Empty;
        newSetting.FindPropertyRelative("cameraSetting").stringValue = string.Empty;
        newSetting.FindPropertyRelative("uiSetting").stringValue = string.Empty;
        newSetting.FindPropertyRelative("gameplaySetting").stringValue = string.Empty;
        newSetting.isExpanded = true;
    }

    private void EnsureStyles()
    {
        if (headerStyle == null)
        {
            headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13
            };
        }

        if (sectionStyle == null)
        {
            sectionStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11
            };
        }
    }
}
