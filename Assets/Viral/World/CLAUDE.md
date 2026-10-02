# World: streaming, saves, pause menu, far field, the vessel loop

Prefab `Prefabs/WorldStreamer` (carries WorldStreamer, Vessel, FarField), bootstrapped from
`ViralBuildAssets.worldStreamer` into any scene with a VirusMovement; clear that field to go back to the scene's own
spawners. `SpawnManager.cs` (Assets/): fills a ball around itself with weighted prefabs, no overlaps; `sizeRange`
(0.5-2.5x prefab scale; replaced `scaleRange`) skewed small (`sizeSkew`), Rigidbody mass x size³ (`massWithSize`).
SpawnManager, ResourceField and WhiteBloodCells skip their own spawning while `WorldStreamer.Active`; the streamer makes
sure ResourceField, WhiteBloodCells and ImmuneSystem exist.

## WorldStreamer
- Sectors load within `loadDistance` of the player, unload past `unloadDistance`. A sector's contents are generated
  **once**, the first time it loads, from (seed, sector) by `layers` in order (cells as anchors, chunks hovering off them
  via `nearAnchors`, white cells, AI viruses at 0 per sector for now, round viruses 0-0.6: `Assets/Viral/Pathogens`). Region density = smooth value noise over sectors
  (`regionSectors`, `voidBelow`: patches and voids; start sector at least `homeDensity`). Counts are per `sectorSize`³ of
  volume (`VolumeScale`) x region density; nothing within the wall margin.
- Objects may straddle borders; placement checks the sector's own placements, the 26 neighbours' unspawned records
  (and records already in the sector) and one `Physics.CheckSphere`. Placements are bucketed in a 16³ grid over the
  sector + neighbours (`AddPlaced` / `PlacedFree`); a flat list was hundreds of records x 24 tries x each object,
  ~3 ms a sector, every frame while the far field fills. Every streamed object gets a `WorldEntity`
  (catalog key = prefab name, seed).
- **Objects belong to the sector they're in**, not where they spawned: a sweep (`sweepPerFrame`) stashes anything in an
  unloaded sector into that sector's record (pose, scale, velocity, mass, `IWorldState` strings) and destroys it;
  loading spawns records back, seeding `UnityEngine.Random` with the entity seed first so Awake-randomized looks match.
  An object swept into a never-generated sector generates it on the spot. Destroyed by the game = gone for good.
- `IWorldState` (`SaveState` / `LoadState` / `Pinned`), stored **keyed by type name** (`WorldStates.Capture` / `Apply`
  in WorldEntity.cs; `EntityRecord.stateKeys`), empty states not stored; a missing component is added on apply (so
  on-demand ones like CellSignal come back). Older records without keys: by position, over the version-1 kinds only.
  A throwing `LoadState` is logged, not fatal. Implementers: ResourceChunk (radius + remaining, pinned while
  extracted), CellInterior, CellSignal (signal, hotspot, converted; decays by vessel time away), Surface (tendrils:
  vectors + age), Organism (the surface it stands on by SaveRef + local point / normal; re-lands, retried each tick
  for 3 s since that surface may spawn later); WhiteBloodCell (nothing; pinned while gripping / engulfing /
  digesting). Anything reparented off `WorldStreamer.Root` (swallowed) is pinned.
- **Ids:** every spawned object gets `WorldEntity.uid` (kept in its record through streaming and saves; the counter is
  saved as `WorldSave.nextUid`); `WorldStreamer.Find(uid)` (dictionary kept by Track / Untrack / Spawn).
- Spawning is time-budgeted (`spawnBudgetMs`, nearest sector first; generating newly loaded sectors shares it: all at
  once was a 60-90 ms hitch). Markers `WorldStreamer.Scan/Sweep/Generate/Spawn`. `Prime()` loads everything in range at
  once (start, after a load). `PathManager.Rescan` at most every `rescanInterval` after changes.
