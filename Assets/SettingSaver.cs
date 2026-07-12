using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

[Serializable]
public class SettingSaver : MonoBehaviour
{
    public string saveFileName = "setting_values.json";
    public SettingDesigner settingDesigner;

    [Serializable]
    public class BindSaveData
    {
        public string gameObjectName;
        public string actionName;
        public string bindingPath;
        public string bindingDisplay;
    }

    [Serializable]
    public class SettingFileData
    {
        public List<BindSaveData> bindData = new List<BindSaveData>();
    }

    public void ApplyPendingBindings()
    {
        foreach (var bind in FindObjectsOfType<Bind>())
        {
            if (!string.IsNullOrEmpty(bind.pendingBindingPath))
            {
                bind.CommitPendingBinding();
            }
        }
    }

    public void SaveBindingsToFile(string relativePath = null)
    {
        var path = string.IsNullOrEmpty(relativePath)
            ? Path.Combine(Application.persistentDataPath, saveFileName)
            : relativePath;

        var fileData = new SettingFileData();
        foreach (var bind in FindObjectsOfType<Bind>())
        {
            if (bind.Value == null)
                continue;

            fileData.bindData.Add(new BindSaveData
            {
                gameObjectName = bind.gameObject.name,
                actionName = bind.Value.name,
                bindingPath = bind.pendingBindingPath,
                bindingDisplay = bind.pendingBindingDisplay
            });
        }

        File.WriteAllText(path, JsonUtility.ToJson(fileData, true));
        Debug.Log($"Saved binding settings to {path}");
    }

    public void LoadBindingsFromFile(string relativePath = null)
    {
        var path = string.IsNullOrEmpty(relativePath)
            ? Path.Combine(Application.persistentDataPath, saveFileName)
            : relativePath;

        if (!File.Exists(path))
        {
            Debug.LogWarning($"SettingSaver: File not found at {path}");
            return;
        }

        var fileText = File.ReadAllText(path);
        var fileData = JsonUtility.FromJson<SettingFileData>(fileText);
        if (fileData == null || fileData.bindData == null)
            return;

        foreach (var bindEntry in fileData.bindData)
        {
            var bind = FindBindByActionName(bindEntry.actionName);
            if (bind == null)
                continue;

            bind.pendingBindingPath = bindEntry.bindingPath;
            bind.pendingBindingDisplay = bindEntry.bindingDisplay;
        }
    }

    private Bind FindBindByActionName(string actionName)
    {
        foreach (var bind in FindObjectsOfType<Bind>())
        {
            if (bind.Value != null && bind.Value.name == actionName)
                return bind;
        }

        return null;
    }
}
