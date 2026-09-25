using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Typed = TerminalUI.Typed;
using Job = CommandBoard.Job;

/// <summary>
/// RTS-style command mode. Q toggles it (Escape leaves): the cursor comes free (mouse look
/// stops; the keys still move) and the mouse selects instead:
/// - Nothing is boxed until it matters: a <see cref="Selectable"/> under the cursor (or inside a box
///   being dragged out) gets a blue box fitted to its shape, selected ones yellow, and every member of
///   a saved group keeps a box in that group's own colour (a ring per group it's in).
/// - Click selects it, drag a box to select everything in it (empty space clears). Shift toggles:
///   adds what's picked, or takes it off if all of it was already selected.
/// - A selection opens a radial menu that stays on the middle of what's selected: the selection split
///   by kind ("VIRUS x12", "CELL x3"); click one to save it as a group, agents as a squad ("Agents 1"),
///   targets as a task ("Cells 1").
/// - A saved group gets a tag in the world, in its colour, on the middle of all its members (off
///   screen ones too): click a task's to select its members (a squad's opens its link menu), its X (or a right click) removes the group.
/// - Drag a squad onto a task (tags in the world or nodes on the board) to link them: a line joins
///   them in the world and on the board, and the link menu opens on it, every choice on show and read
///   top to bottom: the job across the top (attack / extract / move to, the one in force lit, greyed
///   where the targets don't allow it), split / one at a time across the middle, unlink at the bottom,
///   "SQUAD > TASK" over it. A line can also be dropped straight on a thing in the world (and dragged
///   from a selected agent: the selected agents): what it's dropped on, and the dragged-from agents, are
///   saved as groups on the way (the rest of the selection of its kind with it if it's selected; a group
///   of exactly those already saved is reused). Click a line or the squad's tag / node to open it again (the tag again:
///   the squad's next link). Drag a task onto a task to chain them: squads done with the
///   first go on to the next (CommandBoard). Right click a line removes it.
/// - The tasking web along the bottom: every group a node, laid out again on every change (flow left
///   to right: squads, then their tasks, then the tasks chained after those; each column ordered to
///   keep lines from crossing), nodes gliding to their new places.
///
/// Everything is drawn on its own screen-space canvas in the terminal look (TerminalUI) and hit-tested
/// here in screen space, not by the EventSystem. Viruses with a VirusAI and Surfaces are made
/// selectable (agents / cells) when it opens. Creates itself on play.
/// </summary>
public class CommandMode : MonoBehaviour
{
    [Tooltip("Toggles the mode.")]
    public Key toggleKey = Key.Q;
    [Tooltip("Pixels the mouse must move while held for a drag (box select / linking) rather than a click.")]
    [Min(1f)] public float dragThreshold = 6f;
    [Tooltip("Radial menus: distance of their entries from the middle (1080p pixels).")]
    [Min(40f)] public float menuRadius = 130f;
    [Tooltip("Boxes are at least this big on screen (1080p pixels), so far things can still be hovered.")]
    [Min(2f)] public float minBox = 10f;
    [Tooltip("Gap between a box and what it's fitted round (1080p pixels).")]
    [Min(0f)] public float boxPadding = 3f;
    [Min(1f)] public float typeSpeed = 120f;

    [Header("Colours")]
    [Tooltip("Hovered, and inside the box being dragged out.")]
    public Color box = TerminalUI.Target;
    [Tooltip("Selected.")]
    public Color selected = new Color(1f, 0.92f, 0.2f, 1f);
    public Color panel = TerminalUI.Panel;
    public Color line = TerminalUI.Line;
    public Color live = TerminalUI.Live;
    public Color text = TerminalUI.Text;
    public Color task = TerminalUI.Task;
    [Tooltip("Links by job: attack, extract, move to.")]
    public Color attack = TerminalUI.Blood;
    public Color extract = TerminalUI.Live;
    public Color moveTo = TerminalUI.Line;
    [Tooltip("What can't be chosen.")]
    public Color unavailable = new Color(0.55f, 0.58f, 0.62f, 0.45f);

    [Header("Type")]
    public Font font;
    public string[] terminalFonts = TerminalUI.DefaultFonts;

    /// <summary>The command mode is on: the mouse selects and commands (VirusMovement leaves it alone).</summary>
    public static bool Active { get; private set; }

    public IReadOnlyList<Selectable> Selection => _selection;

    const float ChipW = 150f, ChipH = 36f, ColumnGap = 70f, RowGap = 14f, BoardPad = 18f, TitleH = 26f,
                MinBoardW = 560f, MinBoardH = 110f, EntryW = 210f, EntryH = 50f, LinkEntryW = 132f, LinkEntryH = 30f, TagH = 24f, TagX = 18f, LineReach = 7f;

    Camera _cam;
    readonly List<Selectable> _selection = new List<Selectable>();
    Selectable _hovered;
    float _nextScan;

    // The current press: where, and what it started on.
    // Group: a board node or world tag; Close: a tag's X; Agents: a selected agent in the world (a drag
    // from it links the selected agents straight onto something).
    enum Press { None, World, Menu, Group, Close, Line, Agents }
    Press _press;
    Vector2 _pressAt;
    bool _dragging;
    Radial _pressRadial;
    int _pressEntry = -1;
    CommandBoard.Group _pressGroup;
    bool _pressOnTag;
    Edge _pressEdge;

    // Radial menus: the selection by kind (_pick), and a link's settings (_linkMenu).
    class Entry
    {
        public string kind;
        public readonly List<Selectable> members = new List<Selectable>();
        public string saved; // the group it was saved as, or null
        public Color savedColor;
        public RectTransform rect;
        public Image fill, frame, leader;
        public Typed title, action;
    }
    class Radial
    {
        public RectTransform root, dial;
        public readonly List<Entry> entries = new List<Entry>();
        public int count;
        public bool open;
        public float openedAt;
        public Vector2 at; // canvas units
        public Vector2 size = new Vector2(EntryW, EntryH); // an entry's
        public bool compact; // entries are one centred line (no action text)
        public System.Func<int, float, Vector3> place; // entry i's centre (xy) and width (z) at a radius, when
        public int placeCount;                         // this many are open; else evenly round
        public Typed header; // over the menu
    }
    Radial _pick, _linkMenu;
    CommandBoard.Link _link; // the link menu's
    CommandBoard.Squad _noTask; // the link menu, open on a squad with no links

    // A line: a link (squad -> task) or a chain (task -> task), on the board (board units) or in the
    // world (canvas units).
    struct Edge
    {
        public CommandBoard.Link link;
        public CommandBoard.Task from, to; // a chain
        public Vector2 a, b;
        public bool Same(Edge o) => link != null ? link == o.link : o.link == null && from == o.from && to == o.to;
    }
    readonly List<Edge> _edges = new List<Edge>(), _worldEdges = new List<Edge>();

    // Board: a node (chip) per group, gliding toward where the layout puts it (board units, from its
    // bottom left).
    class Chip
    {
        public CommandBoard.Group group;
        public RectTransform rect;
        public Image fill, frame;
        public Text name, count;
        public Vector2 goal;
    }
    readonly List<Chip> _chips = new List<Chip>();
    readonly List<Image> _links = new List<Image>(), _dots = new List<Image>();
    bool _boardDirty = true;
    Vector2 _boardGoal = new Vector2(MinBoardW, MinBoardH);

    // A saved group's tag in the world: its name and an X, in its colour, on the middle of its members.
    class Tag
    {
        public CommandBoard.Group group;
        public RectTransform rect, x;
        public Image fill, frame, xFrame;
        public Text name, xText;
        public float born;
        public bool shown;
    }
    readonly List<Tag> _tags = new List<Tag>();

    readonly List<Image> _worldLines = new List<Image>(), _worldDots = new List<Image>();
    readonly List<Text> _worldLabels = new List<Text>();
    readonly Dictionary<Selectable, int> _rings = new Dictionary<Selectable, int>();

