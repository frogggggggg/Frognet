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
  via `nearAnchors`, white cells, AI viruses at 0 per sector for now). Region density = smooth value noise over sectors
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
- `IWorldState` (`SaveState` / `LoadState` / `Pinned`): ResourceChunk keeps radius + remaining, pinned while extracted;
  WhiteBloodCell pinned while gripping / engulfing / digesting; anything reparented off `WorldStreamer.Root`
  (swallowed) is pinned.
- Spawning is time-budgeted (`spawnBudgetMs`, nearest sector first; generating newly loaded sectors shares it: all at
  once was a 60-90 ms hitch). Markers `WorldStreamer.Scan/Sweep/Generate/Spawn`. `Prime()` loads everything in range at
  once (start, after a load). `PathManager.Rescan` at most every `rescanInterval` after changes.

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
  scene-fogged. Records drift on the GPU (kernel turns each by band rate x time since posed, as `Advance`), culled
  (frustum, `minPixels`, far edge), LOD by pixels, appended per (look, LOD), one `DrawMeshInstancedIndirect` each.
- Records in 32-slot pages owned per sector; a sector change (generated, swept into, loaded, unloaded, dropped)
  rewrites only its pages (`Changed(key, fresh)`).
- **Crossover:** a real object dissolves by StreamFade; its stand-in draws exactly the pixels it drops
  (`StreamFadeClipComplement`), so live objects in the fade band and loaded records not spawned yet get stand-ins too
  (per-frame dynamic list). Newly generated sectors fade in (`bornFade`). Fade starts at ~215 m of a ~450 m live
  bubble, so ~90% of live objects are in it every frame: one Burst `PoseJob` (`ScheduleReadOnly`) over
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

## Saves + pause
- `SaveGame` (static): 3 JSON slots in `persistentDataPath/Saves` (player pose / velocity, `VirusInventory.Restore`
  stores + ring, genes + selection, `WorldStreamer.Capture()`, `layout`, `Vessel.Save` (clock, alerts, wall offset,
  frame angle / rate)); a save from another layout is re-sorted by position. Load frees the player
  (`WhiteBloodCells.Free`, before the captor is destroyed), detaches, clears ropes, moves, `Restore`s the world,
  teleports cameras. Refuses to save while being digested or to load another scene's save.
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
- Not saved: ropes (cleared on load), antibodies, cell signals / converted cells, tendrils, command groups, hand-placed
  scene objects. Sector records stay in memory for every visited sector (page to disk if worlds get huge).
- Far field: colours / band thresholds guessed from the materials, not matched side by side in the crossover band;
  stand-in shrink-wraps run once at start (~10 ms per 1k-tri mesh; the virus prefab may be bigger). Growing the page
  buffer re-uploads it whole. If far space feels empty, raise `Crowded` / `Rich` densities rather than the base.
- Vessel: the wall isn't walkable or solid (making it walkable = chunked Surfaces near the camera). Generated cells are
  all kept, so a save grows with every stretch seen (whole loop ~30k cells); a "filler" tier (untouched records dropped
  and regenerated, fresh each lap) would bound it. Only streamed Rigidbodies and Organisms feel the flow. Band drift is
  quantized (records turn at their band's middle rate). Records ignore narrows (the push moves them out). HoloMap
  doesn't show the loop / regions; nothing names the current region on screen. Far side of the loop is ~7.6 km from
  home: fine for floats; a floating origin is needed if the loop grows.
