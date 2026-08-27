using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.InputSystem;

[CustomEditor(typeof(SettingDesigner))]
public class SettingDesignerEditor : Editor
{
    private const float RowButtonWidth = 62f;
    private const float RowTypeWidth = 62f;
    private const float RowPadding = 4f;

    private SerializedProperty tabsProp;
    private int selectedTab;
    private ReorderableList settingsList;
    private int listTabIndex = -1;
    private System.Action pendingAction;

    private void OnEnable()
    {
        tabsProp = serializedObject.FindProperty("tabs");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        if (tabsProp == null)
        {
            EditorGUILayout.HelpBox("Unable to find tabs property.", MessageType.Error);
            return;
        }

        DrawTabBar();

        if (tabsProp.arraySize > 0)
        {
            selectedTab = Mathf.Clamp(selectedTab, 0, tabsProp.arraySize - 1);
            DrawTab(tabsProp.GetArrayElementAtIndex(selectedTab), selectedTab);
        }
        else
        {
            EditorGUILayout.HelpBox("No tabs yet. Press + to add one.", MessageType.Info);
        }

        serializedObject.ApplyModifiedProperties();

        if (pendingAction != null)
        {
            System.Action action = pendingAction;
            pendingAction = null;
            action();
        }
    }

    private void DrawTabBar()
    {
        EditorGUILayout.BeginHorizontal();

        if (tabsProp.arraySize > 0)
        {
            string[] names = new string[tabsProp.arraySize];
            for (int i = 0; i < tabsProp.arraySize; i++)
            {
                string tabName = tabsProp.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue;
                names[i] = string.IsNullOrWhiteSpace(tabName) ? $"Tab {i + 1}" : tabName;
            }

            selectedTab = GUILayout.Toolbar(Mathf.Clamp(selectedTab, 0, names.Length - 1), names);
        }
        else
        {
            GUILayout.FlexibleSpace();
        }

        if (GUILayout.Button("+", EditorStyles.miniButton, GUILayout.Width(24)))
        {
            AddTab();
            selectedTab = tabsProp.arraySize - 1;
        }

        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space(6);
    }

    private void DrawTab(SerializedProperty tabProp, int tabIndex)
    {
        SerializedProperty settingsProp = tabProp.FindPropertyRelative("settings");

        EditorGUILayout.PropertyField(tabProp.FindPropertyRelative("name"), new GUIContent("Tab Name"));

        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();

        using (new EditorGUI.DisabledScope(tabIndex <= 0))
        {
            if (GUILayout.Button("Move Left", EditorStyles.miniButton, GUILayout.Width(70)))
                Defer(() => MoveTab(tabIndex, tabIndex - 1));
        }

        using (new EditorGUI.DisabledScope(tabIndex >= tabsProp.arraySize - 1))
        {
            if (GUILayout.Button("Move Right", EditorStyles.miniButton, GUILayout.Width(76)))
                Defer(() => MoveTab(tabIndex, tabIndex + 1));
        }

        if (GUILayout.Button("Duplicate", EditorStyles.miniButton, GUILayout.Width(RowButtonWidth)))
            Defer(() => DuplicateTab(tabIndex));

        if (GUILayout.Button("Delete", EditorStyles.miniButton, GUILayout.Width(RowButtonWidth)))
            Defer(() => DeleteTab(tabIndex));

        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space(8);

        GetSettingsList(settingsProp, tabIndex).DoLayoutList();

        EditorGUILayout.Space(4);
        DrawSelectedSetting(settingsProp);
    }

    private ReorderableList GetSettingsList(SerializedProperty settingsProp, int tabIndex)
    {
        if (settingsList != null && listTabIndex == tabIndex)
            return settingsList;

        listTabIndex = tabIndex;
        settingsList = new ReorderableList(serializedObject, settingsProp, true, true, true, false)
        {
            elementHeight = EditorGUIUtility.singleLineHeight + 6f
        };

        settingsList.drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Settings");
        settingsList.drawElementCallback = DrawSettingRow;
        settingsList.onAddCallback = list =>
        {
            AddSetting(list.serializedProperty);
            list.index = list.serializedProperty.arraySize - 1;
        };

        return settingsList;
    }