- `SpawnNew(key, pos, size)` (the command line's spawn): a fresh record stamped with the vessel clock / frame, spawned
  at once; `Keys` = the catalog's prefab names.

## Stream fade (`StreamFade.hlsl`)
Streamed things dissolve (screen-door dither, clipped in every pass so depth / outlines match) by their *centre's*
distance from the player over `fadeLength`, gone `fadeMargin` inside `loadDistance`: nothing pops in or out in view
(exp² fog alone left big cells 8-25% visible at the edge). Globals `_StreamFade` / `_StreamFadeEnd` from
`WorldStreamer.PublishFade` (off without a streamer). Instanced shaders (chunks, white cells, antibodies) fade by
instance position and cull fully faded instances in the vertex stage; cells (`STREAM_FADE_OBJECTS` in
BloodCellTriplanar) by `UNITY_MATRIX_M`'s origin, only renderers with rendering layer bit `WorldEntity.StreamedLayer`
(1<<30, set in `Bind`), so hand-placed scenery never fades. **A new streamed shader: include it and call
`StreamFadeClip` in every pass** (and `MixAtmosphere` before MixFog).
- **Atmospheric perspective** (`MixAtmosphere`, global `_Atmosphere` from `Vessel.perspective*`, zeroed in focus mode /
  without a vessel): colour drains and sinks into the fog colour from 40 to 700 m (strength 0.85), before MixFog, in
  cells (BloodCellForward, so legs too), white cells, chunks, antibodies and stand-ins alike (they match in the
  crossover). The deep fog is a dark orange (1500 m tube): that darkness is what reads as a tunnel. Keep desaturation low
  (0.45; 0.8 turned small things into grey specks). Don't brighten the haze along the tube axis (tried: a lit
  vanishing point + a glow floor lost the tunnel's darkness and looked odd, haze only at the ends).

## Far field (`FarField.cs` + `.compute` + `Custom/FarField`; added by code if missing)
The user wanted to *see* the world past the loaded bubble. Sectors within `FarField.distance` (1600 m) that aren't
loaded are generated too (`FarScan` every `scanInterval` / sector crossed, `GenerateFar` nearest first within
`generateBudgetMs`) and drawn from their records, nothing spawned.
- Stand-in meshes per prefab (its meshes shrink-wrapped onto an icosphere from its origin, 320 / 80 tris; chunks a lumpy
  ball), cel shaded in the prefab's colours (`_Color` / `_DeepColor`, substance, WBC lilac; `looks` overrides),
  scene-fogged. **Cell-family prefabs** (material has `_NoiseScale`: red cells; white cells via `WhiteBloodCells.Look`)
  use `Custom/FarFieldCell` instead: a copy of that material, the cells' own `SurfaceHeight` / `FarTone` /
  `CellShade` on the stand-in (map = mesh pos x record scale, bump turned by the instance rotation; lumps skipped once
  footprint-flat), so the crossover matches; plain `Custom/FarField` was a flat two-tone blob at 50-100 px (the
  "low quality far cells" complaint). Pose / clip shared in `FarFieldCore.hlsl`. Records drift on the GPU (kernel turns each by band rate x time since posed, as `Advance`), culled
  (frustum, `minPixels`, far edge), LOD by pixels, appended per (look, LOD), one `DrawMeshInstancedIndirect` each.
- Records in 32-slot pages owned per sector; a sector change (generated, swept into, loaded, unloaded, dropped)
  rewrites only its pages (`Changed(key, fresh)`).
- **Crossover:** a real object dissolves by StreamFade; its stand-in draws exactly the pixels it drops
  (`StreamFadeClipComplement`), so live objects in the fade band and loaded records not spawned yet get stand-ins too
  (per-frame dynamic list). Newly generated sectors fade in (`bornFade`). While the far field is on the band is
  `WorldStreamer.crossFadeLength` (25 m, 310-335 m), not `fadeLength` (120 m, used without stand-ins): at 120 m ~90% of
  live objects were dithered, real mesh and differently shaded stand-in interleaved, crawling through the screen-fixed
  pattern when moving (the "dotted cells" quality complaint). Poses: one Burst `PoseJob` (`ScheduleReadOnly`) over
  `WorldStreamer.LiveTransforms` + `LiveLooks` (native mirrors of the live list, same swap-back order: keep every
  removal going through `RemoveLive`; made with the first live object, disposed with the last), one slot per live
  object, look -1 when not in the band. On the main thread it was ~0.7 ms of transform reads + string lookups.
  `WorldEntity.farLook` is serialized (hidden) so a play-mode script reload keeps it.
- **Pristine sectors** (`SectorRecord.touched` false: generated for the far field, never loaded or stored into) are
  dropped when they leave range and left out of saves (regenerated from the seed), so memory / saves stay bounded by
  where you've *been*.
- Against "confetti" (vivid specks everywhere): the cull fades a record in from `minPixels` (2) to twice that, and
  **thins** by distance (stable per-record hash vs a share easing 1 -> `thinKeep` from `thinStart` to `distance`,
  dissolving over 0.08 of share).
- Cost: CPU O(changed sectors' records) + O(live) per frame; GPU a thread per slot (~40k at 1600 m) + visible
  stand-ins' vertices; far scan ~10k cell tests per scan, spread over frames 2 radial bands at a time
  (`BeginFarScan` / `StepFarScan` / `NearBand`; all at once was 5-7 ms every second).

## Platelets (`Platelets.cs` + `.compute` + `Custom/Platelets`; on the WorldStreamer prefab)
Scenery: spiky peach stars (activated platelets, the user's reference: lumpy body, long tapering bent tendrils)
zooming along the wall layers. Nothing per platelet on the CPU or stored: the compute places each from its index
(seed) + vessel clock, culls (draw distance, frustum, pixels), appends to near / far lists; two
`DrawMeshInstancedIndirect`. The vertex stage shapes each from its seed off one template mesh (icosphere body +
`Spikes` (8) tendril tubes, ring offsets in position.xy, (k + 1, t) in uv): tendrils on a jittered golden spiral, a
seeded share missing (len 0 collapses inside the body), bent, swaying (`_Sway`); body bulges where they leave.
- Endless field: wraps in a window round the camera in loop angle phi x tube angle theta, each a whole fraction of a
  turn (seamless copies), >= 2.5x the draw distance across on the loop's *inner* side (outer side ~2x sparser), so the
  window's edge fade starts past the reach. Count = `density` (per 1000 m² of band) x window area (~170k at 1600 m).
  Depth from the nominal wall fixed per platelet (`depth`, follows narrows via the radius texture).
- Motion: blood's turn rate at its depth (Flow's profile at the nominal radius) + own `speed` downstream, quantized to
  whole windows per 4096 s, positioned from (clock mod period): exact, no float creep; frame angle + camera phi folded
  into one double-computed phase. Pause stops them (vessel clock). Not saved (needs nothing).
- **Fade = the far field's rules** (`matchFarField`: its distance, edgeFade, minPixels, thinStart / thinKeep, same
  formulas as FarField.compute, dithered): the user found platelets' own fade (gone by 450, then 900 m) unlike
  everything else's. Pixel size goes by the body (`Solid` 1.4 body radii), not the tendril reach (mostly air: tiny
  platelets lingered as wisps); LOD by the reach. Keep any change to the far field's fade mirrored here.
