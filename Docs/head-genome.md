# Head, genome, injection, crafting, gene effects

Files: `Genome.cs`, `GenomeView.cs`, `GenomeBubble.shader`, `GenomeStrand.shader`, `VirusHead.cs`, `FlatMesh.cs`,
`GeneEffects.cs`, `Surface.Tendrils.cs`, `CellTendrils.hlsl`, `InjectionDrill.cs`, `InjectionDrillXRay.shader`,
`Substances/Crafting.cs`, `CellBurst.cs/.hlsl`, `CellShards.cs`, `CellDebris.shader`. Stores / inventory / InventoryView: `Assets/Viral/Substances/CLAUDE.md`.
Audio: `InjectionAudio`, `CraftAudio` (`Assets/Viral/Audio/CLAUDE.md`).

## Genome + gene effects
- `Genome` = what a virus carries in its head: genes (code, name, colour, `targets` (`GeneTarget` flags: RedBloodCell /
  WhiteBloodCell / Resource), `effect` (`GeneEffect`)) and the one loaded for injection (`Select`, `SelectedGene`,
  `SelectionChanged`). A virus starts with **no genes**; they're crafted. `Consume` (one use for now) +
  `VirusInventory.GeneRemoved`; the mount stays an empty DNA slot.
- `GeneEffects.cs`: `KindOf(body)`, `WorksOn(gene, body)`, `Apply` (one case per effect, in `Now`).
- `Gene.delay` (from `Recipe.delay`, saved with the gene): seconds before the effect (`Awaitable.WaitForSecondsAsync`,
  game time; entry point kept in the body's space; skipped if the body is gone). A wrong gene's alarm is at once.
- Recipes: **KIL-1 KILL** (delay 3 s; glucose 10 + protein 10, a cheap test gene, `Glyph.Burst`, yellow), red + white cells:
  `ImmuneSystem.Lysed` (`lysisSignal`; white cells have no CellSignal, so none there) + `CellBurst.Kill` (below).
- **BLT-1 BLIGHT** (glucose 20 + protein 20, `Glyph.Tendrils`, near-black
  violet), red cells only: `CellSignal.Silence()` (no signal / motes for good) + `Surface.Infect(point, normal, colour,
  radiusTime)`.
- **Tendrils** (`Surface.Tendrils.cs`, `CellTendrils.hlsl`): black tendrils creep over the cell from the injection site
  (`InjectionDrill.Site`). Set once into the renderer's property block (`_TendrilOrigin/Normal/Color`: renderer-local
  point + start time, normal + front speed, colour + radius); grown from the clock in the cell's ForwardLit only
  (`CELL_TENDRILS`, uniform branch: clean cells and legs pay nothing): rays out of the entry (isolines of a noise over
  directions, warped more farther out, thinning to the front), side roots, a capillary web behind the ragged front, a
  blot on the entry, skin greyed behind it, a slow pulse outward. Surface distance = chord + how far the normal has
  turned (reaches a flat cell's far face round the rim). Streamed and saved (Surface is an IWorldState: the block's vectors + age); far-field stand-ins don't show it.
- Delivery: `ImmuneSystem.Deliver(drill.Cell, gene, site, normal)`: a gene that works on that body applies its effect;
  any other gene bursts the signal (`wrongGeneBurst`). Returns whether the cell was taken over. Either way a cell
  keeps the strand (`CellInterior.Receive`), shown in its nucleus view.

