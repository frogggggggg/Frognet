using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

/// <summary>
/// The command line's language (CommandLine.cs types it, CmdWorld.cs says what the game's things are). Minecraft-simple,
/// MATLAB-like: everything is a value, and a list shrinks by dots.
/// <code>
///   all                          everything
///   all.redbloodcell             a kind: only those
///   all.(dist &lt; 20)             a condition per element (its properties, index, it)
///   all.mix.(index &lt; 5)         five at random (also .random(5), .nearest(3), .first, .sort(dist))
///   all.cell.pos                 each one's pos (a list); arithmetic runs elementwise
///   me.pos += me.forward*10      change a property (=, +=, -=, *=, /=)
///   me.inventory += 50*glucose   a collection: += puts in, -= takes out
///   all.cell.kill                a verb, done to each
///   x = all.cell.nearest; x.print     variables live for the session
/// </code>
/// Game-agnostic: properties (<see cref="Prop"/>), verbs, kinds, words and functions are registered from outside.
/// Autocomplete (<see cref="Complete"/>) and the live preview (<see cref="Preview"/>) evaluate with <see cref="Env.dry"/>
/// set: verbs, assignments and spawns report what they would do and do nothing.
/// </summary>
public static class Cmd
{
    // ================= values =================

    /// <summary>A list (all, all.cell, all.cell.pos...).</summary>
    public sealed class Many : List<object>
    {
        public Many() { }
        public Many(int capacity) : base(capacity) { }
        public Many(IEnumerable<object> items) : base(items) { }
    }

    /// <summary>Named values reached with a dot (dna.kill).</summary>
    public sealed class Space
    {
        public string name;
        public readonly Dictionary<string, object> members = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        public override string ToString() => name;
    }

    /// <summary>A mistake in a command, shown to the player as is.</summary>
    public sealed class Error : Exception { public Error(string message) : base(message) { } }

    public enum Tone { Info, Result, Error, Echo }

    // ================= what the world registers =================

    /// <summary>A property of some values (things' pos, a vector's x). Several can share a name (on different values).</summary>
    public sealed class Prop
    {
        public string name, help;
        public Func<object, bool> on;              // which values have it
        public Func<object, object> get;
        public Action<object, object> set;         // null: read only
        public Func<object, object, string> add;    // a collection: += puts a value in (returns a report)
        public Func<object, object, string> remove; // -= takes it out
    }

    /// <summary>Something done to each thing of a list (all.cell.kill).</summary>
    public sealed class Verb
    {
        public string name, help, done; // done: "killed"
        public Func<object, bool> on;
        public Func<object, bool> run;  // true: it happened
    }

    /// <summary>An operation on a whole list (all.mix, all.random(5)). A single value is a list of one.</summary>
    public sealed class ListOp
    {
        public string name, help, args; // args as shown: "", "(n)", "(key)"
        public bool needsArgs;          // completion adds "("
        public bool perElement;         // its arguments are evaluated once per element, like .( )
        public Func<Env, Many, Node[], object> run;
    }

    public sealed class Fn
    {
        public string name, help, args;
        public Func<Env, object[], object> run;
    }

    public sealed class Word
    {
        public string name, help;
        public Func<Env, object> get;
    }

