using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;

/// <summary>
/// The command line: / or ` opens it, Enter runs, Escape closes. Typed in the command language (Cmd, CmdLang.cs; the
/// game's words in CmdWorld.cs): suggestions for the word at the caret (Tab takes one, Up/Down pick), the rest of the
/// picked one ghosted after the caret, a live preview of what the line is or would do (evaluated dry), history
/// (Up/Down on an empty popup, kept between sessions), a log that fades out when closed. While open, gameplay keys are
/// off: game code reads <see cref="Keys"/> instead of Keyboard.current. The input line selects like a text box
/// (Shift+moves, Ctrl+A, mouse click / drag / double-click; cut, copy, paste, typing over). Creates itself on play.
/// </summary>
[DefaultExecutionOrder(-300)] // before PauseMenu (-200) and the rest: the frame it closes on Escape they see Typing
public class CommandLine : MonoBehaviour
{
    [Tooltip("Keys that open it.")]
    public Key[] openKeys = { Key.Slash, Key.Backquote };
    public int fontSize = 20;
    [Min(1)] public int logLines = 16;
    [Tooltip("Seconds a line stays on screen after the console closes.")]
    public float fadeAfter = 8f;
    [Min(1)] public int maxSuggestions = 8;
    public float width = 1100f;

    /// <summary>Keys go to the command line (open, or closed this frame): gameplay ignores the keyboard.</summary>
    public static bool Typing => s_open || s_closedFrame == Time.frameCount;
    /// <summary>The keyboard for gameplay: null while typing.</summary>
    public static Keyboard Keys => Typing ? null : Keyboard.current;

    static bool s_open;
    static int s_closedFrame = -1;
    const string HistoryKey = "CommandLine.History";
    const int HistoryMax = 60;
    const float Margin = 24f, InputHeight = 34f, PreviewHeight = 24f, TextLeft = 30f;

    struct Line { public string text; public Cmd.Tone tone; public float time; }

    readonly List<Line> _log = new List<Line>();
    readonly List<string> _history = new List<string>();
    readonly List<Cmd.Suggestion> _suggest = new List<Cmd.Suggestion>();
    readonly StringBuilder _sb = new StringBuilder();
    string _text = "";
    int _caret, _anchor, _wordStart, _sel, _hist = -1, _scroll, _logKey = -1; // selection = _anchor.._caret
    bool _dirty = true, _suggestHidden, _dragging, _freedCursor, _captured;
    float _clickAt;
    int _clicks, _clickIndex;
    string _previewFor;
    float _blink, _repeatAt, _inputOffset;
    KeyControl _repeatKey;
    Keyboard _kb;