    private void DrawSettingRow(Rect rect, int index, bool isActive, bool isFocused)
    {
        SerializedProperty settingsProp = settingsList.serializedProperty;
        if (index < 0 || index >= settingsProp.arraySize)
            return;

        SerializedProperty settingProp = settingsProp.GetArrayElementAtIndex(index);
        SerializedProperty nameProp = settingProp.FindPropertyRelative("name");
        SerializedProperty typeProp = settingProp.FindPropertyRelative("type");

        rect.y += 3f;
        rect.height = EditorGUIUtility.singleLineHeight;

        float labelWidth = rect.width - RowTypeWidth - (RowButtonWidth * 2f) - (RowPadding * 3f);
        Rect nameRect = new Rect(rect.x, rect.y, Mathf.Max(40f, labelWidth), rect.height);
        Rect typeRect = new Rect(nameRect.xMax + RowPadding, rect.y, RowTypeWidth, rect.height);
        Rect duplicateRect = new Rect(typeRect.xMax + RowPadding, rect.y, RowButtonWidth, rect.height);
        Rect deleteRect = new Rect(duplicateRect.xMax + RowPadding, rect.y, RowButtonWidth, rect.height);

        string title = string.IsNullOrWhiteSpace(nameProp.stringValue) ? $"Setting {index + 1}" : nameProp.stringValue;
        EditorGUI.LabelField(nameRect, title);
        EditorGUI.LabelField(typeRect, typeProp.enumDisplayNames[typeProp.enumValueIndex], EditorStyles.miniLabel);

        int capturedIndex = index;

        if (GUI.Button(duplicateRect, "Duplicate", EditorStyles.miniButton))
            Defer(() => DuplicateSetting(listTabIndex, capturedIndex));

        if (GUI.Button(deleteRect, "Delete", EditorStyles.miniButton))
            Defer(() => DeleteSetting(listTabIndex, capturedIndex));
    }

    private void DrawSelectedSetting(SerializedProperty settingsProp)
    {
        if (settingsProp.arraySize == 0)
        {
            EditorGUILayout.LabelField("No settings in this tab yet.", EditorStyles.centeredGreyMiniLabel);
            return;
        }

        int index = settingsList.index;
        if (index < 0 || index >= settingsProp.arraySize)
        {
            EditorGUILayout.LabelField("Select a setting to edit it.", EditorStyles.centeredGreyMiniLabel);
            return;
        }

        SerializedProperty settingProp = settingsProp.GetArrayElementAtIndex(index);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.PropertyField(settingProp.FindPropertyRelative("name"), new GUIContent("Name"));
        EditorGUILayout.PropertyField(settingProp.FindPropertyRelative("type"), new GUIContent("Type"));
        DrawTypeSpecificFields(settingProp);

        EditorGUILayout.Space(6);
        EditorGUILayout.PropertyField(settingProp.FindPropertyRelative("output"), new GUIContent("Output"));
        DrawOutputFields(settingProp);
        EditorGUILayout.EndVertical();
    }

