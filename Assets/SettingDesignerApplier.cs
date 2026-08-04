using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
#endif

public class SettingDesignerApplier : MonoBehaviour
{
    public SettingDesigner designer;
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

    private readonly List<Transform> tab_empties = new List<Transform>();

    public void CreateSettings()
    {
        if (designer == null)
        {
            Debug.LogWarning("SettingDesignerApplier: No designer asset assigned.");
            return;
        }

        if (tab_content == null || setting_content == null)
        {
            Debug.LogWarning("SettingDesignerApplier: Content transforms are not assigned.");
            return;
        }

        tab_empties.Clear();
        ClearChildren(tab_content);
        ClearChildren(setting_content);

        for (int i = 0; i < designer.tabs.Count; i++)
        {
            SettingDesigner.Tab tab = designer.tabs[i];
            Transform tabInstance = Instantiate(tab_prefab, tab_content).transform;
            var textComponent = tabInstance.GetComponentInChildren<TMP_Text>();
            if (textComponent != null)
            {
                textComponent.text = tab.name;
            }

            GameObject tabEmpty = Instantiate(empty_prefab, setting_content);
            tabEmpty.name = tab.name;
            tab_empties.Add(tabEmpty.transform);
            tabEmpty.SetActive(i == 0);

            var button = tabInstance.GetComponent<Button>();
            if (button != null)
            {
#if UNITY_EDITOR
                UnityEventTools.AddIntPersistentListener(button.onClick, ActivateTab, i);
                EditorUtility.SetDirty(button.gameObject);
                EditorSceneManager.MarkSceneDirty(button.gameObject.scene);
#else
                int capturedIndex = i;
                button.onClick.AddListener(() => ActivateTab(capturedIndex));
#endif
            }
        }

        for (int i = 0; i < designer.tabs.Count; i++)
        {
            SettingDesigner.Tab tab = designer.tabs[i];
            Transform targetParent = tab_empties.Count > i ? tab_empties[i] : setting_content;

            foreach (SettingDesigner.Setting setting in tab.settings)
            {
                GameObject prefab = null;
                switch (setting.type)
                {
                    case SettingDesigner.SettingType.Slider:
                        prefab = slider_prefab;
                        break;
                    case SettingDesigner.SettingType.Dropdown:
                        prefab = dropdown_prefab;
                        break;
                    case SettingDesigner.SettingType.Toggle:
                        prefab = toggle_prefab;
                        break;
                    case SettingDesigner.SettingType.Rebind:
                        prefab = rebind_prefab;
                        break;
                }

                if (prefab == null)
                    continue;

                GameObject instance = Instantiate(prefab, targetParent);
                instance.name = string.IsNullOrEmpty(setting.name) ? setting.type.ToString() : setting.name;

                if (label_prefab != null)
                {
                    GameObject labelInstance = Instantiate(label_prefab, instance.transform);
                    labelInstance.name = setting.name + "_Label";
                    var label = labelInstance.GetComponent<TMP_Text>();
                    if (label != null)
                        label.text = setting.name;
                    labelInstance.transform.position += (Vector3)label_offset;
                }

                ApplyStartValues(instance, setting);
                ApplyRebindSeed(instance, setting);
            }
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(setting_content.GetComponent<RectTransform>());
    }

    private static void ApplyStartValues(GameObject instance, SettingDesigner.Setting setting)
    {
        Slider slider = instance.GetComponent<Slider>();
        if (slider != null)
            slider.value = setting.sliderStart;

        Dropdown dropdown = instance.GetComponent<Dropdown>();
        if (dropdown != null)
            dropdown.value = setting.dropdownStartIndex;

        TMP_Dropdown tmpDropdown = instance.GetComponent<TMP_Dropdown>();
        if (tmpDropdown != null)
            tmpDropdown.value = setting.dropdownStartIndex;

        Toggle toggle = instance.GetComponent<Toggle>();
        if (toggle != null)
            toggle.isOn = setting.toggleStart;
    }

    private static void ApplyRebindSeed(GameObject instance, SettingDesigner.Setting setting)
    {
        if (setting.type != SettingDesigner.SettingType.Rebind)
            return;

        if (setting.inputActionAsset == null || string.IsNullOrWhiteSpace(setting.inputActionMapName) || string.IsNullOrWhiteSpace(setting.inputActionName))
            return;

        Bind bind = instance.GetComponent<Bind>();
        if (bind != null)
        {
            var actionMap = setting.inputActionAsset.FindActionMap(setting.inputActionMapName, false);
            if (actionMap == null)
                return;

            var action = actionMap.FindAction(setting.inputActionName, false);
            if (action == null)
                return;

            int bindingIndex = setting.rebindStartBindingIndex;

            if (bindingIndex < 0 || bindingIndex >= action.bindings.Count)
                return;

            var binding = action.bindings[bindingIndex];
            if (binding.isComposite)
                return;

            bind.value = action;
            bind.bindingIndex = bindingIndex;
            bind.ApplySeedBinding(binding.effectivePath);
        }
    }

    public void ActivateTab(int index)
    {
        for (int i = 0; i < setting_content.childCount; i++)
        {
            setting_content.GetChild(i).gameObject.SetActive(i == index);
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