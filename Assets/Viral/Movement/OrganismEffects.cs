using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

// ===========================================================================
// Effects: reusable behaviour a state lists as fields; override only what it
// needs. In the inspector an effect has no header of its own: its settings
// show directly inside the state, and settingless effects take no space.
// ===========================================================================

[Serializable]
public abstract class Effect : OrganismPart
{
    public OrganismState S { get; private set; }
    protected Organism O => S.O;
    internal void Bind(OrganismState state) => S = state;

    public virtual void OnCollision(Collision c) { } // every effect, active or not
    public virtual void OnHalt() { }                  // every effect
    public virtual float TopSpeed => 0f;
}

// ---------------- body ----------------

/// <summary>
/// Kinematic while active; dynamic again after. Interpolation is off meanwhile:
/// the surface poses the transform directly every frame, and Rigidbody
/// interpolation would keep replacing that pose with a blend of old physics poses.
/// </summary>
[Serializable]
public class Kinematic : Effect
{
    RigidbodyInterpolation _interpolation;

    public override void Enter()
    {
        O.SetExternalRotation(false);
        O.Rb.linearVelocity = O.Rb.angularVelocity = Vector3.zero;
        O.Rb.isKinematic = true;
        _interpolation = O.Rb.interpolation;
        O.Rb.interpolation = RigidbodyInterpolation.None;
    }

    public override void Exit()
    {
        O.Rb.rotation = O.transform.rotation; // start physics from the pose we were showing
        O.Rb.isKinematic = false;
        O.Rb.useGravity = O.airGravity;
        O.Rb.interpolation = _interpolation;
    }
}

/// <summary>Stop all movement on enter.</summary>
[Serializable]
public class Halt : Effect
{
    public override void Enter() => O.Halt();
}

/// <summary>Match TargetRotation exactly (nothing to steer).</summary>
[Serializable]
public class SnapRotation : Effect
{
    public override void LateTick(float dt) => O.Rotate(1f);
}

// ---------------- air ----------------

[Serializable]
public class Coast : Effect
{
    [Tooltip("u/s². Lower coasts further.")] public float deceleration = 6f;

    public override void FixedTick(float dt) =>
        O.Rb.linearVelocity = Vector3.MoveTowards(O.Rb.linearVelocity, Vector3.zero, deceleration * dt);
}

/// <summary>Accelerate toward Move * speed, facing the thrust.</summary>
[Serializable]
public class Thrust : Effect
{
    public float speed = 12f;
    [Tooltip("u/s².")] public float acceleration = 40f;
    [Range(0f, 1f), Tooltip("Extra authority against momentum (turning). 0 none, 0.5 ~double, 1 override.")]
    public float control = 0.5f;

    public override float TopSpeed => speed;

    public override void FixedTick(float dt)
    {
        Vector3 v = O.Rb.linearVelocity, target = O.Move * speed, change = target - v;

        // Boost only against momentum, so straight-line build-up still reads as acceleration.
        float against = change.sqrMagnitude > 1e-6f && v.sqrMagnitude > 1e-6f ? Mathf.Clamp01(-Vector3.Dot(change.normalized, v.normalized)) : 0f;
        float accel = acceleration * (1f + against * control / Mathf.Max(1f - control, 1e-4f));

        O.Rb.linearVelocity = Vector3.MoveTowards(v, target, accel * dt);
        O.Face(O.Move); // aim at thrust, not the wandering velocity
    }
}

/// <summary>Dash along aimForward, direction locked, speed shaped over the state's time.</summary>
[Serializable]
public class Burst : Effect
{
    [Tooltip("Peak speed.")] public float speed = 30f;
    [Min(0.01f)] public float duration = 0.35f;
    [Min(0f), Tooltip("Seconds after it ends before it can start again.")] public float cooldown = 0.6f;
    [Tooltip("Fraction of peak speed over the dash. End near cruise speed to hand back smoothly.")]
    public AnimationCurve curve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0.4f);

    Vector3 _dir;

    public override float TopSpeed => speed;
    public override void Enter() { _dir = O.aimForward.normalized; O.Face(_dir); }
    public override void FixedTick(float dt) => O.Rb.linearVelocity = _dir * (speed * curve.Evaluate(S.TimeInState / duration));
}

