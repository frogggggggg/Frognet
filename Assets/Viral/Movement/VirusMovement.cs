using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// Player controller for the virus: input -> Organism intent, plus camera
/// modes, the grounded WorldButton, and focus events.
///
/// WASD / stick: move relative to the camera. Space / South: jump on the
/// ground, charge in the air. Hold the WorldButton (F, its input action): drill -> Focus.
/// F or Escape: leave Focus.
///
/// Rope: LMB pays rope out (starting one if none is held: anchored under you on a
/// surface, trailing a loose end in the air). On a surface LMB also raises the body,
/// and a quick click slams down and anchors instead (start a rope / pin its end); the
/// anchor lands at the slam. With nothing held, a rope end in reach (a loose one, or an
/// anchored one from closer in) shows a phantom rope to you, and LMB takes it in hand.
/// RMB held reels in; an RMB click cuts the rope at your end (it stays, loose); double RMB
/// is a big tug that tears the far end loose, or slurps the rope in if it's already loose.
///
/// Focus: hovering a rope's base grows it; clicking it opens a radial menu on it
/// (RopeRadialMenu: straighten, length). Clicking the virus itself opens its head view
/// (GenomeView: the DNA it carries and its stores, mounted round it; click a strand to inject it).
/// Resource chunks in reach show their core (ResourceField): clicking one starts (or stops) extracting
/// it into its VirusInventory, and opens the head view to show it flowing in. A click elsewhere or
/// Escape closes whichever is open.
///
/// Inventory: the stores are always in the top-left corner (InventoryView); E anywhere opens the head
/// view (out of focus a strand click only loads it; the cursor is freed while it's open).
///
/// While the command mode is on (CommandMode, Q) the mouse belongs to it: no rope or
/// focus clicks here, the keys still move.
/// </summary>
[DefaultExecutionOrder(-10)] // write intent before Organism reads it
[RequireComponent(typeof(Organism))]
public class VirusMovement : MonoBehaviour
{
    [Tooltip("Movement is relative to this. Falls back to Camera.main.")]
    public Transform cameraTransform;

    [Header("Camera")]
    public UniversalCamera[] cameraRigs;
    public string flyingCameraMode = "Flying", groundedCameraMode = "Grounded", focusCameraMode = "Focus";

    [Header("Focus")]
    public UnityEvent onFocusModeEnter, onFocusModeExit;

    [Tooltip("Hold source for drilling. Shown only while grounded and not in Focus.")]
    public WorldButton groundedWorldButton;
    [Min(0f), Tooltip("Seconds off the ground before the button hides (a brief hop or edge doesn't flicker it).")]
    public float buttonGrace = 0.3f;

    [Tooltip("Rope spool driven by the mouse. Empty: found on this object or a child.")]
    public VirusRope rope;
    [Tooltip("Focus: how close (pixels) the cursor must be to a rope's base to pick it.")]
    [Min(1f)] public float ropeBaseRadius = 40f;
    [Tooltip("Focus: the menu a clicked rope base opens. Empty: made on first use.")]
    public RopeRadialMenu ropeMenu;
    [Tooltip("Focus: the least distance (pixels) from the head's centre that counts as clicking the virus; a bigger head on screen reaches further.")]
    [Min(1f)] public float headPickRadius = 40f;
    [Tooltip("Focus: the head view a click on the virus opens. Empty: made on first use.")]
    public GenomeView headView;
    [Tooltip("The stores readout in the top-left corner. Empty: made at start.")]
    public InventoryView inventoryView;
    [Tooltip("Opens / closes the head view (the inventory).")]
    public Key inventoryKey = Key.E;
    [Tooltip("Leaves focus (entering it is the WorldButton's hold, bound to the same key in its input action).")]
    public Key focusKey = Key.F;
    [Tooltip("Focus: the least distance (pixels) from a resource chunk's core that counts as clicking it.")]
    [Min(1f)] public float corePickRadius = 26f;
    [Min(0.05f), Tooltip("Seconds between two RMB clicks that count as a double click (a tug).")]
    public float doubleClickTime = 0.3f;
    [Min(0.05f), Tooltip("RMB released within this many seconds is a click (cut the rope at your end); held longer, it reels.")]
    public float clickTime = 0.25f;

    Organism _o;
    Transform _cam;
    Camera _view;
    string _cameraMode;
    int _pressedBase = -1; // rope base the current LMB press started on
    float _lastRmbClick = float.NegativeInfinity, _rmbDownAt;
    bool _rmbTugged;   // this RMB press was a double click's second: not a click of its own
    bool _cutPending;  // an RMB click: cut once it's clearly not the first of a double
    bool _lmbConsumed; // this LMB press took a rope end: no paying out, raising or anchoring
    Genome _genome;
    VirusInventory _inventory;
    bool _freedCursor; // the open inventory freed the cursor (out of focus)

