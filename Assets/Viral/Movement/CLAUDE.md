# Movement: Organism, states, ticker, player / AI control, crawling

Legs (SpiderLegWalker, LegRenderer) are in `Docs/legs.md`; Surface / SurfaceMap / PathManager in `Docs/surfaces.md`.

## Organism (`Organism.cs`)
Generic creature: Rigidbody, rotation, visual body offset, intent, weighted state tree. Knows nothing about input,
cameras, UI.
- **Intent** (written by controllers): `Move` (world dir, 0-1), `aimForward`, `aimUp`, `Press(intent)`,
  `Hold(intent, value)`. Names are strings in `Intent` (OrganismStates.cs).
- **ISurfaceContact**: `OnSurface`, `SurfaceNormal`, `Surface`. Other systems (legs, rope, UI, AI) read this instead
  of referencing Organism.
- **Brain** (`IOrganismBrain`): optional controller ticked just before the organism, same rate. `VirusAI` sets itself
  as one; having a brain marks an organism as "crowd".
- Ticked by `SimulationTicker`, *not* Unity callbacks: `Tick(dt)`, `LateTick(dt)`, `FixedTick(dt)`. Everything inside
  uses the passed dt.
- `Lean(axis, toward, strength)`: any system may ask (every frame) for a body axis to turn toward a direction, capped
  at `maxLean`°, eased (`leanSpeed`), on top of the states' rotation. `Rotate` eases an un-leaned `_base` so the lean
  never feeds back into control; lean updated once per Tick (SnapRotation calls Rotate again in LateTick).
  VirusMovement feeds it `VirusRope.LeanRequest` (mouth dir -> rope 2 m along) while reeling / paying out / slurping.
- `Restrain(rotation, maxAngle)` / `ClearRestraint()`: something holds the body (a white cell's bite); the rotation
  used stays within maxAngle of `TargetRotation` (`Wanted`).
- `keepUpright`, `Face(dir)`, `Rotate(t)`, `SetExternalRotation(bool)` (rope torque), `BodyOffset` (visual lift along
  `up`), `Shown` (the turned, leaned, lifted visual), `Fluid` (blood flow velocity, set each FixedTick; see World).
- Awake forces `ContinuousSpeculative` collision: a dash (30 m/s = 0.6 m a step) went through thin parts of cells.
  Don't use Continuous / ContinuousDynamic (they only sweep against static / CCD bodies; cells are dynamic; kinematic
  bodies warn). Cost: broadphase bounds grow by velocity x dt, contacts generated a little early; no extra sweeps.
- **Rotation gotcha:** if `body` is empty *or* the Rigidbody's own object, rotation goes through
  `Rigidbody.MoveRotation` in the physics tick (`RotatesRoot`). Writing the transform of an interpolated Rigidbody every
  frame fights interpolation (rotation sticks / jitters with frame timing). Real bug; don't undo it.

## States / effects (`OrganismStates.cs`, `OrganismEffects.cs`)
State = `When()` + effect fields + child-state fields (found by reflection). Highest-weight valid option, descend into
children; children outrank parents, later siblings outrank earlier. Helpers: `Held`, `Pressed`, `Moving`, `Lasting`,
`Cooldown`, `TimeInState`, `Find<T>()`. Effects time themselves with absolute `Time.time`, so they survive low-rate ticks.
```
Flying    (leadAxis) -> Still [Coast], Moving [Thrust], Charging [Burst]
Grounded  [Kinematic, Ground] -> Still, Moving [Crawl], Landing [Ripple, SnapRotation],
          Tethering [TapSlam], Drilling [HoldSlam], Focus [Halt, SnapRotation, Pump]
Jumping   [Launch]   (outranks Grounded)
```
- `TapSlam` (rope tap) and `HoldSlam` (focus inject) ripple the cell through `Ground.RippleCell(speed)`; `rippleSpeed`
  is scaled against the cell's `referenceSpeed` (100 in this scene, so defaults 60 / 150).
- `Pump` (focus, `Intent.Inject` press): body draws up and slams down like a plunger (`ImpactDelay` = lift + slam),
  landing thump at the bottom (`soundSpeed`); GenomeView's injection presses it.
- Landing also carries `ImpactSound`, `LandAlarm` (-> `ImmuneSystem.Landed`); Charging carries `BurstSound`.
- Thrust / Coast / Burst / Launch move relative to `Organism.Fluid`.
- `Intent.Seized`: held by a white cell (Ground won't land it).
- Ground ignores collisions with the surface it's attached to (`Physics.IgnoreCollision`, restored on leaving; only
  *enabled* colliders) and forces that surface's detailed collider (`UseDetailedCollider`).

## SimulationTicker (`SimulationTicker.cs`)
One loop for every Organism (+ brain) and SpiderLegWalker instead of thousands of Unity callbacks, plus
**simulation LOD**: crowd organisms tick every 1..4 frames by camera distance, x2 off screen, staggered, skipped time
handed back as dt (capped). Player (no brain) every frame. Also the **shared per-frame camera** (`CameraPosition`,
`OnScreen`): use it instead of `Camera.main` in anything per-object. Profiler markers `Simulation.Brains`,
`.Organisms`, `.Late`, `.Legs`, `.Fixed`. Created on demand; add one to a scene to tune it.

