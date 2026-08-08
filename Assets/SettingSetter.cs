using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class SettingSetter : MonoBehaviour
{
    [Serializable]
    public class Link
    {
        public Component target;
        public string variableName;
        public string settingKey;
    }

    private const BindingFlags MemberFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    public SettingDesignerApplier applier;
    public List<Link> links = new List<Link>();

    void Start()
    {
        Apply();
    }

    /// <summary>
    /// Hooks every link up to its setting and pushes the current value across.
    /// Call again if the settings UI is rebuilt at runtime.
    /// </summary>
    public void Apply()
    {
        foreach (var link in links)
            Connect(link);
    }

    private void Connect(Link link)
    {
        if (link == null || link.target == null || string.IsNullOrEmpty(link.variableName) || string.IsNullOrEmpty(link.settingKey))
            return;

        Transform control = FindSetting(link.settingKey);
        if (control == null)
        {
            Debug.LogWarning($"SettingSetter: no setting named '{link.settingKey}'.", this);
            return;
        }

        var slider = control.GetComponent<Slider>();
        if (slider != null)
        {
            slider.onValueChanged.AddListener(value => Write(link, value));
            Write(link, slider.value);
            return;
        }

        var toggle = control.GetComponent<Toggle>();
        if (toggle != null)
        {
            toggle.onValueChanged.AddListener(value => Write(link, value));
            Write(link, toggle.isOn);
            return;
        }

        var tmpDropdown = control.GetComponent<TMP_Dropdown>();
        if (tmpDropdown != null)
        {
            tmpDropdown.onValueChanged.AddListener(index => Write(link, index, tmpDropdown.options[index].text));
            Write(link, tmpDropdown.value, tmpDropdown.options[tmpDropdown.value].text);
            return;
        }

        var dropdown = control.GetComponent<Dropdown>();
        if (dropdown != null)
        {
            dropdown.onValueChanged.AddListener(index => Write(link, index, dropdown.options[index].text));
            Write(link, dropdown.value, dropdown.options[dropdown.value].text);
            return;
        }

        Debug.LogWarning($"SettingSetter: setting '{link.settingKey}' has no readable control.", this);
    }

    /// <summary>
    /// Dropdowns write the option text into string members and the index into everything else.
    /// </summary>
    private void Write(Link link, int index, string text)
    {
        SplitPath(link.variableName, out string memberName, out string axis);
        Type type = axis == null ? MemberType(FindMember(link.target, memberName)) : typeof(float);

        Write(link, type == typeof(string) ? text : (object)index);
    }

    private void Write(Link link, object value)
    {
        SplitPath(link.variableName, out string memberName, out string axis);

        MemberInfo member = FindMember(link.target, memberName);
        if (member == null)
        {
            Debug.LogWarning($"SettingSetter: '{link.target.GetType().Name}' has no member '{memberName}'.", this);
            return;
        }

        Type type = MemberType(member);
        object converted;

        if (axis != null)
        {
            converted = WithAxis(ReadMember(member, link.target), type, axis, Convert.ToSingle(value));
            if (converted == null)
            {
                Debug.LogWarning($"SettingSetter: '{link.variableName}' is not a vector component.", this);
                return;
            }
        }
        else
        {
            try
            {
                converted = type.IsEnum
                    ? Enum.ToObject(type, Convert.ToInt32(value))
                    : Convert.ChangeType(value, type);
            }
            catch (Exception)
            {
                Debug.LogWarning($"SettingSetter: cannot write {value} into '{link.variableName}' ({type.Name}).", this);
                return;
            }
        }

        if (member is FieldInfo field)
            field.SetValue(link.target, converted);
        else
            ((PropertyInfo)member).SetValue(link.target, converted);
    }

    /// <summary>
    /// Splits "speed.x" into the member name and the axis, or leaves the axis null.
    /// </summary>
    private static void SplitPath(string path, out string memberName, out string axis)
    {
        int dot = path.IndexOf('.');

        if (dot < 0)
        {
            memberName = path;
            axis = null;
            return;
        }

        memberName = path.Substring(0, dot);
        axis = path.Substring(dot + 1);
    }

    /// <summary>
    /// Returns the vector with one component replaced, or null if the member is not a vector.
    /// </summary>
    private static object WithAxis(object current, Type type, string axis, float number)
    {
        int component = "xyzw".IndexOf(axis, StringComparison.Ordinal);
        if (component < 0 || axis.Length != 1)
            return null;

        if (type == typeof(Vector2) && component < 2)
        {
            Vector2 vector = (Vector2)current;
            vector[component] = number;
            return vector;
        }

        if (type == typeof(Vector3) && component < 3)
        {
            Vector3 vector = (Vector3)current;
            vector[component] = number;
            return vector;
        }

        if (type == typeof(Vector4))
        {
            Vector4 vector = (Vector4)current;
            vector[component] = number;
            return vector;
        }

        return null;
    }

    private static object ReadMember(MemberInfo member, object owner)
    {
        return member is FieldInfo field
            ? field.GetValue(owner)
            : ((PropertyInfo)member).GetValue(owner);
    }

    private static MemberInfo FindMember(Component target, string memberName)
    {
        Type type = target.GetType();

        MemberInfo member = type.GetField(memberName, MemberFlags);
        if (member != null)
            return member;

        PropertyInfo property = type.GetProperty(memberName, MemberFlags);
        return property != null && property.CanWrite ? property : null;
    }

    private static Type MemberType(MemberInfo member)
    {
        if (member is FieldInfo field)
            return field.FieldType;

        return member is PropertyInfo property ? property.PropertyType : null;
    }

    /// <summary>
    /// Locates the generated control for a designer key.
    /// </summary>
    private Transform FindSetting(string key)
    {
        SettingDesigner designer = applier != null ? applier.designer : null;
        Transform root = applier != null ? applier.setting_content : null;

        if (designer == null || root == null)
            return null;

        SettingDesigner.Setting setting = designer.FindByKey(key);
        if (setting == null || string.IsNullOrEmpty(setting.name))
            return null;

        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            if (child.name == setting.name)
                return child;
        }

        return null;
    }
}
