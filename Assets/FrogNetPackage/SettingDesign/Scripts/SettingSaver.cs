using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class SettingSaver : MonoBehaviour
{
    public string saveFileName = "setting_values.json";
    public SettingDesigner settingDesigner;
    public SettingDesignerApplier settingApplier;

    [Tooltip("Load the saved file when the scene starts.")]
    public bool loadOnStart = true;

    [Min(0f), Tooltip("Seconds of quiet after a change before the file is written. Keeps a slider drag from writing every frame.")]
    public float autoSaveDelay = 0.5f;

    private float saveAt = -1f;

    [Serializable]
    public class BindSaveData
    {
        public string key;
        public string gameObjectName;
        public string actionName;
        public List<string> bindingPaths = new List<string>();
    }

    [Serializable]
    public class ValueSaveData
    {
        public string key;
        public float number;
        public bool flag;
    }

    [Serializable]
    public class SettingFileData
    {
        public List<BindSaveData> bindData = new List<BindSaveData>();
        public List<ValueSaveData> sliderData = new List<ValueSaveData>();
        public List<ValueSaveData> dropdownData = new List<ValueSaveData>();
        public List<ValueSaveData> toggleData = new List<ValueSaveData>();
    }

    private void Start()
    {
        if (loadOnStart)
            LoadSettingsFromFile();

        HookAutoSave();
    }

    private void Update()
    {
        if (saveAt < 0f || Time.unscaledTime < saveAt)
            return;

        saveAt = -1f;
        SaveSettingsToFile();
    }

    /// <summary>A pending write would be lost on quit, so it is flushed here.</summary>
    private void OnApplicationQuit()
    {
        if (saveAt < 0f)
            return;

        saveAt = -1f;
        SaveSettingsToFile();
    }

    /// <summary>Queues a write. Repeated calls inside the delay collapse into one file write.</summary>
    public void RequestSave()
    {
        saveAt = Time.unscaledTime + autoSaveDelay;
    }

    private void HookAutoSave()
    {
        foreach (var entry in CollectKeyed<Slider>())
            entry.Value.onValueChanged.AddListener(_ => RequestSave());

        foreach (var entry in CollectKeyed<Dropdown>())
            entry.Value.onValueChanged.AddListener(_ => RequestSave());

        foreach (var entry in CollectKeyed<TMP_Dropdown>())
            entry.Value.onValueChanged.AddListener(_ => RequestSave());

        foreach (var entry in CollectKeyed<Toggle>())
            entry.Value.onValueChanged.AddListener(_ => RequestSave());

        foreach (var entry in CollectKeyed<Bind>())
            entry.Value.Changed += RequestSave;
    }

    /// <summary>
    /// Puts every control back to the start value the designer asset declares and strips every
    /// rebind. The controls fire their change events, so the write is queued on its own.
    /// </summary>
    public void ResetToDefaults()
    {
        if (settingDesigner == null)
        {
            Debug.LogWarning("SettingSaver: no designer assigned, nothing to reset to.", this);
            return;
        }

        foreach (var entry in CollectKeyed<Slider>())
        {
            var setting = FindSetting(entry.Value.name);

            if (setting != null)
                entry.Value.value = setting.sliderStart;
        }

        foreach (var entry in CollectKeyed<Dropdown>())
        {
            var setting = FindSetting(entry.Value.name);

            if (setting != null)
                entry.Value.value = setting.dropdownStartIndex;
        }

        foreach (var entry in CollectKeyed<TMP_Dropdown>())
        {
            var setting = FindSetting(entry.Value.name);

            if (setting != null)
                entry.Value.value = setting.dropdownStartIndex;
        }

        foreach (var entry in CollectKeyed<Toggle>())
        {
            var setting = FindSetting(entry.Value.name);

            if (setting != null)
                entry.Value.isOn = setting.toggleStart;
        }

        foreach (var entry in CollectKeyed<Bind>())
            entry.Value.ResetToDefault();

        RequestSave();
    }

    /// <summary>The applier names each control instance after its setting, which is the only link back.</summary>
    private SettingDesigner.Setting FindSetting(string controlName)
    {
        foreach (var tab in settingDesigner.tabs)
        {
            foreach (var setting in tab.settings)
            {
                if (setting.name == controlName)
                    return setting;
            }
        }

        return null;
    }

    public void ApplyPendingBindings()
    {
        foreach (var entry in CollectKeyed<Bind>())
        {
            entry.Value.Rebuild();
        }
    }

    public void SaveSettingsToFile(string relativePath = null)
    {
        var path = ResolvePath(relativePath);
        var fileData = new SettingFileData();

        foreach (var entry in CollectKeyed<Slider>())
        {
            fileData.sliderData.Add(new ValueSaveData
            {
                key = entry.Key,
                number = entry.Value.value
            });
        }

        foreach (var entry in CollectKeyed<Dropdown>())
        {
            fileData.dropdownData.Add(new ValueSaveData
            {
                key = entry.Key,
                number = entry.Value.value
            });
        }

        foreach (var entry in CollectKeyed<TMP_Dropdown>())
        {
            fileData.dropdownData.Add(new ValueSaveData
            {
                key = entry.Key,
                number = entry.Value.value
            });
        }

        foreach (var entry in CollectKeyed<Toggle>())
        {
            fileData.toggleData.Add(new ValueSaveData
            {
                key = entry.Key,
                flag = entry.Value.isOn
            });
        }

        foreach (var entry in CollectKeyed<Bind>())
        {
            var bind = entry.Value;
            string actionName = bind.GetActionName();
            if (string.IsNullOrEmpty(actionName))
                continue;

            fileData.bindData.Add(new BindSaveData
            {
                key = entry.Key,
                gameObjectName = bind.gameObject.name,
                actionName = actionName,
                bindingPaths = bind.GetBindingPaths()
            });
        }

        File.WriteAllText(path, JsonUtility.ToJson(fileData, true));
    }

    public void LoadSettingsFromFile(string relativePath = null)
    {
        var path = ResolvePath(relativePath);

        if (!File.Exists(path))
        {
            Debug.LogWarning($"SettingSaver: File not found at {path}");
            return;
        }

        var fileData = JsonUtility.FromJson<SettingFileData>(File.ReadAllText(path));
        if (fileData == null)
            return;

        var sliderValues = ToLookup(fileData.sliderData);
        foreach (var entry in CollectKeyed<Slider>())
        {
            if (sliderValues.TryGetValue(entry.Key, out var saved))
                entry.Value.value = saved.number;
        }

        var dropdownValues = ToLookup(fileData.dropdownData);
        foreach (var entry in CollectKeyed<Dropdown>())
        {
            if (dropdownValues.TryGetValue(entry.Key, out var saved))
                entry.Value.value = Mathf.RoundToInt(saved.number);
        }

        foreach (var entry in CollectKeyed<TMP_Dropdown>())
        {
            if (dropdownValues.TryGetValue(entry.Key, out var saved))
                entry.Value.value = Mathf.RoundToInt(saved.number);
        }

        var toggleValues = ToLookup(fileData.toggleData);
        foreach (var entry in CollectKeyed<Toggle>())
        {
            if (toggleValues.TryGetValue(entry.Key, out var saved))
                entry.Value.isOn = saved.flag;
        }

        if (fileData.bindData == null)
            return;

        var restoredBinds = new HashSet<Bind>();

        foreach (var bindEntry in fileData.bindData)
        {
            var bind = FindBind(bindEntry, restoredBinds);
            if (bind == null)
                continue;

            restoredBinds.Add(bind);
            bind.RestoreBindings(bindEntry.bindingPaths);
        }
    }

    public void SaveBindingsToFile(string relativePath = null)
    {
        SaveSettingsToFile(relativePath);
    }

    public void LoadBindingsFromFile(string relativePath = null)
    {
        LoadSettingsFromFile(relativePath);
    }

    private string ResolvePath(string relativePath)
    {
        return string.IsNullOrEmpty(relativePath)
            ? Path.Combine(Application.persistentDataPath, saveFileName)
            : relativePath;
    }

    private static Dictionary<string, ValueSaveData> ToLookup(List<ValueSaveData> entries)
    {
        var lookup = new Dictionary<string, ValueSaveData>();
        if (entries == null)
            return lookup;

        foreach (var entry in entries)
        {
            if (!string.IsNullOrEmpty(entry.key))
                lookup[entry.key] = entry;
        }

        return lookup;
    }

    private Bind FindBind(BindSaveData bindEntry, HashSet<Bind> alreadyRestored)
    {
        var binds = CollectKeyed<Bind>();

        if (!string.IsNullOrEmpty(bindEntry.key))
        {
            foreach (var entry in binds)
            {
                if (entry.Key == bindEntry.key && !alreadyRestored.Contains(entry.Value))
                    return entry.Value;
            }
        }

        foreach (var entry in binds)
        {
            if (entry.Value.GetActionName() == bindEntry.actionName && !alreadyRestored.Contains(entry.Value))
                return entry.Value;
        }

        return null;
    }

    private Transform GetRoot()
    {
        return settingApplier != null ? settingApplier.setting_content : null;
    }

    private List<KeyValuePair<string, T>> CollectKeyed<T>() where T : Component
    {
        var root = GetRoot();
        var components = root != null
            ? root.GetComponentsInChildren<T>(true)
            : FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        var results = new List<KeyValuePair<string, T>>(components.Length);
        var used = new Dictionary<string, int>();

        foreach (var component in components)
        {
            var key = BuildKey(component.transform, root);
            if (used.TryGetValue(key, out int count))
            {
                used[key] = count + 1;
                key = $"{key}#{count + 1}";
            }
            else
            {
                used[key] = 0;
            }

            results.Add(new KeyValuePair<string, T>(key, component));
        }

        return results;
    }

    private static string BuildKey(Transform target, Transform root)
    {
        var builder = new StringBuilder(target.name);
        var parent = target.parent;

        while (parent != null && parent != root)
        {
            builder.Insert(0, parent.name + "/");
            parent = parent.parent;
        }

        return builder.ToString();
    }
}