    // Drawing.
    Canvas _canvas;
    RectTransform _canvasRect, _board, _boxRect, _hoverLabelRect, _boxLayer, _lineLayer, _tagLayer;
    Image _dragLine, _boxFill;
    Text _hoverLabel, _boardHint;
    Typed _banner, _boardTitle;
    readonly List<Image> _brackets = new List<Image>();
    int _bracketsUsed;
    Font _font;
    Sprite _thin, _bold, _chamfer, _frame, _disc, _dialSprite;
    readonly Dictionary<Selectable, Rect> _boxes = new Dictionary<Selectable, Rect>(); // this frame's, on screen only
    readonly List<Object> _made = new List<Object>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        CommandBoard.Clear();
        Active = false;
        if (FindAnyObjectByType<CommandMode>()) return; // the scene has its own, with its own settings
        new GameObject("Command Mode").AddComponent<CommandMode>();
    }

    void OnEnable() => CommandBoard.Changed += MarkBoard;
    void OnDisable()
    {
        CommandBoard.Changed -= MarkBoard;
        SetActive(false);
    }

    void MarkBoard() => _boardDirty = true;

    void OnDestroy()
    {
        foreach (Object o in _made)
            if (o) Destroy(o);
    }

    // ---------------- per frame ----------------

    void Update()
    {
        if (_canvas && _banner == null) // a play-mode script reload dropped the plain parts: build again
        {
            Destroy(_canvas.gameObject);
            _canvas = null;
            _chips.Clear();
            _tags.Clear();
            _links.Clear();
            _dots.Clear();
            _worldLines.Clear();
            _worldDots.Clear();
            _worldLabels.Clear();
            _brackets.Clear();
            _boardDirty = true;
        }

        Mouse m = Mouse.current;
        Keyboard k = Keyboard.current;
        if (k != null && k[toggleKey].wasPressedThisFrame) SetActive(!Active);
        if (Active && k != null && k.escapeKey.wasPressedThisFrame)
        {
            if (_linkMenu != null && _linkMenu.open) CloseRadial(_linkMenu);
            else if (_pick != null && _pick.open) CloseRadial(_pick);
            else SetActive(false);
        }

        if (Time.unscaledTime >= _nextScan) // now and then: new agents / cells, and finished work moved on
        {
            _nextScan = Time.unscaledTime + 1f;
            CommandBoard.Dispatch();
            if (Active) Scan();
        }

        if (!_canvas) return;
        if (!Active)
        {
            if (_canvas.gameObject.activeSelf) _canvas.gameObject.SetActive(false);
            return;
        }
        if (!_canvas.gameObject.activeSelf) _canvas.gameObject.SetActive(true);
        _cam = Camera.main;
        if (!_cam || m == null) return;

        float now = Time.unscaledTime;
        Vector2 mouse = m.position.ReadValue(), at = ToCanvas(mouse);
        bool shift = k != null && k.shiftKey.isPressed;
        if (_link != null && !Contains(CommandBoard.Links, _link)) CloseRadial(_linkMenu); // gone (a group removed, moved on)
        if (_noTask != null && !CommandBoard.Squads.Contains(_noTask)) CloseRadial(_linkMenu);

        if (_boardDirty) SyncChips();
        FitBoxes();
        PlaceTags(mouse, now);
        CollectEdges();

        // What's under the cursor, top first: a menu entry, a board node, a world tag, a line, the world.
        Radial radial = null;
        int entry = -1;
        foreach (Radial r in new[] { _linkMenu, _pick })
            if (radial == null && r.open && (entry = EntryAt(r, mouse)) >= 0) radial = r;
        if (radial == null) entry = -1;
        Chip chip = radial == null ? ChipAt(mouse) : null;
        Tag tag = radial == null && chip == null ? TagAt(mouse) : null;
        bool onX = tag != null && RectTransformUtility.RectangleContainsScreenPoint(tag.x, mouse, null);
        CommandBoard.Group group = chip != null ? chip.group : tag?.group;
        bool overBoard = RectTransformUtility.RectangleContainsScreenPoint(_board, mouse, null);
        bool onLine = false;
        Edge edge = default;
        if (radial == null && group == null)
            onLine = overBoard ? EdgeAt(_edges, at - BoardOrigin, out edge) : EdgeAt(_worldEdges, at, out edge);
        _hovered = radial == null && !overBoard && tag == null && !onLine ? Pick(mouse) : null;

        if (m.leftButton.wasPressedThisFrame)
        {
            _pressAt = mouse;
            _dragging = false;
            _press = radial != null ? Press.Menu : onX ? Press.Close : group != null ? Press.Group
                   : onLine ? Press.Line : overBoard ? Press.None
                   : _hovered && _hovered.category == Selectable.Category.Agent && _selection.Contains(_hovered) ? Press.Agents
                   : Press.World;
            _pressRadial = radial;
            _pressEntry = entry;
            _pressGroup = group;
            _pressOnTag = tag != null;
            _pressEdge = edge;
        }
        if (_press != Press.None && m.leftButton.isPressed && !_dragging && (mouse - _pressAt).magnitude > dragThreshold)
            _dragging = _press == Press.World || _press == Press.Group || _press == Press.Agents;

        if (m.leftButton.wasReleasedThisFrame)
        {
            switch (_press)
            {
                case Press.Menu:
                    if (radial == _pressRadial && entry == _pressEntry) Activate(radial, entry);
                    break;
                case Press.Close:
                    if (onX && tag.group == _pressGroup) CommandBoard.Remove(_pressGroup);
                    break;
                case Press.Agents:
                    if (!_dragging) goto case Press.World;
                    Drop(CommandBoard.FindOrSave(SelectedOf(Selectable.Category.Agent, null)), group);
                    break;
                case Press.Group:
                    if (_dragging) Drop(_pressGroup, group);
                    else if (group != null && group == _pressGroup)
                    {
                        // A squad: its link's menu (again: the next link's). A task: its members.
                        if (group is CommandBoard.Squad squad) OpenSquadMenu(squad);
                        else Select(group.members, shift);
                    }
                    break;
                case Press.Line:
                    if (onLine && edge.Same(_pressEdge) && edge.link != null) OpenLinkMenu(edge.link);
                    break;
                case Press.World:
                    Select(_dragging ? Boxed(_pressAt, mouse) : _hovered ? new List<Selectable> { _hovered } : new List<Selectable>(), shift);
                    break;
            }
            _press = Press.None;
            _dragging = false;
        }

        // Right click: on a group deletes it; on a line, the link or chain.
        if (m.rightButton.wasPressedThisFrame)
        {
            if (group != null) CommandBoard.Remove(group);
            else if (onLine) RemoveEdge(edge);
        }

        DrawBoxes(group, mouse, now);
        DrawDrag(mouse);
        DrawWorldLines(onLine && !overBoard ? edge : default, now);
        DrawPick(radial == _pick ? entry : -1, now);
        DrawLinkMenu(radial == _linkMenu ? entry : -1, now);
        DrawBoard(group, onLine && overBoard ? edge : default, mouse, now, Time.unscaledDeltaTime);
        _banner.Tick(now, typeSpeed);
        _boardTitle.Tick(now, typeSpeed);
    }

    static bool Contains(IReadOnlyList<CommandBoard.Link> list, CommandBoard.Link l)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == l) return true;
        return false;
    }

    void SetActive(bool on)
    {
        if (Active == on) return;
        Active = on;
        UniversalCamera.FreeCursor = on;
        _press = Press.None;
        _dragging = false;
        _hovered = null;
        if (!on)
        {
            if (_pick != null) CloseRadial(_pick);
            if (_linkMenu != null) CloseRadial(_linkMenu);
            return;
        }
        if (!_canvas) Build();
        Scan();
        _banner.Set("06 // COMMAND  [" + toggleKey.ToString().ToUpperInvariant() + "] EXIT", retype: true);
        _banner.start = Time.unscaledTime;
        _boardTitle.Set("07 // TASKING", retype: true);
        _boardTitle.start = Time.unscaledTime + 0.15f;
        _boardDirty = true;
    }

    // Viruses with an AI become agents, surfaces become cells (unless they already say otherwise).
    void Scan()
    {
        foreach (VirusAI ai in FindObjectsByType<VirusAI>(FindObjectsSortMode.None))
            if (!ai.GetComponent<Selectable>()) Selectable.Add(ai.gameObject, Selectable.Category.Agent, "Virus");
        foreach (Surface s in FindObjectsByType<Surface>(FindObjectsSortMode.None))
            if (s.isCell && !s.GetComponent<Selectable>() && !s.GetComponentInParent<VirusMovement>())
                Selectable.Add(s.gameObject, Selectable.Category.Target, "Cell", CommandBoard.Jobs.MoveTo);
    }

    // ---------------- selecting ----------------

    // The selectable under the cursor: the smallest box containing it.
    Selectable Pick(Vector2 mouse)
    {
        Selectable best = null;
        float bestArea = float.MaxValue;
        foreach (var pair in _boxes)
        {
            Rect r = pair.Value;
            if (!r.Contains(mouse)) continue;
            float area = r.width * r.height;
            if (area < bestArea) { bestArea = area; best = pair.Key; }
        }
        return best;
    }

    // Everything whose box centre is inside the dragged rectangle.
    List<Selectable> Boxed(Vector2 a, Vector2 b)
    {
        Rect area = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        var list = new List<Selectable>();
        foreach (var pair in _boxes)
            if (area.Contains(pair.Value.center)) list.Add(pair.Key);
        return list;
    }

    // Plain: replace the selection. Shift: toggle (add what's picked, or take it off if all of it was
    // already selected). Then the menu for what's selected.
    void Select(List<Selectable> picked, bool toggle)
    {
        bool allIn = picked.Count > 0 && picked.TrueForAll(s => !s || _selection.Contains(s));
        if (toggle && allIn) _selection.RemoveAll(picked.Contains);
        else
        {
            if (!toggle) _selection.Clear();
            foreach (Selectable s in picked)
                if (s && !_selection.Contains(s)) _selection.Add(s);
        }
        _selection.RemoveAll(s => !s);
        CloseRadial(_linkMenu);
        if (_selection.Count > 0) OpenPick();
        else CloseRadial(_pick);
    }

    // Every selectable's box on screen this frame (pixels): fitted to its shape (Selectable.ScreenRect),
    // padded, grown to the least size; off screen or behind the camera: none.
    void FitBoxes()
    {
        _boxes.Clear();
        float least = minBox * Scale, pad = boxPadding * Scale;
        foreach (Selectable s in Selectable.All)
        {
            if (!s || !s.ScreenRect(_cam, out Rect r)) continue;
            r = Rect.MinMaxRect(r.xMin - pad, r.yMin - pad, r.xMax + pad, r.yMax + pad);
            if (r.width < least || r.height < least)
                r = new Rect(r.center - new Vector2(Mathf.Max(least, r.width), Mathf.Max(least, r.height)) * 0.5f,
                             new Vector2(Mathf.Max(least, r.width), Mathf.Max(least, r.height)));
            if (r.xMax > 0f && r.yMax > 0f && r.xMin < Screen.width && r.yMin < Screen.height) _boxes[s] = r;
        }
    }

    bool Box(Selectable s, out Rect r)
    {
        r = default;
        return s && _boxes.TryGetValue(s, out r);
    }

    // Screen pixels per canvas unit.
    float Scale => _canvas ? _canvas.scaleFactor : 1f;

    // A screen point in canvas units from the bottom left.
    Vector2 ToCanvas(Vector2 screen) => screen / Scale;

    // The middle of these (world) on the canvas, even when some or all are off screen (kept inside
    // it); false if there are none or it's behind the camera.
    bool CentreOnCanvas(List<Selectable> things, float margin, out Vector2 at)
    {
        at = default;
        Vector3 c = Vector3.zero;
        int n = 0;
        foreach (Selectable s in things)
            if (s) { c += s.WorldBounds.center; n++; }
        if (n == 0) return false;
        Vector3 screen = _cam.WorldToScreenPoint(c / n);
        if (screen.z <= 0f) return false;
        Vector2 size = _canvasRect.rect.size;
        at = ToCanvas(screen);
        at.x = Mathf.Clamp(at.x, margin, Mathf.Max(margin, size.x - margin));
        at.y = Mathf.Clamp(at.y, TagH, Mathf.Max(TagH, size.y - TagH));
        return true;
    }

    // ---------------- radial menus ----------------

    void OpenPick()
    {
        // The selection by kind, agents first.
        Radial r = _pick;
        foreach (Entry e in r.entries) e.members.Clear();
        int used = 0;
        _selection.Sort((a, b) => a.category != b.category ? a.category.CompareTo(b.category) : string.CompareOrdinal(a.kind, b.kind));
        foreach (Selectable s in _selection)
        {
            Entry e = null;
            for (int i = 0; i < used; i++)
                if (r.entries[i].kind == s.kind && r.entries[i].members[0].category == s.category) { e = r.entries[i]; break; }
            if (e == null)
            {
                e = Use(r, used++);
                e.kind = s.kind;
                e.saved = null;
            }
            e.members.Add(s);
        }
        for (int i = 0; i < used; i++)
        {
            Entry e = r.entries[i];
            e.title.Set(e.kind.ToUpperInvariant() + "  x" + e.members.Count, retype: true);
            e.action.Set("[>] SAVE AS " + CommandBoard.NextName(e.members[0].GroupName).ToUpperInvariant(), retype: true);
        }
        OpenRadial(r, used);
    }

    // The link menu, laid out like a form read top to bottom: "SQUAD > TASK", the job as one segmented
    // row over the dial (attack | extract | move to: the one in force lit in its colour, greyed where the
    // targets don't allow it), how the agents share the targets as a row under it (spread | focus), unlink
    // at the bottom. Every choice is on show; a click sets it.
    const int LinkEntries = 6, SplitEntry = 3, OneEntry = 4, UnlinkEntry = 5;
    const float SegmentGap = 3f;

    Vector3 LinkSlot(int i, float rad)
    {
        float w = LinkEntryW, h = LinkEntryH, row = w * 3f + SegmentGap * 2f;
        float jobsY = rad * 0.75f, modesY = -rad * 0.6f;
        if (i < CommandBoard.AllJobs.Length) return new Vector3((i - 1) * (w + SegmentGap), jobsY, w);
        float half = (row - SegmentGap) * 0.5f;
        if (i == SplitEntry || i == OneEntry) return new Vector3((i == SplitEntry ? -1f : 1f) * (half + SegmentGap) * 0.5f, modesY, half);
        return new Vector3(0f, modesY - h - 6f, w);
    }

    void OpenLinkMenu(CommandBoard.Link link)
    {
        _link = link;
        _noTask = null;
        CloseRadial(_pick);
        for (int i = 0; i < LinkEntries; i++) Use(_linkMenu, i);
        RefreshLinkMenu(retype: true);
        OpenRadial(_linkMenu, LinkEntries);
    }

    // A squad's tag or node clicked: its link's menu; clicked again, the next link's (a squad can work
    // several tasks). No links: the menu says how to make one.
    void OpenSquadMenu(CommandBoard.Squad squad)
    {
        List<CommandBoard.Link> links = CommandBoard.LinksOf(squad);
        if (links.Count > 0)
        {
            int at = _linkMenu.open && _link != null ? links.IndexOf(_link) : -1;
            OpenLinkMenu(links[(at + 1) % links.Count]);
            return;
        }
        CloseRadial(_pick);
        CloseRadial(_linkMenu);
        _noTask = squad;
        Entry e = Use(_linkMenu, 0);
        e.title.Set("NO TASK: DRAG ME ONTO TARGETS");
        _linkMenu.header.Set(squad.name.ToUpperInvariant());
        OpenRadial(_linkMenu, 1);
    }

    void RefreshLinkMenu(bool retype)
    {
        List<Entry> e = _linkMenu.entries;
        for (int i = 0; i < CommandBoard.AllJobs.Length; i++)
            e[i].title.Set(CommandBoard.Name(CommandBoard.AllJobs[i]), retype);
        e[SplitEntry].title.Set(CommandBoard.ModeName(false), retype);
        e[OneEntry].title.Set(CommandBoard.ModeName(true), retype);
        e[UnlinkEntry].title.Set("UNLINK", retype);
        List<CommandBoard.Link> links = CommandBoard.LinksOf(_link.squad);
        string head = _link.squad.name.ToUpperInvariant() + "  >  " + _link.task.name.ToUpperInvariant();
        if (links.Count > 1) head += "  (" + (links.IndexOf(_link) + 1) + "/" + links.Count + ": CLICK TAG FOR NEXT)";
        _linkMenu.header.Set(head, retype);
    }

    void Activate(Radial r, int i)
    {
        if (r == _pick) { SaveEntry(i); return; }
        if (_link == null) return;
        if (i < CommandBoard.AllJobs.Length)
        {
            Job job = CommandBoard.AllJobs[i];
            if (_link.task.Affords(job)) CommandBoard.Set(_link, job, _link.oneAtATime);
        }
        else if (i == SplitEntry || i == OneEntry) CommandBoard.Set(_link, _link.job, i == OneEntry);
        else
        {
            CommandBoard.Disconnect(_link);
            CloseRadial(_linkMenu);
            return;
        }
        RefreshLinkMenu(retype: false);
    }

    void SaveEntry(int i)
    {
        Entry e = _pick.entries[i];
        if (e.saved != null) return;
        CommandBoard.Group g = CommandBoard.Save(e.members);
        if (g == null) return;
        e.saved = g.name;
        e.savedColor = g.color;
        e.action.Set("[#] SAVED  " + g.name.ToUpperInvariant());
        // The other entries' names may have moved on.
        for (int j = 0; j < _pick.count; j++)
            if (_pick.entries[j].saved == null)
                _pick.entries[j].action.Set("[>] SAVE AS " + CommandBoard.NextName(_pick.entries[j].members[0].GroupName).ToUpperInvariant());
    }

    // Entry i of a menu, made if it's new.
    Entry Use(Radial r, int i)
    {
        while (r.entries.Count <= i) r.entries.Add(NewEntry(r));
        return r.entries[i];
    }

    void OpenRadial(Radial r, int count)
    {
        r.count = count;
        for (int i = 0; i < r.entries.Count; i++)
        {
            bool on = i < count;
            r.entries[i].rect.gameObject.SetActive(on);
            r.entries[i].leader.gameObject.SetActive(on);
        }
        bool fresh = !r.open;
        r.open = true;
        if (!fresh) return;
        r.openedAt = Time.unscaledTime;
        r.header.start = r.openedAt;
        for (int i = 0; i < count; i++)
        {
            r.entries[i].title.start = r.openedAt + i * 0.05f;
            r.entries[i].action.start = r.openedAt + i * 0.05f + 0.08f;
        }
    }

    void CloseRadial(Radial r)
    {
        if (r == null) return;
        r.open = false;
        if (r.root) r.root.gameObject.SetActive(false);
        if (r == _linkMenu) { _link = null; _noTask = null; }
    }

    // Entry i's direction from the menu's centre: evenly round, starting up and to the right (a compact
    // menu's only entry: below).
    static Vector2 EntryDir(Radial r, int i)
    {
        float a = (r.count == 1 ? (r.compact ? 270f : 30f) : 90f - i * 360f / r.count) * Mathf.Deg2Rad;
        return new Vector2(Mathf.Cos(a), Mathf.Sin(a));
    }

    int EntryAt(Radial r, Vector2 mouse)
    {
        for (int i = 0; i < r.count; i++)
            if (RectTransformUtility.RectangleContainsScreenPoint(r.entries[i].rect, mouse, null)) return i;
        return -1;
    }

    // Place a menu (kept on screen, above the board) and pop its entries out; false if it's closed.
    bool LayoutRadial(Radial r, Vector2 centre, float now, out float ease)
    {
        ease = 0f;
        if (!r.open)
        {
            if (r.root.gameObject.activeSelf) r.root.gameObject.SetActive(false);
            return false;
        }
        if (!r.root.gameObject.activeSelf) r.root.gameObject.SetActive(true);
        Vector2 size = _canvasRect.rect.size;
        bool placed = r.place != null && r.count == r.placeCount;
        float reach = menuRadius + r.size.x + 12f, floor = _board.sizeDelta.y + 40f + menuRadius;
        float roof = menuRadius + r.size.y + (r.header.full.Length > 0 ? 34f : 0f);
        if (placed) // measured from where the entries will sit
        {
            reach = roof = floor = 0f;
            for (int i = 0; i < r.count; i++)
            {
                Vector3 p = r.place(i, menuRadius);
                reach = Mathf.Max(reach, Mathf.Abs(p.x) + p.z * 0.5f + 12f);
                roof = Mathf.Max(roof, p.y + r.size.y * 0.5f + 34f);
                floor = Mathf.Max(floor, -p.y + r.size.y * 0.5f + 12f);
            }
            floor += _board.sizeDelta.y + 30f;
        }
        r.at = new Vector2(Mathf.Clamp(centre.x, reach, Mathf.Max(reach, size.x - reach)),
                           Mathf.Clamp(centre.y, floor, Mathf.Max(floor, size.y - roof)));
        float k = Mathf.Clamp01((now - r.openedAt) / 0.18f);
        ease = 1f - (1f - k) * (1f - k) * (1f - k);
        r.root.anchoredPosition = r.at;
        r.dial.localScale = Vector3.one * Mathf.Lerp(0.4f, 1f, ease);
        r.dial.localRotation = Quaternion.Euler(0f, 0f, -now * 30f);
        for (int i = 0; i < r.count; i++)
        {
            Entry e = r.entries[i];
            float rad = menuRadius * ease;
            if (placed)
            {
                Vector3 p = r.place(i, rad);
                e.rect.sizeDelta = new Vector2(p.z, r.size.y);
                e.rect.anchoredPosition = p;
            }
            else
            {
                Vector2 dir = EntryDir(r, i);
                if (r.compact) e.rect.sizeDelta = new Vector2(Mathf.Max(r.size.x, e.title.text.preferredWidth + 24f), r.size.y);
                e.rect.anchoredPosition = dir * rad + new Vector2(dir.x * e.rect.sizeDelta.x * 0.5f, 0f);
            }
            e.rect.localScale = Vector3.one * Mathf.Lerp(0.6f, 1f, ease);
            e.title.Tick(now, typeSpeed);
            e.action.Tick(now, typeSpeed);
        }
        float top = 38f; // over the highest entry (or the dial)
        for (int i = 0; i < r.count; i++)
            top = Mathf.Max(top, r.entries[i].rect.anchoredPosition.y + r.size.y * 0.5f);
        r.header.text.rectTransform.anchoredPosition = new Vector2(0f, top + 16f);
        r.header.Tick(now, typeSpeed);
        return true;
    }

    // 'on': the setting in force (filled a little in its colour).
    void Paint(Radial r, int i, Color frame, bool hot, Color title, Color action, float ease, bool on = false)
    {
        Entry e = r.entries[i];
        e.frame.color = hot ? live : frame;
        e.fill.color = hot ? new Color(panel.r + 0.05f, panel.g + 0.12f, panel.b + 0.1f, panel.a)
                     : on ? Color.Lerp(panel, new Color(frame.r, frame.g, frame.b, panel.a), 0.3f) : panel;
        e.title.text.color = hot ? live : title;
        e.action.text.color = hot ? live : action;
        Color lead = new Color(frame.r, frame.g, frame.b, 0.6f * ease);
        if (r.place != null && r.count == r.placeCount)
        {
            // Rows: one leader straight up / down from the dial to each row, drawn by its first entry.
            float edge = e.rect.anchoredPosition.y - Mathf.Sign(e.rect.anchoredPosition.y) * r.size.y * 0.5f;
            bool first = i == 0 || i == SplitEntry;
            Line(e.leader, new Vector2(0f, Mathf.Sign(edge) * 38f), new Vector2(0f, edge), 1.5f,
                 first ? new Color(line.r, line.g, line.b, 0.5f * ease) : Color.clear);
            return;
        }
        Vector2 dir = EntryDir(r, i);
        Line(e.leader, dir * 38f, dir * (menuRadius * ease - 4f), 1.5f, lead);
    }

    // The selection menu, on the middle of what's selected.
    void DrawPick(int hover, float now)
    {
        Vector2 centre = _pick.at;
        if (_pick.open) CentreOnCanvas(_selection, 60f, out centre);
        if (!LayoutRadial(_pick, centre, now, out float ease)) return;
        for (int i = 0; i < _pick.count; i++)
        {
            Entry e = _pick.entries[i];
            bool saved = e.saved != null, agents = e.members[0].category == Selectable.Category.Agent;
            Color c = saved ? e.savedColor : agents ? line : task;
            Paint(_pick, i, c, i == hover && !saved, agents ? line : task, saved ? e.savedColor : text, ease);
        }
    }

    // The link menu, on the middle of its line in the world (the drop point if neither end shows).
    void DrawLinkMenu(int hover, float now)
    {
        Vector2 centre = _linkMenu.at;
        if (_link != null)
        {
            Tag a = TagOf(_link.squad), b = TagOf(_link.task);
            if (a != null && a.shown && b != null && b.shown) centre = (a.rect.anchoredPosition + b.rect.anchoredPosition) * 0.5f;
            else if (a != null && a.shown) centre = a.rect.anchoredPosition;
            else if (b != null && b.shown) centre = b.rect.anchoredPosition;
        }
        else if (_noTask != null && TagOf(_noTask) is Tag t && t.shown) centre = t.rect.anchoredPosition;
        if (!LayoutRadial(_linkMenu, centre, now, out float ease)) return;
        Color dim = new Color(line.r, line.g, line.b, 0.5f);
        _linkMenu.header.text.color = _link != null ? _link.squad.color : _noTask != null ? _noTask.color : text;
        if (_link == null)
        {
            if (_linkMenu.count > 0) Paint(_linkMenu, 0, unavailable, false, text, text, ease);
            return;
        }
        for (int i = 0; i < CommandBoard.AllJobs.Length; i++)
        {
            Job job = CommandBoard.AllJobs[i];
            bool can = _link.task.Affords(job), on = _link.job == job;
            Color c = !can ? unavailable : on ? JobColor(job) : dim;
            Paint(_linkMenu, i, c, i == hover && can && !on, !can ? unavailable : on ? JobColor(job) : text, text, ease, on);
        }
        for (int i = SplitEntry; i <= OneEntry; i++)
        {
            bool on = _link.oneAtATime == (i == OneEntry);
            Paint(_linkMenu, i, on ? task : dim, i == hover && !on, on ? task : text, text, ease, on);
        }
        Color blood = TerminalUI.Blood;
        Paint(_linkMenu, UnlinkEntry, new Color(blood.r, blood.g, blood.b, 0.6f), hover == UnlinkEntry, blood, text, ease);
    }

    Color JobColor(Job j) => j == Job.Attack ? attack : j == Job.Extract ? extract : moveTo;

    // ---------------- links ----------------

    // A drag from one group dropped on another: squad and task (either way round) links them and opens
    // the link's menu (already linked: just the menu); task onto task chains the second after the first
    // (or undoes it).
    void LinkDrop(CommandBoard.Group from, CommandBoard.Group to)
    {
        if (from is CommandBoard.Squad s && to is CommandBoard.Task t) OpenLinkMenu(CommandBoard.Connect(s, t));
        else if (from is CommandBoard.Task t2 && to is CommandBoard.Squad s2) OpenLinkMenu(CommandBoard.Connect(s2, t2));
        else if (from is CommandBoard.Task a && to is CommandBoard.Task b) CommandBoard.Chain(a, b);
    }

    // A link line let go: on a group (tag or node) as LinkDrop; on a thing in the world, that thing is
    // saved on the way (with the rest of the selection of its kind if it's selected; an existing group
    // of exactly those is reused) and linked to: agents onto a target, a task onto an agent, a task
    // onto a target chains them.
    void Drop(CommandBoard.Group from, CommandBoard.Group onto)
    {
        if (from == null) return;
        if (onto != null) { LinkDrop(from, onto); return; }
        Selectable thing = _hovered;
        if (!thing) return;
        if (from is CommandBoard.Squad && thing.category == Selectable.Category.Agent) return; // agents onto agents
        if (from.members.Contains(thing)) return;
        List<Selectable> members = _selection.Contains(thing) ? SelectedOf(thing.category, thing.kind) : new List<Selectable> { thing };
        LinkDrop(from, CommandBoard.FindOrSave(members));
    }

    // The selected things of a category (and kind, unless null).
    List<Selectable> SelectedOf(Selectable.Category category, string kind)
    {
        var list = new List<Selectable>();
        foreach (Selectable s in _selection)
            if (s && s.category == category && (kind == null || s.kind == kind)) list.Add(s);
        return list;
    }

    void RemoveEdge(Edge e)
    {
        if (e.link != null) CommandBoard.Disconnect(e.link);
        else if (e.from != null) CommandBoard.Chain(e.from, e.to);
    }

    // The line nearest a point (in the list's units), if within reach.
    static bool EdgeAt(List<Edge> edges, Vector2 p, out Edge hit)
    {
        hit = default;
        float best = LineReach;
        bool any = false;
        foreach (Edge e in edges)
        {
            Vector2 ab = e.b - e.a;
            float t = Mathf.Clamp01(Vector2.Dot(p - e.a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-4f));
            float d = (e.a + ab * t - p).magnitude;
            if (d < best) { best = d; hit = e; any = true; }
        }
        return any;
    }

    // Where the line from a box's centre toward a point leaves the box.
    static Vector2 Exit(Vector2 centre, Vector2 toward, Vector2 half)
    {
        Vector2 d = toward - centre;
        float k = Mathf.Min(half.x / Mathf.Max(Mathf.Abs(d.x), 1e-4f), half.y / Mathf.Max(Mathf.Abs(d.y), 1e-4f));
        return centre + d * Mathf.Min(k, 1f);
    }

    // This frame's lines, board and world, ends on the nodes' / tags' edges.
    void CollectEdges()
    {
        _edges.Clear();
        _worldEdges.Clear();
        void Add(CommandBoard.Link link, CommandBoard.Group from, CommandBoard.Group to)
        {
            var e = new Edge { link = link, from = link == null ? (CommandBoard.Task)from : null, to = link == null ? (CommandBoard.Task)to : null };
            Chip ca = ChipOf(from), cb = ChipOf(to);
            if (ca != null && cb != null)
            {
                Vector2 pa = ca.rect.anchoredPosition, pb = cb.rect.anchoredPosition, half = new Vector2(ChipW, ChipH) * 0.5f;
                e.a = Exit(pa, pb, half);
                e.b = Exit(pb, pa, half);
                _edges.Add(e);
            }
            Tag ta = TagOf(from), tb = TagOf(to);
            if (ta != null && ta.shown && tb != null && tb.shown)
            {
                Vector2 pa = ta.rect.anchoredPosition, pb = tb.rect.anchoredPosition;
                e.a = Exit(pa, pb, ta.rect.sizeDelta * 0.5f);
                e.b = Exit(pb, pa, tb.rect.sizeDelta * 0.5f);
                _worldEdges.Add(e);
            }
        }
        foreach (CommandBoard.Link l in CommandBoard.Links) Add(l, l.squad, l.task);
        foreach (var c in CommandBoard.Chains) Add(null, c.from, c.to);
    }

    // Lines between the tags in the world, with dots running the way the work flows and what they do
    // written on them: links in their job's colour, chains in the task colour.
    void DrawWorldLines(Edge hover, float now)
    {
        for (int n = 0; n < _worldEdges.Count; n++)
        {
            Edge e = _worldEdges[n];
            if (n == _worldLines.Count)
            {
                _worldLines.Add(Solid("Line", _lineLayer));
                _worldDots.Add(Dot("Flow", _lineLayer));
                Text label = TerminalUI.Graphic<Text>("Label", _lineLayer, Vector2.zero, new Vector2(240f, 14f));
                BottomLeft(label.rectTransform);
                TerminalUI.Style(label, _font, 10, text, TextAnchor.MiddleCenter);
                _worldLabels.Add(label);
            }
            bool hot = hover.link != null || hover.from != null ? e.Same(hover) : false;
            Color c = e.link != null ? JobColor(e.link.job) : task;
            SetActive(_worldLines[n], true);
            SetActive(_worldDots[n], true);
            SetActive(_worldLabels[n], true);
            Line(_worldLines[n], e.a, e.b, hot ? 3f : 2f, new Color(c.r, c.g, c.b, hot ? 1f : 0.75f));
            _worldDots[n].rectTransform.anchoredPosition = Vector2.Lerp(e.a, e.b, Mathf.Repeat(now * 0.5f + n * 0.37f, 1f));
            _worldDots[n].color = c;
            Text t = _worldLabels[n];
            t.text = e.link != null ? CommandBoard.Name(e.link.job) + "  //  " + CommandBoard.ModeName(e.link.oneAtATime) : "THEN";
            t.color = new Color(c.r, c.g, c.b, hot ? 1f : 0.8f);
            t.rectTransform.anchoredPosition = (e.a + e.b) * 0.5f + new Vector2(0f, 10f);
        }
        for (int i = _worldEdges.Count; i < _worldLines.Count; i++)
        {
            SetActive(_worldLines[i], false);
            SetActive(_worldDots[i], false);
            SetActive(_worldLabels[i], false);
        }
    }

    static void SetActive(Graphic g, bool on)
    {
        if (g.gameObject.activeSelf != on) g.gameObject.SetActive(on);
    }

    // ---------------- board ----------------

    // A node and a world tag for every group (kept for groups still there, so nodes glide), then lay
    // the board out again.
    void SyncChips()
    {
        _boardDirty = false;
        var groups = new List<CommandBoard.Group>(CommandBoard.Squads);
        groups.AddRange(CommandBoard.Tasks);
        for (int i = _chips.Count - 1; i >= 0; i--)
            if (!groups.Contains(_chips[i].group))
            {
                Destroy(_chips[i].rect.gameObject);
                _chips.RemoveAt(i);
            }
        var fresh = new List<Chip>();
        foreach (CommandBoard.Group g in groups)
            if (ChipOf(g) == null)
            {
                Chip c = NewChip(g);
                _chips.Add(c);
                fresh.Add(c);
            }
        Layout();
        foreach (Chip c in fresh) c.rect.anchoredPosition = c.goal; // new ones appear in place

        for (int i = _tags.Count - 1; i >= 0; i--)
            if (!groups.Contains(_tags[i].group))
            {
                Destroy(_tags[i].rect.gameObject);
                _tags.RemoveAt(i);
            }
        foreach (CommandBoard.Group g in groups)
            if (TagOf(g) == null) _tags.Add(NewTag(g));
        _boardHint.gameObject.SetActive(_chips.Count == 0);
    }

    // Layered layout, flowing left to right: tasks by how far down a chain they are, each squad one
    // column before its earliest task, unlinked squads first. Then each column is ordered by where
    // its lines lead (the average height of what it's joined to, swept back and forth) so lines
    // cross as little as they can, and centred.
    void Layout()
    {
        var rank = new Dictionary<CommandBoard.Group, int>();
        foreach (CommandBoard.Task t in CommandBoard.Tasks) rank[t] = 1;
        int cap = CommandBoard.Tasks.Count + 1; // chains may loop: stop them climbing forever
        for (int pass = 0; pass < cap; pass++)
        {
            bool moved = false;
            foreach (var c in CommandBoard.Chains)
                if (rank[c.from] + 1 > rank[c.to] && rank[c.from] + 1 <= cap)
                {
                    rank[c.to] = rank[c.from] + 1;
                    moved = true;
                }
            if (!moved) break;
        }
        foreach (CommandBoard.Squad s in CommandBoard.Squads)
        {
            int first = int.MaxValue;
            foreach (CommandBoard.Link l in CommandBoard.LinksOf(s)) first = Mathf.Min(first, rank[l.task]);
            rank[s] = first == int.MaxValue ? 0 : first - 1;
        }

        // Columns (dropping empty ones), in the order the groups were made.
        var columns = new List<List<Chip>>();
        var ranks = new List<int>(new HashSet<int>(rank.Values));
        ranks.Sort();
        foreach (int r in ranks)
        {
            var column = new List<Chip>();
            foreach (Chip c in _chips)
                if (rank[c.group] == r) column.Add(c);
            columns.Add(column);
        }

        // Who's joined to whom.
        var near = new Dictionary<Chip, List<Chip>>();
        foreach (Chip c in _chips) near[c] = new List<Chip>();
        void Join(CommandBoard.Group a, CommandBoard.Group b)
        {
            Chip ca = ChipOf(a), cb = ChipOf(b);
            if (ca == null || cb == null) return;
            near[ca].Add(cb);
            near[cb].Add(ca);
        }
        foreach (CommandBoard.Link l in CommandBoard.Links) Join(l.squad, l.task);
        foreach (var c in CommandBoard.Chains) Join(c.from, c.to);

        // Size: the widest column across, the tallest one up.
        int rows = 1;
        foreach (var column in columns) rows = Mathf.Max(rows, column.Count);
        float contentW = columns.Count * ChipW + Mathf.Max(0, columns.Count - 1) * ColumnGap;
        float contentH = rows * ChipH + (rows - 1) * RowGap;
        _boardGoal = new Vector2(Mathf.Max(MinBoardW, contentW + BoardPad * 2f), Mathf.Max(MinBoardH, contentH + BoardPad * 2f + TitleH));
        float left = (_boardGoal.x - contentW) * 0.5f + ChipW * 0.5f;
        float middle = BoardPad + (_boardGoal.y - TitleH - BoardPad * 2f) * 0.5f;

        void Stack(List<Chip> column, float x)
        {
            for (int i = 0; i < column.Count; i++)
                column[i].goal = new Vector2(x, middle + ((column.Count - 1) * 0.5f - i) * (ChipH + RowGap));
        }
        for (int i = 0; i < columns.Count; i++) Stack(columns[i], left + i * (ChipW + ColumnGap));

        for (int sweep = 0; sweep < 6; sweep++)
        {
            bool forward = sweep % 2 == 0;
            for (int j = 0; j < columns.Count; j++)
            {
                List<Chip> column = columns[forward ? j : columns.Count - 1 - j];
                if (column.Count < 2) continue;
                var weight = new Dictionary<Chip, float>();
                foreach (Chip c in column)
                {
                    float sum = 0f;
                    int n = 0;
                    foreach (Chip o in near[c])
                        if (o.goal.x != c.goal.x) { sum += o.goal.y; n++; }
                    weight[c] = n > 0 ? sum / n : c.goal.y;
                }
                var order = new List<Chip>(column);
                column.Sort((a, b) => a == b ? 0
                                    : weight[a] != weight[b] ? weight[b].CompareTo(weight[a]) // highest first
                                    : order.IndexOf(a).CompareTo(order.IndexOf(b)));
                Stack(column, column[0].goal.x);
            }
        }
    }

    Chip ChipAt(Vector2 mouse)
    {
        foreach (Chip c in _chips)
            if (RectTransformUtility.RectangleContainsScreenPoint(c.rect, mouse, null)) return c;
        return null;
    }

    Chip ChipOf(CommandBoard.Group g)
    {
        foreach (Chip c in _chips)
            if (c.group == g) return c;
        return null;
    }

    Vector2 BoardOrigin => _board.anchoredPosition - new Vector2(_board.sizeDelta.x * 0.5f, 0f);

    void DrawBoard(CommandBoard.Group hover, Edge hoverEdge, Vector2 mouse, float now, float dt)
    {
        Vector2 size = _canvasRect.rect.size;
        float glide = 1f - Mathf.Exp(-10f * dt);
        _board.anchoredPosition = new Vector2(size.x * 0.5f, 20f);
        _board.sizeDelta = Vector2.Lerp(_board.sizeDelta, _boardGoal, glide);
        Vector2 grew = _board.sizeDelta - _boardGoal; // nodes ride the board's size while it eases

        foreach (Chip c in _chips)
        {
            c.rect.anchoredPosition = Vector2.Lerp(c.rect.anchoredPosition, c.goal + new Vector2(grew.x * 0.5f, 0f), glide);
            bool done = c.group is CommandBoard.Task t && t.Complete;
            Color col = c.group.color;
            bool hot = c.group == hover || c.group == _pressGroup && _press == Press.Group;
            c.frame.color = hot ? live : done ? new Color(col.r, col.g, col.b, 0.45f) : col;
            c.fill.color = hot ? new Color(panel.r + 0.05f, panel.g + 0.12f, panel.b + 0.1f, panel.a) : panel;
            c.name.color = done ? new Color(col.r, col.g, col.b, 0.5f) : col;
            c.count.text = done ? "DONE" : "x" + c.group.Count;
            c.count.color = done ? live : text;
        }

        // Lines, with dots running along each the way the work flows: links in their job's colour,
        // chains in the task colour.
        for (int n = 0; n < _edges.Count; n++)
        {
            Edge e = _edges[n];
            if (n == _links.Count)
            {
                _links.Add(Solid("Link", _board));
                _dots.Add(Dot("Flow", _board));
                _links[n].transform.SetSiblingIndex(2); // over the board's fill and frame, under the nodes
            }
            SetActive(_links[n], true);
            SetActive(_dots[n], true);
            bool hot = (hoverEdge.link != null || hoverEdge.from != null) && e.Same(hoverEdge);
            Color c = e.link != null ? JobColor(e.link.job) : task;
            Line(_links[n], e.a, e.b, hot ? 3f : 2f, new Color(c.r, c.g, c.b, hot ? 1f : e.link != null ? 0.8f : 0.6f));
            _dots[n].rectTransform.anchoredPosition = Vector2.Lerp(e.a, e.b, Mathf.Repeat(now * 0.7f + n * 0.37f, 1f));
            _dots[n].color = c;
        }
        for (int i = _edges.Count; i < _links.Count; i++)
        {
            SetActive(_links[i], false);
            SetActive(_dots[i], false);
        }

        // Linking: a line from the pressed group (its world tag if the drag started there), or from the
        // middle of the selected agents, to the cursor; lit while it's over something it would link to.
        bool linking = false;
        Vector2 start = default;
        Color color = selected;
        if (_press == Press.Group && _dragging)
        {
            Tag tag = _pressOnTag ? TagOf(_pressGroup) : null;
            Chip chip = _pressOnTag ? null : ChipOf(_pressGroup);
            if (tag != null && tag.shown) { start = tag.rect.anchoredPosition; linking = true; }
            else if (chip != null) { start = chip.rect.anchoredPosition + BoardOrigin; linking = true; }
            color = _pressGroup.color;
        }
        else if (_press == Press.Agents && _dragging)
            linking = CentreOnCanvas(SelectedOf(Selectable.Category.Agent, null), 0f, out start);
        _dragLine.gameObject.SetActive(linking);
        if (!linking) return;
        bool fromAgents = _press == Press.Agents || _pressGroup is CommandBoard.Squad;
        bool onto = hover != null && hover != _pressGroup
                    || _hovered && !(fromAgents && _hovered.category == Selectable.Category.Agent);
        Line(_dragLine, start, ToCanvas(mouse), onto ? 3f : 2f, onto ? live : color);
    }

    // ---------------- world tags ----------------

    // Each group's tag on the middle of all its members (off screen ones too; kept on screen), hidden
    // when that's behind the camera, popping in when saved, pushed apart where tags would overlap.
    void PlaceTags(Vector2 mouse, float now)
    {
        var shown = new List<Tag>();
        foreach (Tag t in _tags)
        {
            float halfW = t.rect.sizeDelta.x * 0.5f;
            t.shown = CentreOnCanvas(t.group.members, halfW, out Vector2 at);
            if (t.rect.gameObject.activeSelf != t.shown) t.rect.gameObject.SetActive(t.shown);
            if (!t.shown) continue;

            Color c = t.group.color;
            bool done = t.group is CommandBoard.Task task && task.Complete;
            t.name.text = t.group.name.ToUpperInvariant() + (done ? "  DONE" : "  x" + t.group.Count);
            t.name.color = c;
            t.frame.color = c;
            bool overX = RectTransformUtility.RectangleContainsScreenPoint(t.x, mouse, null);
            t.xFrame.color = overX ? c : new Color(c.r, c.g, c.b, 0.5f);
            t.xText.color = overX ? text : c;
            t.rect.sizeDelta = new Vector2(t.name.preferredWidth + TagX + 22f, TagH);
            float k = Mathf.Clamp01((now - t.born) / 0.25f); // pops in
            t.rect.localScale = Vector3.one * (Mathf.Min(1f, k * 3f + 0.2f) + 0.25f * Mathf.Sin(k * Mathf.PI));
            t.rect.anchoredPosition = at;
            shown.Add(t);
        }

        shown.Sort((a, b) => b.rect.anchoredPosition.y.CompareTo(a.rect.anchoredPosition.y)); // top first
        for (int i = 1; i < shown.Count; i++)
        {
            RectTransform ri = shown[i].rect;
            for (int j = 0; j < i; j++)
            {
                RectTransform rj = shown[j].rect;
                Vector2 d = ri.anchoredPosition - rj.anchoredPosition;
                if (Mathf.Abs(d.x) < (ri.sizeDelta.x + rj.sizeDelta.x) * 0.5f && Mathf.Abs(d.y) < TagH + 2f)
                    ri.anchoredPosition = new Vector2(ri.anchoredPosition.x, rj.anchoredPosition.y - TagH - 2f);
            }
        }
    }

    Tag TagAt(Vector2 mouse)
    {
        for (int i = _tags.Count - 1; i >= 0; i--) // the one drawn on top
            if (_tags[i].shown && RectTransformUtility.RectangleContainsScreenPoint(_tags[i].rect, mouse, null))
                return _tags[i];
        return null;
    }

    Tag TagOf(CommandBoard.Group g)
    {
        foreach (Tag t in _tags)
            if (t.group == g) return t;
        return null;
    }

    // ---------------- boxes ----------------

    // Boxes only where they mean something (nothing on the rest, or the screen fills with them):
    // every saved group's members in its colour, a ring per group they're in, stacked outward (bold
    // while the group is pointed at, on the board or by its tag); on top, blue under the cursor or
    // inside the box being dragged out (what a click / release would pick), yellow when selected
    // (even under the cursor: it only grows a little).
    void DrawBoxes(CommandBoard.Group hoverGroup, Vector2 mouse, float now)
    {
        _bracketsUsed = 0;
        _rings.Clear();
        for (int list = 0; list < 2; list++)
        {
            int count = list == 0 ? CommandBoard.Squads.Count : CommandBoard.Tasks.Count;
            for (int g = 0; g < count; g++)
            {
                CommandBoard.Group group = list == 0 ? CommandBoard.Squads[g] : (CommandBoard.Group)CommandBoard.Tasks[g];
                bool hot = group == hoverGroup;
                Color c = hot ? group.color : new Color(group.color.r, group.color.g, group.color.b, 0.8f);
                foreach (Selectable s in group.members)
                {
                    if (!Box(s, out Rect r)) continue;
                    _rings.TryGetValue(s, out int ring);
                    _rings[s] = ring + 1;
                    Frame(r, c, hot ? _bold : _thin, 3f + ring * 3f);
                }
            }
        }

        Color hover = new Color(box.r, box.g, box.b, 0.7f + 0.3f * Mathf.Sin(now * 8f));
        bool dragging = _press == Press.World && _dragging;
        Rect drag = Rect.MinMaxRect(Mathf.Min(_pressAt.x, mouse.x), Mathf.Min(_pressAt.y, mouse.y),
                                    Mathf.Max(_pressAt.x, mouse.x), Mathf.Max(_pressAt.y, mouse.y));
        foreach (var pair in _boxes)
        {
            Selectable s = pair.Key;
            bool picked = dragging ? drag.Contains(pair.Value.center) : s == _hovered; // what it would select
            bool chosen = _selection.Contains(s);
            if (!picked && !chosen) continue;
            Frame(pair.Value, chosen ? selected : hover, _bold, picked ? 1f : 0f); // selected stays yellow
        }
        for (int i = _bracketsUsed; i < _brackets.Count; i++)
            if (_brackets[i].gameObject.activeSelf) _brackets[i].gameObject.SetActive(false);

        Rect hr = default;
        bool hoverShown = _hovered && Box(_hovered, out hr);
        _hoverLabel.gameObject.SetActive(hoverShown);
        if (hoverShown)
        {
            _hoverLabel.text = _hovered.kind.ToUpperInvariant() + (_hovered.category == Selectable.Category.Agent ? "  // AGENT" : "  // TARGET");
            _hoverLabel.color = box;
            _hoverLabelRect.anchoredPosition = ToCanvas(new Vector2(hr.xMin, hr.yMax)) + new Vector2(0f, 10f);
        }
    }

    void Frame(Rect r, Color c, Sprite sprite, float grow)
    {
        if (_bracketsUsed == _brackets.Count)
        {
            Image img = TerminalUI.Graphic<Image>("Box", _boxLayer, Vector2.zero, Vector2.zero); // drawn in the order used
            BottomLeft(img.rectTransform);
            img.type = Image.Type.Sliced;
            img.raycastTarget = false;
            _brackets.Add(img);
        }
        Image b = _brackets[_bracketsUsed++];
        if (!b.gameObject.activeSelf) b.gameObject.SetActive(true);
        b.sprite = sprite;
        b.color = c;
        RectTransform rt = b.rectTransform;
        rt.anchoredPosition = ToCanvas(r.center);
        rt.sizeDelta = r.size / Scale + Vector2.one * (grow * 2f);
    }

    // The box being dragged out.
    void DrawDrag(Vector2 mouse)
    {
        bool on = _press == Press.World && _dragging;
        _boxRect.gameObject.SetActive(on);
        if (!on) return;
        Vector2 a = ToCanvas(_pressAt), b = ToCanvas(mouse);
        _boxRect.anchoredPosition = (a + b) * 0.5f;
        _boxRect.sizeDelta = new Vector2(Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));
    }

    // ---------------- building ----------------

    void Build()
    {
        _font = font ? font : TerminalUI.Font(terminalFonts);
        _canvas = TerminalUI.Canvas("Command Mode Canvas", transform, 570);
        Destroy(_canvas.GetComponent<GraphicRaycaster>()); // hit-tested here
        _canvasRect = (RectTransform)_canvas.transform;
        _thin = Keep(TerminalUI.BoxSprite(1.1f));
        _bold = Keep(TerminalUI.BoxSprite(2f));
        _chamfer = Keep(TerminalUI.ChamferSprite(frame: false));
        _frame = Keep(TerminalUI.ChamferSprite(frame: true));
        _disc = Keep(TerminalUI.DiscSprite(frame: false));
        _dialSprite = Keep(TerminalUI.DialSprite(outer: true));

        // Boxes, then the world lines and the groups' tags, under everything else.
        _boxLayer = Layer("Boxes");
        _lineLayer = Layer("Lines");
        _tagLayer = Layer("Tags");

        // Box select.
        _boxRect = TerminalUI.Rect("Box Select", _canvasRect, Vector2.zero, Vector2.zero);
        BottomLeft(_boxRect);
        _boxFill = TerminalUI.Graphic<Image>("Fill", _boxRect, Vector2.zero, Vector2.zero);
        Stretch(_boxFill.rectTransform);
        _boxFill.color = new Color(box.r, box.g, box.b, 0.08f);
        _boxFill.raycastTarget = false;
        Image boxFrame = TerminalUI.Graphic<Image>("Frame", _boxRect, Vector2.zero, Vector2.zero);
        Stretch(boxFrame.rectTransform);
        boxFrame.sprite = _thin;
        boxFrame.type = Image.Type.Sliced;
        boxFrame.color = box;
        boxFrame.raycastTarget = false;
        _boxRect.gameObject.SetActive(false);

        // Hover label.
        _hoverLabel = TerminalUI.Graphic<Text>("Hover", _canvasRect, Vector2.zero, new Vector2(260f, 16f));
        _hoverLabelRect = BottomLeft(_hoverLabel.rectTransform);
        _hoverLabelRect.pivot = new Vector2(0f, 0.5f);
        TerminalUI.Style(_hoverLabel, _font, 11, box, TextAnchor.MiddleLeft);

        // Mode banner, top middle.
        Text banner = TerminalUI.Graphic<Text>("Banner", _canvasRect, Vector2.zero, new Vector2(600f, 20f));
        RectTransform br = banner.rectTransform;
        br.anchorMin = br.anchorMax = new Vector2(0.5f, 1f);
        br.anchoredPosition = new Vector2(0f, -28f);
        TerminalUI.Style(banner, _font, 14, box, TextAnchor.MiddleCenter);
        _banner = new Typed { text = banner };

        // Board.
        _board = TerminalUI.Rect("Board", _canvasRect, Vector2.zero, _boardGoal);
        _board.anchorMin = _board.anchorMax = Vector2.zero;
        _board.pivot = new Vector2(0.5f, 0f);
        Image boardFill = TerminalUI.Graphic<Image>("Fill", _board, Vector2.zero, Vector2.zero);
        Stretch(boardFill.rectTransform);
        boardFill.sprite = _chamfer;
        boardFill.type = Image.Type.Sliced;
        boardFill.color = panel;
        boardFill.raycastTarget = false;
        Image boardFrame = TerminalUI.Graphic<Image>("Frame", _board, Vector2.zero, Vector2.zero);
        Stretch(boardFrame.rectTransform);
        boardFrame.sprite = _frame;
        boardFrame.type = Image.Type.Sliced;
        boardFrame.color = new Color(line.r, line.g, line.b, 0.6f);
        boardFrame.raycastTarget = false;

        Text title = BoardText("Title", new Vector2(16f, -16f), 11, new Color(line.r, line.g, line.b, 0.75f), TextAnchor.MiddleLeft);
        _boardTitle = new Typed { text = title };
        Text legend = BoardText("Legend", new Vector2(-16f, -16f), 10, new Color(text.r, text.g, text.b, 0.45f), TextAnchor.MiddleRight);
        legend.text = "DRAG  AGENTS > TARGETS: LINK   TASK > TASK: THEN";
        _boardHint = BoardText("Hint", new Vector2(0f, 0f), 11, new Color(text.r, text.g, text.b, 0.5f), TextAnchor.MiddleCenter);
        _boardHint.text = "SELECT AGENTS, DRAG FROM THEM ONTO A TARGET";

        // Radial menus, over the board.
        _pick = NewRadial("Selection Menu");
        _linkMenu = NewRadial("Link Menu");
        _linkMenu.compact = true;
        _linkMenu.size = new Vector2(LinkEntryW, LinkEntryH);
        _linkMenu.place = LinkSlot;
        _linkMenu.placeCount = LinkEntries;

        _dragLine = Solid("Linking", _canvasRect);
        _dragLine.gameObject.SetActive(false);

        _canvas.gameObject.SetActive(false);
    }

    RectTransform Layer(string name)
    {
        RectTransform r = TerminalUI.Rect(name, _canvasRect, Vector2.zero, Vector2.zero);
        Stretch(r);
        return r;
    }

    Radial NewRadial(string name)
    {
        var r = new Radial { root = TerminalUI.Rect(name, _canvasRect, Vector2.zero, Vector2.zero) };
        BottomLeft(r.root);
        Image dial = TerminalUI.Graphic<Image>("Dial", r.root, Vector2.zero, new Vector2(76f, 76f));
        dial.sprite = _dialSprite;
        dial.color = new Color(line.r, line.g, line.b, 0.8f);
        dial.raycastTarget = false;
        r.dial = dial.rectTransform;
        Text header = TerminalUI.Graphic<Text>("Header", r.root, Vector2.zero, new Vector2(520f, 18f));
        TerminalUI.Style(header, _font, 12, text, TextAnchor.MiddleCenter);
        r.header = new Typed { text = header };
        r.root.gameObject.SetActive(false);
        return r;
    }

    // Text on the board, placed from the corner its alignment names (left: top left, right: top
    // right, centre: the middle), so it stays put as the board resizes.
    Text BoardText(string name, Vector2 at, int size, Color color, TextAnchor anchor)
    {
        Text t = TerminalUI.Graphic<Text>(name, _board, Vector2.zero, new Vector2(420f, 16f));
        RectTransform r = t.rectTransform;
        Vector2 corner = anchor == TextAnchor.MiddleLeft ? new Vector2(0f, 1f) : anchor == TextAnchor.MiddleRight ? Vector2.one : new Vector2(0.5f, 0.5f);
        r.anchorMin = r.anchorMax = corner;
        r.pivot = new Vector2(corner.x, 0.5f);
        r.anchoredPosition = at;
        TerminalUI.Style(t, _font, size, color, anchor);
        return t;
    }

    Entry NewEntry(Radial r)
    {
        var e = new Entry();
        e.leader = Solid("Leader", r.root);
        e.leader.transform.SetSiblingIndex(0);
        e.rect = TerminalUI.Rect("Entry", r.root, Vector2.zero, r.size);
        e.fill = TerminalUI.Graphic<Image>("Fill", e.rect, Vector2.zero, Vector2.zero);
        Stretch(e.fill.rectTransform);
        e.fill.sprite = _chamfer;
        e.fill.type = Image.Type.Sliced;
        e.fill.raycastTarget = false;
        e.frame = TerminalUI.Graphic<Image>("Frame", e.rect, Vector2.zero, Vector2.zero);
        Stretch(e.frame.rectTransform);
        e.frame.sprite = _frame;
        e.frame.type = Image.Type.Sliced;
        e.frame.raycastTarget = false;
        // Compact: one centred line (the action text is kept, hidden, so every entry has one).
        e.title = new Typed { text = EntryText(e.rect, r.compact ? 0f : 9f, r.compact ? 12 : 14, r.size.x, r.compact) };
        e.action = new Typed { text = EntryText(e.rect, -11f, 10, r.size.x, false) };
        if (r.compact) e.action.text.gameObject.SetActive(false);
        return e;
    }

    Text EntryText(RectTransform parent, float y, int size, float width, bool centred)
    {
        Text t = TerminalUI.Graphic<Text>("Text", parent, new Vector2(0f, y), new Vector2(width - 20f, size + 6f));
        TerminalUI.Style(t, _font, size, text, centred ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft);
        if (centred) Stretch(t.rectTransform); // the entry may widen to fit
        return t;
    }

    Chip NewChip(CommandBoard.Group g)
    {
        var c = new Chip { group = g };
        c.rect = TerminalUI.Rect("Chip", _board, Vector2.zero, new Vector2(ChipW, ChipH));
        BottomLeft(c.rect);
        c.fill = TerminalUI.Graphic<Image>("Fill", c.rect, Vector2.zero, Vector2.zero);
        Stretch(c.fill.rectTransform);
        c.fill.sprite = _chamfer;
        c.fill.type = Image.Type.Sliced;
        c.fill.raycastTarget = false;
        c.frame = TerminalUI.Graphic<Image>("Frame", c.rect, Vector2.zero, Vector2.zero);
        Stretch(c.frame.rectTransform);
        c.frame.sprite = _frame;
        c.frame.type = Image.Type.Sliced;
        c.frame.raycastTarget = false;
        c.name = TerminalUI.Graphic<Text>("Name", c.rect, new Vector2(-12f, 0f), new Vector2(ChipW - 40f, 18f));
        TerminalUI.Style(c.name, _font, 12, line, TextAnchor.MiddleLeft);
        c.name.text = g.name.ToUpperInvariant();
        c.count = TerminalUI.Graphic<Text>("Count", c.rect, new Vector2(ChipW * 0.5f - 24f, 0f), new Vector2(40f, 18f));
        TerminalUI.Style(c.count, _font, 11, text, TextAnchor.MiddleRight);
        return c;
    }

    Tag NewTag(CommandBoard.Group g)
    {
        var t = new Tag { group = g, born = Time.unscaledTime };
        t.rect = TerminalUI.Rect("Tag", _tagLayer, Vector2.zero, new Vector2(120f, TagH));
        BottomLeft(t.rect);
        t.fill = TerminalUI.Graphic<Image>("Fill", t.rect, Vector2.zero, Vector2.zero);
        Stretch(t.fill.rectTransform);
        t.fill.sprite = _chamfer;
        t.fill.type = Image.Type.Sliced;
        t.fill.color = panel;
        t.fill.raycastTarget = false;
        t.frame = TerminalUI.Graphic<Image>("Frame", t.rect, Vector2.zero, Vector2.zero);
        Stretch(t.frame.rectTransform);
        t.frame.sprite = _frame;
        t.frame.type = Image.Type.Sliced;
        t.frame.raycastTarget = false;

        t.name = TerminalUI.Graphic<Text>("Name", t.rect, Vector2.zero, new Vector2(10f, 16f));
        RectTransform nr = t.name.rectTransform;
        nr.anchorMin = nr.anchorMax = nr.pivot = new Vector2(0f, 0.5f);
        nr.anchoredPosition = new Vector2(8f, 0f);
        TerminalUI.Style(t.name, _font, 11, g.color, TextAnchor.MiddleLeft);

        t.x = TerminalUI.Rect("X", t.rect, Vector2.zero, new Vector2(TagX, TagX));
        t.x.anchorMin = t.x.anchorMax = t.x.pivot = new Vector2(1f, 0.5f);
        t.x.anchoredPosition = new Vector2(-3f, 0f);
        t.xFrame = TerminalUI.Graphic<Image>("Frame", t.x, Vector2.zero, Vector2.zero);
        Stretch(t.xFrame.rectTransform);
        t.xFrame.sprite = _thin;
        t.xFrame.type = Image.Type.Sliced;
        t.xFrame.raycastTarget = false;
        t.xText = TerminalUI.Graphic<Text>("Text", t.x, Vector2.zero, new Vector2(TagX, TagX));
        TerminalUI.Style(t.xText, _font, 12, g.color, TextAnchor.MiddleCenter);
        t.xText.text = "X";
        return t;
    }

    // A plain bar (for lines).
    Image Solid(string name, Transform parent)
    {
        Image img = TerminalUI.Graphic<Image>(name, parent, Vector2.zero, Vector2.zero);
        BottomLeft(img.rectTransform);
        img.raycastTarget = false;
        return img;
    }

    Image Dot(string name, Transform parent)
    {
        Image img = TerminalUI.Graphic<Image>(name, parent, Vector2.zero, new Vector2(7f, 7f));
        BottomLeft(img.rectTransform);
        img.sprite = _disc;
        img.color = live;
        img.raycastTarget = false;
        return img;
    }

    // Lay a bar from a to b (its parent's units).
    static void Line(Image img, Vector2 a, Vector2 b, float width, Color color)
    {
        RectTransform r = img.rectTransform;
        Vector2 d = b - a;
        r.anchoredPosition = (a + b) * 0.5f;
        r.sizeDelta = new Vector2(d.magnitude, width);
        r.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
        img.color = color;
    }

    static RectTransform BottomLeft(RectTransform r)
    {
        r.anchorMin = r.anchorMax = Vector2.zero;
        r.pivot = new Vector2(0.5f, 0.5f);
        return r;
    }

    static void Stretch(RectTransform r)
    {
        r.anchorMin = Vector2.zero;
        r.anchorMax = Vector2.one;
        r.sizeDelta = Vector2.zero;
        r.anchoredPosition = Vector2.zero;
    }

    Sprite Keep(Sprite s)
    {
        _made.Add(s);
        _made.Add(s.texture);
        return s;
    }
}