    public static readonly Dictionary<string, Word> Words = new Dictionary<string, Word>(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, Fn> Fns = new Dictionary<string, Fn>(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, ListOp> ListOps = new Dictionary<string, ListOp>(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, Verb> Verbs = new Dictionary<string, Verb>(StringComparer.OrdinalIgnoreCase);
    public static readonly List<Prop> Props = new List<Prop>();
    /// <summary>The player's variables (x = all.cell), kept for the session.</summary>
    public static readonly Dictionary<string, object> Vars = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Kinds ("tags"): whether a value is one (a thing tagged redbloodcell), every kind it is, every kind
    /// there is (so all.whitebloodcell with none about is an empty list, not an error), its main one (summaries).</summary>
    public static Func<object, string, bool> Is = (v, kind) => false;
    public static Func<object, IEnumerable<string>> KindsOf = v => Array.Empty<string>();
    public static Func<string, bool> IsKind = kind => false;
    public static Func<IEnumerable<string>> AllKinds = () => Array.Empty<string>();
    public static Func<object, string> KindName = v => null;
    /// <summary>One line for a value when printed (null: the default).</summary>
    public static Func<object, string> Describe = v => null;
    /// <summary>Extra binary operators (50*glucose): return null when not handled.</summary>
    public static readonly List<Func<string, object, object, object>> Operators = new List<Func<string, object, object, object>>();

    /// <summary>One run of a command: dry (preview / completion: nothing happens), where output goes.</summary>
    public sealed class Env
    {
        public bool dry;
        public Action<string, Tone> output;
        public Action clear;
        /// <summary>A verb, print or report spoke for the statement (no "= value" after it).</summary>
        public bool reported;
        /// <summary>Dry: what the last statement would have said.</summary>
        public string note;
        internal Scope scope;
        internal object lastOld, lastNew;
        internal Dictionary<string, object> dryVars; // a dry run's assignments (x = ...; x.count previews)

        internal bool TryVar(string name, out object value)
        {
            value = null;
            return dryVars != null && dryVars.TryGetValue(name, out value) || Vars.TryGetValue(name, out value);
        }

        public void Say(string text, Tone tone = Tone.Info)
        {
            reported = true;
            if (dry) note = text;
            else output?.Invoke(text, tone);
        }
    }

    internal sealed class Scope
    {
        public object it;
        public int index;
        public Scope up;
    }

    // ================= running =================

    /// <summary>Runs every statement of a line (split by ';'), reporting as it goes. Throws <see cref="Error"/>.</summary>
    public static object Run(string line, Env e)
    {
        EnsureCore();
        List<Node> program = new Parser(line).Program();
        object last = null;
        foreach (Node statement in program)
        {
            e.reported = false;
            e.note = null;
            last = Statement(statement, line, e);
        }
        return last;
    }

    /// <summary>What the line would do or be, without doing it (the console's live preview). Null: nothing to say.</summary>
    public static string Preview(string line, out bool bad)
    {
        bad = false;
        if (string.IsNullOrWhiteSpace(line)) return null;
        EnsureCore();
        var e = new Env { dry = true };
        string said = null;
        try
        {
            List<Node> program = new Parser(line).Program();
            foreach (Node statement in program)
            {
                e.reported = false;
                e.note = null;
                object v = Statement(statement, line, e);
                said = e.reported ? e.note : "= " + Summary(v);
            }
        }
        catch (Error err) { bad = true; return err.Message; }
        catch (Exception err) { bad = true; return err.Message; }
        return said;
    }

    static object Statement(Node statement, string src, Env e)
    {
        if (!(statement is SetN s))
        {
            object v = Eval(statement, e);
            if (!e.reported) e.Say(Summary(v), Tone.Result);
            return v;
        }
        object rhs = Eval(s.value, e);
        string target = src.Substring(s.target.at, s.target.end - s.target.at).Trim();
        e.lastOld = e.lastNew = null;
        e.reported = false;
        int n = Write(s.target, s.op, rhs, e);
        if (e.reported) return rhs; // a collection's add / remove reported itself
        if (n == 0) e.Say(target + ": nothing to change", Tone.Result);
        else if (n == 1 && e.lastNew != null) e.Say($"{target}: {Summary(e.lastOld)} -> {Summary(e.lastNew)}", Tone.Result);
        else e.Say($"{target} {s.op} {Summary(rhs)}  ({n} changed)", Tone.Result);
        return rhs;
    }

    // ================= lexer =================

    enum Tk { Num, Str, Id, Op, End }

    struct Token
    {
        public Tk kind;
        public string text;
        public float num;
        public int at, end;
    }

    static readonly string[] Ops2 = { "+=", "-=", "*=", "/=", "==", "!=", "<=", ">=", "&&", "||" };
    const string Ops1 = "+-*/%<>=!()[],.|;";

    static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';
    static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    static int ReadWord(string s, int i)
    {
        while (i < s.Length && IsWordChar(s[i])) i++;
        return i;
    }

    // "dna-kill" is one word when a word by that name exists (otherwise '-' is a minus: pos-me.pos).
    static bool IsHyphenWord(string w) => w.IndexOf('-') > 0 && Words.ContainsKey(w);

    static bool IsHyphenPrefix(string w)
    {
        foreach (string k in Words.Keys)
            if (k.IndexOf('-') > 0 && k.StartsWith(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static List<Token> Lex(string s)
    {
        var list = new List<Token>();
        int i = 0;
        while (true)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length)
            {
                list.Add(new Token { kind = Tk.End, text = "", at = i, end = i });
                return list;
            }
            char c = s[i];
            int start = i;
            if (char.IsDigit(c) || c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1]))
            {
                bool dot = false;
                while (i < s.Length && (char.IsDigit(s[i]) || !dot && s[i] == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
                {
                    if (s[i] == '.') dot = true;
                    i++;
                }
                string raw = s.Substring(start, i - start);
                if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) throw new Error($"'{raw}' isn't a number");
                list.Add(new Token { kind = Tk.Num, text = raw, num = v, at = start, end = i });
                continue;
            }
            if (IsWordStart(c))
            {
                i = ReadWord(s, i);
                while (i + 1 < s.Length && s[i] == '-' && IsWordStart(s[i + 1]))
                {
                    int j = ReadWord(s, i + 1);
                    if (!IsHyphenWord(s.Substring(start, j - start))) break;
                    i = j;
                }
                list.Add(new Token { kind = Tk.Id, text = s.Substring(start, i - start), at = start, end = i });
                continue;
            }
            if (c == '"' || c == '\'')
            {
                int close = s.IndexOf(c, i + 1);
                int stop = close < 0 ? s.Length : close;
                list.Add(new Token { kind = Tk.Str, text = s.Substring(i + 1, stop - i - 1), at = start, end = Math.Min(s.Length, stop + 1) });
                i = Math.Min(s.Length, stop + 1);
                continue;
            }
            string two = i + 1 < s.Length ? s.Substring(i, 2) : null;
            if (two != null && Array.IndexOf(Ops2, two) >= 0)
            {
                list.Add(new Token { kind = Tk.Op, text = two, at = start, end = i + 2 });
                i += 2;
                continue;
            }
            if (Ops1.IndexOf(c) >= 0)
            {
                list.Add(new Token { kind = Tk.Op, text = c.ToString(), at = start, end = i + 1 });
                i++;
                continue;
            }
            throw new Error($"unexpected '{c}'");
        }
    }

    // ================= parser =================

    public abstract class Node { public int at, end; }
    sealed class Lit : Node { public object value; }
    sealed class NameN : Node { public string name; }
    sealed class VecN : Node { public Node x, y, z; }
    sealed class AbsN : Node { public Node inner; }
    sealed class UnN : Node { public char op; public Node a; }
    sealed class BinN : Node { public string op; public Node a, b; }
    sealed class DotN : Node { public Node target; public string name; public Node[] args; } // args null: no brackets
    sealed class WhereN : Node { public Node target, cond; }
    sealed class IdxN : Node { public Node target, index; }
    sealed class CallN : Node { public string name; public Node[] args; }
    sealed class SetN : Node { public Node target, value; public string op; }

    static readonly Node[] None = new Node[0];

    sealed class Parser
    {
        readonly List<Token> _t;
        int _i;

        public Parser(string src) { _t = Lex(src ?? ""); }

        Token Peek => _t[_i];
        int End => _t[Math.Max(0, _i - 1)].end;
        bool IsOp(string op) => Peek.kind == Tk.Op && Peek.text == op;

        bool Eat(string op)
        {
            if (!IsOp(op)) return false;
            _i++;
            return true;
        }

        bool EatWord(string w)
        {
            if (Peek.kind != Tk.Id || !string.Equals(Peek.text, w, StringComparison.OrdinalIgnoreCase)) return false;
            _i++;
            return true;
        }

        void Expect(string op)
        {
            if (!Eat(op)) throw new Error(Peek.kind == Tk.End ? $"missing '{op}'" : $"expected '{op}' before '{Peek.text}'");
        }

        Error Unexpected() => new Error(Peek.kind == Tk.End ? "the line ends early" : $"unexpected '{Peek.text}'");

        public List<Node> Program()
        {
            var list = new List<Node>();
            while (Peek.kind != Tk.End)
            {
                if (Eat(";")) continue;
                list.Add(Statement());
                if (Peek.kind != Tk.End && !Eat(";")) throw Unexpected();
            }
            return list;
        }

        public Node Expression()
        {
            Node n = Or();
            if (Peek.kind != Tk.End) throw Unexpected();
            return n;
        }

        Node Statement()
        {
            Node target = Or();
            if (Peek.kind == Tk.Op && (Peek.text == "=" || Peek.text == "+=" || Peek.text == "-=" || Peek.text == "*=" || Peek.text == "/="))
            {
                string op = Peek.text;
                _i++;
                Node value = Or();
                return new SetN { target = target, op = op, value = value, at = target.at, end = value.end };
            }
            return target;
        }

        Node Bin(string op, Node a, Node b) => new BinN { op = op, a = a, b = b, at = a.at, end = b.end };

        Node Or()
        {
            Node a = And();
            while (Eat("||") || EatWord("or")) a = Bin("||", a, And());
            return a;
        }

        Node And()
        {
            Node a = Compare();
            while (Eat("&&") || EatWord("and")) a = Bin("&&", a, Compare());
            return a;
        }

        Node Compare()
        {
            Node a = Add();
            while (Peek.kind == Tk.Op && (Peek.text == "<" || Peek.text == ">" || Peek.text == "<=" || Peek.text == ">=" || Peek.text == "==" || Peek.text == "!="))
            {
                string op = Peek.text;
                _i++;
                a = Bin(op, a, Add());
            }
            return a;
        }

        Node Add()
        {
            Node a = Mul();
            while (Peek.kind == Tk.Op && (Peek.text == "+" || Peek.text == "-"))
            {
                string op = Peek.text;
                _i++;
                a = Bin(op, a, Mul());
            }
            return a;
        }

        Node Mul()
        {
            Node a = Unary();
            while (Peek.kind == Tk.Op && (Peek.text == "*" || Peek.text == "/" || Peek.text == "%"))
            {
                string op = Peek.text;
                _i++;
                a = Bin(op, a, Unary());
            }
            return a;
        }

        Node Unary()
        {
            int at = Peek.at;
            if (Eat("-")) { Node a = Unary(); return new UnN { op = '-', a = a, at = at, end = a.end }; }
            if (Eat("!") || EatWord("not")) { Node a = Unary(); return new UnN { op = '!', a = a, at = at, end = a.end }; }
            return Post();
        }

        Node Post()
        {
            Node n = Prim();
            while (true)
            {
                if (Eat("."))
                {
                    if (Eat("("))
                    {
                        Node cond = Or();
                        if (IsOp("=")) throw new Error("inside .( ) compare with ==, not =");
                        Expect(")");
                        n = new WhereN { target = n, cond = cond, at = n.at, end = End };
                        continue;
                    }
                    if (Peek.kind != Tk.Id) throw new Error(Peek.kind == Tk.End ? "a name goes after '.'" : $"'{Peek.text}' after '.' (a name or ( ) goes there)");
                    string name = Peek.text;
                    _i++;
                    Node[] args = IsOp("(") ? Args() : null;
                    n = new DotN { target = n, name = name, args = args, at = n.at, end = End };
                }
                else if (Eat("["))
                {
                    Node index = Or();
                    Expect("]");
                    n = new IdxN { target = n, index = index, at = n.at, end = End };
                }
                else return n;
            }
        }

        // Arguments are statements, so do(pos += up*5) works.
        Node[] Args()
        {
            Expect("(");
            if (Eat(")")) return None;
            var list = new List<Node>();
            do list.Add(Statement()); while (Eat(",") || Eat(";"));
            Expect(")");
            return list.ToArray();
        }

        Node Prim()
        {
            Token t = Peek;
            switch (t.kind)
            {
                case Tk.Num: _i++; return new Lit { value = t.num, at = t.at, end = t.end };
                case Tk.Str: _i++; return new Lit { value = t.text, at = t.at, end = t.end };
                case Tk.Id:
                    _i++;
                    if (IsOp("(")) { Node[] args = Args(); return new CallN { name = t.text, args = args, at = t.at, end = End }; }
                    return new NameN { name = t.text, at = t.at, end = t.end };
                case Tk.End: throw new Error("the line ends early");
            }
            if (Eat("("))
            {
                Node a = Or();
                if (Eat(","))
                {
                    Node b = Or();
                    Node c = Eat(",") ? Or() : null;
                    Expect(")");
                    return new VecN { x = a, y = b, z = c, at = t.at, end = End };
                }
                Expect(")");
                a.at = t.at;
                a.end = End;
                return a;
            }
            if (Eat("|"))
            {
                Node a = Add();
                Expect("|");
                return new AbsN { inner = a, at = t.at, end = End };
            }
            throw Unexpected();
        }
    }

    // ================= evaluation =================

    static object Eval(Node n, Env e)
    {
        switch (n)
        {
            case Lit l: return l.value;
            case NameN nm: return Resolve(nm.name, e);
            case VecN v: return new Vector3(Num(Eval(v.x, e), "x"), Num(Eval(v.y, e), "y"), v.z != null ? Num(Eval(v.z, e), "z") : 0f);
            case AbsN a: return Map(Eval(a.inner, e), Abs);
            case UnN u:
                object x = Eval(u.a, e);
                return u.op == '-' ? Map(x, Negate) : Map(x, y => !Truthy(y));
            case BinN b:
                if (b.op == "&&") return Truthy(Eval(b.a, e)) && Truthy(Eval(b.b, e));
                if (b.op == "||") return Truthy(Eval(b.a, e)) || Truthy(Eval(b.b, e));
                return Binary(b.op, Eval(b.a, e), Eval(b.b, e));
            case DotN d: return Member(Eval(d.target, e), d.name, d.args, e);
            case WhereN w: return Where(AsMany(Eval(w.target, e)), w.cond, e);
            case IdxN i: return Index(Eval(i.target, e), Eval(i.index, e));
            case CallN c: return Call(c, e);
            case SetN _: throw new Error("an assignment can't be used as a value");
        }
        throw new Error("can't evaluate that");
    }

    static object Resolve(string name, Env e)
    {
        for (Scope s = e.scope; s != null; s = s.up)
        {
            if (Eq(name, "it")) return s.it;
            if (Eq(name, "index")) return (float)s.index;
            if (TryOwnMember(s.it, name, out object v)) return v;
        }
        if (e.TryVar(name, out object var)) return var;
        if (Words.TryGetValue(name, out Word w)) return w.get(e);
        if (Eq(name, "index") || Eq(name, "it")) throw new Error($"'{name}' only means something inside .( ): all.({name} < 5)");
        if (Fns.TryGetValue(name, out Fn f)) throw new Error($"{name} is a function: {name}{f.args}");
        if (IsKind(name)) throw new Error($"'{name}' is a kind: all.{name}");
        throw new Error(Unknown(name, Candidates(e)));
    }

    // A single value's own member, as a bare name inside .( ): a property, or whether it is a kind.
    static bool TryOwnMember(object it, string name, out object value)
    {
        value = null;
        if (it == null || it is Many) return false;
        Prop p = PropOf(it, name);
        if (p != null) { value = p.get(it); return true; }
        if (IsKind(name)) { value = Is(it, name); return true; }
        return false;
    }

    static object Member(object v, string name, Node[] args, Env e)
    {
        if (v is Many m)
        {
            if (ListOps.TryGetValue(name, out ListOp op)) return op.run(e, m, args ?? None);
            if (Verbs.TryGetValue(name, out Verb verb)) { NoArgs(name, args); return Do(verb, m, e); }
            if (IsKind(name))
            {
                NoArgs(name, args);
                var kept = new Many();
                foreach (object x in m) if (Is(x, name)) kept.Add(x);
                return kept;
            }
            NoArgs(name, args);
            var each = new Many(m.Count);
            bool any = false;
            foreach (object x in m)
            {
                Prop p = PropOf(x, name);
                if (p == null) continue;
                any = true;
                each.Add(p.get(x));
            }
            if (any || m.Count == 0 && IsPropName(name)) return each;
            throw new Error(NoMember(m, name));
        }
        if (v is Space space)
        {
            if (space.members.TryGetValue(name, out object member)) return member;
            throw new Error(Unknown($"{space.name}.{name}", space.members.Keys));
        }
        if (v == null) throw new Error($"nothing has no '{name}'");
        Prop prop = PropOf(v, name);
        if (prop != null) { NoArgs(name, args); return prop.get(v); }
        if (Verbs.TryGetValue(name, out Verb vb))
        {
            if (!vb.on(v)) throw new Error($"can't {name} {Describe(v) ?? TypeName(v)}");
            NoArgs(name, args);
            Do(vb, new Many { v }, e);
            return v;
        }
        if (ListOps.TryGetValue(name, out ListOp lop)) return lop.run(e, new Many { v }, args ?? None);
        if (IsKind(name)) return Is(v, name) ? new Many { v } : new Many();
        throw new Error(NoMember(v, name));
    }

    static void NoArgs(string name, Node[] args)
    {
        if (args != null) throw new Error($"{name} takes no ( )");
    }

    static object Do(Verb verb, Many m, Env e)
    {
        int n = 0;
        var kinds = new Many();
        foreach (object x in m)
        {
            if (x == null || !verb.on(x)) continue;
            if (e.dry || verb.run(x)) { n++; kinds.Add(x); }
        }
        e.Say(n == 0 ? $"nothing to {verb.name}" : $"{(e.dry ? verb.name : verb.done)} {Counted(kinds)}", Tone.Result);
        return m;
    }

    static Many Where(Many m, Node cond, Env e)
    {
        var kept = new Many();
        for (int i = 0; i < m.Count; i++)
            if (Truthy(Each(e, cond, m[i], i))) kept.Add(m[i]);
        return kept;
    }

    /// <summary>Evaluates 'n' with 'it' as the element (its properties are bare names, index is 'i').</summary>
    public static object Each(Env e, Node n, object it, int i)
    {
        var s = new Scope { it = it, index = i, up = e.scope };
        e.scope = s;
        try { return Eval(n, e); }
        finally { e.scope = s.up; }
    }

    // Statements (do's arguments) per element.
    static void EachStatement(Env e, Node n, object it, int i)
    {
        var s = new Scope { it = it, index = i, up = e.scope };
        e.scope = s;
        try
        {
            if (n is SetN set) Write(set.target, set.op, Eval(set.value, e), e);
            else Eval(n, e);
        }
        finally { e.scope = s.up; }
    }

    static object Index(object v, object i)
    {
        int k = Int(i, "an index");
        if (v is Many m)
        {
            if (k < 0) k += m.Count;
            if (k < 0 || k >= m.Count) throw new Error($"[{Int(i, "")}] is outside a list of {m.Count}");
            return m[k];
        }
        if (v is Vector3 vec)
        {
            if (k < 0 || k > 2) throw new Error("a vector has [0], [1], [2]");
            return vec[k];
        }
        throw new Error($"can't take [{k}] of {TypeName(v)}");
    }

    static object Call(CallN c, Env e)
    {
        if (!Fns.TryGetValue(c.name, out Fn f))
        {
            if (ListOps.ContainsKey(c.name) || Verbs.ContainsKey(c.name)) throw new Error($"{c.name} goes after a dot: all.{c.name}{(c.args.Length > 0 ? "(...)" : "")}");
            throw new Error(Unknown(c.name + "( )", Fns.Keys));
        }
        var args = new object[c.args.Length];
        for (int i = 0; i < args.Length; i++) args[i] = Eval(c.args[i], e);
        return f.run(e, args);
    }

    // ================= assignment =================

    // Writes 'op rhs' into a target; returns how many values it changed.
    static int Write(Node target, string op, object rhs, Env e)
    {
        switch (target)
        {
            case NameN nm:
                for (Scope s = e.scope; s != null; s = s.up)
                    if (s.it != null && !(s.it is Many) && PropOf(s.it, nm.name) != null) return SetOn(s.it, nm.name, op, rhs, e, null);
                if (Words.ContainsKey(nm.name) || Fns.ContainsKey(nm.name)) throw new Error($"'{nm.name}' can't be changed (a property of it can: {nm.name}.pos = ...)");
                bool had = e.TryVar(nm.name, out object old);
                if (op != "=" && !had) throw new Error($"'{nm.name}' has no value yet ({nm.name} = ... first)");
                object value = op == "=" ? rhs : Binary(op.Substring(0, 1), old, rhs);
                e.lastOld = old;
                e.lastNew = value;
                if (e.dry) (e.dryVars ??= new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase))[nm.name] = value;
                else Vars[nm.name] = value;
                return 1;
            case DotN d when d.args == null:
                return SetOn(Eval(d.target, e), d.name, op, rhs, e, d.target);
            case IdxN _:
                throw new Error("[i] can only be read; filter instead: list.(index == 2).pos = ...");
        }
        throw new Error("only a name or a property can be changed: me.speed = 5, x = all.cell");
    }

    static int SetOn(object b, string name, string op, object rhs, Env e, Node from)
    {
        if (b is Many m)
        {
            if (m.Count == 0) return 0;
            if (IsValue(m[0])) return WriteBack(from, Change(m, name, op, rhs, e), e, name);
            var r = rhs as Many;
            bool paired = r != null && r.Count == m.Count && !IsCollection(m[0], name);
            if (r != null && !paired && !IsCollection(m[0], name)) throw new Error($"{m.Count} values on the left, {r.Count} on the right");
            int n = 0;
            for (int i = 0; i < m.Count; i++) n += SetOn(m[i], name, op, paired ? r[i] : rhs, e, null);
            return n;
        }
        if (IsValue(b)) return WriteBack(from, Change(b, name, op, rhs, e), e, name);
        Prop p = PropOf(b, name) ?? throw new Error(NoMember(b, name));
        if ((op == "+=" || op == "-=") && (op == "+=" ? p.add : p.remove) != null)
        {
            Func<object, object, string> f = op == "+=" ? p.add : p.remove;
            if (e.dry) { e.Say($"{name} {op} {Summary(rhs)}"); return 1; }
            // Each report once, with a count: "+dna KIL-1 x5".
            var reports = new List<string>();
            var counts = new Dictionary<string, int>();
            foreach (object item in rhs is Many items ? items : new Many { rhs })
            {
                string r = f(b, item);
                if (string.IsNullOrEmpty(r)) continue;
                if (counts.TryGetValue(r, out int c)) counts[r] = c + 1;
                else { counts[r] = 1; reports.Add(r); }
            }
            var said = new StringBuilder();
            foreach (string r in reports) said.Append(said.Length > 0 ? ", " : "").Append(r).Append(counts[r] > 1 ? " x" + counts[r] : "");
            e.Say(said.Length > 0 ? said.ToString() : "nothing changed", Tone.Result);
            return 1;
        }
        if (p.set == null) throw new Error($"{name} can only be read" + (p.add != null ? $" (put in with {name} +=, take out with -=)" : ""));
        object before = p.get(b);
        object value = op == "=" ? rhs : Binary(op.Substring(0, 1), before, rhs);
        e.lastOld = before;
        e.lastNew = value;
        if (!e.dry) p.set(b, value);
        return 1;
    }

    // A vector's x (or a list of vectors'): the changed value goes back into what it came from (me.pos.y += 5).
    static int WriteBack(Node from, object changed, Env e, string name)
    {
        if (from == null) throw new Error($"can't change {name} here");
        return Write(from, "=", changed, e);
    }

    static object Change(object v, string name, string op, object rhs, Env e)
    {
        if (v is Many m)
        {
            var r = rhs as Many;
            if (r != null && r.Count != m.Count) throw new Error($"{m.Count} values on the left, {r.Count} on the right");
            var result = new Many(m.Count);
            for (int i = 0; i < m.Count; i++) result.Add(Change(m[i], name, op, r != null ? r[i] : rhs, e));
            return result;
        }
        if (v is Vector3 vec)
        {
            int k = Eq(name, "x") ? 0 : Eq(name, "y") ? 1 : Eq(name, "z") ? 2 : -1;
            if (k >= 0)
            {
                vec[k] = Num(op == "=" ? rhs : Binary(op.Substring(0, 1), vec[k], rhs), name);
                return vec;
            }
            if (Eq(name, "len"))
            {
                float len = Num(op == "=" ? rhs : Binary(op.Substring(0, 1), vec.magnitude, rhs), name);
                return vec.sqrMagnitude > 1e-12f ? vec.normalized * len : vec;
            }
        }
        throw new Error($"can't change {name} of {TypeName(v)}");
    }

    static bool IsCollection(object v, string name)
    {
        Prop p = PropOf(v, name);
        return p != null && (p.add != null || p.remove != null);
    }

    // ================= operators =================

    static object Binary(string op, object a, object b)
    {
        foreach (Func<string, object, object, object> f in Operators) // the world's first: 50*glucose scales, not repeats
        {
            object r = f(op, a, b);
            if (r != null) return r;
        }
        // Lists of things (not numbers): + joins, - removes, * n repeats (dna.kill*5, all.cell - all.redbloodcell).
        if (op == "+" && IsItems(a) && IsItems(b)) { Many r = AsMany(a); var j = new Many(r); j.AddRange(AsMany(b)); return j; }
        if (op == "-" && IsItems(a) && IsItems(b))
        {
            var drop = new HashSet<object>(AsMany(b));
            var r = new Many();
            foreach (object x in AsMany(a)) if (!drop.Contains(x)) r.Add(x);
            return r;
        }
        if (op == "*" && (IsItems(a) && b is float || a is float && IsItems(b)))
        {
            object item = a is float ? b : a;
            float times = a is float f ? f : (float)b;
            if (times < 0f || Mathf.Abs(times - Mathf.Round(times)) > 1e-4f) throw new Error($"repeat a whole number of times, not {F(times)}");
            if (times > 1000f) throw new Error("at most 1000 copies");
            var r = new Many();
            for (int i = 0; i < (int)Mathf.Round(times); i++)
                if (item is Many m) r.AddRange(m); else r.Add(item);
            return r;
        }
        if (a is Many || b is Many)
        {
            var la = a as Many;
            var lb = b as Many;
            if (la != null && lb != null && la.Count != lb.Count) throw new Error($"lists of {la.Count} and {lb.Count} don't line up");
            int n = la != null ? la.Count : lb.Count;
            var r = new Many(n);
            for (int i = 0; i < n; i++) r.Add(Binary(op, la != null ? la[i] : a, lb != null ? lb[i] : b));
            return r;
        }
        switch (op)
        {
            case "==": return Same(a, b);
            case "!=": return !Same(a, b);
            case "&&": return Truthy(a) && Truthy(b);
            case "||": return Truthy(a) || Truthy(b);
        }
        if (op == "+" && (a is string || b is string)) return Show(a) + Show(b);
        if (a is Vector3 || b is Vector3)
        {
            if (op == "<" || op == ">" || op == "<=" || op == ">=") throw new Error("compare lengths, not vectors: |v| < 5");
            Vector3 x = Vec(a), y = Vec(b);
            switch (op)
            {
                case "+": return x + y;
                case "-": return x - y;
                case "*": return Vector3.Scale(x, y);
                case "/":
                    if (y.x == 0f || y.y == 0f || y.z == 0f) throw new Error("divide by zero");
                    return new Vector3(x.x / y.x, x.y / y.y, x.z / y.z);
                case "%": return new Vector3(Mod(x.x, y.x), Mod(x.y, y.y), Mod(x.z, y.z));
            }
        }
        float p = Num(a, "left of " + op), q = Num(b, "right of " + op);
        switch (op)
        {
            case "+": return p + q;
            case "-": return p - q;
            case "*": return p * q;
            case "/": if (q == 0f) throw new Error("divide by zero"); return p / q;
            case "%": return Mod(p, q);
            case "<": return p < q;
            case ">": return p > q;
            case "<=": return p <= q;
            case ">=": return p >= q;
        }
        throw new Error($"no '{op}' for {TypeName(a)} and {TypeName(b)}");
    }

    // Things, genes, amounts... or a list of them: what +, - and * n treat as items, not numbers.
    static bool IsItems(object v)
    {
        if (v == null || IsValue(v) || v is Space) return false;
        if (!(v is Many m)) return true;
        foreach (object x in m) if (x == null || IsValue(x) || x is Many) return false;
        return true;
    }

    static float Mod(float a, float b)
    {
        if (b == 0f) throw new Error("divide by zero");
        return a - b * Mathf.Floor(a / b);
    }

    static object Map(object v, Func<object, object> f)
    {
        if (!(v is Many m)) return f(v);
        var r = new Many(m.Count);
        foreach (object x in m) r.Add(Map(x, f));
        return r;
    }

    static object Abs(object v) => v is Vector3 vec ? vec.magnitude : (object)Mathf.Abs(Num(v, "|x|"));

    static object Negate(object v)
    {
        foreach (Func<string, object, object, object> f in Operators)
        {
            object r = f("*", v, -1f);
            if (r != null) return r;
        }
        return v is Vector3 vec ? -vec : (object)-Num(v, "-x");
    }

    public static bool Truthy(object v) => v switch
    {
        null => false,
        bool b => b,
        float f => f != 0f,
        string s => s.Length > 0,
        Many m => m.Count > 0,
        UnityEngine.Object o => o != null,
        _ => true,
    };

    static bool Same(object a, object b)
    {
        if (a == null || b == null) return a == null && b == null;
        if (a is float || a is bool || b is float || b is bool)
            return (a is float || a is bool) && (b is float || b is bool) && Mathf.Abs(Num(a, "") - Num(b, "")) < 1e-5f;
        if (a is Vector3 va && b is Vector3 vb) return (va - vb).sqrMagnitude < 1e-8f;
        if (a is string sa && b is string sb) return string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase);
        if (a is string ka && !(b is string)) return Is(b, ka);
        if (b is string kb && !(a is string)) return Is(a, kb);
        return Equals(a, b);
    }

    // ================= conversions =================

    public static float Num(object v, string what)
    {
        switch (v)
        {
            case float f: return f;
            case bool b: return b ? 1f : 0f;
            case int i: return i;
            case double d: return (float)d;
        }
        throw new Error($"{(string.IsNullOrEmpty(what) ? "that" : what)} should be a number, not {TypeName(v)}");
    }

    public static int Int(object v, string what) => Mathf.RoundToInt(Num(v, what));

    public static Vector3 Vec(object v)
    {
        switch (v)
        {
            case Vector3 vec: return vec;
            case float f: return new Vector3(f, f, f);
        }
        Vector3? p = ToPosition?.Invoke(v);
        if (p.HasValue) return p.Value;
        throw new Error($"expected a vector, not {TypeName(v)}");
    }

    /// <summary>Where a value is when used as a position (a thing: its pos). Set by the world.</summary>
    public static Func<object, Vector3?> ToPosition;

    public static Many AsMany(object v) => v as Many ?? (v == null ? new Many() : new Many { v });

    static bool IsValue(object v) => v == null || v is float || v is bool || v is string || v is Vector3;

    public static string TypeName(object v) => v switch
    {
        null => "nothing",
        float _ => "a number",
        bool _ => "true/false",
        string _ => "text",
        Vector3 _ => "a vector",
        Many m => $"a list of {m.Count}",
        Space s => s.name,
        _ => KindName(v) ?? v.GetType().Name,
    };

    static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ================= properties =================

    public static Prop PropOf(object v, string name)
    {
        if (v == null) return null;
        foreach (Prop p in Props)
            if (Eq(p.name, name) && p.on(v)) return p;
        return null;
    }

    static bool IsPropName(string name)
    {
        foreach (Prop p in Props) if (Eq(p.name, name)) return true;
        return false;
    }

    // ================= text =================

    public static string F(float f) => f.ToString(Mathf.Abs(f) >= 100f ? "0" : "0.##", CultureInfo.InvariantCulture);
    public static string F(Vector3 v) => $"({F(v.x)}, {F(v.y)}, {F(v.z)})";

    static string Show(object v) => v is string s ? s : Summary(v);

    /// <summary>A value in one line.</summary>
    public static string Summary(object v)
    {
        switch (v)
        {
            case null: return "nothing";
            case float f: return F(f);
            case bool b: return b ? "true" : "false";
            case string s: return s;
            case Vector3 vec: return F(vec);
            case Many m:
                if (m.Count == 0) return "nothing (empty list)";
                if (m.Count == 1) return "[" + Summary(m[0]) + "]";
                if (!IsValue(m[0]) && KindName(m[0]) != null) return Counted(m);
                var sb = new StringBuilder("[");
                for (int i = 0; i < m.Count && i < 8; i++) sb.Append(i > 0 ? ", " : "").Append(Summary(m[i]));
                if (m.Count > 8) sb.Append(", ...");
                return sb.Append("]  (").Append(m.Count).Append(')').ToString();
        }
        return Describe(v) ?? v.ToString();
    }

    // "12: 10 redbloodcell, 2 whitebloodcell"
    static string Counted(Many m)
    {
        if (m.Count == 1) return Describe(m[0]) ?? Summary(m[0]);
        var counts = new Dictionary<string, int>();
        var order = new List<string>();
        foreach (object x in m)
        {
            string k = KindName(x) ?? TypeName(x);
            if (!counts.ContainsKey(k)) { counts[k] = 0; order.Add(k); }
            counts[k]++;
        }
        order.Sort((a, b) => counts[b].CompareTo(counts[a]));
        if (order.Count == 1) return m.Count + " " + order[0];
        var sb = new StringBuilder();
        sb.Append(m.Count).Append(" things: ");
        for (int i = 0; i < order.Count && i < 5; i++) sb.Append(i > 0 ? ", " : "").Append(counts[order[i]]).Append(' ').Append(order[i]);
        if (order.Count > 5) sb.Append(", ...");
        return sb.ToString();
    }

    static string Unknown(string name, IEnumerable<string> known)
    {
        string best = null;
        int bestD = int.MaxValue;
        foreach (string k in known)
        {
            int d = Distance(name.ToLowerInvariant(), k.ToLowerInvariant());
            if (d < bestD) { bestD = d; best = k; }
        }
        return best != null && bestD <= Mathf.Max(1, name.Length / 3) ? $"unknown '{name}' (did you mean {best}?)" : $"unknown '{name}'";
    }

    static string NoMember(object v, string name)
    {
        var names = new List<string>();
        var list = new List<Suggestion>();
        MembersOf(v, list);
        foreach (Suggestion s in list) names.Add(s.text);
        if (v is Many) names.AddRange(AllKinds());
        string u = Unknown(name, names);
        return $"{TypeName(v)} has no '{name}'" + (u.Contains("did you mean") ? u.Substring(u.IndexOf(" (", StringComparison.Ordinal)) : "");
    }

    static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Mathf.Min(Mathf.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    // ================= built-in list operations and functions =================

    static bool s_core;

    public static void EnsureCore()
    {
        if (s_core) return;
        s_core = true;

        AddOp("count", "", "how many", (e, m, a) => (float)m.Count);
        AddOp("mix", "", "shuffled (then .(index < 5) keeps five at random)", (e, m, a) => Shuffled(m));
        AddOp("random", "(n)", "one at random, or n of them", (e, m, a) =>
            a.Length == 0 ? (m.Count == 0 ? null : m[UnityEngine.Random.Range(0, m.Count)]) : Take(Shuffled(m), Int(Eval(a[0], e), "n")));
        AddOp("first", "(n)", "the first one, or the first n", (e, m, a) =>
            a.Length == 0 ? (m.Count == 0 ? null : m[0]) : Take(m, Int(Eval(a[0], e), "n")));
        AddOp("last", "(n)", "the last one, or the last n", (e, m, a) =>
        {
            if (a.Length == 0) return m.Count == 0 ? null : m[m.Count - 1];
            int n = Mathf.Clamp(Int(Eval(a[0], e), "n"), 0, m.Count);
            return new Many(m.GetRange(m.Count - n, n));
        });
        AddOp("reverse", "", "back to front", (e, m, a) => { var r = new Many(m); r.Reverse(); return r; });
        AddOp("sort", "(key)", "smallest first, by a key per element: sort(dist), sort(-pos.y)", (e, m, a) => Sorted(e, m, a.Length > 0 ? a[0] : null), perElement: true);
        AddOp("min", "(key)", "the smallest; with a key, the element with the smallest: min(dist)", (e, m, a) => Extreme(e, m, a, -1), perElement: true);
        AddOp("max", "(key)", "the largest; with a key, the element with the largest", (e, m, a) => Extreme(e, m, a, 1), perElement: true);
        AddOp("sum", "", "all added up", (e, m, a) => Sum(m));
        AddOp("mean", "", "the average", (e, m, a) => m.Count == 0 ? (object)0f : Binary("/", Sum(m), (float)m.Count));
        AddOp("where", "(cond)", "the ones where cond holds (same as .(cond))", (e, m, a) => Where(m, One(a, "where"), e), perElement: true, needsArgs: true);
        AddOp("map", "(expr)", "each one's expr: map(|pos - me.pos|)", (e, m, a) =>
        {
            Node n = One(a, "map");
            var r = new Many(m.Count);
            for (int i = 0; i < m.Count; i++) r.Add(Each(e, n, m[i], i));
            return r;
        }, perElement: true, needsArgs: true);
        AddOp("do", "(a = b, ...)", "runs statements on each: do(pos += up*5, scale *= 2)", (e, m, a) =>
        {
            if (a.Length == 0) throw new Error("do(...) needs something to do");
            if (e.dry) { e.Say($"do on {m.Count}"); return m; }
            for (int i = 0; i < m.Count; i++)
                foreach (Node n in a) EachStatement(e, n, m[i], i);
            e.Say($"done on {m.Count}", Tone.Result);
            return m;
        }, perElement: true, needsArgs: true);
        AddOp("print", "", "prints each one", (e, m, a) =>
        {
            if (m.Count == 0) { e.Say("nothing (empty list)", Tone.Result); return m; }
            if (e.dry) { e.Say($"print {m.Count}"); return m; }
            for (int i = 0; i < m.Count && i < 60; i++) e.Say(Describe(m[i]) ?? Summary(m[i]), Tone.Result);
            if (m.Count > 60) e.Say($"... and {m.Count - 60} more", Tone.Result);
            return m;
        });
        AddOp("kinds", "", "the kinds in it, with counts", (e, m, a) =>
        {
            var counts = new Dictionary<string, int>();
            foreach (object x in m)
                foreach (string k in KindsOf(x))
                    counts[k] = counts.TryGetValue(k, out int c) ? c + 1 : 1;
            var keys = new List<string>(counts.Keys);
            keys.Sort((x, y) => counts[y].CompareTo(counts[x]));
            var sb = new StringBuilder();
            foreach (string k in keys) sb.Append(sb.Length > 0 ? ", " : "").Append(k).Append(' ').Append(counts[k]);
            e.Say(sb.Length > 0 ? sb.ToString() : "no kinds", Tone.Result);
            var r = new Many();
            foreach (string k in keys) r.Add(k);
            return r;
        });

        AddFn("random", "(a, b)", "a random number: 0-1, 0-a or a-b", (e, a) =>
            a.Length == 0 ? UnityEngine.Random.value
            : a.Length == 1 ? UnityEngine.Random.Range(0f, Num(a[0], "a"))
            : (object)UnityEngine.Random.Range(Num(a[0], "a"), Num(a[1], "b")));
        AddFn("sphere", "(r)", "a random point within r of (0,0,0): me.pos + sphere(20)", (e, a) =>
            UnityEngine.Random.insideUnitSphere * (a.Length > 0 ? Num(a[0], "r") : 1f));
        AddFn("abs", "(x)", "size of a number, length of a vector (also |x|)", (e, a) => Map(Arg(a, 0, "abs"), Abs));
        AddFn("sqrt", "(x)", "square root", (e, a) => Map(Arg(a, 0, "sqrt"), v => Mathf.Sqrt(Num(v, "x"))));
        AddFn("sin", "(deg)", "sine of degrees", (e, a) => Map(Arg(a, 0, "sin"), v => Mathf.Sin(Num(v, "x") * Mathf.Deg2Rad)));
        AddFn("cos", "(deg)", "cosine of degrees", (e, a) => Map(Arg(a, 0, "cos"), v => Mathf.Cos(Num(v, "x") * Mathf.Deg2Rad)));
        AddFn("round", "(x)", "nearest whole number", (e, a) => Map(Arg(a, 0, "round"), v => (float)Mathf.Round(Num(v, "x"))));
        AddFn("floor", "(x)", "whole number below", (e, a) => Map(Arg(a, 0, "floor"), v => Mathf.Floor(Num(v, "x"))));
        AddFn("ceil", "(x)", "whole number above", (e, a) => Map(Arg(a, 0, "ceil"), v => Mathf.Ceil(Num(v, "x"))));
        AddFn("min", "(a, b)", "the smaller", (e, a) => Binary("<", Arg(a, 0, "min"), Arg(a, 1, "min")) is bool lt && lt ? a[0] : a[1]);
        AddFn("max", "(a, b)", "the larger", (e, a) => Binary(">", Arg(a, 0, "max"), Arg(a, 1, "max")) is bool gt && gt ? a[0] : a[1]);
        AddFn("clamp", "(x, a, b)", "x kept between a and b", (e, a) => Mathf.Clamp(Num(Arg(a, 0, "clamp"), "x"), Num(Arg(a, 1, "clamp"), "a"), Num(Arg(a, 2, "clamp"), "b")));
        AddFn("lerp", "(a, b, t)", "from a to b by t (0-1)", (e, a) =>
            Binary("+", Arg(a, 0, "lerp"), Binary("*", Binary("-", Arg(a, 1, "lerp"), a[0]), Num(Arg(a, 2, "lerp"), "t"))));
        AddFn("dot", "(a, b)", "dot product", (e, a) => Vector3.Dot(Vec(Arg(a, 0, "dot")), Vec(Arg(a, 1, "dot"))));
        AddFn("cross", "(a, b)", "cross product", (e, a) => Vector3.Cross(Vec(Arg(a, 0, "cross")), Vec(Arg(a, 1, "cross"))));
        AddFn("norm", "(v)", "the same direction, length 1", (e, a) => Map(Arg(a, 0, "norm"), v => Vec(v).normalized));
        AddFn("dist", "(a, b)", "distance between two points or things", (e, a) => Vector3.Distance(Vec(Arg(a, 0, "dist")), Vec(Arg(a, 1, "dist"))));
        AddFn("print", "(x, ...)", "prints values", (e, a) =>
        {
            var sb = new StringBuilder();
            foreach (object v in a) sb.Append(sb.Length > 0 ? " " : "").Append(Show(v));
            e.Say(sb.ToString(), Tone.Result);
            return a.Length == 1 ? a[0] : null;
        });
        AddFn("clear", "()", "clears the log", (e, a) => { if (!e.dry) e.clear?.Invoke(); e.reported = true; return null; });
        AddFn("help", "(x)", "what you can do with x (a thing, a list, a word)", (e, a) =>
        {
            if (a.Length == 0) { Help(e); return null; }
            var list = new List<Suggestion>();
            MembersOf(a[0], list);
            e.Say($"{TypeName(a[0])}:", Tone.Result);
            foreach (Suggestion s in list) e.Say($"  .{s.text}  {s.help}");
            return null;
        });

        AddWord("true", "yes", e => true);
        AddWord("false", "no", e => false);
        AddWord("pi", "3.14159", e => Mathf.PI);
        AddWord("time", "seconds since play started", e => Time.time);
        AddWord("help", "how the command line works", e => { Help(e); return null; });
        AddWord("vars", "your variables", e =>
        {
            if (Vars.Count == 0) e.Say("no variables yet (x = all.cell.nearest)", Tone.Result);
            foreach (var kv in Vars) e.Say($"{kv.Key} = {Summary(kv.Value)}", Tone.Result);
            return null;
        });
    }

    static void Help(Env e)
    {
        e.Say("all                      everything; shrink it with dots", Tone.Result);
        e.Say("all.redbloodcell         a kind      all.(dist < 20)  a condition per element", Tone.Result);
        e.Say("all.mix.(index < 5)      five at random  (.random(5) .nearest(3) .first .sort(dist) .count)", Tone.Result);
        e.Say("me.pos += me.forward*10  change a property:  =  +=  -=  *=  /=", Tone.Result);
        e.Say("me.inventory += 50*glucose,  += dna-kill,  -= dna.kill      me.speed *= 2", Tone.Result);
        e.Say("all.cell.kill            verbs: kill, delete;  .print lists them", Tone.Result);
        e.Say("spawn(cell, aim, 5)      x = all.cell.nearest(3);  x.do(pos += up*10)", Tone.Result);
        e.Say("|v| length,  aim = the point under the crosshair,  target = the thing there", Tone.Result);
        e.Say("help(me), help(all.cell): what works on it.   Tab completes, Up/Down history, ; splits", Tone.Result);
    }

    public static void AddOp(string name, string args, string help, Func<Env, Many, Node[], object> run, bool perElement = false, bool needsArgs = false) =>
        ListOps[name] = new ListOp { name = name, args = args, help = help, run = run, perElement = perElement, needsArgs = needsArgs };

    public static void AddFn(string name, string args, string help, Func<Env, object[], object> run) =>
        Fns[name] = new Fn { name = name, args = args, help = help, run = run };

    public static void AddWord(string name, string help, Func<Env, object> get) =>
        Words[name] = new Word { name = name, help = help, get = get };

    public static object Arg(object[] a, int i, string fn)
    {
        if (i >= a.Length) throw new Error($"{fn}{(Fns.TryGetValue(fn, out Fn f) ? f.args : "( )")} needs {i + 1} argument{(i > 0 ? "s" : "")}");
        return a[i];
    }

    static Node One(Node[] a, string name)
    {
        if (a.Length != 1) throw new Error($"{name}( ) takes one thing");
        return a[0];
    }

    public static Many Shuffled(Many m)
    {
        var r = new Many(m);
        for (int i = r.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (r[i], r[j]) = (r[j], r[i]);
        }
        return r;
    }

    public static Many Take(Many m, int n) => new Many(m.GetRange(0, Mathf.Clamp(n, 0, m.Count)));

    // Sorted by a key per element (numbers, or text); stable.
    public static Many Sorted(Env e, Many m, Node key, Func<object, float> keyOf = null)
    {
        var keys = new object[m.Count];
        for (int i = 0; i < m.Count; i++)
            keys[i] = keyOf != null ? keyOf(m[i]) : key != null ? Each(e, key, m[i], i) : m[i];
        var order = new int[m.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (x, y) =>
        {
            int c = keys[x] is string sx && keys[y] is string sy ? string.Compare(sx, sy, StringComparison.OrdinalIgnoreCase)
                  : keys[x] is Vector3 || keys[y] is Vector3 ? Vec(keys[x]).sqrMagnitude.CompareTo(Vec(keys[y]).sqrMagnitude)
                  : Num(keys[x], "the sort key").CompareTo(Num(keys[y], "the sort key"));
            return c != 0 ? c : x.CompareTo(y);
        });
        var r = new Many(m.Count);
        foreach (int i in order) r.Add(m[i]);
        return r;
    }

    static object Extreme(Env e, Many m, Node[] a, int sign)
    {
        if (m.Count == 0) return null;
        Many s = Sorted(e, m, a.Length > 0 ? a[0] : null);
        return sign < 0 ? s[0] : s[s.Count - 1];
    }

    static object Sum(Many m)
    {
        if (m.Count == 0) return 0f;
        object total = m[0];
        for (int i = 1; i < m.Count; i++) total = Binary("+", total, m[i]);
        return total;
    }

    // ================= completion =================

    public struct Suggestion
    {
        public string text, help, tail; // tail: typed after it when taken ("(" for what needs arguments)
    }

    /// <summary>Suggestions for the word at the caret (after a dot: what the value before it has; else words,
    /// variables, functions and, inside .( ), the element's properties). Returns where that word starts.</summary>
    public static int Complete(string line, int caret, List<Suggestion> into)
    {
        into.Clear();
        EnsureCore();
        caret = Mathf.Clamp(caret, 0, line.Length);
        int ws = caret;
        while (ws > 0 && IsWordChar(line[ws - 1])) ws--;
        if (ws > 1 && line[ws - 1] == '-')
        {
            int w0 = ws - 1;
            while (w0 > 0 && IsWordChar(line[w0 - 1])) w0--;
            if (w0 < ws - 1 && IsHyphenPrefix(line.Substring(w0, caret - w0))) ws = w0;
        }
        if (ws < caret && char.IsDigit(line[ws])) return ws;
        if (InString(line, ws)) return ws;
        string prefix = line.Substring(ws, caret - ws);

        var e = new Env { dry = true };
        var found = new List<Suggestion>();
        try
        {
            e.scope = ScopeAt(line, ws, e);
            if (ws > 0 && line[ws - 1] == '.')
            {
                int start = ChainStart(line, ws - 1);
                if (start < 0 || start >= ws - 1) return ws;
                object v = Eval(new Parser(line.Substring(start, ws - 1 - start)).Expression(), e);
                MembersOf(v, found);
            }
            else WordsAt(e, found);
        }
        catch (Exception) { return ws; } // half-typed: nothing to offer

        Rank(found, prefix, into);
        return ws;
    }

    static bool InString(string s, int at)
    {
        char open = '\0';
        for (int i = 0; i < at; i++)
        {
            if (open == '\0' && (s[i] == '"' || s[i] == '\'')) open = s[i];
            else if (s[i] == open) open = '\0';
        }
        return open != '\0';
    }

    // The element scopes open at 'pos' (inside .( ) or sort( ) etc.), innermost last, with the first element of
    // each list as 'it' (what completion shows the properties of).
    static Scope ScopeAt(string line, int pos, Env e)
    {
        var open = new List<int>();
        char quote = '\0';
        for (int i = 0; i < pos; i++)
        {
            char c = line[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c == '"' || c == '\'') quote = c;
            else if (c == '(') open.Add(i);
            else if (c == ')' && open.Count > 0) open.RemoveAt(open.Count - 1);
        }
        foreach (int p in open)
        {
            int dot = -1;
            if (p > 0 && line[p - 1] == '.') dot = p - 1;
            else
            {
                int w = p;
                while (w > 0 && IsWordChar(line[w - 1])) w--;
                if (w < p && w > 0 && line[w - 1] == '.' && ListOps.TryGetValue(line.Substring(w, p - w), out ListOp op) && op.perElement) dot = w - 1;
            }
            if (dot < 0) continue;
            int start = ChainStart(line, dot);
            if (start < 0 || start >= dot) continue;
            Many list = AsMany(Eval(new Parser(line.Substring(start, dot - start)).Expression(), e));
            e.scope = new Scope { it = list.Count > 0 ? list[0] : null, index = 0, up = e.scope };
        }
        return e.scope;
    }

    // Where the chain of dots ending at 'end' starts: all.cell.(x).mix -> the 'a' of all.
    static int ChainStart(string s, int end)
    {
        int i = end;
        while (i > 0)
        {
            char c = s[i - 1];
            if (IsWordChar(c))
            {
                while (i > 0 && IsWordChar(s[i - 1])) i--;
                while (i > 1 && s[i - 1] == '-' && IsWordChar(s[i - 2])) // dna-kill
                {
                    int j = i - 1;
                    while (j > 0 && IsWordChar(s[j - 1])) j--;
                    if (!IsHyphenWord(s.Substring(j, ReadWord(s, i) - j))) break;
                    i = j;
                }
            }
            else if (c == ')' || c == ']')
            {
                int depth = 0, k = i - 1;
                for (; k >= 0; k--)
                {
                    if (s[k] == ')' || s[k] == ']') depth++;
                    else if (s[k] == '(' || s[k] == '[') { depth--; if (depth == 0) break; }
                }
                if (k < 0) return -1;
                i = k;
                if (i > 0 && IsWordChar(s[i - 1])) continue; // a call: take its name too
            }
            else break;
            if (i > 0 && s[i - 1] == '.') { i--; continue; }
            break;
        }
        return i;
    }

    /// <summary>What can follow a dot after 'v'.</summary>
    public static void MembersOf(object v, List<Suggestion> into)
    {
        if (v is Space space)
        {
            foreach (var kv in space.members) into.Add(new Suggestion { text = kv.Key, help = Summary(kv.Value) });
            return;
        }
        if (v is Many m)
        {
            var kinds = new Dictionary<string, int>();
            foreach (object x in m)
                foreach (string k in KindsOf(x))
                    kinds[k] = kinds.TryGetValue(k, out int c) ? c + 1 : 1;
            foreach (var kv in kinds) into.Add(new Suggestion { text = kv.Key, help = $"kind: {kv.Value} here" });
            foreach (ListOp op in ListOps.Values) into.Add(new Suggestion { text = op.name, help = op.args + "  " + op.help, tail = op.needsArgs ? "(" : null });
            foreach (Verb verb in Verbs.Values)
                if (Any(m, verb.on)) into.Add(new Suggestion { text = verb.name, help = "verb: " + verb.help });
            foreach (Prop p in Props)
                if (Any(m, p.on)) into.Add(new Suggestion { text = p.name, help = "each one's " + p.help });
            return;
        }
        if (v == null) return;
        foreach (Prop p in Props)
            if (p.on(v)) into.Add(new Suggestion { text = p.name, help = PropHelp(p) });
        foreach (Verb verb in Verbs.Values)
            if (verb.on(v)) into.Add(new Suggestion { text = verb.name, help = "verb: " + verb.help });
        if (ListOps.TryGetValue("print", out ListOp print)) into.Add(new Suggestion { text = print.name, help = print.help });
    }

    static string PropHelp(Prop p) => p.help + (p.add != null ? "  (+= / -=)" : p.set != null ? "" : "  (read)");

    // Samples up to 64 elements (a property or verb on any of them is offered).
    static bool Any(Many m, Func<object, bool> on)
    {
        int step = Mathf.Max(1, m.Count / 64);
        for (int i = 0; i < m.Count; i += step)
            if (m[i] != null && on(m[i])) return true;
        return false;
    }

    static void WordsAt(Env e, List<Suggestion> into)
    {
        if (e.scope != null)
        {
            into.Add(new Suggestion { text = "index", help = "its place in the list (0 first)" });
            into.Add(new Suggestion { text = "it", help = "the element itself" });
            object it = e.scope.it;
            if (it != null)
                foreach (Prop p in Props)
                    if (p.on(it)) into.Add(new Suggestion { text = p.name, help = "its " + p.help });
            foreach (string k in AllKinds()) into.Add(new Suggestion { text = k, help = "is it one (true / false)" });
        }
        foreach (var kv in Vars) into.Add(new Suggestion { text = kv.Key, help = "yours: " + Summary(kv.Value) });
        foreach (Word w in Words.Values) into.Add(new Suggestion { text = w.name, help = w.help });
        foreach (Fn f in Fns.Values) into.Add(new Suggestion { text = f.name, help = f.args + "  " + f.help, tail = "(" });
    }

    static IEnumerable<string> Candidates(Env e)
    {
        foreach (string k in Vars.Keys) yield return k;
        foreach (string k in Words.Keys) yield return k;
        foreach (string k in Fns.Keys) yield return k;
        foreach (string k in AllKinds()) yield return k;
    }

    // Starts-with first (shorter first), then contains; one of each text.
    static void Rank(List<Suggestion> found, string prefix, List<Suggestion> into)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var starts = new List<Suggestion>();
        var contains = new List<Suggestion>();
        foreach (Suggestion s in found)
        {
            if (string.IsNullOrEmpty(s.text) || !seen.Add(s.text)) continue;
            if (prefix.Length == 0 || s.text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) starts.Add(s);
            else if (prefix.Length > 1 && s.text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0) contains.Add(s);
        }
        if (prefix.Length > 0) starts.Sort((a, b) => a.text.Length != b.text.Length ? a.text.Length.CompareTo(b.text.Length) : string.CompareOrdinal(a.text, b.text));
        into.AddRange(starts);
        into.AddRange(contains);
        // Only the word itself, already typed out: nothing to offer.
        if (into.Count == 1 && string.Equals(into[0].text, prefix, StringComparison.OrdinalIgnoreCase) && into[0].tail == null) into.Clear();
    }
}