## VirusMovement (`VirusMovement.cs`)
Player controller only: input -> intent, camera modes, WorldButton, focus, rope mouse control, rope-base hover/click
in focus mode.
- **F** held on a surface = the WorldButton's hold (input action `Player/Inject` in `virus.inputactions`) -> focus;
  F pressed in focus leaves it (`focusKey`). **E** toggles the head view (`inventoryKey`, on press). Escape exits focus
  only when the rope menu is closed (`ClaimsEscape` for the pause menu).
- WorldButton details: `Docs/camera-ui.md`. The scene's old world-space visual (`visualRoot`) is switched off.
- Ground movement is screen-relative using the camera's right projected on the surface (NOT
  `Cross(normal, forward)`, which flips on the far side).
- Rope controls: `Docs/rope.md`. Head click (`headPickRadius` or its on-screen size; a rope base wins) opens
  GenomeView. `UpdateCores` shows chunk cores in focus. `PointerOverUI` also checks `GenomeView.Covers`.
- Drops its mouse handling while `CommandMode.Active`; returns early while `PauseMenu.IsOpen`.

## VirusAI (`VirusAI.cs`)
Autonomous viruses, same intent interface. Chases a target (the player by default):
- Flying -> `PathManager` field. Target on a cell -> fly at the surface point under it with that cell *excluded* from
  the field (so it lands instead of avoiding), then crawl the shortest way round via `SurfaceField` (straight at it
  once in the goal's triangle or the next). Wrong cell -> jump off. (Crawling doesn't use PathManager: the projected
  chord got trapped on cubes and concave shapes.)
- Crowds (shared spatial hash; 3D in air, along the surface on a shared cell): overlaps are *distances* eased apart
  over `crowdSettleTime` (each side half), not full-speed pushes (overshot on stale positions, crowds vibrated). A
  virus touching a stopped (`_settled`) one ahead and nearer the goal stops too (queues: crowds ring the goal instead
  of shoving the front row). `regroupDistance` + a start/stop dead zone = hysteresis. `Move` eased toward each
  decision (`steerSmoothing`). Keeps clear of the target.
- Ground moves are in the *shown* frame (tangent to `Organism.up`, see NavSurface's roll). Thinks on a staggered
  timer (`thinkInterval`). Disables a leftover `VirusMovement` on the same object.
- `ICommandable.Order(Transform, Job)` (command mode): chases a creature, lands on a cell (nearest side, via
  `Surface.ClosestPoint`) and holds spread out, or goes to anything else; `Order(null, ..)` = back to chasing the player.

## SurfaceField (`SurfaceField.cs`)
Shortest way round a Surface to one goal, any shape. Dijkstra over the graph's welded vertices (A* welds split mesh
vertices, so a cube's faces connect) from the goal triangle, then a per-vertex gradient (area-weighted triangle
gradients); a crawler blends its triangle's three barycentrically: smooth, no minima but the goal. Cached per
(Surface, goal instance id); reflooded only when the goal changes triangle, at most every 0.25 s; dropped after 5 s
unused. Cost: O(V log V) per chased goal per reflood, O(1) per crawler query; topology built once per graph.

## NavSurface (`NavSurface.cs`)
Attachment to a Surface's graph (graph space). Works on any shape:
- Lifts along per-corner arcs by directional curvature (round on spheres, straight along cylinders, flat on flat
  faces; groups split at `Surface.creaseAngle`).
- Jumps in the normal (> 5°/pose = hard edges) rolled out over `edgeRoll` walked.
- Steps aimed ahead+down so they wrap convex edges (and up when blocked, to climb walls); heading carried across
  edges; walkable side fixed at landing from the mesh's winding (graphs are built with `recalculateNormals` off, which
  would rewind 3D meshes).
- `Normal` = *shown* normal (rolled round hard edges); `SurfaceNormal` = real one. `Crawl` rotates the heading from
  the shown frame onto the real face before stepping (stepping along the shown tangent just past an edge pointed off
  the new face and walked 0: crawlers stuck on every cube edge). `ToShown` maps a real-face direction into the shown
  frame (what `Move` should be in).
- Steps are taken **in the facet's plane**: smooth heading turned onto the facet (FromTo smooth normal -> face
  normal), sub-stepped at <= half its shortest edge (max 4), carried on by the smooth normal between sub-steps.
  (Stepping along the smooth tangent left the facet and GetNearest pulled it back sideways: the rim walk zig-zagged.)
- Crawler normal = `Surface.SmoothNormal` (see `Docs/surfaces.md`).

## Open items
- Crowd cost candidates: A* `GetNearest` per crawling agent per physics step; agent-vs-agent physics collisions (a
  layer that ignores itself); PathManager two-tier grid (`Docs/surfaces.md`).
- Inspector values reset in an earlier refactor: check Organism > Grounded > surface > `hoverHeight` (>= collider
  radius), snap distance, layers, Flying lead axis.
