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
        public string cameraSetting = string.Empty;
        public string uiSetting = string.Empty;
        public string gameplaySetting = string.Empty;
    }
}