## Cell death (`CellBurst.cs`, `CellShards.cs`, `CellBurst.hlsl`, `Hidden/CellDebris`)
Generic burst of any large cell-like body from a point: `CellBurst.Kill(body, at)`. Manager made on demand.
- Timeline (s, same constants in C# and hlsl): `Swell` 0.3 (body inflates ~0.09 R, blebs, shivers), then the body is
  clipped whole (`BurstCover(age)`) and its broken copy takes over in the same frame; cracks open from the entry,
  pieces are thrown over `Break` 0.35, far side `Spread` 0.2 later. Worked in body radii (q = (pos - centre) / R,
  frame fixed to the body). At `Swell` (`Tear`): organisms standing on it are detached (thrown off at `throwOff`,
  player leaves focus), `Surface.RemoveColliders` + every child collider off. At `Gone` the root is destroyed
  (WorldEntity, else Rigidbody, else Surface / IBurstable object; streamed = gone for good), PathManager rescans.
- Body hook, two kinds: a MeshRenderer whose shader defines `CELL_BURST` (BloodCellTriplanar: `_Burst` = entry q +
  start, `_BurstInfo` = mesh centre + radius, per-renderer property block; swell in `EvaluateCell`, `BURST_CLIP` in
  every pass's fragment); or `IBurstable` (WhiteBloodCell: `BurstState` in the instance buffer's `burst`, swell after
  Evaluate, clip in each pass; it lets its catch go (`Free` / `Release`), stops ticking, stays pinned; `BurstShape`
  hands over its swell seed).
- Pieces (`CellShards.Build`, once per mesh; unreadable / null mesh = level-3 icosphere in its bounds): 96-seed
  weighted Voronoi of the source triangles (distance + normal bend so a thin middle's faces stay apart, per-seed
  Perlin wander and weight: ragged cracks, varied sizes). Each piece is a closed solid: outer surface, the same sunk
  0.08 R (reversed), walls on edges no same-piece triangle shares (welded by position: seams aren't cracks); uv2.w =
  radius of a ball of its volume. Two meshes with the *same* pieces (fine triangles inherit their source triangle's
  piece; separate assignments made detail switches flicker): fine (Phong-subdivided once with the material's
  `_PhongStrength`) and coarse (source triangles), coarse below `fullDetailPixels` across the radius.
- `Hidden/CellDebris` vertex stage, from the clock only: rebuilds the body's surface at the kill's pose (object-space
  map `pos * scale`, relief from `_Displace` + `BurstSwellStill`, the body's seed: the handoff matches), crack (lift
  0.025 R, draw in 3%), release `Swell + Break * (0.1..1)` at the piece's local time, thrown along its normal with drag
  (K 2.5) and tumble, rounds into its volume's ball over 0.45 s (vertices pushed out from the pivot to the radius,
  normals radial), drifts with `b.velocity`, life 1.4-3 s: noise dissolve (fragment, glowing edge, same clip in
  depth passes) + shrink to 75%. Fragment bumps in the map frame then turns by the piece's rotation (`frame`).
  Inside faces and broken edges tinted `_SubsurfaceColor` (a rounded piece is half membrane, half inside).
  Material = `CopyMatchingPropertiesFromMaterial` of the source (`_SHADING_CEL` copied; multi_compile_local).
- Don't: give the pieces `BurstSwell`'s shiver (57 Hz buzz) or per-vertex `PixelMetres` / `DetailFade` (relief
  re-filtered as they move = crawl): both are taken at the piece's pivot / without shiver. The relief FBM runs only
  while a piece isn't round yet (`[branch]`), so most of its life the vertex stage has no noise.
- Cost: CPU O(bursts) a frame; GPU one `RenderMeshPrimitives` per (look, shape, detail) with visible bursts, the
  whole broken mesh per burst (red cell fine ~14k tris, coarse ~4k; 3 passes). Off screen / past 900 m skipped.
  First kill of a shape builds it (1280 tris x 96 seeds: ~1 ms, unmeasured).
- Not done: sound (`CellBurst.Burst` event is there), CellInterior stores don't spill as resources. The detail
  texture's triplanar blend uses the world normal (slightly off on a tumbling piece; `_DetailStrength` is 0 on cells).
  WBC pieces are a sphere (no arm). A save mid-burst leaves the cell out (`WorldEntity.Dying`): it's gone after a
  load. Unverified in play mode.

## VirusHead (`VirusHead.cs`)
Splits the head off the body mesh at start: the body is one mesh (Icosphere) whose crystal shell is one material slot
with the ball inside it; every non-crystal *triangle* with its centre inside the crystal's ellipsoid goes with the
crystal into a child "Head" pivoted on the head's bottom (the crystal's point facing the mesh's middle); the body keeps
a per-instance copy with the rest. So the head grows under the cursor by plain scaling (`hoverScale`, bottom fixed) and
depth / outlines follow. Needs the model's Read/Write (virus.blend: isReadable on). In focus mode the visible head is
the *ball* (the crystal is transparent, no depth): `Sphere()` = the ball, fitted as a sphere to the biggest connected
piece wholly inside the crystal (body bits poking in skewed a bounds box). Growing only the crystal (shader-side)
changed nothing visible.

## InjectionDrill (`InjectionDrill.cs`, on the Virus)
Procedural fluted drill that screws from under the virus to the centre of the surface it stands on when focus starts
(VirusMovement focus events, added by code), spins, ripples the cell at entry, winds back in on exit. Replaces
VirusAccess's UI bar. `Site`, `Span`, `Cell`, `Deliver()` (ripples the cell). Second material
`InjectionDrillXRay.shader` draws it over everything (Overlay+10, ZTest Always) only inside the sweep circle
(`_InvertSweep`), since it's inside the planet and the sweep flattens it.

## GenomeView (head view)
Opens in focus mode by clicking the head, or E anywhere (out of focus the cursor is freed, and a strand click only
loads the gene: `CanInject`). Starting an extraction opens it. Escape or a click elsewhere closes it (plays backwards);
it and the rope menu close each other. `Covers` is checked by VirusMovement's `PointerOverUI` (a clear click catcher
was culled and clicks counted as "elsewhere").
- **Bubble:** a tube grows out of the ball on screen and swells into a big sphere off to one side (`sideAngle` off the
  virus's up, the side that fits the screen, **picked once per opening** and then kept as an offset from the head, eased
  (`follow`); re-aiming every frame swung it as the virus turned; `rise`, `neckWidth`), flaring out of the head and
  pinched into the sphere like a drop. One outline round head + tube + sphere; on the head only a ring at its edge is
  filled (the real ball shows through). Focus mode's colours (sweep material's `_FillColor` / `_HighlightColor`, live:
  `matchFocusSweep`). One full-screen UI image, `Hidden/GenomeBubble` (head circle + tube capsule + sphere
  smooth-unioned as a distance field in canvas units).