/// <summary>Launch along the organism's up (the last surface normal) on enter.</summary>
[Serializable]
public class Launch : Effect
{
    public float force = 8f;

    public override void Enter()
    {
        O.Rb.isKinematic = false;
        O.Rb.linearVelocity = O.up * force;
    }
}

// ---------------- ground ----------------

/// <summary>Attaches to a Surface on contact and rides it every frame.</summary>
[Serializable]
public class Ground : Effect
{
    public NavSurface nav = new NavSurface();

    public bool Attached => nav.Attached;
    public Vector3 Normal => nav.Normal;

    // Last touchdown, for effects like Ripple.
    public Collider HitCollider { get; private set; }
    public Vector3 HitPoint { get; private set; }
    public float HitSpeed { get; private set; }

    public override void Enter() => O.keepUpright = true; // on a curved cell, lagging the up is what tilts
    public override void Tick(float dt) => Pose();
    public override void LateTick(float dt) => Pose(); // re-pin after the cell's final pose

    public override void Exit()
    {
        nav.Release();
        O.keepUpright = false;
    }

    public override void OnCollision(Collision c)
    {
        if (!S.enabled || Attached || c.contactCount == 0 || !nav.Accepts(c.gameObject)) return;

        ContactPoint hit = c.GetContact(0);
        HitCollider = c.collider;
        HitPoint = hit.point;
        HitSpeed = c.relativeVelocity.magnitude; // before landing zeroes it
        Land(hit.point, hit.normal, c.gameObject.GetComponentInParent<Surface>());
    }

    /// <summary>Attach and switch in now (kinematic before the next physics step). False if outranked.</summary>
    public bool Land(Vector3 point, Vector3 up, Surface on)
    {
        if (!nav.TryAttach(point, up, on, O.RotTarget.forward)) return false;
        O.Refresh();
        if (!O.InState(S)) { nav.Release(); return false; }
        Pose();
        return true;
    }

    /// <summary>Ripple the cell under the body as if hit at 'speed' (scaled by the cell's Reference Speed).</summary>
    public void RippleCell(float speed)
    {
        if (!Attached || speed <= 0f || !nav.Surface) return;
        nav.Surface.AddImpact(O.transform.position - Normal * nav.hoverHeight, speed);
    }

    public void Detach(Vector3 velocity)
    {
        nav.Release();
        O.Refresh();
        O.Rb.isKinematic = false;
        O.Rb.linearVelocity = velocity;
    }

    public void Pose()
    {
        if (!Attached) return;
        nav.Pose(out Vector3 position, out Quaternion rotation);
        O.transform.position = position;
        O.TargetRotation = rotation;
        O.up = nav.Normal;
    }
}

/// <summary>Ripple the cell just landed on, on enter.</summary>
[Serializable]
public class Ripple : Effect
{
    public override void Enter()
    {
        Ground s = S.Find<Ground>();
        Surface surface = s != null && s.HitCollider ? s.HitCollider.GetComponentInParent<Surface>() : null;
        if (surface) surface.AddImpact(s.HitPoint, s.HitSpeed);
    }
}

/// <summary>Walk the surface along Move, with optional speed curves.</summary>
[Serializable]
public class Crawl : Effect
{
    public float speed = 4f;
    [Tooltip("Ramp speed with the curves (X time 0-1, Y progress 0-1). Off = instant.")] public bool useSpeedCurves;
    [Min(0.01f)] public float accelerationTime = 0.28f, decelerationTime = 0.20f;
    public AnimationCurve accelerationCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
    public AnimationCurve decelerationCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    float _now, _t, _from;
    bool _hadInput;
    Ground _surface;

    public bool Coasting => _now > 0f;
    public override float TopSpeed => speed;
    public override void Exit() => OnHalt();
    public override void OnHalt() { _now = _t = _from = 0f; _hadInput = false; }