    Canvas _canvas;
    Font _font;
    Image _logPanel, _inputPanel, _caretImage, _selImage, _suggestPanel, _suggestSel;
    Text _logText, _inputText, _ghostText, _previewText;
    Text[] _rows;
    RectTransform _inputContent;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<CommandLine>()) return; // the scene has its own, with its own settings
        new GameObject("Command Line").AddComponent<CommandLine>();
    }

    void Start()
    {
        LoadHistory();
        Build();
        Log("/ opens the command line. Type help.", Cmd.Tone.Echo);
    }

    void OnDisable()
    {
        if (_kb != null) _kb.onTextInput -= OnText;
        _kb = null;
        if (s_open) Close();
    }

    // ---------------- opening ----------------

    void Open()
    {
        s_open = true;
        CmdWorld.Refresh();
        _suggestHidden = false;
        _dirty = true;
        _scroll = 0;
        _blink = 0f;
        _freedCursor = !UniversalCamera.FreeCursor; // a pointer to select with
        UniversalCamera.FreeCursor = true;
    }

    void Close()
    {
        s_open = false;
        s_closedFrame = Time.frameCount;
        _dirty = true;
        _hist = -1;
        _dragging = false;
        if (_freedCursor) UniversalCamera.FreeCursor = false;
        if (_captured) UniversalCamera.PointerCaptured = false;
        _freedCursor = _captured = false;
    }

    void Update()
    {
        if (_canvas && _logText == null) { Destroy(_canvas.gameObject); _canvas = null; } // a script reload dropped the parts
        if (!_canvas) Build();
        CmdWorld.Ensure();

        Keyboard k = Keyboard.current;
        if (k != _kb)
        {
            if (_kb != null) _kb.onTextInput -= OnText;
            _kb = k;
            if (_kb != null) _kb.onTextInput += OnText;
        }

        if (!s_open)
        {
            if (k != null && !PauseMenu.IsOpen && s_closedFrame != Time.frameCount)
                foreach (Key key in openKeys)
                    if (k[key].wasPressedThisFrame) { Open(); break; }
        }
        else
        {
            if (k != null) HandleKeys(k);
            if (s_open) HandleMouse(Mouse.current);
        }

        if (_dirty) Refresh();
        Draw();
    }

    // ---------------- typing ----------------

    void OnText(char c)
    {
        if (!s_open || char.IsControl(c)) return;
        Insert(c.ToString());
    }

    void Insert(string s)
    {
        DeleteSelection();
        _text = _text.Insert(_caret, s);
        _caret += s.Length;
        Edited();
    }

    void Edited()
    {
        _dirty = true;
        _suggestHidden = false;
        _sel = 0;
        _blink = 0f;
        _anchor = _caret;
    }

    bool HasSelection => _anchor != _caret;
    int SelStart => Mathf.Min(_anchor, _caret);
    int SelEnd => Mathf.Max(_anchor, _caret);

    void DeleteSelection()
    {
        if (!HasSelection) return;
        int from = SelStart;
        _text = _text.Remove(from, SelEnd - from);
        _caret = _anchor = from;
        Edited();
    }

    void HandleKeys(Keyboard k)
    {
        bool ctrl = k.ctrlKey.isPressed, shift = k.shiftKey.isPressed;
        if (k.escapeKey.wasPressedThisFrame) { Close(); return; }
        if (k.enterKey.wasPressedThisFrame || k.numpadEnterKey.wasPressedThisFrame) { Submit(); return; }
        if (k.tabKey.wasPressedThisFrame) Accept();
        if (Repeat(k.backspaceKey))
        {
            if (HasSelection) DeleteSelection();
            else if (_caret > 0)
            {
                int from = ctrl ? WordLeft(_caret) : _caret - 1;
                _text = _text.Remove(from, _caret - from);
                _caret = from;
                Edited();
            }
        }
        if (Repeat(k.deleteKey))
        {
            if (HasSelection) DeleteSelection();
            else if (_caret < _text.Length)
            {
                int to = ctrl ? WordRight(_caret) : _caret + 1;
                _text = _text.Remove(_caret, to - _caret);
                Edited();
            }
        }
        // Without Shift a selection collapses to the side moved towards; with it, the caret drags the selection.
        if (Repeat(k.leftArrowKey))
            Move(ctrl ? WordLeft(_caret) : HasSelection && !shift ? SelStart : _caret - 1, shift);
        if (Repeat(k.rightArrowKey))
        {
            if (_caret == _text.Length && !shift && !HasSelection && SuggestShown) Accept(); // like the ghost says
            else Move(ctrl ? WordRight(_caret) : HasSelection && !shift ? SelEnd : _caret + 1, shift);
        }
        if (k.homeKey.wasPressedThisFrame) Move(0, shift);
        if (k.endKey.wasPressedThisFrame) Move(_text.Length, shift);
        if (Repeat(k.upArrowKey)) Step(-1);
        if (Repeat(k.downArrowKey)) Step(1);
        if (k.pageUpKey.wasPressedThisFrame) { _scroll = Mathf.Min(_scroll + logLines / 2, Mathf.Max(0, _log.Count - logLines)); _logKey = -1; }
        if (k.pageDownKey.wasPressedThisFrame) { _scroll = Mathf.Max(0, _scroll - logLines / 2); _logKey = -1; }
        if (!ctrl) return;
        if (k.aKey.wasPressedThisFrame) { _anchor = 0; Move(_text.Length, true); }
        if (k.vKey.wasPressedThisFrame) Insert(GUIUtility.systemCopyBuffer.Replace('\n', ' ').Replace("\r", ""));
        if (k.cKey.wasPressedThisFrame) GUIUtility.systemCopyBuffer = HasSelection ? Selected : _text; // nothing picked: the line
        if (k.xKey.wasPressedThisFrame && HasSelection) { GUIUtility.systemCopyBuffer = Selected; DeleteSelection(); }
        if (k.lKey.wasPressedThisFrame) ClearLog();
    }

    string Selected => _text.Substring(SelStart, SelEnd - SelStart);

    // Click: caret there (Shift: extend), drag: select, double-click: the word, triple: the line.
    void HandleMouse(Mouse m)
    {
        if (m == null) return;
        Vector2 at = m.position.ReadValue();
        if (m.leftButton.wasPressedThisFrame && RectTransformUtility.RectangleContainsScreenPoint(_inputPanel.rectTransform, at, null))
        {
            int i = IndexAt(at);
            float now = Time.unscaledTime;
            _clicks = now - _clickAt < 0.35f && Mathf.Abs(i - _clickIndex) <= 1 ? _clicks % 3 + 1 : 1;
            _clickAt = now;
            _clickIndex = i;
            bool shift = Keyboard.current != null && Keyboard.current.shiftKey.isPressed;
            if (_clicks == 2) { _anchor = WordLeft(Mathf.Min(i + 1, _text.Length)); Move(WordRight(_anchor), true); }
            else if (_clicks == 3) { _anchor = 0; Move(_text.Length, true); }
            else { Move(i, shift); _dragging = true; }
            if (!UniversalCamera.PointerCaptured) _captured = UniversalCamera.PointerCaptured = true; // no drag-to-look
        }
        else if (_dragging && m.leftButton.isPressed) { int i = IndexAt(at); if (i != _caret) Move(i, true); }
        if (!m.leftButton.isPressed)
        {
            _dragging = false;
            if (_captured) UniversalCamera.PointerCaptured = _captured = false;
        }
    }

    // The character boundary nearest a screen point on the input line.
    int IndexAt(Vector2 screen)
    {
        RectTransformUtility.ScreenPointToLocalPointInRectangle(_inputContent, screen, null, out Vector2 local);
        if (!_font) return _text.Length;
        _font.RequestCharactersInTexture(_text, fontSize);
        float x = 0f;
        for (int i = 0; i < _text.Length; i++)
        {
            float w = _font.GetCharacterInfo(_text[i], out CharacterInfo ci, fontSize) ? ci.advance : 0f;
            if (local.x < x + w * 0.5f) return i;
            x += w;
        }
        return _text.Length;
    }

    bool Repeat(KeyControl key)
    {
        float now = Time.unscaledTime;
        if (key.wasPressedThisFrame) { _repeatKey = key; _repeatAt = now + 0.4f; return true; }
        if (key.isPressed && _repeatKey == key && now >= _repeatAt) { _repeatAt = now + 0.035f; return true; }
        return false;
    }

    void Move(int to, bool extend = false)
    {
        _caret = Mathf.Clamp(to, 0, _text.Length);
        if (!extend) _anchor = _caret;
        _dirty = true;
        _blink = 0f;
    }

    int WordLeft(int i)
    {
        while (i > 0 && !char.IsLetterOrDigit(_text[i - 1])) i--;
        while (i > 0 && char.IsLetterOrDigit(_text[i - 1])) i--;
        return i;
    }

    int WordRight(int i)
    {
        while (i < _text.Length && !char.IsLetterOrDigit(_text[i])) i++;
        while (i < _text.Length && char.IsLetterOrDigit(_text[i])) i++;
        return i;
    }

    bool SuggestShown => !_suggestHidden && _suggest.Count > 0 && _text.Length > 0;

    // Up / Down: through the suggestions when they show, else through history.
    void Step(int dir)
    {
        if (SuggestShown)
        {
            _sel = (_sel + dir + _suggest.Count) % _suggest.Count;
            _dirty = true;
            return;
        }
        if (_history.Count == 0) return;
        if (_hist < 0)
        {
            if (dir > 0) return;
            _hist = _history.Count - 1;
        }
        else
        {
            _hist = Mathf.Max(0, _hist + dir);
            if (_hist >= _history.Count) _hist = -1; // past the newest: back to an empty line
        }
        _text = _hist < 0 ? "" : _history[_hist];
        _caret = _anchor = _text.Length;
        _dirty = true;
        _suggestHidden = true; // the popup doesn't take over Up / Down until something is typed
    }

    void Accept()
    {
        if (!SuggestShown) return;
        Cmd.Suggestion s = _suggest[Mathf.Clamp(_sel, 0, _suggest.Count - 1)];
        int end = _caret;
        while (end < _text.Length && (char.IsLetterOrDigit(_text[end]) || _text[end] == '_')) end++;
        string insert = s.text + s.tail;
        _text = _text.Substring(0, _wordStart) + insert + _text.Substring(end);
        _caret = _anchor = _wordStart + insert.Length;
        _dirty = true;
        _suggestHidden = true;
        _blink = 0f;
    }

    void Submit()
    {
        string line = _text.Trim();
        if (line.Length == 0) { Close(); return; }
        Log("> " + line, Cmd.Tone.Echo);
        if (_history.Count == 0 || _history[_history.Count - 1] != line) _history.Add(line);
        if (_history.Count > HistoryMax) _history.RemoveRange(0, _history.Count - HistoryMax);
        SaveHistory();
        _hist = -1;
        _text = "";
        _caret = 0;
        _scroll = 0;
        Edited();
        var env = new Cmd.Env { output = Log, clear = ClearLog };
        try { Cmd.Run(line, env); }
        catch (Cmd.Error err) { Log(err.Message, Cmd.Tone.Error); }
        catch (Exception err)
        {
            Log("error: " + err.Message, Cmd.Tone.Error);
            Debug.LogException(err);
        }
    }

    // ---------------- log ----------------

    void Log(string text, Cmd.Tone tone)
    {
        foreach (string part in text.Split('\n'))
            _log.Add(new Line { text = part, tone = tone, time = Time.unscaledTime });
        if (_log.Count > 400) _log.RemoveRange(0, _log.Count - 400);
        _logKey = -1;
    }

    void ClearLog()
    {
        _log.Clear();
        _scroll = 0;
        _logKey = -1;
    }

    void LoadHistory()
    {
        _history.Clear();
        string saved = PlayerPrefs.GetString(HistoryKey, "");
        if (saved.Length > 0) _history.AddRange(saved.Split('\n'));
    }

    void SaveHistory()
    {
        PlayerPrefs.SetString(HistoryKey, string.Join("\n", _history));
    }

    // ---------------- drawing ----------------

    // On a change of text or caret: suggestions, preview, ghost, caret.
    void Refresh()
    {
        _dirty = false;
        if (!_canvas) return;
        _inputPanel.gameObject.SetActive(s_open);
        _logPanel.enabled = s_open;
        _logKey = -1;
        if (!s_open)
        {
            _suggestPanel.gameObject.SetActive(false);
            _previewText.text = "";
            return;
        }

        _wordStart = Cmd.Complete(_text, _caret, _suggest);
        if (_sel >= _suggest.Count) _sel = 0;
        if (_previewFor != _text)
        {
            _previewFor = _text;
            string p = Cmd.Preview(_text, out bool bad);
            _previewText.text = p ?? "";
            _previewText.color = bad ? Dim(TerminalUI.Blood, 0.8f) : Dim(TerminalUI.Line, 0.75f);
        }

        _inputText.text = _text;
        float caretX = Measure(_text, _caret);
        float view = width - TextLeft - 12f;
        if (caretX - _inputOffset > view - 8f) _inputOffset = caretX - view + 8f;
        if (caretX - _inputOffset < 0f) _inputOffset = caretX;
        if (Measure(_text, _text.Length) < view) _inputOffset = 0f;
        _inputContent.anchoredPosition = new Vector2(-_inputOffset, 0f);
        _caretImage.rectTransform.anchoredPosition = new Vector2(caretX, 0f);
        _anchor = Mathf.Clamp(_anchor, 0, _text.Length);
        _selImage.enabled = HasSelection;
        if (HasSelection)
        {
            float from = Measure(_text, SelStart);
            _selImage.rectTransform.anchoredPosition = new Vector2(from, 5f);
            _selImage.rectTransform.sizeDelta = new Vector2(Measure(_text, SelEnd) - from, InputHeight - 10f);
        }

        // The rest of the picked suggestion, ghosted after the caret (at the end of the line only).
        _ghostText.text = "";
        bool shown = SuggestShown;
        if (shown && _caret == _text.Length)
        {
            Cmd.Suggestion s = _suggest[_sel];
            int typed = _caret - _wordStart;
            if (typed <= s.text.Length && string.Compare(s.text, 0, _text, _wordStart, typed, StringComparison.OrdinalIgnoreCase) == 0)
                _ghostText.text = "<color=#00000000>" + _text + "</color>" + s.text.Substring(typed) + (s.tail ?? "");
        }

        _suggestPanel.gameObject.SetActive(shown);
        if (!shown) return;
        int count = Mathf.Min(maxSuggestions, _suggest.Count);
        int first = Mathf.Clamp(_sel - count / 2, 0, Mathf.Max(0, _suggest.Count - count));
        int lines = count + (_suggest.Count > count ? 1 : 0);
        float row = Row;
        // Rows from the top; the panel grows upward from just above the input.
        for (int r = 0; r < _rows.Length; r++)
        {
            Text t = _rows[r];
            t.gameObject.SetActive(r < lines);
            if (r >= lines) continue;
            t.rectTransform.anchoredPosition = new Vector2(10f, 5f + (lines - 1 - r) * row);
            if (r == count) { t.text = $"<color=#7FA8B866>{_sel + 1}/{_suggest.Count}</color>"; continue; }
            Cmd.Suggestion s = _suggest[first + r];
            _sb.Clear();
            _sb.Append(first + r == _sel ? "<color=#FFFFFF>" : "<color=#9EE8FF>").Append(s.text).Append("</color>");
            if (!string.IsNullOrEmpty(s.help)) _sb.Append("  <color=#7FA8B899>").Append(Clip(s.help, 72)).Append("</color>");
            t.text = _sb.ToString();
        }
        var panel = _suggestPanel.rectTransform;
        panel.sizeDelta = new Vector2(SuggestWidth, lines * row + 10f);
        float x = Mathf.Clamp(Margin + TextLeft + Measure(_text, _wordStart) - _inputOffset - 10f, Margin, Margin + width - 200f);
        panel.anchoredPosition = new Vector2(x, Margin + InputHeight + 2f);
        _suggestSel.rectTransform.anchoredPosition = new Vector2(0f, 5f + (lines - 1 - (_sel - first)) * row);
        _suggestSel.rectTransform.sizeDelta = new Vector2(SuggestWidth, row);
    }

    float Row => fontSize + 6f;
    const float SuggestWidth = 780f;

    // Every frame: the caret's blink and the log's fade (the log's text rebuilt only when what shows changes).
    void Draw()
    {
        if (!_canvas) return;
        float now = Time.unscaledTime;
        if (s_open)
        {
            _blink += Time.unscaledDeltaTime;
            _caretImage.enabled = (_blink % 1f) < 0.55f;
        }

        // What shows: the last lines (scrolled) while open; closed, the recent ones fading out.
        int end = _log.Count - (s_open ? _scroll : 0);
        int start = Mathf.Max(0, end - logLines);
        int key = start * 7919 + end * 31 + (s_open ? 1 : 0);
        if (!s_open)
        {
            while (start < end && now - _log[start].time > fadeAfter) start++;
            for (int i = start; i < end; i++) key = key * 17 + Mathf.RoundToInt(Mathf.Clamp01(fadeAfter - (now - _log[i].time)) * 10f);
        }
        if (key == _logKey) return;
        _logKey = key;
        _sb.Clear();
        for (int i = start; i < end; i++)
        {
            float alpha = s_open ? 1f : Mathf.Clamp01(fadeAfter - (now - _log[i].time));
            if (alpha <= 0f) continue;
            if (_sb.Length > 0) _sb.Append('\n');
            _sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGBA(Dim(ToneColor(_log[i].tone), alpha))).Append('>')
               .Append(_log[i].text).Append("</color>");
        }
        _logText.text = _sb.ToString();
    }

    static Color ToneColor(Cmd.Tone t) => t switch
    {
        Cmd.Tone.Result => TerminalUI.Line,
        Cmd.Tone.Error => TerminalUI.Blood,
        Cmd.Tone.Echo => Dim(TerminalUI.Text, 0.55f),
        _ => TerminalUI.Text,
    };

    static Color Dim(Color c, float a) => new Color(c.r, c.g, c.b, c.a * a);

    static string Clip(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";

    // Canvas units from the start of 's' to character 'upto' (the font's advances at our size).
    float Measure(string s, int upto)
    {
        if (!_font || upto <= 0) return 0f;
        _font.RequestCharactersInTexture(s, fontSize);
        float w = 0f;
        for (int i = 0; i < upto && i < s.Length; i++)
            if (_font.GetCharacterInfo(s[i], out CharacterInfo ci, fontSize)) w += ci.advance;
        return w;
    }

    // ---------------- building ----------------

    void Build()
    {
        _font = TerminalUI.Font(new[] { "Consolas", "Lucida Console", "Menlo", "OCR A Extended", "Courier New" });
        _canvas = TerminalUI.Canvas("Command Line Canvas", transform, 900);
        Transform root = _canvas.transform;
        float row = fontSize + 4f;

        float logBottom = Margin + InputHeight + PreviewHeight + 6f;
        _logPanel = Panel("Log Panel", root, new Vector2(Margin, logBottom - 4f), new Vector2(width, logLines * row + 12f), TerminalUI.Panel);
        _logText = Label("Log", root, new Vector2(Margin + 10f, logBottom), new Vector2(width - 20f, logLines * row), TextAnchor.LowerLeft);
        _logText.horizontalOverflow = HorizontalWrapMode.Wrap;
        _logText.verticalOverflow = VerticalWrapMode.Truncate;

        _inputPanel = Panel("Input", root, new Vector2(Margin, Margin), new Vector2(width, InputHeight), new Color(0.01f, 0.04f, 0.11f, 0.94f));
        Transform input = _inputPanel.transform;
        Panel("Edge", input, new Vector2(0f, InputHeight - 2f), new Vector2(width, 2f), Dim(TerminalUI.Line, 0.6f));
        Text prompt = Label("Prompt", input, new Vector2(10f, 0f), new Vector2(20f, InputHeight), TextAnchor.MiddleLeft);
        prompt.text = ">";
        prompt.color = TerminalUI.Live;
        RectTransform view = Place(new GameObject("View", typeof(RectTransform)).GetComponent<RectTransform>(), input, new Vector2(TextLeft, 0f), new Vector2(width - TextLeft - 12f, InputHeight));
        view.gameObject.AddComponent<RectMask2D>();
        _inputContent = Place(new GameObject("Content", typeof(RectTransform)).GetComponent<RectTransform>(), view, Vector2.zero, new Vector2(4000f, InputHeight));
        _ghostText = Label("Ghost", _inputContent, Vector2.zero, new Vector2(4000f, InputHeight), TextAnchor.MiddleLeft);
        _ghostText.color = Dim(TerminalUI.Line, 0.4f);
        _selImage = Panel("Selection", _inputContent, new Vector2(0f, 5f), new Vector2(0f, InputHeight - 10f), Dim(TerminalUI.Target, 0.45f));
        _selImage.enabled = false;
        _inputText = Label("Text", _inputContent, Vector2.zero, new Vector2(4000f, InputHeight), TextAnchor.MiddleLeft);
        _inputText.supportRichText = false;
        _inputText.color = TerminalUI.Text;
        _caretImage = Panel("Caret", _inputContent, new Vector2(0f, 5f), new Vector2(2f, InputHeight - 10f), TerminalUI.Live);

        _previewText = Label("Preview", root, new Vector2(Margin + TextLeft, Margin + InputHeight + 2f), new Vector2(width - TextLeft, PreviewHeight), TextAnchor.MiddleLeft);
        _previewText.supportRichText = false;
        _previewText.fontSize = fontSize - 3;

        _suggestPanel = Panel("Suggestions", root, Vector2.zero, new Vector2(SuggestWidth, 100f), new Color(0.01f, 0.05f, 0.13f, 0.97f));
        _suggestSel = Panel("Picked", _suggestPanel.transform, Vector2.zero, new Vector2(SuggestWidth, Row), Dim(TerminalUI.Target, 0.35f));
        _rows = new Text[maxSuggestions + 1];
        for (int r = 0; r < _rows.Length; r++)
            _rows[r] = Label("Row " + r, _suggestPanel.transform, Vector2.zero, new Vector2(SuggestWidth - 20f, Row), TextAnchor.MiddleLeft);
        _suggestPanel.gameObject.SetActive(false);

        _inputPanel.gameObject.SetActive(false);
        _logPanel.enabled = false;
        _dirty = true;
        _logKey = -1;
    }

    static RectTransform Place(RectTransform r, Transform parent, Vector2 at, Vector2 size)
    {
        r.SetParent(parent, false);
        r.anchorMin = r.anchorMax = r.pivot = Vector2.zero;
        r.anchoredPosition = at;
        r.sizeDelta = size;
        return r;
    }

    static Image Panel(string name, Transform parent, Vector2 at, Vector2 size, Color color)
    {
        Image img = TerminalUI.Graphic<Image>(name, parent, at, size);
        Place(img.rectTransform, parent, at, size);
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    Text Label(string name, Transform parent, Vector2 at, Vector2 size, TextAnchor anchor)
    {
        Text t = TerminalUI.Graphic<Text>(name, parent, at, size);
        Place(t.rectTransform, parent, at, size);
        TerminalUI.Style(t, _font, fontSize, TerminalUI.Text, anchor);
        t.supportRichText = true;
        return t;
    }
}
