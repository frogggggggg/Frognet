using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>Intent names. Add your own anywhere; they're just strings.</summary>
public static class Intent
{
    public const string Jump = "Jump", Charge = "Charge"; // press
    public const string Tether = "Tether";                 // hold: raise; quick tap slams
    public const string Drill = "Drill";                   // hold, value = progress 0-1
    public const string Focus = "Focus";                   // press to request, hold to stay
}

public enum Phase { Update, Late, Fixed }

/// <summary>Shared lifecycle of states and effects.</summary>
[Serializable]
public abstract class OrganismPart : ToggleSection
{
    public virtual void Enter() { }
    public virtual void Exit() { }
    public virtual void Tick(float dt) { }
    public virtual void LateTick(float dt) { }
    public virtual void FixedTick(float dt) { }

    internal void Run(Phase phase, float dt)
    {
        switch (phase)
        {
            case Phase.Update: Tick(dt); break;
            case Phase.Late: LateTick(dt); break;
            default: FixedTick(dt); break;
        }
    }
}

// ===========================================================================
// A state is: when it applies + its effects + its substates.
//
//   When()         its own condition; parents are checked automatically.
//   Effect fields  reusable behaviour (OrganismEffects.cs) with its own settings.
//   State fields   substates. Children outrank their parent; later siblings
//                  outrank earlier ones (override Weight to change).
//
// Parents run first, and switching only exits/enters the levels that change,
// so Moving -> Still never re-runs Grounded's Kinematic. Override the
// lifecycle methods only for behaviour no effect covers.
// ===========================================================================

[Serializable]
public abstract class OrganismState : OrganismPart
{
    /// <summary>Code hooks (not shown in the inspector). Organism.StateChanged covers the whole tree.</summary>
    public event Action Entered, Exited;

    [NonSerialized] internal List<OrganismState> children;
    [NonSerialized] internal List<Effect> effects;
    [NonSerialized] int _order;
    [NonSerialized] float _enteredAt, _exitedAt = float.NegativeInfinity;

    public Organism O { get; private set; }
    public OrganismState Parent { get; private set; }
    public OrganismState[] Chain { get; private set; } // root first
    public string Name => GetType().Name;

    public virtual int Weight => _order;
    public virtual bool When() => true;

    public float TimeInState => Time.time - _enteredAt;
    public float TimeSinceExit => Time.time - _exitedAt;

    // Condition helpers.
    protected bool Held(string intent) => O.Holding(intent);
    protected bool Pressed(string intent) => O.Pressed(intent);
    protected bool Moving => O.Move.sqrMagnitude > 1e-6f;
    protected bool Active => O.InState(this);
    protected bool Lasting(float seconds) => Active && TimeInState < seconds;
    protected bool Cooldown(float seconds) => TimeSinceExit >= seconds;

    /// <summary>Nearest effect of type T on this state or a parent.</summary>
    public T Find<T>() where T : Effect
    {
        for (var s = this; s != null; s = s.Parent)
            foreach (Effect e in s.effects) if (e is T t) return t;
        return null;
    }

    public bool Is(OrganismState s) => Array.IndexOf(Chain, s) >= 0;

    internal bool Valid => enabled && When();

    internal void Bind(Organism o, OrganismState parent, int order)
    {
        O = o; Parent = parent; _order = order;
        Chain = parent == null ? new[] { this } : parent.Chain.Append(this).ToArray();
        children = new List<OrganismState>();
        effects = new List<Effect>();
    }

    internal void DoEnter()
    {
        _enteredAt = Time.time;
        foreach (Effect e in effects) e.Enter();
        Enter();
        Entered?.Invoke();
    }

    internal void DoExit()
    {
        foreach (Effect e in effects) e.Exit();
        Exit();
        _exitedAt = Time.time;
        Exited?.Invoke();
    }
}

// ===========================================================================
// Built-in tree
//   Flying    Still, Moving, Charging
//   Grounded  Still, Moving, Landing, Tethering, Drilling, Focus
//   Jumping
// ===========================================================================

[Serializable]
public class Flying : OrganismState
{
    [Tooltip("Local axis that leads while flying (the body travels underside-first with Down).")]
    public Vector3 leadAxis = Vector3.down;

    public FlyStill still = new FlyStill();
    public FlyMoving moving = new FlyMoving();
    public Charging charging = new Charging();
}

[Serializable]
public class FlyStill : OrganismState
{
    public Coast coast = new Coast();
}

[Serializable]
public class FlyMoving : OrganismState
{
    public Thrust thrust = new Thrust();
    public override bool When() => Moving;
}

[Serializable]
public class Charging : OrganismState
{
    public Burst burst = new Burst();
    public override bool When() => Lasting(burst.duration) || (Pressed(Intent.Charge) && Cooldown(burst.cooldown));
}

[Serializable]
public class Grounded : OrganismState
{
    public Kinematic kinematic = new Kinematic();
    public Ground surface = new Ground();

    public GroundStill still = new GroundStill();
    public GroundMoving moving = new GroundMoving();
    public Landing landing = new Landing();
    public Tethering tethering = new Tethering();
    public Drilling drilling = new Drilling();
    public Focus focus = new Focus();

    public override bool When() => surface.Attached;
}

[Serializable]
public class GroundStill : OrganismState { }

[Serializable]
public class GroundMoving : OrganismState
{
    public Crawl crawl = new Crawl();
    public override bool When() => Moving || crawl.Coasting;
}

/// <summary>Just touched down: settles before walking (outranks Moving).</summary>
[Serializable]
public class Landing : OrganismState
{
    [Tooltip("Seconds after touchdown before crawling.")] public float duration = 0.25f;
    public Ripple ripple = new Ripple();
    public SnapRotation snap = new SnapRotation();
    public override bool When() => Parent.TimeInState < duration;
}

/// <summary>Tether held: body raised and still. A quick tap slams down (TapSlam.Impact).</summary>
[Serializable]
public class Tethering : OrganismState
{
    public TapSlam slam = new TapSlam();
    public override bool When() => Held(Intent.Tether) || slam.Busy;
}

[Serializable]
public class Drilling : OrganismState
{
    public HoldSlam slam = new HoldSlam();
    public override bool When() => Held(Intent.Drill) || Pressed(Intent.Focus) || slam.Slamming;
}

[Serializable]
public class Focus : OrganismState
{
    public Halt halt = new Halt();
    public SnapRotation snap = new SnapRotation();
    public override bool When() => Held(Intent.Focus);
}

/// <summary>
/// Leaving the ground. Outranks Grounded, so a Jump press while attached
/// switches straight here. No air control for a moment, then Flying takes
/// over (or nothing, if Flying is off).
/// </summary>
[Serializable]
public class Jumping : OrganismState
{
    [Tooltip("Seconds before air control takes over.")] public float duration = 0.2f;
    public Launch launch = new Launch();
    public override bool When() => Lasting(duration) || (Pressed(Intent.Jump) && O.InState(O.grounded));
}