    public override void Tick(float dt)
    {
        Ground s = _surface ??= S.Find<Ground>();
        if (s == null || !s.Attached) return;

        Vector3 wish = Vector3.ProjectOnPlane(O.Move, s.Normal);
        bool input = wish.sqrMagnitude > 1e-6f;
        if (input) s.nav.SetHeading(wish); // coasting keeps the last heading

        float step = Ramp(input ? O.Move.magnitude : 0f, dt) * dt;
        if (step <= 0f) return;

        if (!s.nav.Crawl(step, out float moved)) { s.Detach(s.nav.Heading * _now); return; } // walked off the mesh
        O.speed = moved / dt;
        s.Pose();
    }

    float Ramp(float input, float dt)
    {
        float target = speed * input;
        if (!useSpeedCurves) return _now = target;

        bool pressed = input > 1e-3f;
        if (pressed != _hadInput) { _hadInput = pressed; _t = 0f; _from = _now; } // restart only on press/release

        _t = Mathf.Min(1f, _t + dt / (pressed ? accelerationTime : decelerationTime));
        float progress = (pressed ? accelerationCurve : decelerationCurve).Evaluate(_t);
        return _now = _t >= 1f ? target : Mathf.Max(0f, Mathf.LerpUnclamped(_from, target, progress));
    }
}

/// <summary>
/// Hold beat: the progress intent's value lifts the body; at 1 (or a result
/// press) it slams down, then holds the result intent after a delay.
/// </summary>
[Serializable]
public class HoldSlam : Effect
{
    public string progressIntent = Intent.Drill, resultIntent = Intent.Focus;
    [Min(0f)] public float liftAmount = 0.30f, slamDepth = 0.16f;
    [Min(0.1f), Tooltip(">1 builds harder near completion.")] public float liftPower = 1.35f;
    [Min(0.01f)] public float slamDuration = 0.10f;
    [Min(0f), Tooltip("Seconds from completion until the result.")] public float resultDelay = 0.18f;
    [Min(0f), Tooltip("How hard the slam ripples the cell, as an impact speed (the cell's Reference Speed = strength 1). 0 = none.")]
    public float rippleSpeed = 150f;

    float _slamAt = -1f, _from;
    bool _rippled;

    public bool Slamming => _slamAt >= 0f;
    public void Cancel() { _slamAt = -1f; O.Hold(progressIntent, 0f); }
    public override void Exit() => _slamAt = -1f;

    public override void Tick(float dt)
    {
        float progress = O.Value(progressIntent);

        if (!Slamming && (progress >= 1f || O.Pressed(resultIntent)))
        {
            _slamAt = Time.time;
            _from = O.BodyOffset;
            _rippled = false;
            O.Halt();
        }

        if (!Slamming)
        {
            O.BodyOffset = liftAmount * Mathf.Pow(progress, liftPower);
            return;
        }

        float t = Time.time - _slamAt;
        if (t < slamDuration) O.BodyOffset = Mathf.LerpUnclamped(_from, -slamDepth, Mathf.Pow(t / slamDuration, 3f)); // hard in, eases home after
        else if (!_rippled)
        {
            _rippled = true;
            S.Find<Ground>()?.RippleCell(rippleSpeed);
        }
        if (t >= resultDelay) { Cancel(); O.Hold(resultIntent); }
    }
}

/// <summary>
/// Hold the intent: the body rises and stays up. Let go within tapTime: it
/// slams down and fires Impact at the bottom. Held longer: no slam on release,
/// the body just eases home.
/// </summary>
[Serializable]
public class TapSlam : Effect
{
    public string intent = Intent.Tether;
    [Min(0f)] public float liftAmount = 0.2f;
    [Min(0.01f), Tooltip("Seconds to rise.")] public float liftTime = 0.12f;
    [Min(0.01f), Tooltip("Releasing sooner than this counts as a tap.")] public float tapTime = 0.25f;
    [Min(0f)] public float slamDepth = 0.16f;
    [Min(0.01f)] public float slamDuration = 0.10f;
    [Min(0f), Tooltip("How hard the slam ripples the cell, as an impact speed (the cell's Reference Speed = strength 1). 0 = none.")]
    public float rippleSpeed = 60f;