- **Mounts:** round the inside of the outline sit `VirusInventory.ring` (DNA slots = a gene or empty; store slots = a
  `VirusInventory.slots` bar; they share `maxSlots`), evenly spaced from the tube's mouth (midway between two), each on
  a white base growing from the outline. DNA = a flat saturated double helix running in like a spoke
  (`strandLength`), A-T / G-C rungs colour-coded; stores = an outlined bar filling from the base inward in the
  substance's colour (lip at the front, quarter ticks, flashes as it fills); empty DNA = dashes. Labels outside the
  outline. All one mesh (`Hidden/GenomeStrand`, vertex colours with dim / highlight baked in, ortho, command buffer into
  an MSAA RT). Drag a mount round the ring (`dragThreshold`): others reflow live (`reflowTime`), snaps on release. A
  free mount shows "+" (LMB: add a store slot, RMB: a DNA slot); RMB on an empty mount frees it. Pointing jiggles it;
  picking is by angle. One `Helix` builder draws a strand along any spine.
- **Injection:** clicking a strand selects it and slides it along a canvas path (its spoke, across to the tube's mouth,
  down the tube into the ball, through the body, down `InjectionDrill.Span` to the tip) like a rope pulled through,
  shrinking toward the tube's width (never under `minInjectWidth` px). Legs, each expo in-out: into the head
  (`toHeadTime`), to the top of the drill (`toDrillTime`), a creeping pause (`drillPause`), then `Intent.Inject` makes
  the virus pump (the strand pulled back a little as the body rises). The drill often projects to a few pixels, so the
  strand keeps a set length for that leg (sized to the drill it became a dot); at the slam (white pulse ring at the
  drill's top) it shoots down the drill (cubic out, `zoomTime`, fading streak thinning toward the tip); at the tip (a
  burst of rings in the gene's colour) `InjectionDrill.Deliver()` and `Genome.Injected` fire; the gene is used up.
  `InjectSpeed` and `Injection` stage (`IntoTube`) drive InjectionAudio. ~2.1 s end to end (0.7 / 0.4 / 0.25 + pump
  + 0.45).
- **Lock:** while `Injecting`, the view ignores the pointer (`Interact` bails) and VirusMovement takes no input in focus
  (no F / Escape / Space out, no E, rope bases, head, cores, nucleus; `ToggleFocusMode` refused; the camera still
  looks). Only leaving focus by force (CellBurst's `ExitFocusMode`, seized, leaving the surface, death) ends it:
  `OnStateChanged` -> `CancelInjection` (strand kept if not yet delivered, spent if it was). A destroyed genome drops it.
- **Delivery shows** for `verdictTime` (2.2 s): three staggered shockwaves in the gene's colour at the tip + white
  ring + flash + 12 spikes (red when it didn't take); the sphere kicks (damped swell) and its outline/fill flash the
  gene's colour (red: rejected); the labels read `DELIVERED <code // name>` / `[OK] GENE TOOK HOLD` or
  `[X] REJECTED // ALARM RAISED`; the cell ripple is stronger (`Deliver(1.6)` took, `1` not).
- **Extraction flow:** while a chunk is extracted into this virus (`ResourceField.Extracting`), drops of its colour
  flow from the chunk on screen into the head, up the tube, across to its store's bar (`VirusInventory.SlotFor`) and
  down into the fill (`flowSpeed`, `flowSpacing`, `flowSize`). Chunk -> head is *timed* (`crossSpeed`, clamped to
  `crossTime`) on a Hermite curve (leaves slowly, rushes, meets `flowSpeed` at the head), then paced; drops placed by
  age, so a new stream's front grows out on the same curve. No sideways wobble (too springy). Drops share a trunk and
  pick their branch as they pass its end, by the store current *then* (`Stream.targets` / `since`): when a bar fills,
  drops past the branch still land in it while new ones turn into the next bar (swinging the line looked like a spill).
  Bars show their amount delayed by travel time (`Look.delay`, a (time, amount) history), labels too.
- Travelling strand + flows share one canvas-sized overlay texture (only while something moves; a `UIShape` UI mesh
  never showed). `FlatMesh` = shared flat-shape builder (Quad, Taper, Disc, Ring, Apply), also used by InventoryView.

## Synthesizer (`Substances/Crafting.cs`, on the virus, made on demand)
Recipes = cost substances by name -> a gene, or a substance when `liquid`.
- A hub in the sphere's middle (`hubSize`, dial turning) with a nozzle reaching flush to the mounts' inner ends. Click
  the hub: mounts pull back toward the outline (`menuStrands`, via `Len`: every strand / bar / pick / flow length goes
  through it) and recipes pop out in rings filling the sphere (`optionSize`, shrunk to fit, then paged with the wheel,
  page dots in the hub). Each disc shows its `Crafting.Glyph` (flat icon per recipe, told apart by shape, `DrawGlyph`)
  and cost pips lit for what you have.
- Pointing at one shows on the bars what it would use (`Crafting.Plan`: last slot first) as stripes crawling toward the
  hub, boxed green (can) / red (can't), and what it's short of as red dashes past the fill of that store (`_short`; a
  pattern, not a shade: a dimmer fill read as more level). Can't = dark disc, grey, red slash, missing pips as hollow
  red rings; can = disc tinted its colour with a breathing green ring. Short, or no free DNA slot (a free mount becomes
  one) = can't.
- Making it: nozzle turns to each store (`turnTime`), sucks its share (`suckTime`, `VirusInventory.TakeFrom` as it
  goes, drops down the spoke, hub fills with the mix), turns to the output and pushes the strand out (`dispenseTime`,
  `Look.dispenseAt`, eased in and out); after a job the idle drift starts where it stopped (it used to whip to its old
  idle angle). Shut mid-way: the job carries on unseen (hidden `LateUpdate` still runs Gather / Craft / Settle, so reopening shows it
  where it got to); only a lost genome / a different virus finishes it at once (`FinishCraft`). No mount can be freed while a job runs.
- Events for audio: `GenomeView.Synthesis` (`CraftStage`) + `GenomeView.Drawing`.
