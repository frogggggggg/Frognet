# Frognet: notes for Claude

Unity 6000.0.63 URP project (new Input System only; A* Pathfinding Project free 4.2; DOTween).
Gameplay lives in `Assets/Viral`. The rest of `Assets` is an older/unrelated project; don't bother learning it.
The game: you're a virus inside one blood vessel bent into a loop. Space-exploration / factory / RTS at virus scale;
you never attack directly, so evasion and defence matter.

## How I like to work

- Simplify and systematize. Systems separate but pluggable, reusable across creatures
  (and ideally games), easy to extend.
- Don't be afraid to restructure or cut overdone code.
- Quick turnarounds without dropping quality.
- **Build for huge scale from the start.** Assume thousands of anything (creatures, antibodies, legs,
  ropes, puffs) and make each system cheap, near-perfect and scalable the first time, not "fine for 90":
  - Draw: one instanced/indirect draw per mesh+material group from a GraphicsBuffer (LegRenderer,
    fumes, antibodies), never a renderer + MaterialPropertyBlock per object. Per-instance data
    (pose, seed, state) goes in the buffer.
  - Animate on the GPU (vertex stage: wiggle, sway, ripples), shared by every pass so depth/outlines
    follow. The CPU only eases a few numbers per object.
  - LOD everything: a lighter mesh past a distance, cull off screen / past a draw distance (the
    shared `SimulationTicker.OnScreen` / `CameraPosition`, never `Camera.main` per object), and tick
    far / off-screen things every few frames, staggered, with the skipped time handed back as dt.
  - Shared, built once: meshes, materials, lookup tables. No per-frame allocations.
  - Neighbour queries through a spatial hash / grid, never all-vs-all. If something is still
    O(n x m), say so and note it under Open items.
  - When adding a system, state its cost per object and what bounds it.
- After editing, check for compile errors (see **Verifying changes**) and fix them.

## Where the system notes are

**Before editing a file, read its notes.** Folder `CLAUDE.md` files load by themselves when you read a file in that
folder; `Docs/*.md` files don't, so open the one for the files you're touching. After changing a system, update its
notes (not this file), tersely: facts, rules, costs, and "don't do X (why)"; no narration.

| Files | Notes |
|---|---|
| `Assets/Viral/Movement/*`: Organism, OrganismStates/Effects, SimulationTicker, VirusMovement, VirusAI, SurfaceField, NavSurface | `Assets/Viral/Movement/CLAUDE.md` (auto) |
| `SpiderLegWalker`, `LegRenderer`, `LegSimulation.compute`, `LegData.hlsl`, `BloodCellLegs.*` | `Docs/legs.md` |
| `Surface.cs`, `Surface.Collider.cs`, `Surface.Ripples.cs`, `SurfaceMap.*`, `RippleField.*`, `PathManager.cs` | `Docs/surfaces.md` |
| `BloodCellCore/Forward.hlsl`, `BloodCellTriplanar.shader`, `ScreenInvert*`, `TransparentDepthForPost*`, `AstrophageCrystalTop`, `SkyboxCache`, `StylizedCellSky*`, `AmbientParticles`, `HoloMap*` | `Docs/rendering.md` |
| `VirusRope.cs`, `RopeBlood.*`, `RopeRadialMenu.cs` | `Docs/rope.md` |
| `Genome`, `GenomeView`, `GenomeBubble/Strand.shader`, `VirusHead`, `FlatMesh`, `GeneEffects`, `Surface.Tendrils.cs`, `CellTendrils.hlsl`, `CellBurst.*`, `CellShards`, `CellDebris.shader`, `InjectionDrill*`, `Substances/Crafting.cs` | `Docs/head-genome.md` |
| `CommandMode`, `CommandBoard`, `Selectable` | `Docs/command-mode.md` |
| `ImmuneSystem`, `CellSignal`, `Antibody*`, `AntibodyHold`, `AntibodyMesh`, `BodyHull`, `AlarmMote*` | `Docs/immune.md` |
| `WhiteBloodCell*`, `ShrinkWrap*` | `Docs/white-blood-cells.md` |
| `UniversalCamera`, `TerminalUI`, `WorldButton`, `ControlsHint` | `Docs/camera-ui.md` |
| `Assets/Viral/Substances/*`: chunks, ResourceField, DustClouds, VirusInventory, InventoryView, SubstanceStore | `Assets/Viral/Substances/CLAUDE.md` (auto) |
| `Assets/Viral/Cells/*`: CellInterior (organelles, stores, metabolism), CellProfile, OrganelleType, CellInteriorView, CellReadout, NucleusView | `Assets/Viral/Cells/CLAUDE.md` (auto) |
| `Assets/Viral/World/*`: streaming, StreamFade, FarField, Vessel, SaveGame, PauseMenu; `Assets/SpawnManager.cs` | `Assets/Viral/World/CLAUDE.md` (auto) |
| `Assets/Viral/Audio/*`: every sound + the music | `Assets/Viral/Audio/CLAUDE.md` (auto) |
| `Assets/Viral/Console/*`: CommandLine (the / console), Cmd language, CmdWorld | `Assets/Viral/Console/CLAUDE.md` (auto) |
| `Assets/Viral/Pathogens/*`: wild viruses (RoundVirusAI), CellInfection, population limits | `Assets/Viral/Pathogens/CLAUDE.md` (auto) |

