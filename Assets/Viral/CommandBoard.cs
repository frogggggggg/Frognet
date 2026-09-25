using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The command mode's plan: saved groups and who works on what. Agent groups (<see cref="Squad"/>,
/// "Agents 1") are linked to tasks (<see cref="Task"/>, "Cells 1"), many to many: a squad can work
/// several tasks, a task can have several squads. Each <see cref="Link"/> says what to do there (its
/// <see cref="Job"/>: attack, extract, move to; only what the targets allow) and how the squad's agents
/// take the targets on (<see cref="Link.oneAtATime"/>: split evenly over them, or all on one until it's
/// dealt with). Tasks can be chained (A then B): when a link's work is done, its squad moves on to the
/// tasks after it (<see cref="Advance"/>). Pure data plus <see cref="Dispatch"/>, which hands each agent
/// its order.
///
/// A task is anything that can give an agent an order and say when a link's work on it is done. Only
/// <see cref="TargetTask"/> for now; others (guard an area, escort...) subclass <see cref="Task"/>.
/// </summary>
public static class CommandBoard
{
    /// <summary>What a link's agents do at its targets.</summary>
    public enum Job { Attack, Extract, MoveTo }

    /// <summary>What targets allow (a <see cref="Selectable.affords"/>).</summary>
    [Flags]
    public enum Jobs { None = 0, Attack = 1, Extract = 2, MoveTo = 4 }

    public static Jobs Flag(Job j) => (Jobs)(1 << (int)j);

    public static readonly Job[] AllJobs = { Job.Attack, Job.Extract, Job.MoveTo };

    public static string Name(Job j) => j == Job.MoveTo ? "MOVE TO" : j.ToString().ToUpperInvariant();

    /// <summary>How a link's agents share its targets (<see cref="Link.oneAtATime"/>): spread over them, or
    /// focused on one at a time.</summary>
    public static string ModeName(bool oneAtATime) => oneAtATime ? "FOCUS" : "SPREAD";

    /// <summary>A saved group of selectables (members that are gone are dropped as it's read).</summary>
    public abstract class Group
    {
        public string name;
        /// <summary>Its own colour: its members' boxes, its tag in the world, its node on the board.</summary>
        public Color color = Color.white;
        public readonly List<Selectable> members = new List<Selectable>();

        public int Count
        {
            get
            {
                members.RemoveAll(m => !m);
                return members.Count;
            }
        }

        /// <summary>The middle of its members (world), false if none are left.</summary>
        public bool Centre(out Vector3 centre)
        {
            centre = Vector3.zero;
            int n = 0;
            foreach (Selectable m in members)
                if (m) { centre += m.WorldBounds.center; n++; }
            if (n > 0) centre /= n;
            return n > 0;
        }
    }

    /// <summary>Agents saved together ("Agents 1").</summary>
    public class Squad : Group { }

    /// <summary>A squad on a task: what it does there, and how its agents share the targets.</summary>
    public class Link
    {
        public Squad squad;
        public Task task;
        public Job job;
        /// <summary>Off: the agents are split evenly over the targets. On: all on one target (the one
        /// nearest the squad) until it's dealt with, then the next; for moving, the nearest.</summary>
        public bool oneAtATime;
        /// <summary>One at a time: the target being worked.</summary>
        public Selectable active;
        /// <summary>Every agent is at its goal (Dispatch).</summary>
        public bool arrived;
    }

    /// <summary>Work for squads.</summary>
    public abstract class Task : Group
    {
        /// <summary>The goal for the index-th of 'count' agents the link has on this task, or null.</summary>
        public abstract Transform GoalFor(Link link, int index, int count);

        /// <summary>The link's work here is finished: its squad moves on to the tasks chained after.</summary>
        public abstract bool Done(Link link);

        /// <summary>Finished for everyone (every target dealt with): passed through by chains.</summary>
        public virtual bool Complete => false;

        /// <summary>Whether this job makes sense here (not attacking a cell...).</summary>
        public abstract bool Affords(Job job);
    }

    /// <summary>Go at these targets. Split: the agents are shared out over the targets not yet dealt
    /// with. One at a time: all at the one nearest the squad until it's dealt with. Attack / extract are
    /// done when every target is gone or complete (a cell: yours), moving when every agent is there.</summary>
    public class TargetTask : Task
    {
        readonly List<Selectable> _open = new List<Selectable>();

        List<Selectable> Open()
        {
            _open.Clear();
            foreach (Selectable m in members)
                if (m && !m.Complete) _open.Add(m);
            return _open;
        }

        public override Transform GoalFor(Link link, int index, int count)
        {
            List<Selectable> open = Open();
            if (link.job == Job.MoveTo) // moving: any of them will do
            {
                members.RemoveAll(m => !m);
                open = members;
            }
            if (open.Count == 0) return Count == 0 ? null : members[index % Count].transform; // all done: stay by them
            if (!link.oneAtATime) return open[index % open.Count].transform;

            if (!link.active || !open.Contains(link.active)) // pick the nearest to the squad, and keep it
            {
                link.active = open[0];
                if (link.squad.Centre(out Vector3 at))
                {
                    float best = float.MaxValue;
                    foreach (Selectable s in open)
                    {
                        float d = (s.WorldBounds.center - at).sqrMagnitude;
                        if (d < best) { best = d; link.active = s; }
                    }
                }
            }
            return link.active.transform;
        }

