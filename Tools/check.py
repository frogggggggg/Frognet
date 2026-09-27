"""Compile-check the game outside Unity: C# with Unity's own Roslyn, shaders with Tools/hlslc.py. Prints only errors.

usage: python Tools/check.py            C# + every pass of shaders changed in git (and shaders including changed .hlsl)
       python Tools/check.py --all      C# + every Viral shader / compute shader
       python Tools/check.py --cs       C# only
       python Tools/check.py --shaders  changed shaders only
       python Tools/check.py --full     also run fxc's optimizer (slow: ~1.5 min for LegSimulation; catches
                                        optimizer-only errors such as failed loop unrolls). Default skips it.
       python Tools/check.py --hook     Stop hook: C# / shaders changed since the last clean run, silent if none;
                                        exit 2 with the errors on stderr (Claude sees them and fixes them)

C#: references, defines and language version come from Assembly-CSharp.csproj (its HintPaths, and its project
references as Library/ScriptAssemblies/<name>.dll); the file list comes from disk (the csproj's is often stale):
Assets/*.cs + Assets/Viral/**/*.cs minus Editor/ folders and the old project's Command.cs / Entity.cs / Map.cs (their
type-lookup errors stop the compiler before flow analysis and hide real errors like CS0165).
Shaders: every pass (by index) / kernel, in parallel, default variant only (check a keyword variant by hand:
hlslc.py ... KEYWORD); hull / domain stages and CGPROGRAM shaders aren't checked (listed as notes).
"""
import json, os, re, subprocess, sys, tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VIRAL = os.path.join(ROOT, 'Assets', 'Viral')
CSPROJ = os.path.join(ROOT, 'Assembly-CSharp.csproj')
OLD_PROJECT = {'Command.cs', 'Entity.cs', 'Map.cs'}
SHADER_EXT = ('.shader', '.compute')
INCLUDE_EXT = ('.hlsl', '.cginc')
WORK = os.path.join(tempfile.gettempdir(), 'frognet-check')
STATE = os.path.join(WORK, 'last-clean.json')
os.makedirs(WORK, exist_ok=True)


def rel(p): return os.path.relpath(p, ROOT).replace('\\', '/')


# ---------- C# ----------

def cs_files():
    files = [os.path.join(ROOT, 'Assets', f) for f in os.listdir(os.path.join(ROOT, 'Assets'))
             if f.endswith('.cs') and f not in OLD_PROJECT]
    for d, dirs, fs in os.walk(VIRAL):
        dirs[:] = [x for x in dirs if x != 'Editor' and not x.endswith('~')]
        files += [os.path.join(d, f) for f in fs if f.endswith('.cs')]
    return sorted(files)


def check_cs(files):
    proj = open(CSPROJ, encoding='utf-8').read()
    defines = re.search(r'<DefineConstants>(.*?)</DefineConstants>', proj).group(1)
    lang = (re.search(r'<LangVersion>(.*?)</LangVersion>', proj) or [None, '9.0'])[1]
    refs = re.findall(r'<HintPath>(.*?)</HintPath>', proj)
    for name in re.findall(r'<ProjectReference Include="(.*?)\.csproj"', proj):
        dll = os.path.join(ROOT, 'Library', 'ScriptAssemblies', name + '.dll')
        if os.path.exists(dll): refs.append(dll)
    engine = next(r for r in refs if r.endswith('UnityEngine.dll'))
    data = engine[:engine.index(os.sep + 'Managed' + os.sep)] if os.sep + 'Managed' + os.sep in engine \
        else engine[:engine.index('/Managed/')]
    dotnet = os.path.join(data, 'NetCoreRuntime', 'dotnet.exe')
    csc = os.path.join(data, 'DotNetSdkRoslyn', 'csc.dll')

    rsp = os.path.join(WORK, 'check.rsp')
    with open(rsp, 'w', encoding='utf-8') as f:
        f.write('-nostdlib+ -noconfig -target:library -nologo -langversion:%s -nowarn:0169,0414,0649,0162,0168,0219\n' % lang)
        f.write('-out:"%s"\n' % os.path.join(WORK, 'check.dll'))
        f.write('-define:%s\n' % defines)
        for r in refs: f.write('-r:"%s"\n' % os.path.join(ROOT, r) if not os.path.isabs(r) else '-r:"%s"\n' % r)
        for s in files: f.write('"%s"\n' % s)
    out = subprocess.run([dotnet, csc, '@' + rsp], cwd=ROOT, capture_output=True, text=True, errors='replace')
    errors = sorted({l.replace(ROOT + os.sep, '').strip() for l in (out.stdout + out.stderr).splitlines()
                     if ': error ' in l})
    return errors, out.returncode == 0 and not errors