## Verifying changes without Unity

Unity is usually open on this project, so a second Editor instance can't be launched.

- **`python Tools/check.py`** does all of the below and prints only errors: C# (Unity's Roslyn, ~12 s) + every pass of
  shaders changed in git or including a changed .hlsl (in parallel, fxc optimizer skipped). `--all` every Viral shader
  (~1 min), `--full` with the optimizer (slow; catches failed unrolls), `--cs` / `--shaders`. A **Stop hook**
  (`.claude/settings.json`, `--hook`) runs it at the end of each turn when C# / shaders changed since the last clean
  run and hands the errors back to fix (one round per turn). The details below are how it works / for doing it by hand.
- **C#:** the generated `Assembly-CSharp.csproj` is often stale (missing new files). Copy it,
  replace its `<Compile Include>` list with every `Assets/*.cs` + `Assets/Viral/**/*.cs`
  (excluding `Editor/`), then `dotnet build <copy>.csproj -nologo -v q`. Real references,
  real errors. Keep the copy out of the repo. If `dotnet` has no SDK, use Unity's compiler instead:
  `<Unity>/Editor/Data/NetCoreRuntime/dotnet.exe <Unity>/Editor/Data/DotNetSdkRoslyn/csc.dll @check.rsp`
  with the csproj's HintPaths as `-r:`, its DefineConstants, `-nostdlib+ -noconfig -target:library`,
  and the same file list. **Leave out `Assets/Command.cs`, `Entity.cs` and `Map.cs`**
  (old project, types missing outside Unity): their type-lookup errors stop the compiler before flow
  analysis, which hid a real CS0165 (unassigned variable) in new code. Expect 0 errors.