- Cost: a compute thread per platelet per frame (~170k, ~0.05-0.1 ms GPU guessed; 8 MB pose buffer), vertices only
  for visible (near ~390 verts, far ~120 under `lodPixels`), a few thousand after pixel cull + thinning. Skipped
  entirely when the camera is farther than the draw distance from the band, in focus mode, for ortho cameras.
- Open: unverified in play mode (look, density, speeds guessed). Theta window is sized for the nominal radius: in a
  narrow it shrinks (wrap edges may come into view there). Not solid, no collisions, not seen by the immune system.

## Saves + pause
- `SaveGame` (static, version 2): 3 JSON slots in `persistentDataPath/Saves`, the whole session: player (pose,
  velocity, its IWorldStates = footing, `VirusInventory.Restore` stores + ring, genes + selection),
  `WorldStreamer.Capture()` (`layout`, `nextUid`, `Vessel.Save`: clock, alerts, wall offset, frame angle / rate; a save
  from another layout is re-sorted by position; objects `Dying` (bursting / being swallowed) left out), hand-placed
  scene objects (Rigidbody / Surface / IWorldState owners outside streamed / player / antibodies: pose, velocity,
  states, by scene path; on load a cell / creature the save lacks is destroyed), `ImmuneSystem.Capture` (antibodies),
  `VirusRope.Capture` (ropes), `CommandBoard.Capture` (plan). Refuses to save while being digested or to load another
  scene's save. Version-1 saves still load (no antibodies / ropes / plan in them).
- **References** between saved things go by `SaveRef` (World/SaveRef.cs): "p" player, "e:uid" streamed, "a:i"
  antibody (list order; ImmuneSystem.Capture prunes it first, so capture it before the board), "s:name#k/..." scene
  path, "|path" down to a child. Resolve only once the target is back, hence the load order: free the player
  (`WhiteBloodCells.Free`, before the captor is destroyed), detach, clear ropes, move; world (`Restore` + `Prime`
  spawns everything in range at once); scene objects; player states (re-lands) + stores + genes; antibodies; ropes;
  board (re-dispatches orders); cameras teleport.
- A new system's state: `IWorldState` on the component if it lives on a world object, else a `Capture` / `Restore`
  pair called from SaveGame in that order.
- `PauseMenu` (creates itself, execution order -200): Escape opens it only when `VirusMovement.ClaimsEscape` (focus /
  rope menu / head view) and command mode don't want it; time scale 0, audio paused, cursor freed; SAVE / LOAD per slot
  + RESUME, terminal look, hit-tested in screen space. VirusMovement and CommandMode return early while `IsOpen`.

