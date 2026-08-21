using UnityEngine;
using TMPro;
using UnityEngine.UI;
using System.Collections.Generic;
using System;
using PurrNet;
using Unity.VisualScripting;
using System.Linq;
using UnityEngine.InputSystem;

public class Command : NetworkBehaviour
{
    public TMP_InputField inputField;
    public ScrollRect suggestions;
    public ScrollRect outputScroll;
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

    void Update()
    {
        if(InputSystem.actions["submit"].IsPressed())
        {
            if(inputField != null)
            {
                if(inputField.isFocused)
                {
                    SubmitCommand(inputField.text);
                    inputField.text = string.Empty;
                    inputField.ActivateInputField();
                }
            }
        }
    }

    void Start()
    {
        actionDictionary = new Dictionary<string, Definition>(StringComparer.OrdinalIgnoreCase)
        {
            { "spawn", new Definition {
                parameters = new[] { typeof(object), typeof(Vector3) },
                run = args => Spawn((object)args[0], (Vector3)args[1]) } },
            { "move", new Definition {
                parameters = new[] { typeof(Transform), typeof(float) },
                run = args => ((Transform)args[0]).Translate(Vector3.forward * (float)args[1]) } }
        };
        Transform playerTransform = PlayerManager.TryGetLocal(out var player) ? player.transform : null;

        wordDictionary = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            { "player", playerTransform },
            { "here", playerTransform ? playerTransform.position : Vector3.zero }
        };
        foreach (ItemData data in ItemData.All)
        {
            wordDictionary[data.itemName] = data;
        }

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

        if(!Permissions.LocalAllows(commandWord))
            throw CommandError("You do not have permission to use: " + commandWord);

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

    public void AutoFill(string command)
    {
        string [] parts = (command ?? string.Empty).Split(new[] { ' ' });
        string currentWord = parts.Length > 0 ? parts[parts.Length - 1] : string.Empty;

        actionDictionary.TryGetValue(parts[0], out Definition function);


        SetSuggestionsPosition();
        ClearOptions(suggestions);
        int count = 0;

        if(function != null && parts.Length > 1) foreach(string key in wordDictionary.Keys)
        {
            if(function.parameters.Length < parts.Length - 1) continue;
            Type expected = function.parameters[parts.Length - 2];
            if(key.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase) && expected.IsInstanceOfType(wordDictionary[key]))
            {
                if(currentWord.Length == key.Length) continue;
                count++;
                AddOption(suggestions, key, currentWord.Length);
            }
        }
        if(function == null) foreach(string key in actionDictionary.Keys)
        {
            if(key.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase))
            {
                if(currentWord.Length == key.Length) continue;
                count++;
                AddOption(suggestions, key, currentWord.Length);
            }
        }

        int childCount = Mathf.Clamp(count, 0, 5);
        float height = (suggestions.content.GetComponent<GridLayoutGroup>().cellSize.y + suggestions.content.GetComponent<GridLayoutGroup>().spacing.y) * childCount;
        suggestions.GetComponent<RectTransform>().sizeDelta = new Vector2(suggestions.GetComponent<RectTransform>().sizeDelta.x, height);
    }

    void AddOption(ScrollRect scroll, string text, int highlightLength = 0)
    {
        GameObject option = Instantiate(scroll.content.GetChild(0).gameObject, scroll.content);
        option.SetActive(true);
        string highlighted = "<color=yellow>" + text.Substring(0, highlightLength) + "</color>" + text.Substring(highlightLength);
        option.GetComponentInChildren<TMP_Text>().text = highlighted;
    }
    void ClearOptions(ScrollRect scroll)
    {
        for (int i = scroll.content.childCount - 1; i >= 1; i--)
        {
            Destroy(scroll.content.GetChild(i).gameObject);
        }
    }

    void SetSuggestionsPosition()
    {
        TMP_Text textComponent = inputField.textComponent;
        textComponent.ForceMeshUpdate(); 

        TMP_TextInfo textInfo = textComponent.textInfo;
        int lastSpaceIndex = inputField.text.LastIndexOf(' ');
        int firstCharOfLastWordIndex = Mathf.Clamp(lastSpaceIndex, 0, int.MaxValue);
        
        TMP_CharacterInfo charInfo = textInfo.characterInfo[firstCharOfLastWordIndex];
        Vector3 localEndPoint = charInfo.bottomRight;
        Vector3 worldEndPoint = textComponent.transform.TransformPoint(localEndPoint);
        worldEndPoint.y = textComponent.rectTransform.rect.yMax + textComponent.transform.position.y;

        suggestions.transform.position = worldEndPoint;
    }

    Exception CommandError(string message = null)
    {
        GameObject outputText = outputScroll.content.GetChild(0).gameObject;
        GameObject newText = Instantiate(outputText, outputScroll.content);
        newText.SetActive(true);
        newText.GetComponent<TMP_Text>().text = message;

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
        if (targetType == typeof(bool)) return bool.TryParse(word, out bool flag) ? flag : (object)null;

        if (targetType == typeof(int) || targetType == typeof(float) || targetType == typeof(Vector3))
        {
            if (TryEvaluateMath(word, targetType, out object value, out string mathError))
                return value;

            throw CommandError(mathError);
        }

        return null;
    }

    /// <summary>Refuses locally for the message, then asks the server, which refuses again for real.</summary>
    public void Spawn(object entity, Vector3 position)
    {

        if(!Permissions.LocalAllows("spawn"))
            throw CommandError("You do not have permission to spawn items.");

        if(entity.GetType() == typeof(Item)) SpawnItemRpc((Item)entity, position);
        else throw CommandError("Unknown entity type: " + entity.GetType().Name);
    }

    [ServerRpc(requireOwnership: false)]
    void SpawnItemRpc(Item item, Vector3 position, RPCInfo info = default)
    {
        if(!Permissions.Instance || !Permissions.Instance.Allows(info.sender, "spawnitem"))
            return;

        if(item.IsEmpty || itemPrefab == null)
            return;

        GameObject spawnedItem = Instantiate(itemPrefab, position, Quaternion.identity);
        spawnedItem.GetComponent<Pickup>().Initialize(item);
    }

    /// <summary>
    /// Evaluates an expression over numbers and vectors and converts it to <paramref name="targetType"/>.
    /// </summary>
    bool TryEvaluateMath(string expression, Type targetType, out object result, out string error)
    {
        result = null;

        if (!MathExpression.TryEvaluate(expression, LookupWord, out var value, out error))
            return false;

        if (targetType == typeof(Vector3))
        {
            if (!value.isVector)
            {
                error = $"'{expression}' is a number, but a position was expected";
                return false;
            }

            result = value.vector;
            return true;
        }

        if (value.isVector)
        {
            error = $"'{expression}' is a vector, but a number was expected";
            return false;
        }

        if (targetType == typeof(int))
        {
            if (Mathf.Abs(value.scalar - Mathf.Round(value.scalar)) > 0.0001f)
            {
                error = $"'{expression}' is {value.scalar}, but a whole number was expected";
                return false;
            }

            result = Mathf.RoundToInt(value.scalar);
            return true;
        }

        result = value.scalar;
        return true;
    }

    object LookupWord(string word)
    {
        return wordDictionary != null && wordDictionary.TryGetValue(word, out object value) ? value : null;
    }

}
