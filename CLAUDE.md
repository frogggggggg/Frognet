# Frognet: notes for Claude

Unity 6000.0.63 URP project (new Input System only; A* Pathfinding Project free 4.2; DOTween).
Gameplay lives in `Assets/Viral` (movement in `Assets/Viral/Movement`). The rest of
`Assets` is an older/unrelated project; don't bother learning it.

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

## Verifying changes without Unity

Unity is usually open on this project, so a second Editor instance can't be launched.

- **C#:** the generated `Assembly-CSharp.csproj` is often stale (missing new files). Copy it,
  replace its `<Compile Include>` list with every `Assets/*.cs` + `Assets/Viral/**/*.cs`
  (excluding `Editor/`), then `dotnet build <copy>.csproj -nologo -v q`. Real references,
  real errors. Keep the copy out of the repo. If `dotnet` has no SDK, use Unity's compiler instead:
  `<Unity>/Editor/Data/NetCoreRuntime/dotnet.exe <Unity>/Editor/Data/DotNetSdkRoslyn/csc.dll @check.rsp`
  with the csproj's HintPaths as `-r:`, its DefineConstants, `-nostdlib+ -noconfig -target:library`,
  and the same file list. **Leave out `Assets/Command.cs`, `Entity.cs` and `Map.cs`**
  (old project, types missing outside Unity): their type-lookup errors stop the compiler before flow
  analysis, which hid a real CS0165 (unassigned variable) in new code. Expect 0 errors.
- **Shaders:** can't be compiled outside Unity. Unity imports on focus and writes errors to
  `~/AppData/Local/Unity/Editor/Editor.log`; grep it for `Shader error`. That log is also the
  fastest way to find runtime exceptions the user hasn't mentioned.
- Nothing here has been run in play mode by Claude. Say so when reporting.
- **Sounds:** synthesized clips can be rendered to WAV outside Unity: compile the scripts as above
  into a dll, then a tiny console program (Unity's `NetCoreRuntime/dotnet.exe`, a `runtimeconfig.json`
  for its bundled Microsoft.NETCore.App) that calls the builders by reflection and writes 16-bit WAVs.
  Hand the user the files to listen to. Piano (Resources samples) only plays inside Unity.

## Sound and music direction

Every sound and all music follow one vibe: **Breath of the Wild meets Spore**. Sparse, airy piano
(single notes and open arpeggios, lots of space between them, soft felt attack), lydian / pentatonic
colour, glassy shimmer and soft pads, big gentle reverb; organic sounds are wet, squishy and
underwater-muffled (this is inside a body). Transitions and UI moments are small musical gestures
(a rising arpeggio, a falling one back), not generic whooshes or beeps. Nothing harsh or
aggressive; tension comes from sparseness and dissonant intervals, not loudness.

## Architecture

### Organism (`Movement/Organism.cs`)
Generic creature: Rigidbody, rotation, visual body offset, intent, weighted state tree.
Knows nothing about input, cameras, UI.

- **Intent** (written by controllers): `Move` (world dir, 0-1), `aimForward`, `aimUp`,
  `Press(intent)`, `Hold(intent, value)`. Names are strings in `Intent` (OrganismStates.cs).
- **ISurfaceContact**: `OnSurface`, `SurfaceNormal`, `Surface`. Other systems (legs, rope,
  UI, AI) read this instead of referencing Organism.
- **Brain** (`IOrganismBrain`): optional controller ticked just before the organism, at the
  same rate. `VirusAI` sets itself as one; having a brain marks an organism as "crowd".
- Ticked by `SimulationTicker`, *not* Unity callbacks: `Tick(dt)`, `LateTick(dt)`,
  `FixedTick(dt)`. Everything inside must use the passed dt.
- `Lean(axis, toward, strength)`: any system may ask (every frame) for a body axis to turn
  toward a direction, capped at `maxLean` degrees and eased (`leanSpeed`), on top of the states'
  rotation. `Rotate` eases an un-leaned `_base` so the lean never feeds back into control; the
  lean is updated once per Tick (SnapRotation calls Rotate again in LateTick). VirusMovement
  feeds it `VirusRope.LeanRequest` (mouth dir -> rope 2 m along) while reeling/paying out/slurping.
- `keepUpright`, `Face(dir)`, `Rotate(t)`, `SetExternalRotation(bool)` (rope torque),
  `BodyOffset` (visual lift along `up`).
- **Rotation gotcha:** if `body` is empty *or* the Rigidbody's own object, rotation goes
  through `Rigidbody.MoveRotation` in the physics tick (`RotatesRoot`). Writing the transform
  of an interpolated Rigidbody every frame fights interpolation and the rotation sticks or
  jitters depending on frame timing. This was a real bug; don't undo it.

### States / effects (`OrganismStates.cs`, `OrganismEffects.cs`)
State = `When()` + effect fields + child-state fields (found by reflection). Highest-weight
valid option, descend into children; children outrank parents, later siblings outrank earlier.
Helpers: `Held`, `Pressed`, `Moving`, `Lasting`, `Cooldown`, `TimeInState`, `Find<T>()`.
Effects time themselves with absolute `Time.time`, so they survive being ticked at a low rate.

```
Flying    (leadAxis) -> Still [Coast], Moving [Thrust], Charging [Burst]
Grounded  [Kinematic, Ground] -> Still, Moving [Crawl], Landing [Ripple, SnapRotation],
          Tethering [TapSlam], Drilling [HoldSlam], Focus [Halt, SnapRotation]
Jumping   [Launch]   (outranks Grounded)
```
`TapSlam` (rope tap) and `HoldSlam` (focus inject) each ripple the cell through
`Ground.RippleCell(speed)`; their `rippleSpeed` is scaled against the cell's
`referenceSpeed` (100 in this scene, so defaults are 60 / 150).

### SimulationTicker (`Movement/SimulationTicker.cs`)
One loop for every Organism (+ brain) and SpiderLegWalker instead of thousands of Unity
callbacks, plus **simulation LOD**: crowd organisms tick every 1..4 frames by camera distance,
×2 off screen, staggered, with skipped time handed back as dt (capped). Player (no brain)
always every frame. Also the **shared per-frame camera** (`CameraPosition`, `OnScreen`) —
use it instead of `Camera.main` in anything per-object. Profiler markers: `Simulation.Brains`,
`.Organisms`, `.Late`, `.Legs`, `.Fixed`. Created on demand; add one to a scene to tune it.

### VirusMovement (`Movement/VirusMovement.cs`)
Player controller only: input -> intent, camera modes, WorldButton, focus, rope mouse control,
rope-base hover/click in focus mode. Ground movement is screen-relative using the camera's
right projected on the surface (NOT `Cross(normal, forward)`, which flips on the far side).