## Vessel (`Vessel.cs` + `VesselWall.shader` / `VesselWall.mat`)
The world is the inside of one blood vessel bent into a torus (`circumference` 24 km, tube `radius` 1.5 km, seeded
width wobble + `narrows`), so drifting downstream brings you back round (lap = circumference / `centreSpeed`, ~7 min).
Game direction: space-exploration / factory / RTS at virus scale; the base floats in the calm centre, fast outer layers
and the wall slide past, landmarks come round each lap. The streamer moves the vessel onto `_home` (player's start;
centreline along the prefab's forward, axis its up).
- **World frame = the blood round the player:** the frame turns round the loop's axis at the blood's rate where the
  player is (`followPlayer`, eased over `frameEase`; `FrameAngle` saved), so whatever is near them is nearly at rest in
  world space. A change of the frame's rate is a change of reference only: `FollowPlayer` gives every free Rigidbody
  (streamed + Organisms) the same velocity change; kinematic movers read the flow. (Near the wall everything used to
  move at ~v0 in world space and every mismatch showed as rubber banding.)
- **Flow** `Flow(p)` = a turn rate round the loop's axis: relative to the wall (v0/R)(1 - (r/a)^n); relative to the
  centreline blood -(v0/R)(r/a)^n (what stored records turn at, `AngularRate`); minus the frame's rate; times rho.
  n = `profileExponent`, **1 by default, not laminar 2**, and `centreSpeed` 60: with r² the middle few hundred metres
  moved as one (no sense of the middle moving faster); n = 1 gives even shear (~12 m/s per 300 m). Two traps fixed: a
  continuity speed-up through narrows made the base's neighbourhood surge to ~100 m/s; linear speeds minus the wall's
  rigid turn left 1-4 m/s shear at the centre.
- **Narrows squeeze the flow evenly:** `Flow` adds a radial part x * da/dS * v0 (1 - Lag(x)) (`SlopeAt`), so blood keeps
  its share of the width (~4x denser inside a 0.5 narrow). With only the margin push, a narrow swept the outer half into
  one shell against the wall (packed cells on 48 pieces each: massive lag).
- Within `InnerMargin` (= `wallMargin` + wall relief) of the nominal wall it pushes back in (capped `maxWallPush`; no
  wall collider); nothing generated there.
- Everything floating takes the flow: `Organism.Fluid`; `Vessel.FixedUpdate` drags loose streamed Rigidbodies
  (`WorldEntity.Drifts`, non-Organisms) toward it (`drag`, compensating their own damping); ResourceChunk adds it to its
  home, Antibody and WhiteBloodCell to their step; AmbientParticles carried by it.
- Coordinates: `ToTube` -> phi, s (world arc), S (wall frame = s + S0, S0 integrated at v0 + frame rate x R), r, theta;
  `FromTube`; `Clock` = world time (double, saved).
- **Regions:** stretches of `regionLength` with seeded types (`regionTypes`: Plasma (always the start), Rich, Inflamed,
  Crowded, Sparse), each scaling streamer layers by `Layer.role` (Cells / Resources / Immune), tinting the fog, holding
  an alert that ImmuneSystem noise raises (`Alert`, `alertPerSignal`) decaying to the type's `restAlert`
  (`alertHalfLife`). Immune density rises toward the wall (margination), capped `maxDensity`.
- **Atmosphere:** drives URP fog (exp²): deep colour (warm orange (0.29, 0.126, 0.045), the look alert used to bring,
  which the user wanted always; alpha 0 = skybox `_HorizonColor`, black in Viral.unity) in the middle, `wallGlowColor`
  within `glowDistance` of the wall, region tint, alert colour; faded to `focusFog` in focus mode (its ortho camera sits
  far back); restored on disable. `fogDensity` 0.0012 (gone by ~1.9 / density = the far distance; keep them matched).
  Off in focus mode and for orthographic cameras.
- **Wall:** one grid mesh placed in the vertex stage from globals (`_VesselCentre/E1/E2/Axis`, radius profile
  `_VesselRadiusTex`), covering the **whole loop** with rings packed toward the camera (phi offset ~ u²; a window round
  the camera showed sky down the tube), hazed by its own thin haze (not scene fog), capped so it still shows from the
  middle. Look = editable `VesselWall.mat` (`wallMaterial`; shape stays on Vessel); toned down (muted darker colours,
  ink fading with distance, more haze) after it competed with the cells. Camera far plane raised to 4 sqrt(R a) ~ 10 km
  (`extendFarPlane`, restored on disable). While drawn the sky is never seen: the camera clears to the fog colour and
  SkyboxCache stops baking (`HidesSky`); DOF's Gaussian far blur smeared it, so a runtime global Volume (priority 1000)
  pushes the far blur out (`sharpWall`; off in focus mode). Focus mode skips the wall and gets the sky back. Triangles
  ordered nearest ring first (unordered cost ~4 ms).
- Wall look: **real relief** in the vertex stage (every pass): endothelial Voronoi cells (`tileSize`, periodic both ways,
  no seam) as pillows with raised nuclei (`wallRelief` m) on long wavy folds along the flow (`wallFolds` m); the same
  height gives the pixel normal analytically (screen-derivative bumps shimmered). Cel shaded: bands from main light +
  facing, nuclei a flat darker shade, constant-screen-width ink lines in the junctions, a rim (`wallLightColor`), wet
  glint on the pillows, seams shaded, ink ring round each nucleus; detail faded by on-screen cell size, relief by distance.
- Pattern laid out **conformally** (cells change size, never shape): round the tube by the torus's isothermal angle (grid
  vertices too), along it by a warp in `_VesselRadiusTex` (RGBA: a, da/dS, warp) that follows narrows' slopes and shrinks
  cells with the tube; normals lean with the slope. (Laid out by centreline length + plain angle, cells were 2.3x longer
  on the outer side and stretched through narrows; now exact outside narrows, within 8% for 95% inside.)