        public override bool Done(Link link) => link.job == Job.MoveTo ? link.arrived : Complete;

        public override bool Complete
        {
            get
            {
                foreach (Selectable m in members)
                    if (m && !m.Complete) return false;
                return true;
            }
        }

        public override bool Affords(Job job)
        {
            foreach (Selectable m in members)
                if (m && (m.affords & Flag(job)) != 0) return true;
            return false;
        }
    }

    public static readonly List<Squad> Squads = new List<Squad>();
    public static readonly List<Task> Tasks = new List<Task>();
    static readonly List<Link> s_links = new List<Link>();
    static readonly List<(Task from, Task to)> s_chains = new List<(Task, Task)>();
    static readonly Dictionary<string, int> s_numbers = new Dictionary<string, int>();
    static int s_colors;

    // Group colours, handed out in turn: bright and far apart, none of them the command mode's hover
    // blue or selection yellow.
    static readonly Color[] Palette =
    {
        new Color(1f, 0.33f, 0.38f), new Color(0.3f, 1f, 0.55f), new Color(1f, 0.42f, 0.9f), new Color(0.3f, 0.95f, 1f),
        new Color(0.72f, 0.45f, 1f), new Color(1f, 0.58f, 0.22f), new Color(0.65f, 1f, 0.28f), new Color(1f, 0.62f, 0.7f),
    };

    /// <summary>Groups, links (or their settings) or chains changed.</summary>
    public static event Action Changed;

    /// <summary>Save these selectables as a group: a squad for agents, a target task for targets,
    /// named after them and numbered ("Agents 2").</summary>
    public static Group Save(List<Selectable> members)
    {
        if (members == null || members.Count == 0) return null;
        bool agents = members[0].category == Selectable.Category.Agent;
        Group g = agents ? new Squad() : new TargetTask();
        g.members.AddRange(members);
        string baseName = members[0].GroupName;
        s_numbers.TryGetValue(baseName, out int n);
        s_numbers[baseName] = ++n;
        g.name = baseName + " " + n;
        g.color = Palette[s_colors++ % Palette.Length];
        if (g is Squad s) Squads.Add(s);
        else Tasks.Add((Task)g);
        Changed?.Invoke();
        Dispatch();
        return g;
    }

    /// <summary>The group already saved with exactly these members (same category), else a new one
    /// saved from them (a line dragged straight onto things saves them on the way).</summary>
    public static Group FindOrSave(List<Selectable> members)
    {
        members.RemoveAll(m => !m);
        if (members.Count == 0) return null;
        bool agents = members[0].category == Selectable.Category.Agent;
        int count = agents ? Squads.Count : Tasks.Count;
        for (int i = 0; i < count; i++)
        {
            Group g = agents ? Squads[i] : (Group)Tasks[i];
            if (g.Count != members.Count) continue;
            bool same = true;
            foreach (Selectable m in members)
                if (!g.members.Contains(m)) { same = false; break; }
            if (same) return g;
        }
        return Save(members);
    }

    /// <summary>The name the next group saved from these would get.</summary>
    public static string NextName(string baseName) =>
        baseName + " " + ((s_numbers.TryGetValue(baseName, out int n) ? n : 0) + 1);

    public static void Remove(Group g)
    {
        var squads = new List<Squad>();
        foreach (Link l in s_links)
            if (l.squad == g || l.task == g) squads.Add(l.squad);
        s_links.RemoveAll(l => l.squad == g || l.task == g);
        s_chains.RemoveAll(c => c.from == g || c.to == g);
        if (g is Squad s) { Squads.Remove(s); squads.Add(s); }
        if (g is Task t) Tasks.Remove(t);
        foreach (Squad q in squads) Idle(q);
        Changed?.Invoke();
        Dispatch();
    }

    // ---------------- links: squad -> task ----------------

    public static IReadOnlyList<Link> Links => s_links;

    public static Link Find(Squad s, Task t) => s_links.Find(l => l.squad == s && l.task == t);

    /// <summary>The squad's link to the task, made (doing the first job the targets allow) if there
    /// isn't one.</summary>
    public static Link Connect(Squad s, Task t)
    {
        if (s == null || t == null) return null;
        Link l = Find(s, t);
        if (l != null) return l;
        l = new Link { squad = s, task = t, job = DefaultJob(t) };
        s_links.Add(l);
        Changed?.Invoke();
        Dispatch();
        return l;
    }

    public static void Disconnect(Link l)
    {
        if (l == null || !s_links.Remove(l)) return;
        Idle(l.squad);
        Changed?.Invoke();
        Dispatch();
    }