### VirusAI (`Movement/VirusAI.cs`)
Autonomous viruses, same intent interface. Chases a target (the player by default):
flying -> `PathManager` field; target on a cell -> fly at the surface point under it with that
cell *excluded* from the field (so it lands instead of avoiding), then crawl the shortest way round
the surface via `SurfaceField` (straight at it once in the goal's triangle or the next one); on the
wrong cell -> jump off. (Crawling no longer uses PathManager: the projected chord got trapped on
cubes and concave shapes.) Crowds (shared spatial hash; 3D in air, along the surface on a shared
cell): overlaps are *distances* eased apart over `crowdSettleTime` (each side takes half), not
full-speed pushes, which overshot on stale positions and made crowds vibrate; a virus touching a
stopped (`_settled`) one ahead and nearer the goal stops too (queues, so crowds ring the goal
instead of shoving the front row); `regroupDistance` + a start/stop dead zone are the hysteresis;
`Move` is eased toward each decision (`steerSmoothing`). Keeps clear of the target. Ground moves are
in the *shown* frame (tangent to `Organism.up`, see NavSurface's roll). Thinks on a staggered timer
(`thinkInterval`). Disables a leftover `VirusMovement` on the same object.

### SurfaceField (`Movement/SurfaceField.cs`)
Shortest way round a Surface to one goal, any shape. Dijkstra over the graph's welded vertices
(A* welds split mesh vertices, so a cube's faces connect) from the goal triangle, then a per-vertex
gradient (area-weighted triangle gradients); a crawler blends its triangle's three barycentrically:
smooth, and no minima but the goal. Cached per (Surface, goal instance id); reflooded only when the
goal changes triangle, at most every 0.25 s; dropped after 5 s unused. Cost: O(V log V) per chased
goal per reflood (V = mesh vertices), O(1) per crawler query; topology built once per graph.

### PathManager (`PathManager.cs`)
Gridless potential-flow field (sink + doublets), trap-free. `GetField/GetDirection(pos, target,
ignoreA, ignoreB)`. Creatures are obstacles by default (`ignoreOrganisms = false`, the user
wants this). `oneSpherePerCreature` merges each creature's colliders into one sphere; body
poses are read once per Rigidbody per step, with offsets/radii baked at `Rescan()`.
**Known weakness:** its lookup grid is sized to the largest obstacle (a cell, ~70m), so
creature-sized spheres share a few huge buckets and each query walks most of them. A two-tier
grid (small buckets for creatures) is the next fix if the AI pass is hot.

### Legs: SpiderLegWalker + LegRenderer + BloodCellLegs
- `SpiderLegWalker` simulates the gait (phase-locked: two alternating groups, feet aimed where
  they'll sit at mid-stance so they never trail; catch-up steps; upright feet; air grab pose
  when spinning fast). Feet and their **normals** are stored relative to the cell they stand on.
- It no longer builds meshes. Each frame it writes 6 small records to `LegRenderer`, which
  draws every walker's legs as instances of one tube mesh — one draw per (material, tube
  resolution, shadows) group — and copies the leg material onto `Custom/BloodCellLegs`.
- Off screen (by more than `offscreenMargin`) or past `cullDistance` the walker skips everything
  and re-plants when seen again. The shared view is sampled before the camera's LateUpdate, so it
  trails a frame: visibility checks are padded (else edge legs blinked and re-planted on camera moves).
- Past `lodDistance` legs submit a lighter tube (about half the rings, fewer sides): its own group.
- Swing: travel starts 10% after the lift and the arc peaks early (peel up, reach, set down).
- Edges: the upper leg arches off the body's surface (`hipUp`), the lower comes down onto the foot's
  (`bendUp`), both lifted by exactly how deep the straight root-to-foot line sinks into the corner
  (`CornerSink`: body plane from `_hubHeight`, learned from feet on the body's face; foot plane), else
  not at all. A guessed tan(half angle) lift arched every edge leg long and read as stretching. A foot steps out of turn past rest reach + the planned lead at this
  speed + `SpanMargin` (capped at `MaxSpan`), not a flat 2x leg length.
- `Probe` ignores hits more than 60° off its direction (`MinProbeFacing`): a probe aimed past a cube's
  edge skimmed the side face and planted feet on bumps far down it.
- Shader: foot and root caps pushed out into rounded nubs, tip tapered; the noise sample is
  reused by DepthNormals; inside the focus sweep legs go plain (`LegSweepCover`, reads the global
  `_InvertSweep`). LegRenderer copies the leg material every frame only in the editor.

### Cell shader family
`BloodCellCore.hlsl` (properties, noise, ripple, bump, depth fragments) and
`BloodCellForward.hlsl` (cel/PBR lighting) were cut verbatim out of `BloodCellTriplanar.shader`
so cells and legs share exactly one code path. `BloodCellTriplanar` keeps tessellation;
`BloodCellLegs` builds its geometry in the vertex stage from the leg buffer.
Legs use `_TessMax = 1` (tessellation is for planets, not thin legs).

### Ripples
`Surface.AddImpact` (Surface.Ripples.cs; was CellImpactRipples; per surface, 64 slots, fades dropped, weakest replaced, near-simultaneous
impacts merged) feeds both the cell shader and `RippleField` — a global list, grouped by cell,
that lets *anything* ride the waves via `RippleField.hlsl` (`positionWS += RippleFieldOffset(...)`),
with no per-object code. Legs/rope/body opt in with `_FollowRipples`.

### Rope (`VirusRope.cs`)
XPBD rope, no input of its own. `PlaceAnchor()`, `reel`, `feed`. Breaks on *sustained* tension
(`breakDelay`), not spikes. Either anchored end is a "base": hover grows it, clicking toggles
**straighten** for the whole rope. Straight with two bases = a rigid rod of the rope's own
length, welded square to both surfaces (`rodStrength`), which moves and turns the planets to
fit and doesn't snap; rods sharing bodies settle as a network. Rope forces treat the body the
player stands on as internal (no more spinning your own planet).
Pulling up a base (grab or tug) marks the rope `suspended`: its settings (straight, pending
length) are kept but dormant until the held end is placed (`FinishActiveRope`). Any length change
by the spool (reel, pay out, auto payout) replaces a pending length.
**Cauterize** (`Cauterize(handle)`, rod only): blood front climbs from that base (`pump` metres,
`cauterizeSpeed`), drawn as **beads that don't exist**: `BuildBlood` makes a see-through proxy
per rope (a tube as wide as the beads can reach, `ProxySides`, one ring per rope ring plus one
past each end; a disc over each base) on its own child renderer, and `Custom/RopeBlood`
ray-traces the beads inside it per pixel (steps of <= 3/4 bead through only the band the beads sit
in -- near side, then the far side if the ray passes beside the core -- testing the ~4 nearest
lattice beads per step, stepping a fixed distance from where the ray enters, never spread over
the stretch -- spread or fixed-count steps skipped beads at grazing / along-the-rope views =
see-through; skirts are closed pucks, lid + wall + floor, marched from lid down to the floor (not stopped at the surface plane: bead undersides hang below it, and a flat stop cut them open seen from below); writes SV_Depth and sphere normals, so shadows / depth normals / focus outlines
see real spheres). The rules live in the shader (per-rope `_Bead*` vectors via a property block):
`beadStrands` strands in a 3-start helix, alternating shades, on the shell `ShellRadius(s)` (full
swell behind, bolus at the front, lub-dub `Heartbeat` bursts, `baseFlare` into each base), each
bead appearing once the fill passes its hash (snap in one by one over `pumpBlend`); skirts =
`skirtRings` rings on each base's surface, sized from the tube's live `ShellRadius` at the base
(a fixed size let the swelling tube lift off its skirt). Each bead is a tiny cell: the cells' `SurfaceHeight`
lumps (`_BeadLumps` across a bead, mapped in the proxy's frame so they ride the rope, own noise
patch per bead) with `BumpNormal` and `CellShade`; alternate beads shift the height toward the deep
colour. Look = `beadLook` if set, else the material of the cell the blood came from
(`SourceLook`), copied onto a runtime material per look (`_beadMats`). CPU
cost per rope: a tiny proxy mesh + 5 vectors per frame. No loose beads ahead of the front
(they'd be outside the proxy). The rope mesh itself stays a plain tube (the core). At
the far base it seals: particles frozen in A's anchor space (`sealedLocal`), sim skipped, bodies
welded with a `FixedJoint` on the dynamic one (none dynamic: nothing). Straighten/length/grab
locked; losing a base unseals (joint destroyed).
Controls (VirusMovement): LMB pays out (`StartFromSpool`: anchored under you on a surface, a
loose trailing end in the air; a grounded tap slams + `PlaceAnchor`, which pins your end and
keeps a loose far end loose). With nothing held, the
nearest loose end within `grabRadius` (else anchored end within `baseGrabRadius`, both from the
body surface) shows a phantom rope (`PhantomEnd`, drawn with the rope's own tube builder), and
an LMB click `GrabEnd`s it (rope reversed so the held end is B, gap bridged with particles); that
press is spent. RMB held reels; an RMB click (released within `clickTime`) = `ReleaseHeld`, cut
at your end, applied once the double-click window passes. Double RMB = `Tug`: far end torn loose
(its body jerked toward you) and whipped in; already loose = slurp (`slurpSpeed` until gone).
Mouth (`frontOnly`): the held end attaches on one side of the body (`mouthSide`, default Below =
-`playerRopePoint.up`, which Organism keeps as the virus's up) at `bodyRadius`. Placement only:
an earlier body-wrap solver pushed the rope, which shoved the player and the planet; removed.
The curl round the body into the mouth is drawn only (`CurlToMouth` in BuildMesh, `curl` in
body radii): keep it out of the solver.

### Camera (`UniversalCamera.cs`)
Modular rig (behaviour list per mode). Added: **transition momentum** — on a mode switch the
pose difference decays on a damped spring seeded with the camera's own velocity, so it carries
its motion instead of easing from a standstill (`transitionMomentum`, `transitionDamping`).
`SpeedFOV` measures speed against the movement's top speed, but that reference only rises as fast
as the body really speeds up (a Burst raised it instantly and the FOV dipped). `UniversalCamera.PointerCaptured` lets gameplay own the pointer (rope bases) so drag-to-look
ignores that click.
`FrameSurface` step (focus mode): orthographic, slid across the view onto the centre of the
surface the target stands on (ISurfaceContact, mesh local bounds), size = surface radius + margin,
grown to keep the target in frame; backs off so the planet isn't near-clipped.

### Other
- `Surface.cs` (component): makes a MeshRenderer walkable. One A* NavMeshGraph per *mesh*,
  built on first use in mesh-local space (scaled up to 100 units for A*'s mm precision) and
  shared by every Surface with that mesh; queries go through the renderer's Transform, so
  any position/rotation/scale works without a rebuild. Creates the AstarPath if missing.
  Meshes need Read/Write for builds. Replaced `NavmeshGraphBinder` (same script GUID).
  Per corner it stores a smoothed normal + directional curvature (least-squares fit, so a
  cylinder is straight along its axis). On Awake it swaps
  the MeshFilter to a shared copy with a crack-free displacement direction in UV3 (angle-
  weighted average of all faces at a position); `BloodCellTriplanar` displaces along it.
- `NavSurface.cs`: attachment to a Surface's graph (graph space). Works on any shape:
  lifts along per-corner arcs by directional curvature (round on spheres, straight along
  cylinders, flat on flat faces; groups split
  at `Surface.creaseAngle`), jumps in the normal (> 5°/pose, i.e. hard edges) rolled out over `edgeRoll` walked,
  steps aimed ahead+down so they wrap convex edges (and up when blocked, to climb walls),
  heading carried across edges, and the walkable side fixed at landing from the mesh's
  winding (graphs are built with `recalculateNormals` off, which would rewind 3D meshes).
  `Normal` is the *shown* normal (rolled round hard edges); `SurfaceNormal` the real one. `Crawl`
  rotates the heading from the shown frame onto the real face before stepping: stepping along the
  shown tangent just past an edge pointed off the new face, projected back to the same spot, walked
  0, so the roll never wore off and crawlers stuck on every cube edge. `ToShown` maps a real-face
  direction into the shown frame (what `Move` should be in).
- `SpawnManager.cs` (Assets/): fills a ball around itself with weighted prefabs, no overlaps. Sizes vary:
  `sizeRange` (0.5-2.5x the prefab's scale; replaced `scaleRange`, which the scene had at 1-1) skewed small
  (`sizeSkew`), Rigidbody mass x size^3 (`massWithSize`).
- `Surface.isCell` (default on): off = walkable but not a cell (no immune signal via `CellSignal.For`, not a
  command-mode "Cell", not a spawn anchor). Resource chunks use it.
- `AmbientParticles.cs`: GPU speck field around the camera, wrapped into a box, smeared by the
  **player's** velocity, only visible above a speed.
- `InjectionDrill.cs` (on the Virus): procedural fluted drill that screws from under the virus to
  the centre of the surface it stands on when focus starts (VirusMovement focus events, added by
  code), spins, ripples the cell at entry, winds back in on exit. Replaces VirusAccess's UI bar.
  Second material `InjectionDrillXRay.shader` draws it over everything (Overlay+10, ZTest Always) but
  only inside the sweep circle (`_InvertSweep`, zeroed when the sweep ends), since the drill is
  inside the planet and the sweep flattens it.
- `RopeRadialMenu.cs`: bio-lab terminal style shared with the focus sweep (navy/cyan, acid green =
  engaged, chamfered scanlined panels, rotating dial, typed-in monospace text from OS fonts).
  Focus mode, clicking a rope base opens it on the base (VirusMovement):
  Straighten toggle + Length field + Cauterize (bottom panel, blood-red progress bar) (`VirusRope.SetLength`: eases every segment alike at feed speed,
  re-spaces particles past 0.6-1.6x spacing; rods push their bodies to fit). Self-built uGUI with
  the built-in legacy font and generated disc/ring sprites; talks to the rope only by handle
  (`IsBase`, `BasePoint`, `IsStraight`, `ToggleStraight`, `LengthOf`, `SetLength`). Click elsewhere
  or Escape closes it (Escape exits focus only when it's closed).
- `TerminalUI.cs`: the focus-mode terminal look shared by its screens: palette, generated sprites
  (chamfer, dial, disc, scanlines), OS terminal font, canvas/rect builders, `Typed` text. Use it for
  any new screen instead of copying from the rope menu.
- `Genome.cs` + `GenomeView.cs` + `VirusHead.cs`: `Genome` = what a virus carries in its head (genes: code,
  name, colour) and the one loaded for injection (`Select`, `SelectedGene`, `SelectionChanged`; injection
  itself is still to come, e.g. InjectionDrill reading `SelectedGene`).
  `VirusHead` splits the head off the body mesh at start: the body is one mesh (Icosphere) whose crystal
  shell is one material slot and whose ball sits inside it; every non-crystal *triangle* with its centre
  inside the crystal's ellipsoid goes with the crystal into a child "Head" pivoted on the head's bottom (the
  crystal's point facing the mesh's middle); the body keeps a per-instance copy with the rest. So the head
  grows under the cursor by plain scaling (`hoverScale`, bottom fixed) and depth / focus outlines / shadows
  follow. Needs the model's Read/Write (virus.blend import: isReadable on). In focus mode the visible head
  is the *ball* (the crystal is transparent, no depth, so the sweep doesn't show it): `Sphere()` is the ball,
  fitted as a sphere to the biggest connected piece wholly inside the crystal (body bits poking in skewed a
  plain bounds box, and the bubble's head circle didn't meet the real ball).
  Growing only the crystal (a shader-side attempt) changed nothing visible.
  In focus mode a click on the head (VirusMovement: `headPickRadius` or its on-screen size; a rope base
  wins) opens `GenomeView`: a tube grows out of the ball on screen and swells into a big sphere off to one
  side (`sideAngle` off the virus's up, the side that fits the screen, never over the virus; `rise`,
  `neckWidth`), flaring out of the head and pinched into the sphere like a drop. One outline runs round
  head + tube + sphere; on the head only a ring at its edge is filled, the real ball shows through. Focus mode's own colours (the sweep material's `_FillColor` / `_HighlightColor`, read live:
  `matchFocusSweep`). One full-screen UI image, `Hidden/GenomeBubble` (head circle + tube capsule + sphere
  smooth-unioned as a distance field in canvas units). Inside, the DNA is a flat, simple, saturated 2D
  diagram (`Hidden/GenomeStrand`, vertex colours, ortho, command buffer into an MSAA render texture): each
  gene a double helix running from a dot on the outline in toward the middle like a spoke (`strandLength`;
  spaced round from the tube's mouth so it falls midway between two),
  A-T / G-C rungs colour-coded, the code label just outside the outline. Still (no scrolling); pointing at
  one makes it jiggle (`jiggle`, a damped wave out from the outline). Strands grow in as it opens; picking
  is by angle. One `Helix` builder draws a strand along any spine (straight spoke, or a path).
  **Injection test:** clicking a strand selects it and slides it along a canvas path (its spoke, across to
  the tube's mouth, down the tube into the ball, through the body, down `InjectionDrill.Span` to the tip)
  like a rope pulled through, shrinking and funnelling toward the tube's width (`injectDuration`; `headShare` of it to reach the head, the
  rest down the virus and drill, the strand shrunk to fit that leg -- by distance alone that leg was a few
  pixels and flashed by, so it seemed to vanish; never under
  `minInjectWidth` px: the tube can be a few pixels, and sized to it exactly the strand vanished), drawn like the sphere's strands (command buffer into a screen-sized render texture, a full-screen RawImage; a
  `UIShape` UI mesh never showed, removed); at the drill tip `InjectionDrill.Deliver()` ripples the
  cell and `Genome.Injected` fires; then the strand grows back in the sphere. Escape or a click elsewhere closes it (plays
  backwards); the rope menu and it close each other.
- **Command mode (RTS)**: `CommandMode.cs` + `CommandBoard.cs` + `Selectable.cs`. Q toggles
  (`toggleKey`; Escape leaves): `UniversalCamera.FreeCursor` unlocks/shows the cursor whatever the camera mode says (look
  that needs a locked cursor stops), `CommandMode.Active` makes VirusMovement drop its mouse handling (keys
  still move) and sets `PointerCaptured` (focus mode's drag-look is LMB). `Selectable` (category Agent /
  Target, `kind`, `GroupName`; static `All`; screen box from renderer bounds) goes on anything; VirusAI
  viruses (agents, "Virus") and Surfaces (targets, "Cell") get one when the mode opens. Boxes are fitted to
  the shape: `Selectable.ScreenRect` projects, per renderer, the mesh's extreme vertex along ~40 directions
  (found once; unreadable meshes use their bounds' corners), not the world AABB. No idle boxes (a gray box on
  everything was spam): hovered or inside the drag box pulses `TerminalUI.Target` blue, selected is bold yellow (`selected`, stays yellow when hovered), and
  every saved group's members keep a box in the group's own `color` (`CommandBoard` palette, no blue/yellow; a ring per
  group, stacked outward; bold while the group is pointed at) (full outlines, TerminalUI.BoxSprite, drawn in a
  layer in the order used); boxes are computed once a frame (`FitBoxes`). Click selects,
  drag boxes (box centre inside); Shift toggles (adds, or removes if all of it was selected already; no Ctrl). A selection opens a radial menu (`_pick`) that follows the middle of the selection in the world
  (`CentreOnCanvas`: mean of the members' bounds centres, projected, kept on screen) split by kind; clicking an
  entry saves it: agents -> `CommandBoard.Squad` ("Agents 1"), targets -> `CommandBoard.Task` ("Cells 1",
  numbered per group name). Each group gets a world **tag** (`PlaceTags`: name + count + an X, in its colour, on
  the middle of *all* its members, off-screen ones included, kept on screen; overlapping tags pushed apart): click
  selects the members, X or RMB removes the group. Clicking a *squad's* tag (or node) opens its link menu instead (again: its
  next link; no links: a hint to drag it onto targets).
  **Links** (`CommandBoard.Link`: squad, task, `job`, `oneAtATime`): drag a squad onto a task (tags or board
  nodes, either way round) -> `Connect` (default job: extract, else attack, else move) and the **link menu**
  (`_linkMenu`, compact radial laid out in rows (`LinkSlot`), read top to bottom like a form,
  "SQUAD > TASK" header; follows the middle of the link's world line): ATTACK | EXTRACT | MOVE TO as one segmented row over the dial (the
  one in force lit; greyed unless some target's `Selectable.affords` allows it: cells MoveTo, antibodies Attack|MoveTo,
  set it on anything else), SPREAD | FOCUS as a row under it (`CommandBoard.ModeName`; the code still says `oneAtATime`), UNLINK under that. Lines join the tags in the world (`DrawWorldLines`,
  job-coloured, flow dots, "EXTRACT // SPREAD" label; chains orange "THEN") and the nodes on the board; click
  either to reopen the menu, RMB removes it. Split = agents shared over the open targets; one at a time = all on
  the target nearest the squad (`Link.active`, kept until it's complete, then the next nearest); for MOVE TO that
  is just the nearest. Task -> task drag chains (`CommandBoard.Chain`): when a link is `Task.Done(link)` (attack /
  extract: every target gone or `Selectable.Complete`, an `ICompletable` next to it, `CellSignal` = converted;
  move to: every agent within `ArriveDistance` of its goal's bounds) its squad moves on to the next tasks not yet
  `Complete` (`Advance`, run by `Dispatch`; the job kept where allowed, and the mode). The tasking board (bottom
  middle) is a web: a node per group, re-laid out on every change (`Layout`: layered left to right, tasks by
  chain depth, each squad one column before its earliest task, columns ordered by neighbours' average height,
  swept back and forth, to cut crossings), nodes and board easing to their new places. `Dispatch` (on change +
  every second) shares each squad's agents over its links and asks the task `GoalFor(link, i, n)`; new work types
  (guard an area...) subclass `Task` (`GoalFor`, `Done`, `Complete`, `Affords`). Agents take orders through
  `ICommandable.Order(Transform, Job)`: VirusAI chases a creature, lands on a cell (nearest side) and holds spread
  out, or goes to anything else (the job doesn't change how yet); `Order(null, ..)` returns it to its default
  (chase the player). All hit-testing is in screen space in CommandMode, not the EventSystem.
- **Immune system** (the game's idea: you never attack directly, so evasion and defence matter):
  `ImmuneSystem.cs` (manager, creates itself when the scene has Surfaces) + `CellSignal.cs` (per cell, added
  on demand by `CellSignal.For(transform)`) + `Antibody.cs` + `SignalFume.shader`. Ten times a second every
  `Organism.All` on a cell raises that cell's signal (`idleRate` / `walkRate` / `focusRate`, around a moving
  `Hotspot`); signals halve every `halfLife`. `CellSignal.Pull` = a simple gravity field toward loud cells
  (strength / (1 + (d/falloff)^2)). Antibodies (command-mode targets "Antibodies", ticked in one loop by the
  manager, far / off-screen ones every 2..8 frames (`tickDistance`), stuck ones every frame). **Look:**
  `AntibodyMesh.Build(detail)` (lumpy looped arms = closed tube loops with a hole, stem = two twisted chains,
  hinge blob; Worley beads displaced along the normal; vertex colours blue / magenta folds / teal patches,
  alpha = fold occlusion; uv0 = part + hinge-to-tip) at two details (near beaded, far ~500 verts,
  `lodDistance`). `Custom/Antibody` wiggles it in every pass (arms flap/clap/bend/twist about the hinge,
  stem sways, a wave crawls along the chains; grip closes the arms) and shades it glassy (wrap diffuse,
  back-light, rim). Drawn only by `ImmuneSystem.DrawAntibodies`: frustum + `drawDistance` culled, one
  `RenderMeshPrimitives` per LOD from one buffer (pose + `Antibody.Wiggle` = seed, agitation by state,
  grip). Each antibody keeps a `forceRenderingOff` MeshRenderer with the far mesh only for Selectable's box.
  Behaviour: Drift along the pull + noise -> Patrol (circle over a
  loud cell's hotspot) -> Chase a virus they see (`stickChance`, else ignore it a while) -> Stuck (ride at
  a local offset, arms in; `Antibody.StuckOn(organism)` counts them, no effect yet). `ambientCount` wander
  from the start; loud cells call more in from `arriveDistance` (`reinforceRate`, capped). Fumes: CPU
  puffs (ring buffer) rising round each loud cell's hotspot, two GraphicsBuffers, one
  `Graphics.RenderPrimitives` of billboards. A gene delivered by the head view's injection goes to
  `ImmuneSystem.Deliver(drill.Cell, gene)`: `controlGene` (default INT-5, or the cell's own) converts the
  cell (silent for good), any other gene bursts its signal (`wrongGeneBurst`).
- **Audio** (`Assets/Viral/Audio`): clips are built once at load into float buffers and shared.
  - `Synth.cs` = the parts: seeded noise, sweepable biquads, `Swoosh`, `Bubble`, a Freeverb-style
    `Reverb`, `Normalize`, `Loop` (seamless), and `Clip` (mono for 3D). Build new effects from them.
  - **Piano = real samples, never synthesized** (no sound effect uses it now; `Piano.cs` and
    `Resources/Piano` are kept for later, but Resources ship in builds: delete them if unused). Two additive piano models both sounded synthetic.
    `Piano.Note(buf, start, midiNote, gain, pan, hold)` plays the nearest recorded note from
    `Resources/Piano`, resampled at most 1.5 semitones, with a damper after `hold`. The samples are
    Salamander Grand (CC-BY 3.0, Alexander Holm: credit him in the game; `CREDITS.txt`), from
    tonejs.github.io/audio/salamander/<note>.mp3, named C/Ds/Fs/A + octave. There are A4..A6 so
    far; download more with the same names to widen the range. `GetData` needs Load Type =
    Decompress On Load. Each sample is trimmed to its onset at load (MP3s start with uneven
    silence, which made rhythms land late by different amounts). For other realistic instruments, fetch free recordings the same way.
  - `FocusSound.cs` (creates itself on play, follows the player's focus events). Entering focus
    plays a wet, muffled underwater inject; after `transitionDelay` comes the sweep's transition,
    now **no piano** (the user asked to remove it): a soft underwater bloom = a broad noise wash
    swelling 300 Hz -> 1.6 kHz, 16 small bubbles rising and thinning, and a faint glassy `Shimmer`
    (D6 + A6, detuned sine pairs, 0.12 s ease-in, quiet) in a big soft reverb. Leaving focus: the
    wash falling, a few sinking bubbles, a soft A5 shimmer. Piano history, for reference: a pad +
    bass + loud swoosh + big reverb was "too synth and epic"; runs were "too much / too positive /
    too much flourish"; 12 three-note gestures never settled. So: organic, soft, glassy only as a
    faint colour, never a melody.
  - If Unity's audio device is stalled (the mixer clock doesn't advance) it is reset once
    (`CheckDevice`). That was probably why nothing played at first (unconfirmed).
  - `Sfx.cs`: pool of 24 3D voices for world one-shots from any number of emitters. A call is
    dropped after one distance check against `SimulationTicker.CameraPosition` when out of range,
    when over 8 starts this frame, or when every voice is busy with something louder.
  - `CreatureAudio.cs` (creates itself on play):
    - Step: "spider on a slime ball" (6 variants, pitched by leg size); `SpiderLegWalker` calls it when a foot plants, passing its
      owner. Every player step plays, and every step of the `voicedWalkers` (2) nearest other
      creatures crawling within `nearStepRange`: whole gaits keep their rhythm, while random single
      legs from many sounded erratic. Everyone else is the **crowd bed**: one smooth 2D loop (a low
      brown-noise wash + 450 quiet low "bloo"s, too dense to pick out; sparse pitched blips sounded
      like popcorn). Its level follows *motion*, not step events: a scan of `Organism.All` every
      `scanInterval` sums crawl pace (`Crawl.CurrentSpeed / speed`, ignoring < 0.15) x distance
      falloff. Driving it by steps made it trail off after a crowd stopped, because feet keep
      taking settling steps for a second or two. Cost: O(creatures) per scan. Steps are a soft round
      "bloo" (`Bloo`: a low tone gliding down into its note, 8 ms onset, a breath of noise, 1.2 kHz
      low-pass). A bright click, then a thumpy squelch, were both "too sharp and loud".
    - Impact: a *trampoline* bounce (the `ImpactSound` effect on Landing). A stretchy "sproing"
      glides up in pitch with a spring wobble, then rebounds come quicker (gap x0.68) and quieter,
      like a ball settling. It's built at 4 strength tiers picked by `HitSpeed / fullImpactSpeed`:
      harder = lower, a bigger glide, 2..6 bounces, longer gaps and rings, a membrane thump.
      Volume also scales. The user wanted it bouncier than a plain jelly "bwoing", and the sound
      itself (not only its volume) should change with impact strength.
    - Burst: an underwater whoosh + bubbles (the `BurstSound` effect on Charging).
    - The player's flight loop is *water, not air*, and must be pleasant to hear for a long time.
      It's a smooth, deep, slowly swelling rush: brown noise low-passed, plus a broad quiet flow
      band. Bubbles (shrill) and a resonant gurgle band (unpleasant) were cut: nothing narrow or
      bright in a loop. It gets louder and a little clearer with speed (low-pass 250..1750 Hz);
      pitch only moves 0.92..1.08. It must stay in the background (the user: it "took up too much
      presence"): volume 0.15 x speed^`flightCurve` (1.8, so it's faint at cruising speed), and the
      rumble under 140 Hz is cut.
  - `RopeAudio.cs` (creates itself on play when there's a VirusRope) listens to VirusRope's
    events, so the rope has no audio code: `Anchored(point, normal)` (StartNewRope /
    FinishActiveRope), `Spooled(metres)` (length change of the held rope per physics step, + out /
    - in, auto payout included) and `Stowed` (reeled all the way in).
    - Anchor: a wet, sticky attach (soft slap + suction "plup" gliding up + glue stretch + the cell's
      jelly wobble). A crisp "ch" "didn't fit attaching a thing to a cell": keep contact sounds organic.
    - Spool: two *different* loops (the user: "the in like a slurp, the out like a line being
      cast"). Out (`BuildCast`): a soft mid swish (broad 850 Hz band drifting +-18% twice a loop),
      a warm 280-1300 Hz body, a faint 2.1 kHz sheen, capped at 2.8 kHz (a bright 2.5 + 4 kHz line
      hiss was "unpleasant and hissy"). In (`BuildSlurp`): a wet
      "aw" (650 / 1100 Hz soft formants) in irregular gulps (level = slowed noise squared, ~10-20
      surges/s), low bubbles on the surges, a little sucked air. Each has its own pitch / low-pass
      range in Update (cast 1.5..3.5 kHz, slurp 0.9..2.7 kHz). A soft "squeeze" as it starts from
      rest. History: a clicker + zip was replaced; a low brown-noise loop "sounded like flying";
      sparse random squelch grains had "too much variation"; an impulse train through vowel
      resonances "sounded like a lawnmower"; a narrow swept resonance + a falling "plip" per turn
      "sounds like a lasergun"; one shared recipe (wet noise + bubbles) for both directions wasn't
      it either. So: nothing tonal, no pulse trains, no fast pitch drops, no narrow filters.
    - Stow: a wet "plp" when the rope finishes reeling in.
  - `AmbientMusic.cs` (creates itself on play) loops `Resources/Music/Exploring.wav` (streamed,
    Vorbis) at `volume` 0.18 with a fade in. It must stay *background* (the user: "more background and
    less loud"): piano soft and dark (low-pass 2.8 kHz), melody quiet, lots of reverb (mostly room). The track is a real composition, not generated at
    runtime: `MusicSource~/compose.py` (folder hidden from Unity) renders it offline with numpy
    from real samples (Salamander piano; tonejs-instruments violin / contrabass / harp, CC-BY:
    `Resources/Music/CREDITS.txt`), with FFT convolution reverb and the tail wrapped onto the
    start so it loops seamlessly. Written for the game, not a chord loop: 68 bpm with rubato,
    ~140 s in four sections. **Drift**: open fifths, a flowing harp figure (Spore's water) fades
    in, piano fragments hint at the motif. **Theme**: a real piano melody on the focus sound's
    rising three, dotted rhythms and sighs, a varied left hand, contrabass. **Wonder**: a shift to
    Bb lydian (discovery), the harp states the motif and the piano answers, a glassy high violin.
    **Breath**: near silence, glassy high notes, a reversed piano swell back into the loop.
    Humanized timing and velocity, softer notes darker, rolled dyads, watery wobble on strings.
    The first version (one broken-chord pattern per chord) was "too simple to be good".
    To change it: edit PROG / CH / MEL in compose.py, then run it with a Python venv holding
    `numpy` + `miniaudio`, samples in `samples/<instrument>/` next to it (download commands at the
    top of the script), and the output path as its argument. The user rejected generated "random
    sparse" piano phrases over a noise bed: they wanted "an actual good sounding background".
  - Any clip can be replaced by assigning a recorded one.
- **Resources + inventory** (`Assets/Viral/Substances`; prefabs `GlucoseChunk`, `ProteinChunk`, `ResourceField`
  in `Prefabs/`: new objects / enemies / pickups go there as prefabs). `Substance` = name / code / colour (slots
  match by name; a new substance is just a new chunk prefab). `ResourceChunk` (the prefab is the type: substance,
  `ChunkMesh.Shape` Lumpy (glucose) / Coil (protein: a globule of beads fused along a folded chain), size
  range skewed small (`sizeSkew`), yield ~ r^3) is **landable**: a walkable body like a small cell (`Surface` with
  `isCell` off + a non-convex MeshCollider, both on `ChunkMesh.Walk`: a 1280-triangle hull of the shape with the knobs
  smoothed off; walking every knob of the drawn mesh flicked the crawler's up and shook the camera). **Kinematic,
  moved by its own code, not physics:** `ResourceChunk.Step` bobs it round its place (`bob`, `bobPeriod`) and turns it
  (`spin`), all from a phase that only advances while nobody stands on it (`settleTime` eases it still / awake);
  a dynamic body bumping it nudges it (`bumpResponse`, capped `maxBump`, damped). Dynamic and feather-light it was
  shoved by the rider's own collider every physics step and interpolation fought the pose: the old camera jitter.
  `ResourceField.Update` (order -20, before the ticker poses riders) finds who stands on what (one pass over
  `Organism.All`) and steps chunks: near / stood on / settling / extracted every frame, others every 4 (8 off
  screen) frames staggered; a chunk at rest writes nothing. Ripples: impacts on its Surface go to `RippleField`, and
  `Custom/ResourceChunk` rides it (offset + normal from two extra taps, only while anything ripples); the wave shape
  is the chunk material's `_Ripple*` values (every chunk's hidden renderer carries `ResourceField.SharedChunkMaterial`
  so Surface / RippleField read them), strength via `Surface.referenceSpeed = rippleReference / radius` (bigger
  chunks ripple more). Its MeshRenderer is `forceRenderingOff` (there for Surface and command mode's box, where it's
  a target named after its substance). The transform's scale *is* the radius; draining shrinks the transform
  (`Shrink`), so anyone standing on it rides in; poofing detaches them. `ResourceField` spawns the listed prefabs (a
  share floating 3-16 m off cells, the rest among them, grid-checked for overlaps incl. the bob), draws every chunk
  from its transform in one buffer, one `RenderMeshPrimitives` per (shape, LOD) (`Custom/ResourceChunk`: faint
  breathing wobble, and as it drains lumpy deformation + squash pulses, in the vertex stage; no GPU motion, the pose
  must match what's walked). In editor play with no field in the scene, `Bootstrap` instantiates
  the prefab (logs it); builds need the prefab in the scene. Focus mode: `VirusMovement.UpdateCores` calls
  `ShowCores`; chunks within `extractRange` show a core (`Custom/ResourceCore`, Overlay+10, ZTest Always, only
  inside the sweep like the drill x-ray; hidden behind cells by a CPU raycast per chunk every `sightInterval`);
  a click toggles extraction into `VirusInventory` (renamed: the old project owns `Inventory`): a dust stream
  flows in, it shrinks (`CurrentRadius`) and deforms, and past `poofAt` it bursts (`DustClouds.Burst`) and is
  destroyed. Labels beside hovered / extracting cores. `DustClouds`: generic analytic GPU puffs (burst / stream
  along a curve), CPU only writes new ones into a ring buffer; Overlay+5 so they show over the focus sweep.
  `VirusInventory`: `slotCount` slots, each one substance up to `capacity`. **E tap** (released within
  `clickTime`; held on a surface it still drills) toggles the head view (`GenomeView`) anywhere, and
  `InventoryView` (slot bars, "05 // STORES") follows it beside the sphere (`GenomeView.Placement`). Out of
  focus the cursor is freed (`UniversalCamera.FreeCursor`), the rope ignores the mouse, and a strand click only
  loads the gene (`GenomeView.CanInject`). `ResourceAudio`: start "plup", drain loop, breathy bubbly poof with
  a faint E6/B6 shimmer.
- **White blood cells** (`WhiteBloodCell.cs` per cell, `WhiteBloodCells.cs` manager, `WhiteBloodCellMesh.cs`,
  `Custom/WhiteBloodCell`; prefabs `WhiteBloodCell` (the cell) + `WhiteBloodCells` (manager: count, sizes, shader),
  editor auto-adds the manager prefab if the scene has none). Phagocytes: they only eat pathogens. A walkable ball
  (Surface with `isCell` off: no signal, not a "Cell" target; kinematic Rigidbody, sphere collider, never
  turns so standing viruses don't spin; its MeshRenderer is `forceRenderingOff`, only the walkable mesh + command
  box "White Cell"). Exactly one absorbing **spot**, the tip of a pseudopod (`Spot`, `Reach`): it swings over the
  body or out along a stretched arm at `spotSpeed` (pulls the arm in to swing far; won't extend through another
  cell), and anything the mouth really touches (`Touching`: within `mouthSize` across, up to its size + `touchMargin`
  in front) is swallowed: flying, on another cell, or on this one (the spot then crawls across the body after you). States: Patrol (crawl to a nearby cell, louder
  CellSignal = likelier) -> Examine (spot feels over that cell) -> Hunt (a virus within `senseRange` of its
  surface, + `antibodyRange` per antibody stuck on it (`Antibody.StuckOn`), or touching it) -> Engulf -> Digest.
  Crawl is amoeboid (surging speed, eased), pushed off other colliders (one overlap per think). Swallowing
  (`WhiteBloodCells.Capture/Hold/Finish`): the victim is detached, its Organism, VirusAI, legs and colliders
  switched off, stuck antibodies destroyed (`ImmuneSystem.EatStuck`), settled in the mouth, the lips wrap round it (`Mood.y`, prey radius in `Mood.w`), the arm pulls it back, it
  sinks in and shrinks; then an AI virus is
  destroyed and the player respawns at its start after `respawnDelay`. Events `Noticed` / `Engulfing` /
  `Absorbed` / `Respawned` (`WhiteBloodCellAudio`: a low tritone swell when one starts hunting the player, a wet
  gulp). **Look:** all in the vertex stage, every pass: the mesh is a unit sphere in the *reach frame* (+Z = the
  spot, rings packed in the cap `CapAngle` = shader `CAP_ANGLE`); membrane sampled in the *body* frame so it stays
  put while the spot slides: soft undulation + ruffles (ridged value noise in patches, like the micrograph look;
  Worley popcorn lumps were rejected as "balls"), fine creases per pixel from the noise's analytic gradient;
  leading-edge lobes + tapered tail from its velocity. The cap is a surface of revolution: radius = smooth max of
  the body's section and a round-tipped, irregular, meandering finger's (pinned at both ends so the mouth stays on
  the spot), so it grows out of the body with a fillet. Mouth: irregular breathing rim, ruffled lip, drifting
  wisps (hunger); wrapping folds the tip into an outer + inner layer round the prey, lips meeting at uneven pace.
  Rides `RippleField` (impacts publish there: the cell's hidden renderer gets the material, which carries the
  `_Ripple*` settings). Normals by finite differences, ripples included.
  One `RenderMeshPrimitives` per LOD (near ~8.6k verts with ruffles, far ~800 without). Cost per cell: a think
  every `thinkInterval` = `WhiteBloodCells.Near` (organism grid rebuilt at most once a frame, O(organisms)) +
  one overlap query; idle far / off-screen cells tick every 2..8 frames.
- `ControlsHint.cs`: bottom-right terminal panel with the controls for the current mode (flight /
  surface / focus; lists are data at the top: "[RMB][RMB]" = two prompt icons, plain words are tags),
  retyped on change, H folds it. Inputs are game-style prompts (TerminalUI.PillSprite: circle / pill keycaps;
  MouseSprite: a mouse with the button lit). Creates itself on play.
- `HoloMap.cs` + `HoloMap.shader` / `HoloMapScreen.shader`: hologram map, top-right. Draws real
  scene geometry (grouped by mesh, instanced) + virus dots into a render texture, viewed from
  the camera's direction, faded toward the range sphere. Creates itself on play.
- `TransparentDepthForPostFeature.cs`: stamps transparent objects into the depth texture before
  post so depth of field stops blurring them. **Still not working** — see open items.
- `ScreenInvertTest.cs`: focus-mode sweep (starts from the impact under the virus: `fromImpact`,
  surface point captured on trigger, pinned in the surface's space; ripple echoes trail the front), drawn with the `ScreenInvertSweep.mat` asset itself
  (edit it live; per-frame values go through a property block). Outlines within `highlightRadius`
  of the player (nearest outline tap, world position from depth) use the material's Player Outline colour; also fades depth of field out while the sweep covers
  the screen (its outlines were being blurred into a glow).
  **Backs:** while sweeping (play mode), cell materials go `_Cull` Off (restored after; they're
  shared assets) and the global keyword `INVERT_BACKFACES` makes `BloodCellTriplanar`'s colour and
  depth passes drop front faces inside the sweep circle (`InvertSweepClip` in BloodCellCore, same
  clip-space maths as ScreenInvertSweep, `_InvertSweep` = centre + eased progress), so the depth
  outlines trace the insides. Shadow pass untouched; legs never enable the keyword.
  **Plain outlines:** inside the circle (`InvertSweepCover`) cells drop texture displacement and
  bump but keep ripples (shape and normals: the user wants ripples outlined); the sweep compares the scene
  normals texture (requested by ScreenInvertTransparentDepthFeature), not normals rebuilt from
  depth, which faceted every triangle. Rule: smooth-shaded surfaces show no geometry, only
  silhouettes and real hard edges.
- `AstrophageCrystalTop.shader`: virus shell. DNA frames built once per pixel, DNA faded and
  march shortened with distance, wire noise skipped away from edges.

## Gotchas worth keeping

- **Play-mode script reload wipes plain C# fields** (MaterialPropertyBlock, CommandBuffer,
  lists) while keeping the component and its Unity object references. This has bitten three
  scripts. Recreate anything missing at the top of the frame.
- **Global shader arrays lock their size** at first `SetGlobalVectorArray` for the editor
  session. Growing one means renaming the property.
- URP drives `_Time.y` from `Time.time` in play mode (not `timeSinceLevelLoad`).
- `UNITY_ANY_INSTANCING_ENABLED` is always *defined* (0 or 1) — test its value, or use
  `UNITY_PROCEDURAL_INSTANCING_ENABLED`.
- Unity UI draws a material's **first pass only**.
- A fully clear UI `Image` used as a click catcher is culled (`CanvasRenderer.cullTransparentMesh`) and then
  the EventSystem doesn't hit it: set `cullTransparentMesh = false`, or test the shape directly. The head
  view's sphere did this, so a click on a strand counted as "elsewhere" and closed the view
  (`GenomeView.Covers` is now checked in VirusMovement's `PointerOverUI`).

## Open items

- **Nothing in this session was verified in play mode.** Shaders compiled by Unity without
  errors after the last fixes, but visuals and performance are unconfirmed.
- **Depth of field still blurs transparent objects / world-space UI.** The stamp pass runs
  (log-confirmed) but its debug view drew nothing, so its renderer lists come back empty. It
  now uses an override *material* (override *shader* drew nothing here). Unresolved.
- **Crowd performance:** after GPU legs + ticker LOD + collider compression, profile again.
  Remaining candidates: PathManager's two-tier grid, agent-vs-agent physics collisions (a layer
  that ignores itself), A* `GetNearest` per crawling agent per physics step.
  Antibodies are still O(antibodies x organisms) in `Look` (every ~0.25 s) and O(antibodies x cells) in
  `Avoid` / `Patrolled` per tick; at thousands they need the spatial hash, and past that a data-only
  (Burst jobs) simulation instead of a GameObject each.
- Resources: `ResourceField` draw / reach / step loops are O(chunks) per frame (cheap per chunk, fine into the
  thousands); past ~10k give them a spatial grid and step them in a TransformAccessArray job. Kinematic chunks don't
  collide with each other (a bump can push one into another), and a rope-dragged cell stops dead against one.
  PathManager sizes MeshCollider obstacles by bounds (a bit big for chunks). Its prefab isn't in `Viral.unity` yet (editor auto-adds it).
- For builds, keep these assigned (Shader.Find only saves the editor): `WhiteBloodCells.shader` (set in its prefab; the prefab must be in the scene), `SpiderLegWalker.legShader`,
  `HoloMap` shaders, `TransparentDepthForPostFeature.shader`, `AmbientParticles.shader`,
  `VirusRope.phantomMaterial` (else it needs URP Unlit in the build), `ImmuneSystem.fumeShader` (Hidden/SignalFume) and `antibodyShader` (Custom/Antibody; else no antibodies drawn), `GenomeView` shaders (Hidden/GenomeBubble, Hidden/GenomeStrand: add a GenomeView to the scene with them assigned and set it as VirusMovement's `headView`), `VirusRope.bloodMaterial`
  (a `Custom/RopeBlood` material; else found by name, no beads without it).
- Inspector values reset in an earlier refactor: check Organism > Grounded > surface >
  `hoverHeight` (>= collider radius), snap distance, layers, Flying lead axis.
