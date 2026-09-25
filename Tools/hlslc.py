"""Compile Unity HLSL outside Unity with d3dcompiler_47 (D3DCompile), includes inlined by hand.
usage: hlslc.py compute <file.compute> <kernel> [DEFINE...]
       hlslc.py shader <file.shader> <pass-name> <vs> <ps> [DEFINE...]
"""
import ctypes, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))  # the project (this is in Tools/)
CACHE = os.path.join(ROOT, 'Library', 'PackageCache')
PKGS = {d.split('@')[0]: os.path.join(CACHE, d) for d in os.listdir(CACHE)}

def resolve(inc, cur):
    if inc.startswith('Packages/'):
        parts = inc.split('/')
        return os.path.join(PKGS[parts[1]], *parts[2:])
    for base in (os.path.dirname(cur), os.path.join(ROOT, 'Assets', 'Viral')):
        p = os.path.normpath(os.path.join(base, inc))
        if os.path.exists(p): return p
    raise FileNotFoundError(inc + ' from ' + cur)

def inline(text, cur, seen_once):
    out = []
    for i, line in enumerate(text.split('\n')):
        m = re.match(r'\s*#\s*include\s+"([^"]+)"', line)
        if m:
            try:
                p = resolve(m.group(1), cur)
                src = open(p, encoding='utf-8', errors='replace').read()
            except (KeyError, FileNotFoundError, OSError):
                out.append('// missing (platform branch): ' + m.group(1)); continue
            if '#pragma once' in src:
                if p in seen_once: out.append(''); continue
                seen_once.add(p)
            out.append(f'#line 1 "{p}"')
            out.append(inline(src, p, seen_once))
            out.append(f'#line {i + 2} "{cur}"')
        else:
            out.append(line)
    return '\n'.join(out)

d3d = ctypes.WinDLL('d3dcompiler_47.dll')

class Macro(ctypes.Structure):
    _fields_ = [('Name', ctypes.c_char_p), ('Definition', ctypes.c_char_p)]

def compile_src(src, entry, target, defines, name):
    # Unity's preprocessor takes empty-parameter macros that D3DCompile's rejects: drop them (unused here).
    src = src.replace('#define PopMarker()', '').replace('#define PushMarker(str)', '')
    macros = (Macro * (len(defines) + 1))(*[Macro(k.encode(), v.encode()) for k, v in defines], Macro(None, None))
    code = ctypes.c_void_p(); errs = ctypes.c_void_p()
    b = src.encode('utf-8')
    hr = d3d.D3DCompile(b, len(b), name.encode(), macros, None, entry.encode(), target.encode(), 0x1000, 0, ctypes.byref(code), ctypes.byref(errs))
    msg = ''
    if errs.value:
        vt = ctypes.cast(ctypes.cast(errs, ctypes.POINTER(ctypes.c_void_p))[0], ctypes.POINTER(ctypes.c_void_p))
        GetPtr = ctypes.WINFUNCTYPE(ctypes.c_void_p, ctypes.c_void_p)(vt[3])
        GetSize = ctypes.WINFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p)(vt[4])
        msg = ctypes.string_at(GetPtr(errs), GetSize(errs)).decode(errors='replace')
    lines = [l for l in msg.splitlines() if 'error' in l.lower() or ('warning' in l.lower() and 'Viral' in l)]
    print(f'{name} [{entry} {target}]: {"OK" if hr == 0 else "FAILED"}')
    for l in lines[:25]: print('  ' + l)
    return hr == 0

BASE = [('SHADER_API_D3D11', '1'), ('UNITY_UV_STARTS_AT_TOP', '1'), ('UNITY_REVERSED_Z', '1'), ('SHADER_TARGET', '45'),
        ('UNITY_VERSION', '6000'), ('UNITY_PLATFORM_WINDOWS', '1')]

def extra(args): return [(a.split('=')[0], a.split('=')[1] if '=' in a else '1') for a in args]

mode = sys.argv[1]
if mode == 'compute':
    path, kernel = sys.argv[2], sys.argv[3]
    src = open(path, encoding='utf-8').read()
    src = '\n'.join(l for l in src.split('\n') if not l.strip().startswith('#pragma kernel'))
    src = inline(src, path, set())
    ok = compile_src(src, kernel, 'cs_5_0', BASE + [('SHADER_STAGE_COMPUTE', '1')] + extra(sys.argv[4:]), os.path.basename(path))
else:
    path, pass_name, vs, ps = sys.argv[2:6]
    text = open(path, encoding='utf-8').read()
    inc = re.search(r'HLSLINCLUDE(.*?)ENDHLSL', text, re.S).group(1)
    pm = re.search(r'Name\s+"' + re.escape(pass_name) + r'".*?HLSLPROGRAM(.*?)ENDHLSL', text, re.S).group(1)
    pm = '\n'.join(l for l in pm.split('\n') if not l.strip().startswith('#pragma'))
    src = inline(inc + '\n' + pm, path, set())
    d = BASE + extra(sys.argv[6:])
    ok = compile_src(src, vs, 'vs_5_0', d + [('SHADER_STAGE_VERTEX', '1')], f'{os.path.basename(path)}:{pass_name}')
    ok &= compile_src(src, ps, 'ps_5_0', d + [('SHADER_STAGE_FRAGMENT', '1')], f'{os.path.basename(path)}:{pass_name}')
sys.exit(0 if ok else 1)