    public Organism Organism => _o ? _o : _o = GetComponent<Organism>();
    public VirusInventory Inventory => _inventory ? _inventory : _inventory = VirusInventory.Of(this);
    public bool IsGrounded => Organism.InState(Organism.grounded);
    public bool IsFocusMode => Organism.InState(Organism.grounded.focus);
    /// <summary>Escape would close something of ours (focus, the rope menu, the head view): the pause menu waits.</summary>
    public bool ClaimsEscape => IsFocusMode || MenuOpen || ViewOpen;

    // Pass-throughs for existing callers.
    public float speed => Organism.speed;
    public float totalSpeed => Organism.totalSpeed;
    public float normalizedSpeed => Organism.normalizedSpeed;
    public Vector3 surfaceNormal => Organism.grounded.surface.Normal;
    public bool ExternalFlightRotationControl => Organism.ExternalRotation;
    public void SetExternalFlightRotationControl(bool on) => Organism.SetExternalRotation(on);

    void Awake()
    {
        _cam = cameraTransform;
        if (!rope) rope = GetComponentInChildren<VirusRope>(true);
        Organism.StateChanged += OnStateChanged;
        Organism.grounded.tethering.slam.Impact += OnTetherImpact;
        RefreshButton();
    }

    void Start() => EnsureViews(); // the corner readout is always up

    void OnDisable()
    {
        UniversalCamera.PointerCaptured = false;
        if (_freedCursor && !CommandMode.Active) UniversalCamera.FreeCursor = false;
        _freedCursor = false;
        if (rope)
        {
            rope.HoveredBase = -1;
            rope.ShowBaseCores = false;
        }
        _pressedBase = -1;
    }

    void OnDestroy()
    {
        if (!_o) return;
        _o.StateChanged -= OnStateChanged;
        _o.grounded.tethering.slam.Impact -= OnTetherImpact;
    }

    void OnTetherImpact()
    {
        if (rope) rope.PlaceAnchor();
    }