    private void Defer(System.Action action)
    {
        pendingAction = action;
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
                DrawActionPicker(settingProp, "inputActionAsset", "inputActionMapName", "inputActionName", "rebindStartBindingIndex");
                break;
        }
    }

    private static void DrawActionPicker(SerializedProperty settingProp, string assetField, string mapField, string actionField, string bindingField)
    {
        SerializedProperty assetProp = settingProp.FindPropertyRelative(assetField);
        SerializedProperty mapNameProp = settingProp.FindPropertyRelative(mapField);
        SerializedProperty actionNameProp = settingProp.FindPropertyRelative(actionField);
        SerializedProperty bindingIndexProp = settingProp.FindPropertyRelative(bindingField);

        EditorGUILayout.PropertyField(assetProp, new GUIContent("Action Asset"));

        InputActionAsset actionAsset = assetProp.objectReferenceValue as InputActionAsset;
        if (actionAsset == null)
            return;

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
            if (mapNames[i] == mapNameProp.stringValue)
                mapIndex = i;
        }

        mapIndex = EditorGUILayout.Popup(new GUIContent("Action Map"), mapIndex, mapNames);
        mapNameProp.stringValue = mapNames[mapIndex];

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
            if (actionNames[i] == actionNameProp.stringValue)
                actionIndex = i;
        }

        actionIndex = EditorGUILayout.Popup(new GUIContent("Action"), actionIndex, actionNames);
        actionNameProp.stringValue = actionNames[actionIndex];

        DrawBindingIndexPopup(actionMap.actions[actionIndex], bindingIndexProp, "Binding");
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

        switch (outputType)
        {
            case SettingDesigner.OutputType.Audio:
                EditorGUILayout.PropertyField(settingProp.FindPropertyRelative("audioGroup"), new GUIContent("Audio Group"));
                break;
            case SettingDesigner.OutputType.InputAction:
                DrawActionPicker(settingProp, "outputInputActionAsset", "outputInputActionMapName", "outputInputActionName", "outputInputActionBindingIndex");
                break;
            case SettingDesigner.OutputType.Key:
                EditorGUILayout.PropertyField(settingProp.FindPropertyRelative("key"), new GUIContent("Key"));
                break;
        }
    }

    private SerializedProperty SettingsOf(int tabIndex)
    {
        return tabsProp.GetArrayElementAtIndex(tabIndex).FindPropertyRelative("settings");
    }

    private void DuplicateTab(int tabIndex)
    {
        serializedObject.Update();
        tabsProp.InsertArrayElementAtIndex(tabIndex);
        AppendCopySuffix(tabsProp.GetArrayElementAtIndex(tabIndex + 1));
        serializedObject.ApplyModifiedProperties();
        selectedTab = tabIndex + 1;
        InvalidateSettingsList();
        Repaint();
    }

    private void MoveTab(int from, int to)
    {
        serializedObject.Update();
        tabsProp.MoveArrayElement(from, to);
        serializedObject.ApplyModifiedProperties();
        selectedTab = to;
        InvalidateSettingsList();
        Repaint();
    }

    private void DeleteTab(int tabIndex)
    {
        serializedObject.Update();
        tabsProp.DeleteArrayElementAtIndex(tabIndex);
        serializedObject.ApplyModifiedProperties();
        selectedTab = Mathf.Clamp(selectedTab, 0, Mathf.Max(0, tabsProp.arraySize - 1));
        InvalidateSettingsList();
        Repaint();
    }

    private void DuplicateSetting(int tabIndex, int index)
    {
        serializedObject.Update();
        SerializedProperty settingsProp = SettingsOf(tabIndex);
        settingsProp.InsertArrayElementAtIndex(index);
        AppendCopySuffix(settingsProp.GetArrayElementAtIndex(index + 1));
        serializedObject.ApplyModifiedProperties();

        if (settingsList != null)
            settingsList.index = index + 1;

        Repaint();
    }

    private void DeleteSetting(int tabIndex, int index)
    {
        serializedObject.Update();
        SerializedProperty settingsProp = SettingsOf(tabIndex);
        settingsProp.DeleteArrayElementAtIndex(index);
        serializedObject.ApplyModifiedProperties();

        if (settingsList != null)
            settingsList.index = Mathf.Clamp(settingsList.index, -1, settingsProp.arraySize - 1);

        Repaint();
    }

    private void InvalidateSettingsList()
    {
        settingsList = null;
        listTabIndex = -1;
    }

    private static void AppendCopySuffix(SerializedProperty element)
    {
        SerializedProperty nameProp = element.FindPropertyRelative("name");
        nameProp.stringValue = $"{nameProp.stringValue} Copy";
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
        newSetting.FindPropertyRelative("key").stringValue = string.Empty;
        newSetting.isExpanded = true;
    }
}