# Mathf.Max / Min with 3+ arguments take a params float[]: an allocation per call (40 KB/frame in FarField once).
PARAMS_MINMAX = re.compile(r'Mathf\.(Max|Min)\(')


def params_minmax(files):
    found = []
    for path in files:
        src = open(path, encoding='utf-8', errors='replace').read()
        for m in PARAMS_MINMAX.finditer(src):
            depth, commas, i = 0, 0, m.end()
            while i < len(src):
                c = src[i]
                if c in '([{': depth += 1
                elif c in ')]}':
                    if depth == 0: break
                    depth -= 1
                elif c == ',' and depth == 0: commas += 1
                i += 1
            if commas >= 2:
                found.append('%s(%d): error: Mathf.%s with %d arguments allocates a params array; nest 2-argument calls'
                             % (rel(path), src.count('\n', 0, m.start()) + 1, m.group(1), commas + 1))
    return found


# ---------- shaders ----------

def viral_shaders():
    found = []
    for d, dirs, fs in os.walk(VIRAL):
        dirs[:] = [x for x in dirs if not x.endswith('~')]
        found += [os.path.join(d, f) for f in fs if f.endswith(SHADER_EXT + INCLUDE_EXT)]
    return found


def affected(changed):
    """Shaders to check for a set of changed shader / include files: the shaders themselves plus every shader that
    includes a changed include, transitively."""
    every = viral_shaders()
    text = {p: open(p, encoding='utf-8', errors='replace').read() for p in every}
    dirty, todo = set(), [c for c in changed]
    while todo:
        p = todo.pop()
        if p in dirty: continue
        dirty.add(p)
        if p.endswith(INCLUDE_EXT):
            name = os.path.basename(p)
            todo += [q for q in every if re.search(r'#\s*include\s+"[^"]*\b' + re.escape(name) + '"', text[q])]
    return sorted(p for p in dirty if p.endswith(SHADER_EXT))


def run_hlslc(args, full):
    env = dict(os.environ)
    if not full: env['HLSLC_FAST'] = '1'
    out = subprocess.run([sys.executable, os.path.join(ROOT, 'Tools', 'hlslc.py')] + args, cwd=ROOT, env=env,
                         capture_output=True, text=True, errors='replace')
    # hlslc prints warnings too; keep the verdict lines and errors only
    text = '\n'.join(l for l in (out.stdout + out.stderr).splitlines() if 'warning' not in l.lower())
    return out.returncode == 0, text.strip()


def shader_jobs(path):
    """hlslc argument lists for every kernel / pass of a shader, plus notes on what can't be checked."""
    src = open(path, encoding='utf-8', errors='replace').read()
    if path.endswith('.compute'):
        return [['compute', path, k] for k in re.findall(r'^\s*#pragma\s+kernel\s+(\w+)', src, re.M)], []
    if 'HLSLPROGRAM' not in src:
        return [], [rel(path) + ': no HLSLPROGRAM (skipped)']
    include = re.search(r'HLSLINCLUDE(.*?)ENDHLSL', src, re.S)
    include = include.group(1) if include else ''
    jobs, notes = [], []
    for i, m in enumerate(re.finditer(r'HLSLPROGRAM(.*?)ENDHLSL', src, re.S)):
        body = m.group(1)
        vs = re.search(r'#pragma\s+vertex\s+(\w+)', body) or re.search(r'#pragma\s+vertex\s+(\w+)', include)
        ps = re.search(r'#pragma\s+fragment\s+(\w+)', body) or re.search(r'#pragma\s+fragment\s+(\w+)', include)
        if vs and ps: jobs.append(['shader', path, '#%d' % i, vs.group(1), ps.group(1)])
        else: notes.append('%s pass #%d: no vertex/fragment pragma (skipped)' % (rel(path), i))
    return jobs, notes


