# Frognet: notes for Claude

Unity 6000.0.63 URP project (new Input System only; A* Pathfinding Project free 4.2; DOTween).
Gameplay lives in `Assets/Viral` (movement in `Assets/Viral/Movement`). The rest of
`Assets` is an older/unrelated project; don't bother learning it.

## How I like to work

- Simplify and systematize. Systems separate but pluggable, reusable across creatures
  (and ideally games), easy to extend.
- Don't be afraid to restructure or cut overdone code.
- Quick turnarounds without dropping quality.
- After editing, check for compile errors (see **Verifying changes**) and fix them.

## Verifying changes without Unity

Unity is usually open on this project, so a second Editor instance can't be launched.

- **C#:** the generated `Assembly-CSharp.csproj` is often stale (missing new files). Copy it,
  replace its `<Compile Include>` list with every `Assets/*.cs` + `Assets/Viral/**/*.cs`
  (excluding `Editor/`), then `dotnet build <copy>.csproj -nologo -v q`. Real references,
  real errors. Keep the copy out of the repo. **Leave out `Assets/Command.cs`, `Entity.cs` and `Map.cs`**
  (old project, types missing outside Unity): their type-lookup errors stop the compiler before flow
  analysis, which hid a real CS0165 (unassigned variable) in new code. Expect 0 errors.
- **Shaders:** can't be compiled outside Unity. Unity imports on focus and writes errors to
  `~/AppData/Local/Unity/Editor/Editor.log`; grep it for `Shader error`. That log is also the
  fastest way to find runtime exceptions the user hasn't mentioned.
- Nothing here has been run in play mode by Claude. Say so when reporting.

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
cell *excluded* from the field (so it lands instead of avoiding), then crawl using the same
field projected onto the surface; on the wrong cell -> jump off. Separation in air (3D) and on
the ground (along the surface) via a shared spatial hash; keeps clear of the target. Thinks on
a staggered timer (`thinkInterval`). Disables a leftover `VirusMovement` on the same object.

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
- `SpawnManager.cs` (Assets/): fills a ball around itself with weighted prefabs, no overlaps.
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
  (strength / (1 + (d/falloff)^2)). Antibodies (Y of three built-in capsules, command-mode targets
  "Antibodies", ticked in one loop by the manager): Drift along the pull + noise -> Patrol (circle over a
  loud cell's hotspot) -> Chase a virus they see (`stickChance`, else ignore it a while) -> Stuck (ride at
  a local offset, arms in; `Antibody.StuckOn(organism)` counts them, no effect yet). `ambientCount` wander
  from the start; loud cells call more in from `arriveDistance` (`reinforceRate`, capped). Fumes: CPU
  puffs (ring buffer) rising round each loud cell's hotspot, two GraphicsBuffers, one
  `Graphics.RenderPrimitives` of billboards. A gene delivered by the head view's injection goes to
  `ImmuneSystem.Deliver(drill.Cell, gene)`: `controlGene` (default INT-5, or the cell's own) converts the
  cell (silent for good), any other gene bursts its signal (`wrongGeneBurst`).
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
- For builds, keep these assigned (Shader.Find only saves the editor): `SpiderLegWalker.legShader`,
  `HoloMap` shaders, `TransparentDepthForPostFeature.shader`, `AmbientParticles.shader`,
  `VirusRope.phantomMaterial` (else it needs URP Unlit in the build), `ImmuneSystem.fumeShader` (Hidden/SignalFume) and `antibodyMaterial` (else URP Lit by name), `GenomeView` shaders (Hidden/GenomeBubble, Hidden/GenomeStrand: add a GenomeView to the scene with them assigned and set it as VirusMovement's `headView`), `VirusRope.bloodMaterial`
  (a `Custom/RopeBlood` material; else found by name, no beads without it).
- Inspector values reset in an earlier refactor: check Organism > Grounded > surface >
  `hoverHeight` (>= collider radius), snap distance, layers, Flying lead axis.
