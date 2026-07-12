using UnityEngine;
using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine.Audio;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
#endif

public class SettingDesigner : MonoBehaviour
{
    public GameObject slider_prefab;
    public GameObject dropdown_prefab;
    public GameObject toggle_prefab;
    public GameObject rebind_prefab;
    public GameObject tab_prefab;
    public GameObject empty_prefab;
    public Transform tab_content;
    public Transform setting_content;
    public GameObject label_prefab;
    public Vector2 label_offset = new Vector2(0, 0);
    public List<Tab> tabs = new List<Tab>();
    private List<Transform> tab_empties = new List<Transform>();

    [System.Serializable]
    public class Tab
    {
        public string name;
        public List<Setting> settings = new List<Setting>();
    }

    public enum SettingType
    {
        Slider,
        Dropdown,
        Toggle,
        Rebind
    }

    public enum OutputType
    {
        Audio,
        InputAction,
        CameraSetting,
        UI,
        Gameplay
    }

    [System.Serializable]
    public class Setting
    {
        public string name;
        public SettingType type;
        public OutputType output;
        public object startValue; // Generic initial value for the selected output

        // Slider fields
        public float sliderMin = 0f;
        public float sliderMax = 1f;

        // Dropdown fields
        public List<string> dropdownOptions = new List<string>();

        // Toggle fields
        public float toggleStart = 0f;

        // Output-specific target fields
        public AudioMixerGroup audioGroup;
        public InputActionReference inputActionReference;
        public string cameraSetting = string.Empty;
        public string uiSetting = string.Empty;
        public string gameplaySetting = string.Empty;

        // Rebind fields

    }

    public void CreateSettings()
    {
        tab_empties.Clear();
        ClearChildren(tab_content);
        ClearChildren(setting_content);

        for (int i = 0; i < tabs.Count; i++)
        {
            Tab tab = tabs[i];
            Transform tabInstance = Instantiate(tab_prefab, tab_content).transform;
            var textComponent = tabInstance.GetComponentInChildren<TMP_Text>();
            if (textComponent != null)
            {
                textComponent.text = tab.name;
            }

            GameObject tabEmpty = Instantiate(empty_prefab, setting_content);
            tabEmpty.name = tab.name;
            tab_empties.Add(tabEmpty.transform);
            tabEmpty.SetActive(i == 0); // Activate only the first tab's content by default

            var button = tabInstance.GetComponent<Button>();
            if (button != null)
            {
#if UNITY_EDITOR
                UnityEventTools.AddIntPersistentListener(button.onClick, ActivateTab, i);
                EditorUtility.SetDirty(button.gameObject);
                EditorSceneManager.MarkSceneDirty(button.gameObject.scene);
#else
                button.onClick.AddListener(() => ActivateTab(i));
#endif
            }
        }

        for (int i = 0; i < tabs.Count; i++)
        {
            Tab tab = tabs[i];
            Transform targetParent = tab_empties.Count > i ? tab_empties[i] : setting_content;

            foreach (Setting setting in tab.settings)
            {
                GameObject prefab = null;
                switch (setting.type)
                {
                    case SettingType.Slider:
                        prefab = slider_prefab;
                        break;
                    case SettingType.Dropdown:
                        prefab = dropdown_prefab;
                        break;
                    case SettingType.Toggle:
                        prefab = toggle_prefab;
                        break;
                    case SettingType.Rebind:
                        prefab = rebind_prefab;
                        break;
                }

                if (prefab == null)
                    continue;

                GameObject instance = Instantiate(prefab, targetParent);
                instance.name = string.IsNullOrEmpty(setting.name) ? setting.type.ToString() : setting.name;

                GameObject labelInstance = Instantiate(label_prefab, instance.transform);
                labelInstance.name = setting.name + "_Label";
                labelInstance.GetComponent<TMP_Text>().text = setting.name;
                labelInstance.transform.position += (Vector3)label_offset;

                object targetComponent = instance?.GetComponent<Slider>()
                    ?? instance?.GetComponent<Dropdown>()
                    ?? instance?.GetComponent<Toggle>()
                    ?? instance?.GetComponent<Bind>();

                if (targetComponent != null)
                {
                    SetVariable(targetComponent, "value", setting.startValue);
                }
                
            }
        }
        LayoutRebuilder.ForceRebuildLayoutImmediate(setting_content.GetComponent<RectTransform>());
    }

    public void ActivateTab(int index)
    {
        for (int i = 0; i < setting_content.childCount; i++)
        {
            setting_content.GetChild(i).gameObject.SetActive(i == index);
        }
    }

    private object GetVariable(object target, string variableName)
    {
        if (target == null || string.IsNullOrEmpty(variableName))
            return null;

        Type targetType = target.GetType();

        FieldInfo field = targetType.GetField(variableName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null)
            return field.GetValue(target);

        PropertyInfo property = targetType.GetProperty(variableName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null && property.GetIndexParameters().Length == 0 && property.CanRead)
            return property.GetValue(target, null);

        return null;
    }

    private void SetVariable(object target, string variableName, object value)
    {
        if (target == null || string.IsNullOrEmpty(variableName))
            return;

        Type targetType = target.GetType();

        FieldInfo field = targetType.GetField(variableName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null && !field.IsInitOnly)
        {
            if (value == null || field.FieldType.IsAssignableFrom(value.GetType()))
            {
                field.SetValue(target, value);
            }
            else
            {
                try
                {
                    field.SetValue(target, Convert.ChangeType(value, field.FieldType));
                }
                catch (Exception)
                {
                    // Ignore incompatible values.
                }
            }
            return;
        }

        PropertyInfo property = targetType.GetProperty(variableName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null && property.GetIndexParameters().Length == 0 && property.CanWrite)
        {
            if (value == null || property.PropertyType.IsAssignableFrom(value.GetType()))
            {
                property.SetValue(target, value, null);
            }
            else
            {
                try
                {
                    property.SetValue(target, Convert.ChangeType(value, property.PropertyType), null);
                }
                catch (Exception)
                {
                    // Ignore incompatible values.
                }
            }
        }
    }

    private void ClearChildren(Transform parent)
    {
        if (parent == null)
            return;

        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            DestroyImmediate(parent.GetChild(i).gameObject);
        }
    }
}