    void Update()
    {
        if (PauseMenu.IsOpen) return; // its clicks and keys aren't ours
        if (!_cam && Camera.main) _cam = Camera.main.transform;
        RefreshButton(); // toggles only on mismatch, so an active hold is never reset
        bool focus = IsFocusMode, commanding = CommandMode.Active;
        UpdateInventoryKey(focus, commanding);
        int hoveredBase = UpdateRopeBases(focus && !commanding);
        bool headCaptured = UpdateHeadView(focus && !commanding, hoveredBase >= 0);
        bool coreCaptured = UpdateCores(focus && !commanding, hoveredBase >= 0 || headCaptured);
        UpdateCursor(focus);
        UniversalCamera.PointerCaptured = commanding || hoveredBase >= 0 || _pressedBase >= 0 || headCaptured || coreCaptured ||
                                          headView && headView.Dragging || ((MenuOpen || ViewOpen) && PointerOverUI());

        if (IsFocusMode)
        {
            SetRope(false, false);
            if (rope) rope.PhantomEnd = -1;
            Keyboard k = commanding ? null : Keyboard.current;
            if (k != null && k[focusKey].wasPressedThisFrame) ExitFocusMode();
            else if (k != null && k.escapeKey.wasPressedThisFrame)
            {
                if (MenuOpen) ropeMenu.Close(); // Escape closes the menu or head view first
                else if (ViewOpen) headView.Close();
                else ExitFocusMode();
            }
            return;
        }

        bool grounded = IsGrounded;
        Vector3 forward = _cam ? _cam.forward : Vector3.forward;
        Vector3 up = _cam ? _cam.up : Vector3.up;
        Vector3 right = _cam ? _cam.right : Vector3.right;

        _o.aimForward = forward;
        _o.aimUp = up;

        if (grounded)
        {
            // Screen-relative on the surface's tangent plane. Right comes from the camera's own right,
            // NOT Cross(normal, forward): that flips left/right whenever the surface faces away from
            // the camera, e.g. walking around the far side of a cell.
            Vector3 n = _o.grounded.surface.Normal;
            Vector3 f = Vector3.ProjectOnPlane(forward, n);
            if (f.sqrMagnitude < 1e-4f) f = Vector3.ProjectOnPlane(up, n); // looking straight down the normal
            f.Normalize();

            Vector3 r = Vector3.ProjectOnPlane(right, n);
            right = r.sqrMagnitude > 1e-4f ? r.normalized : Vector3.Cross(n, f);
            forward = f;
        }

        Vector2 input = ReadMove();
        _o.Move = forward * input.y + right * input.x; // Move clamps to length 1, so diagonals aren't faster

        WorldButton b = groundedWorldButton;
        _o.Hold(Intent.Drill, b && b.IsHolding ? Mathf.Max(b.HoldProgress, 1e-4f) : 0f);

        if (ActionPressed()) _o.Press(grounded ? Intent.Jump : Intent.Charge);

        // Rope. LMB: with nothing held and an end in reach (loose, or anchored from closer in; shown
        // as a phantom) takes it; otherwise pays out (grounded: raises too, a tap slams + anchors).
        // RMB held reels in, an RMB click cuts the rope at your end, double RMB tugs.
        Mouse m = commanding || ViewOpen ? null : Mouse.current; // the command mode / open inventory has the mouse
        bool lmb = m != null && m.leftButton.isPressed, rmb = m != null && m.rightButton.isPressed;
        if (!lmb) _lmbConsumed = false;

        if (rope)
        {
            bool held = rope.HasRopeAttachedToPlayer;
            int phantom = held ? -1 : rope.LooseEndInReach();
            if (!held && phantom < 0) phantom = rope.BaseEndInReach();
            rope.PhantomEnd = phantom;

            // Take it. The press is spent: no paying out, raising or anchoring.
            if (phantom >= 0 && m != null && m.leftButton.wasPressedThisFrame)
            {
                rope.GrabEnd(phantom);
                _lmbConsumed = true;
            }

            if (m != null && m.rightButton.wasPressedThisFrame)
            {
                _rmbDownAt = Time.time;
                _rmbTugged = Time.time - _lastRmbClick <= doubleClickTime;
                if (_rmbTugged)
                {
                    _cutPending = false; // the first click was half of this double, not a cut
                    rope.Tug();
                    _lastRmbClick = float.NegativeInfinity; // a third click starts a new pair
                }
            }
            if (m != null && m.rightButton.wasReleasedThisFrame && !_rmbTugged && Time.time - _rmbDownAt <= clickTime)
            {
                _lastRmbClick = Time.time;
                _cutPending = true;
            }
            if (_cutPending && Time.time - _lastRmbClick > doubleClickTime)
            {
                _cutPending = false;
                rope.ReleaseHeld();
            }
        }

        _o.Hold(Intent.Tether, grounded && lmb && !_lmbConsumed ? 1f : 0f);

        bool payOut = lmb && !_lmbConsumed && (!grounded || _o.grounded.tethering.slam.Committed);
        if (rope && payOut) rope.StartFromSpool();
        SetRope(rmb, payOut);

        // Reeling or paying out turns the body (a little: Organism caps it) so the mouth follows the rope.
        if (rope && rope.LeanRequest(out Vector3 mouth, out Vector3 toward, out float pull)) _o.Lean(mouth, toward, pull);
    }

    void SetRope(bool reel, bool feed)
    {
        if (!rope) return;
        rope.reel = reel;
        rope.feed = feed;
    }

    // Focus: hover grows a rope's base, a click on it opens the rope menu there; a click anywhere
    // else (not on the menu) closes it. Returns the hovered base (-1 none).
    int UpdateRopeBases(bool focus)
    {
        if (!rope) return -1;
        rope.ShowBaseCores = focus; // a glowing core in each base says it can be clicked
        Mouse m = Mouse.current;
        bool held = m != null && m.leftButton.isPressed;
        bool clicked = m != null && m.leftButton.wasPressedThisFrame;
        if (!held || !focus) _pressedBase = -1;
        if (!focus && MenuOpen) ropeMenu.Close();

        bool overUI = PointerOverUI();
        int hovered = -1;
        bool free = focus && m != null && (!held || clicked) && !overUI;
        if (free) hovered = rope.BaseAt(ViewCamera, m.position.ReadValue(), ropeBaseRadius);

        if (hovered >= 0 && clicked)
        {
            if (!ropeMenu) ropeMenu = RopeRadialMenu.Create();
            if (ViewOpen) headView.Close();
            ropeMenu.Open(rope, hovered, ViewCamera);
            _pressedBase = hovered;
        }
        else if (focus && clicked && !overUI && MenuOpen) ropeMenu.Close();

        rope.HoveredBase = hovered >= 0 ? hovered : _pressedBase >= 0 ? _pressedBase : MenuOpen ? ropeMenu.Handle : -1;
        return hovered;
    }