def check_shaders(paths, full):
    """Every pass / kernel of the given shaders, in parallel. Returns (failure texts, notes, failed shader count)."""
    from concurrent.futures import ThreadPoolExecutor
    jobs, notes = [], []
    for p in paths:
        j, n = shader_jobs(p)
        jobs += j
        notes += n
    with ThreadPoolExecutor(max_workers=os.cpu_count() or 4) as pool:
        results = list(pool.map(lambda a: (a[1], run_hlslc(a, full)), jobs))
    fails = [text for _, (ok, text) in results if not ok]
    return fails, notes, len({p for p, (ok, _) in results if not ok})


# ---------- change tracking ----------

def git_dirty():
    out = subprocess.run(['git', 'status', '--porcelain', '--untracked-files=all', '--', 'Assets/Viral'], cwd=ROOT,
                         capture_output=True, text=True).stdout
    paths = [os.path.join(ROOT, l[3:].strip().strip('"')) for l in out.splitlines()]
    return [p for p in paths if p.endswith(SHADER_EXT + INCLUDE_EXT) and os.path.exists(p)]


def stamps(paths): return {rel(p): os.path.getmtime(p) for p in paths}


def load_state():
    try: return json.load(open(STATE))
    except (OSError, ValueError): return {}


# ---------- main ----------

def main():
    args = set(sys.argv[1:])
    hook = '--hook' in args
    if hook:
        try:
            if json.loads(sys.stdin.read() or '{}').get('stop_hook_active'): return 0  # one fix-up round per turn
        except ValueError: pass
    do_cs = not (args & {'--shaders'})
    do_shaders = not (args & {'--cs'})

    files = cs_files()
    shaders_all = viral_shaders()
    now = stamps(files + shaders_all)
    last = load_state()
    changed_since = [os.path.join(ROOT, p) for p, t in now.items() if last.get(p) != t]

    if hook:
        do_cs = any(p.endswith('.cs') for p in changed_since)
        shader_targets = affected([p for p in changed_since if p.endswith(SHADER_EXT + INCLUDE_EXT)])
        if last == {}: shader_targets = affected(git_dirty())  # first run: no baseline, check what git says changed
        if not do_cs and not shader_targets: return 0
    elif '--all' in args:
        shader_targets = sorted(p for p in shaders_all if p.endswith(SHADER_EXT))
    else:
        shader_targets = affected(git_dirty())

    report, ok = [], True
    if do_cs:
        errors, cs_ok = check_cs(files)
        lint = params_minmax(files)
        errors += lint
        cs_ok &= not lint
        ok &= cs_ok
        report.append('C#: %d files, %s' % (len(files), 'OK' if cs_ok else '%d error(s)' % len(errors)))
        report += ['  ' + e for e in errors[:40]]
        if len(errors) > 40: report.append('  ... %d more' % (len(errors) - 40))
    if do_shaders:
        fails, notes, bad = check_shaders(shader_targets, '--full' in args)
        report += fails
        ok &= bad == 0
        report.append('Shaders: %d checked, %s' % (len(shader_targets), 'OK' if bad == 0 else '%d failed' % bad))
        if not hook: report += ['  note: ' + n for n in notes]

    if ok:
        json.dump(now, open(STATE, 'w'))
        if hook: return 0
    text = '\n'.join(report)
    if hook:
        sys.stderr.write('Compile check (Tools/check.py) found errors; fix them:\n' + text + '\n')
        return 2
    print(text)
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