    /// <summary>Fires when a tap's slam hits bottom.</summary>
    public event Action Impact;

    // Each press is timed on its own, so taps landing mid-slam or right after one still count.
    float _pressAt = -1f, _liftAt = -1f, _slamAt = -1f, _from;
    bool _wasHeld, _slamQueued;

    bool Held => O.Holding(intent);
    bool Active => O.InState(S);
    public bool Slamming => _slamAt >= 0f;

    // Released this frame before Tick has seen it: keep the state alive so the slam can start.
    bool TapReleased => _wasHeld && !Held && Time.time - _pressAt < tapTime;

    /// <summary>Keeps the state alive after a quick release long enough to slam.</summary>
    public bool Busy => Slamming || _slamQueued || TapReleased;

    /// <summary>This press is held past tapTime: no slam coming, safe to treat as a long hold.</summary>
    public bool Committed => Active && Held && !Slamming && _pressAt >= 0f && Time.time - _pressAt >= tapTime;

    public override void Enter() => ResetPress();
    public override void Exit() => ResetPress();

    void ResetPress()
    {
        _pressAt = _liftAt = _slamAt = -1f;
        _wasHeld = _slamQueued = false;
    }

    public override void Tick(float dt)
    {
        bool held = Held;
        if (held && !_wasHeld) { _pressAt = Time.time; _liftAt = -1f; }
        else if (!held && _wasHeld && Time.time - _pressAt < tapTime) _slamQueued = true; // a tap: slam once free
        _wasHeld = held;

        if (Slamming)
        {
            float t = (Time.time - _slamAt) / slamDuration;
            if (t < 1f) { O.BodyOffset = Mathf.LerpUnclamped(_from, -slamDepth, t * t * t); return; } // hard into impact
            _slamAt = -1f;
            S.Find<Ground>()?.RippleCell(rippleSpeed);
            Impact?.Invoke();
        }

        if (_slamQueued)
        {
            _slamQueued = false;
            _slamAt = Time.time;
            _from = O.BodyOffset;
            return;
        }

        if (!held) return; // eases home on its own

        // Rise from wherever the body is (e.g. the bottom of a slam), not from rest.
        if (_liftAt < 0f) { _liftAt = Time.time; _from = O.BodyOffset; }
        O.BodyOffset = Mathf.Lerp(_from, liftAmount, Mathf.SmoothStep(0f, 1f, (Time.time - _liftAt) / liftTime));
    }
}

#if UNITY_EDITOR
/// <summary>Draws an effect's settings inline in its state, with no foldout.</summary>
[CustomPropertyDrawer(typeof(Effect), true)]
class EffectDrawer : PropertyDrawer
{
    static IEnumerable<SerializedProperty> Fields(SerializedProperty p)
    {
        SerializedProperty it = p.Copy(), end = p.GetEndProperty();
        if (!it.NextVisible(true)) yield break;

        while (!SerializedProperty.EqualContents(it, end))
        {
            if (it.name != "enabled") yield return it;
            if (!it.NextVisible(false)) yield break;
        }
    }

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        float h = 0f;
        foreach (SerializedProperty f in Fields(property))
            h += EditorGUI.GetPropertyHeight(f, true) + EditorGUIUtility.standardVerticalSpacing;
        return Mathf.Max(0f, h - EditorGUIUtility.standardVerticalSpacing);
    }

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        float y = position.y;
        foreach (SerializedProperty f in Fields(property))
        {
            float h = EditorGUI.GetPropertyHeight(f, true);
            EditorGUI.PropertyField(new Rect(position.x, y, position.width, h), f, true);
            y += h + EditorGUIUtility.standardVerticalSpacing;
        }
    }
}
#endif