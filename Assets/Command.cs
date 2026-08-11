using UnityEngine;
using TMPro;
using UnityEngine.UI;
using System.Collections.Generic;
using System;

public class Command : MonoBehaviour
{
    public TMP_InputField inputField;
    public GameObject itemPrefab;
    public static Command Instance;


    /// <summary>
    /// A command's signature has to be declared, not discovered. Reflecting over an
    /// Action&lt;object[]&gt; only ever reports one parameter of type object[], because that is the
    /// delegate's own signature — the casts inside the body are invisible to it.
    /// </summary>
    class Definition
    {
        public Type[] parameters;
        public Action<object[]> run;
    }

    Dictionary<string, Definition> actionDictionary;
    Dictionary<string, object> wordDictionary;

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        actionDictionary = new Dictionary<string, Definition>(StringComparer.OrdinalIgnoreCase)
        {
            { "spawn", new Definition {
                parameters = new[] { typeof(GameObject) },
                run = args => Instantiate((GameObject)args[0], transform) } },
            { "move", new Definition {
                parameters = new[] { typeof(Transform), typeof(float) },
                run = args => ((Transform)args[0]).Translate(Vector3.forward * (float)args[1]) } }
            {}
        };
        wordDictionary = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            { "player", transform },
            { "find", args => transform.Find((string)args[0]) }
        };

    }
    public void SubmitCommand(string command)
    {
        try
        {
            RunCommand(command);
        }
        catch (Exception error)
        {
            Debug.LogError(error.Message, this);
        }
    }

    void RunCommand(string command)
    {
        string[] parts = (command ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0) return;

        string commandWord = parts[0];

        Definition function = ProcessCommandWord(commandWord);

        object[] args = new object[function.parameters.Length];

        if(parts.Length-1 > args.Length) throw CommandError("Too many arguments provided for command: " + commandWord);
        if(parts.Length-1 < args.Length) throw CommandError("Not enough arguments provided for command: " + commandWord);

        for(int i = 0; i < args.Length; i++)
        {
            Type expected = function.parameters[i];
            args[i] = WordToObject(parts[i + 1], expected);
            if(args[i] == null) throw CommandError($"Argument {i + 1} is of the wrong type. Expected {expected.Name}, got '{parts[i + 1]}'");
        }

        function.run.Invoke(args);
    }

    Exception CommandError(string message = null)
    {
        return new Exception(message);
    }

    Definition ProcessCommandWord(string word)
    {
        if (actionDictionary.TryGetValue(word, out Definition definition))
        {
            return definition;
        }
        throw CommandError("Unknown command: " + word);
    }

    /// <summary>Returns null when the word cannot become the requested type.</summary>
    object WordToObject(string word, Type targetType)
    {
        if (wordDictionary.TryGetValue(word, out object named))
        {
            return targetType.IsInstanceOfType(named) ? named : null;
        }

        if (targetType == typeof(string)) return word;
        if (targetType == typeof(int)) return int.TryParse(word, out int number) ? number : (object)null;
        if (targetType == typeof(float)) return float.TryParse(word, out float value) ? value : (object)null;
        if (targetType == typeof(bool)) return bool.TryParse(word, out bool flag) ? flag : (object)null;

        return null;
    }

    public GameObject SpawnItem(ItemData data, Vector3 position)
    {
        //do it over network later
        GameObject item = Instantiate(ItemPrefab, position, Quaternion.identity);
        item.GetComponent<Pickup>().Initialize(data);
        return item;
    }

}