    /// <summary>Change a link's job (if its task allows it) or its mode.</summary>
    public static void Set(Link l, Job job, bool oneAtATime)
    {
        if (l == null) return;
        if (l.task.Affords(job)) l.job = job;
        l.oneAtATime = oneAtATime;
        l.active = null;
        l.arrived = false;
        Changed?.Invoke();
        Dispatch();
    }

    // Extract where it can, else attack, else move.
    static Job DefaultJob(Task t)
    {
        if (t.Affords(Job.Extract)) return Job.Extract;
        if (t.Affords(Job.Attack)) return Job.Attack;
        return Job.MoveTo;
    }

    static readonly List<Link> s_squadLinks = new List<Link>();

    public static List<Link> LinksOf(Squad s)
    {
        s_squadLinks.Clear();
        foreach (Link l in s_links)
            if (l.squad == s) s_squadLinks.Add(l);
        return s_squadLinks;
    }

    // A squad with no work left: its agents go back to their own devices.
    static void Idle(Squad s)
    {
        if (LinksOf(s).Count > 0) return;
        foreach (Selectable m in s.members)
            if (m) m.Commandable?.Order(null, Job.MoveTo);
    }

    // ---------------- chains: task -> task ----------------

    public static bool Chained(Task from, Task to) => s_chains.Contains((from, to));

    /// <summary>Chain 'to' after 'from' (squads done there go on to it), or undo it. A chain the other
    /// way round is replaced.</summary>
    public static void Chain(Task from, Task to)
    {
        if (from == null || to == null || from == to) return;
        if (!s_chains.Remove((from, to)))
        {
            s_chains.Remove((to, from));
            s_chains.Add((from, to));
        }
        Changed?.Invoke();
        Dispatch();
    }

    public static IReadOnlyList<(Task from, Task to)> Chains => s_chains;

    // The first tasks down the chains from this one not yet complete (complete ones are passed through).
    static void NextOpen(Task from, List<Task> into)
    {
        var seen = new HashSet<Task> { from };
        var queue = new Queue<Task>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            Task t = queue.Dequeue();
            foreach (var c in s_chains)
            {
                if (c.from != t || !seen.Add(c.to)) continue;
                if (c.to.Complete) queue.Enqueue(c.to);
                else into.Add(c.to);
            }
        }
    }

    /// <summary>Squads whose link's work is done move on to the next open tasks chained after it (all
    /// of them, sharing their agents out), keeping the job where the new task allows it and the mode.
    /// With nowhere to go they stay.</summary>
    public static bool Advance()
    {
        bool moved = false;
        var next = new List<Task>();
        for (int i = s_links.Count - 1; i >= 0; i--)
        {
            Link l = s_links[i];
            if (!l.task.Done(l)) continue;
            next.Clear();
            NextOpen(l.task, next);
            if (next.Count == 0) continue;
            s_links.RemoveAt(i);
            foreach (Task n in next)
                if (Find(l.squad, n) == null)
                    s_links.Add(new Link { squad = l.squad, task = n, job = n.Affords(l.job) ? l.job : DefaultJob(n), oneAtATime = l.oneAtATime });
            moved = true;
        }
        if (moved) Changed?.Invoke();
        return moved;
    }

    // ---------------- orders ----------------

    /// <summary>How near (metres, from its bounds) an agent must be to its goal to count as there.</summary>
    public static float ArriveDistance = 6f;

    /// <summary>Move squads on from finished work, then give every agent of every linked squad its
    /// order: a squad's agents are shared out over its links in turn, and each link's task gives each
    /// of them a goal. Called on every change, and now and then (CommandMode) so finished work and gone
    /// targets are noticed.</summary>
    public static void Dispatch()
    {
        Advance();
        foreach (Squad s in Squads)
        {
            List<Link> links = new List<Link>(LinksOf(s));
            if (links.Count == 0) continue;
            foreach (Link l in links) l.arrived = true;
            int agents = s.Count;
            for (int i = 0; i < agents; i++)
            {
                Selectable agent = s.members[i];
                ICommandable c = agent.Commandable;
                if (c == null) continue;
                Link link = links[i % links.Count];
                int onLink = (agents - 1 - i % links.Count) / links.Count + 1; // this squad's agents on it
                Transform goal = link.task.GoalFor(link, i / links.Count, onLink);
                c.Order(goal, link.job);
                if (goal && Near(agent, goal)) continue;
                link.arrived = false;
            }
        }
    }

    static bool Near(Selectable agent, Transform goal)
    {
        Bounds b = goal.TryGetComponent(out Selectable s) ? s.WorldBounds : new Bounds(goal.position, Vector3.zero);
        return b.SqrDistance(agent.transform.position) <= ArriveDistance * ArriveDistance;
    }

    /// <summary>Forget everything (a new scene).</summary>
    public static void Clear()
    {
        Squads.Clear();
        Tasks.Clear();
        s_links.Clear();
        s_chains.Clear();
        s_numbers.Clear();
        s_colors = 0;
        Changed?.Invoke();
    }
}