- **Shaders:** `python Tools/hlslc.py` compiles them outside Unity with Windows' `d3dcompiler_47.dll`
  (D3DCompile, includes inlined from `Library/PackageCache`, Unity's backwards-compatibility flag, D3D11 defines):
  `shader <file.shader> <pass name | #index> <vs> <ps> [KEYWORD...]` (the HLSLINCLUDE + that pass, default variant plus
  the keywords given) or `compute <file.compute> <kernel>`; `HLSLC_FAST=1` skips the optimizer (LegSimulation 95 s -> 23 s). Real fxc errors, including clashes with SRP names (it caught
  a `Sq` that SRP's Common.hlsl already defines). Run it on every pass after shader edits. Unity also imports on focus
  and writes errors to `~/AppData/Local/Unity/Editor/Editor.log`; grep it for `Shader error`. That log is also the
  fastest way to find runtime exceptions the user hasn't mentioned.
- **Performance:** create `Temp/ClaudeProfile.request` (content `quick` skips the experiments) and Unity plays and writes
  `Temp/ClaudeProfile.txt` (CPU markers, GC by call stack, a GPU-module round); never delete that report, wait for its
  mtime to change. `Temp/ClaudeCapture.request` -> `Temp/ClaudeCapture.txt` reports the frames the Profiler window holds
  (e.g. a connected dev build). **GPU costs: measure in a build** (F9 = `PerfBenchmark`, writes `benchmark.txt` next to
  the player log in `%LOCALAPPDATA%Low/DefaultCompany/Mouse Simulator`); editor GPU deltas and D3D11 per-sample GPU
  times are noise. Unity imports edited scripts only on focus.
  `Temp/ClaudeMemory.request` (content `play`: edit mode, then 40 s into play) -> `Temp/ClaudeMemory.txt`: live managed
  heap + bytes held per Assembly-CSharp field. The editor itself holds ~700 MB, so the overlay's "managed" is mostly editor.
  `Temp/ClaudeQuality.request` (a tier name) switches the editor's quality tier. Both in `Editor/EditorRequests.cs`.
- Nothing here has been run in play mode by Claude. Say so when reporting.
- **Sounds:** synthesized clips can be rendered to WAV outside Unity: compile the scripts as above
  into a dll, then a tiny console program (Unity's `NetCoreRuntime/dotnet.exe`, a `runtimeconfig.json`
  for its bundled Microsoft.NETCore.App) that calls the builders by reflection and writes 16-bit WAVs.
  Hand the user the files to listen to. Piano (Resources samples) only plays inside Unity.

## Rules for every system

- **Look:** stylized, cel shaded; the user doesn't want lighting or shadows (real-time shadows are off in every URP tier).
- **Sound / music:** Breath of the Wild meets Spore: sparse, soft, wet, underwater, background. Full direction in
  `Assets/Viral/Audio/CLAUDE.md`; read it before making any sound.
- **Ticking:** creatures and legs are ticked by `SimulationTicker` (`Tick(dt)` etc.), not Unity callbacks; use the passed dt.
  Per-object camera queries go through `SimulationTicker.CameraPosition` / `OnScreen`.
- **New objects / enemies / pickups** are prefabs in `Assets/Viral/Prefabs`.
- **Builds:** `Assets/Viral/Resources/ViralBuildAssets.asset` (`ViralBuildAssets.cs`) references every Viral shader,
  WhiteBloodCellBake.compute, LegSimulation.compute (`legSimulation`), FarField.compute (`farField`) and the bootstrap
  prefabs (WhiteBloodCells, ResourceField, WorldStreamer), so `Shader.Find` and bootstraps work in a build
  (AssetDatabase is editor only: the first build had no antibodies, legs, chunks, white cells or head view). **Add any
  new shader found by name, or prefab spawned from code, to it.** A `shader_feature` keyword that a runtime-built
  material needs is stripped unless a material asset uses it: make it `multi_compile`.
- **Streamed things** (anything the WorldStreamer spawns) fade: a new streamed shader includes `World/StreamFade.hlsl`
  and calls `StreamFadeClip` in every pass (+ `MixAtmosphere`); see the World notes.
- **Blood flow:** anything floating must take the vessel's flow (`Vessel.FlowAt`, `Organism.Fluid`) and, if carried,
  move every frame while on screen; see the World notes.
- **Noise:** new noisy player actions go through `ImmuneSystem.Alarm(cell, point, normal, signal)`.
- **UI screens** use `TerminalUI` (palette, sprites, font, `Typed`).
- **Keyboard shortcuts** read `CommandLine.Keys` (null while the command line has the keys), not `Keyboard.current`.
- **Meshes** used by Surface / SurfaceMap / VirusHead / BodyHull need Read/Write.

## Gotchas

- **Play-mode script reload wipes plain C# fields** (MaterialPropertyBlock, CommandBuffer, lists) while keeping the
  component and its Unity object references. Has bitten three scripts. Recreate anything missing at the top of the frame.
- **Global shader arrays lock their size** at first `SetGlobalVectorArray` for the editor session. Growing one means
  renaming the property.
- URP drives `_Time.y` from `Time.time` in play mode (not `timeSinceLevelLoad`).
- **`atan2(0, 0)` is NaN on D3D.** Guard any atan2 of a direction that can be on the axis (a mesh pole vanished).
- `UNITY_ANY_INSTANCING_ENABLED` is always *defined* (0 or 1): test its value, or use `UNITY_PROCEDURAL_INSTANCING_ENABLED`.
- **`Mathf.Max` / `Min` with 3+ arguments allocate** (a `params float[]`): nest two-argument calls. Per object per
  frame it was ~40 KB/frame of garbage in FarField (GC spikes 12-14 ms). `Tools/check.py` flags them.
- HLSL has no short-circuit `||`. fxc can drop RWStructuredBuffer stores placed right before a `continue`: do each
  item's work in a function with early returns and one store after the call (`Docs/legs.md`).
- Unity UI draws a material's **first pass only**. A fully clear UI `Image` click catcher is culled
  (`cullTransparentMesh`) and the EventSystem misses it.
- Writing the transform of an interpolated Rigidbody every frame fights interpolation (see Organism's rotation gotcha).

## Open items (cross-cutting; per-system ones are in their notes)

- **Nothing was verified in play mode.** Shaders compiled by Unity without errors after the last fixes, but visuals and
  performance are unconfirmed.
- **Crowd performance:** profile again after GPU legs + ticker LOD + collider compression. Candidates: PathManager's
  two-tier grid, agent-vs-agent physics collisions, A* `GetNearest` per crawler per step, piece-vs-piece cell contacts,
  antibodies' O(n x m) loops (see the Movement, surfaces and immune notes).

## Compact instructions

When compacting, keep: the task and what the user asked for, files changed, decisions and corrections the user made,
unresolved errors, and which notes files were read.