    // Focus: a click on the virus (its head, or within headPickRadius of it) opens the head view;
    // a click anywhere else (not on it) closes it. A rope base under the cursor wins. Returns
    // whether the cursor is on the virus (so the camera's drag-to-look leaves that click alone).
    bool UpdateHeadView(bool focus, bool baseHovered)
    {
        if (headView) headView.CanInject = focus; // out of focus there's no drill: strands only load
        if (!focus)
        {
            // Out of focus only the inventory key opens it; a click off it closes it.
            Mouse mouse = Mouse.current;
            if (ViewOpen && mouse != null && mouse.leftButton.wasPressedThisFrame && !PointerOverUI()) headView.Close();
            if (_genome) _genome.Hovered = ViewOpen;
            return false;
        }
        Mouse m = Mouse.current;
        Camera cam = ViewCamera;
        if (m == null || !cam) return false;
        bool clicked = m.leftButton.wasPressedThisFrame;
        bool overUI = PointerOverUI();

        if (!_genome) _genome = Genome.Of(this); // not ??=: Unity's fake null
        bool onHead = !overUI && !baseHovered && (!m.leftButton.isPressed || clicked) && OverHead(cam, m.position.ReadValue());
        if (onHead && clicked)
        {
            EnsureViews();
            if (MenuOpen) ropeMenu.Close();
            headView.Open(_genome, cam);
        }
        else if (clicked && !overUI && ViewOpen) headView.Close();
        _genome.Hovered = onHead || ViewOpen; // grows while pointed at, and stays grown while open
        return onHead;
    }

    bool OverHead(Camera cam, Vector2 pointer)
    {
        Vector3 centre = _genome.HeadSphere(out float radius);
        Vector3 screen = cam.WorldToScreenPoint(centre);
        if (screen.z <= 0f) return false;
        float reach = Mathf.Max(headPickRadius, Vector2.Distance(screen, cam.WorldToScreenPoint(centre + cam.transform.right * radius)));
        return Vector2.Distance(screen, pointer) <= reach;
    }

    // ---------------- inventory ----------------

    // The key toggles it (the drill into focus is F's now: WorldButton).
    void UpdateInventoryKey(bool focus, bool commanding)
    {
        Keyboard k = Keyboard.current;
        if (k == null) return;
        if (commanding)
        {
            if (ViewOpen && !focus) headView.Close(); // the command mode takes the cursor
            return;
        }
        if (k[inventoryKey].wasPressedThisFrame) ToggleInventory();
        if (!focus && ViewOpen && k.escapeKey.wasPressedThisFrame) headView.Close();
    }

    public void ToggleInventory()
    {
        if (ViewOpen) headView.Close();
        else OpenInventory();
    }

    public void OpenInventory()
    {
        if (ViewOpen) return;
        Camera cam = ViewCamera;
        if (!cam) return;
        if (!_genome) _genome = Genome.Of(this);
        EnsureViews();
        if (MenuOpen) ropeMenu.Close();
        headView.CanInject = IsFocusMode;
        headView.Open(_genome, cam);
    }

    void EnsureViews()
    {
        if (!headView) headView = GenomeView.Create();
        if (!inventoryView) inventoryView = InventoryView.Create();
        headView.Inventory = Inventory;
        if (!_genome) _genome = Genome.Of(this);
        inventoryView.Bind(headView, Inventory, _genome);
    }

    // Out of focus the open inventory needs a pointer: freed while it's open (unless the command mode
    // already has it).
    void UpdateCursor(bool focus)
    {
        bool want = ViewOpen && !focus;
        if (want == _freedCursor) return;
        _freedCursor = want;
        if (want) UniversalCamera.FreeCursor = true;
        else if (!CommandMode.Active) UniversalCamera.FreeCursor = false;
    }

    // Focus: resource chunks in reach show their cores; pointing at one lights it, a click starts (or
    // stops) extracting it (starting opens the head view, where it's seen flowing into its store).
    // Rope bases and the head win. Returns whether the cursor is on a core.
    bool UpdateCores(bool focus, bool blocked)
    {
        if (!ResourceField.Any) return false;
        ResourceField field = ResourceField.Instance;
        if (!field) return false;
        if (!focus)
        {
            field.Hovered = null;
            return false;
        }
        field.ShowCores(_o);
        Mouse m = Mouse.current;
        Camera cam = ViewCamera;
        if (m == null || !cam) return false;
        bool clicked = m.leftButton.wasPressedThisFrame;
        bool free = !blocked && !PointerOverUI() && (!m.leftButton.isPressed || clicked);
        ResourceChunk chunk = free ? field.CoreAt(cam, m.position.ReadValue(), corePickRadius) : null;
        field.Hovered = chunk;
        if (chunk && clicked)
        {
            field.ToggleExtract(chunk, Inventory, _o);
            if (chunk.Extractor == Inventory) OpenInventory();
        }
        return chunk;
    }

