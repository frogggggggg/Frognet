using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.InputSystem;

[CreateAssetMenu(menuName = "Frognet/Setting Designer", fileName = "SettingDesigner")]
public class SettingDesigner : ScriptableObject
{
    public List<Tab> tabs = new List<Tab>();

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
        Key
    }

    /// <summary>
    /// Every key defined across all tabs, in tab order, without duplicates.
    /// </summary>
    public List<string> GetKeys()
    {
        var keys = new List<string>();

        foreach (var tab in tabs)
        {
            foreach (var setting in tab.settings)
            {
                if (setting.output == OutputType.Key && !string.IsNullOrEmpty(setting.key) && !keys.Contains(setting.key))
                    keys.Add(setting.key);
            }
        }

        return keys;
    }

    public Setting FindByKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        foreach (var tab in tabs)
        {
            foreach (var setting in tab.settings)
            {
                if (setting.output == OutputType.Key && setting.key == key)
                    return setting;
            }
        }

        return null;
    }

    [System.Serializable]
    public class Setting
    {
        public string name;
        public SettingType type;
        public OutputType output;

        // Slider fields
        public float sliderStart = 0f;
        public float sliderMin = 0f;
        public float sliderMax = 1f;

        // Dropdown fields
        public List<string> dropdownOptions = new List<string>();
        public int dropdownStartIndex = 0;

        // Toggle fields
        public bool toggleStart = false;
        public int rebindStartBindingIndex = 0;

        // Output-specific target fields
        public AudioMixerGroup audioGroup;
        public InputActionAsset outputInputActionAsset;
        public string outputInputActionMapName = string.Empty;
        public string outputInputActionName = string.Empty;
        public int outputInputActionBindingIndex = 0;
        public InputActionAsset inputActionAsset;
        public string inputActionMapName = string.Empty;
        public string inputActionName = string.Empty;
        public string key = string.Empty;
    }
}