- **Streaming in the loop:** sectors are drifting cells = radial band b (`sectorSize` wide) x slice k along the loop *in
  that band's own turning frame* x slice j round the tube. Each band turns rigidly (`AngularRate` at its middle), so a
  stored record never changes cell; records carry `time` (vessel clock of their pose) and `frame` (frame angle then) and
  are turned into place (`Advance`) when spawned or placement-checked: storage costs nothing while away. Record
  velocities are stored *relative to the blood* (`WorldEntity.Capture` / `Apply` subtract / add `FlowAt`; world-space
  velocities came back ~30 m/s off after the frame changed). Generated records are stamped with the frame angle too
  (unstamped, new sectors landed far round the loop). Cells near the wall stream past, so new ones keep generating there
  (once each; they come back next lap).
- **Drift vs simulation LOD:** anything carried by the flow must move every frame while on screen: far chunks step
  every 4 frames but are *drawn* carried on by their last step's velocity (ResourceField); antibodies / white cells
  split `Carry(now)` from their LOD'd `Tick`; streamed dynamic cells are Rigidbody-interpolated within
  `Vessel.interpolateWithin` (200 m) of the camera, set on each body's drag turn (10% hysteresis) and at spawn
  (`Vessel.Interpolates`); past that a physics step is well under a pixel (interpolating all ~1000 was ~0.5 ms).
- New chunk records carry no state string (their scale is the radius; `Spawn` resizes them): one `List<string>` +
  string per chunk was the far generator's garbage.
- Cost: `FlowAt` O(1) (atan2 + sqrt + table lookup) per body turn; drag at most `dragPerStep` (256) bodies per physics
  step, round-robin, each given the time since its last turn (all live bodies every step was thousands of native
  Rigidbody calls x the catch-up steps of a slow frame: 60-260 ms in the profile; don't go back to all per step);
  regions O(regions) once a second; wall 1 draw, ~18k verts.

## Open items (all unverified in play mode; tuning guessed)
- Not saved: white cells' hunt / arm pose (they re-think), a gene effect waiting on its delay, extraction / injection
  in progress, camera angle, UI. A hand-placed object destroyed since the scene loaded can't come back (needs a scene
  reload). A rope end anchored to a streamed object that streams out comes loose (as before). Sector records stay in
  memory for every visited sector (page to disk if worlds get huge).
- Far field: colours / band thresholds guessed from the materials, not matched side by side in the crossover band;
  stand-in shrink-wraps run once at start (~10 ms per 1k-tri mesh; the virus prefab may be bigger). Growing the page
  buffer re-uploads it whole. If far space feels empty, raise `Crowded` / `Rich` densities rather than the base.
- Vessel: the wall isn't walkable or solid (making it walkable = chunked Surfaces near the camera). Generated cells are
  all kept, so a save grows with every stretch seen (whole loop ~30k cells); a "filler" tier (untouched records dropped
  and regenerated, fresh each lap) would bound it. Only streamed Rigidbodies and Organisms feel the flow. Band drift is
  quantized (records turn at their band's middle rate). Records ignore narrows (the push moves them out). HoloMap
  doesn't show the loop / regions; nothing names the current region on screen. Far side of the loop is ~7.6 km from
  home: fine for floats; a floating origin is needed if the loop grows.