    bool MenuOpen => ropeMenu && ropeMenu.IsOpen;
    bool ViewOpen => headView && headView.IsOpen;

    Camera ViewCamera
    {
        get
        {
            if (!_view || _view.transform != _cam) _view = _cam ? _cam.GetComponent<Camera>() : null;
            return _view ? _view : Camera.main;
        }
    }

    // The head view's sphere is asked directly: its click catcher is an invisible image, which the
    // EventSystem can skip (not drawn, so not hit), and a click on a strand then closed the view.
    bool PointerOverUI() => EventSystem.current && EventSystem.current.IsPointerOverGameObject()
                            || ViewOpen && Mouse.current != null && headView.Covers(Mouse.current.position.ReadValue());

    static Vector2 ReadMove()
    {
        Vector2 v = Vector2.zero;
        Keyboard k = Keyboard.current;

        if (k != null)
            v = new Vector2((k.dKey.isPressed ? 1f : 0f) - (k.aKey.isPressed ? 1f : 0f),
                            (k.wKey.isPressed ? 1f : 0f) - (k.sKey.isPressed ? 1f : 0f));

        if (Gamepad.current != null) v += Gamepad.current.leftStick.ReadValue();
        return Vector2.ClampMagnitude(v, 1f);
    }

    static bool ActionPressed() =>
        (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame) ||
        (Gamepad.current != null && Gamepad.current.buttonSouth.wasPressedThisFrame);

    // ---------------- focus ----------------

    /// <summary>Request Focus; Drilling plays the slam first. Safe to hook to the button's completion event.</summary>
    public void EnterFocusMode()
    {
        if (IsGrounded && !IsFocusMode) _o.Press(Intent.Focus);
    }

    public void ExitFocusMode()
    {
        _o.Hold(Intent.Focus, 0f);
        _o.grounded.drilling.slam.Cancel();
    }

    public void ToggleFocusMode()
    {
        if (IsFocusMode || _o.InState(_o.grounded.drilling)) ExitFocusMode();
        else EnterFocusMode();
    }

    void OnStateChanged(OrganismState previous, OrganismState current)
    {
        OrganismState focus = _o.grounded.focus;
        bool wasFocus = previous != null && previous.Is(focus);
        bool isFocus = current != null && current.Is(focus);
        bool grounded = current != null && current.Is(_o.grounded);

        if (!grounded)
        {
            // Nothing focus-related survives leaving the surface.
            _o.Hold(Intent.Focus, 0f);
            _o.Hold(Intent.Drill, 0f);
            _o.Hold(Intent.Tether, 0f);
        }

        if (wasFocus != isFocus && ViewOpen) headView.Close(); // injecting vs loading changes: start over
        if (wasFocus && !isFocus) onFocusModeExit?.Invoke(); // reverse the screen effect before the camera moves
        RefreshButton();
        SetCameraMode(isFocus ? focusCameraMode : grounded ? groundedCameraMode : flyingCameraMode);
        if (isFocus && !wasFocus) onFocusModeEnter?.Invoke();
    }

    // ---------------- button / camera ----------------

    /// <summary>Show only while grounded and not focused. Show() resets a hold, so only call it on a mismatch.</summary>
    void RefreshButton()
    {
        WorldButton b = groundedWorldButton;
        if (!b) return;
        bool grounded = IsGrounded;
        if (grounded) _groundedAt = Time.unscaledTime;
        // Off the ground for a moment (an edge, a hop) keeps it up: hiding re-popped it and reset the hold.
        bool want = !IsFocusMode && (grounded || b.IsVisible && Time.unscaledTime - _groundedAt < buttonGrace);
        if (want != b.IsVisible) b.SetVisible(want);
    }

    float _groundedAt = -10f;

    /// <summary>Only on an actual change, so substate swaps don't restart rig transitions.</summary>
    void SetCameraMode(string mode)
    {
        if (string.IsNullOrEmpty(mode) || mode == _cameraMode || cameraRigs == null) return;
        _cameraMode = mode;
        foreach (UniversalCamera rig in cameraRigs) if (rig) rig.SetMode(mode);
    }
}