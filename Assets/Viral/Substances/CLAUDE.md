# Substances: resource chunks, extraction, inventory

Prefabs `GlucoseChunk`, `ProteinChunk`, `ResourceField` in `Assets/Viral/Prefabs`. `Crafting.cs` (the synthesizer) is
documented in `Docs/head-genome.md`. Audio: `Audio/ResourceAudio.cs` (start "plup", drain loop, breathy bubbly poof
with a faint E6/B6 shimmer).

## Substance / chunks
- `Substance` = name / code / colour (slots match by name; a new substance is just a new chunk prefab). Glucose
  yellowish white, protein saturated orange; no red substances (red blood cells are red).
- `ResourceChunk`: the prefab is the type (substance, `ChunkMesh.Shape` Lumpy (glucose) / Coil (protein: a globule of
  beads fused along a folded chain), size range skewed small (`sizeSkew`), yield ~ r³).
- **Landable:** a walkable body like a small cell (`Surface` with `isCell` off + a non-convex MeshCollider, both on
  `ChunkMesh.Walk`: a 1280-triangle hull with the knobs smoothed off; walking every knob of the drawn mesh flicked the
  crawler's up and shook the camera).
- **Kinematic, moved by its own code, not physics:** `ResourceChunk.Step` bobs it round its place (`bob`, `bobPeriod`)
  and turns it (`spin`), from a phase that only advances while nobody stands on it (`settleTime` eases still / awake); a
  dynamic body bumping it nudges it (`bumpResponse`, capped `maxBump`, damped). (Dynamic and feather-light it was shoved
  by the rider's collider every step and interpolation fought the pose: the old camera jitter.) Adds the blood flow to
  its home; steps every frame on screen when drifting > 0.3 m/s.
- The transform's scale *is* the radius; draining shrinks it (`Shrink`, `CurrentRadius`), so riders ride in; poofing
  detaches them.
- Ripples: impacts on its Surface go to `RippleField`, and `Custom/ResourceChunk` rides it (offset + normal from two
  extra taps, only while anything ripples). Wave shape = the chunk material's `_Ripple*` (every chunk's hidden renderer
  carries `ResourceField.SharedChunkMaterial` so Surface / RippleField read them); strength via
  `Surface.referenceSpeed = rippleReference / radius` (bigger chunks ripple more).
- Its MeshRenderer is `forceRenderingOff` (there for Surface and command mode's box; a target named after its substance).
- `IWorldState`: keeps radius + remaining; pinned while extracted.

## ResourceField
- `Update` (order -20, before the ticker poses riders): finds who stands on what (one pass over `Organism.All`) and
  steps chunks: near (`lodDistance`) / stood on / settling / extracted every frame, others every 4 (8 off screen)
  frames staggered; a chunk at rest writes nothing. Chunks cache their pose (`Pos` / `Rot` / `Size` / `StepTime`; only
  `Step` moves them, `Centre` = `Pos`): no transform reads per frame. `DrawChunks` draws each at `Pos + Velocity x
  (now - StepTime)`, so 4-frame steps don't judder; only a far chunk's collider moves in steps.
- Spawns the listed prefabs (a share floating 3-16 m off cells, the rest among them, grid-checked for overlaps incl. the
  bob) unless `WorldStreamer.Active`. Draws every chunk from its transform in one buffer, one `RenderMeshPrimitives` per
  (shape, LOD) (`Custom/ResourceChunk`: faint breathing wobble, and as it drains lumpy deformation + squash pulses, in
  the vertex stage; **no GPU motion: the pose must match what's walked**).
- In editor play with no field in the scene, `Bootstrap` instantiates the prefab (logs it); builds need it in
  ViralBuildAssets. The prefab isn't in `Viral.unity` yet.
- Focus mode: `VirusMovement.UpdateCores` calls `ShowCores`; only the chunk the virus stands on shows a core
  (`Custom/ResourceCore`, Overlay+10, ZTest Always, only inside the sweep); the user: no extracting from a chunk you're
  not on (`extractRange` is gone). A click toggles extraction (stops when that body steps off: `ExtractorBody`) into
  `VirusInventory`: a dust stream flows in, it shrinks and deforms, past `poofAt` it bursts (`DustClouds.Burst`) and is
  destroyed. Labels beside hovered / extracting cores. `Extracting` drives the head view's flow.
- `DustClouds`: generic analytic GPU puffs (burst / stream along a curve), CPU only writes new ones into a ring buffer;
  Overlay+5 so they show over the focus sweep.

## VirusInventory
(Named so because the old project owns `Inventory`.) `slotCount` store slots, each one substance up to `capacity`, plus
the head's `ring` (`Ring(genes)` reconciles it; `AddMount` / `RemoveMount` (empty only) / `MoveMount`), `SlotFor`,
`TakeFrom`, `GeneRemoved`, `Restore` (saves). **E** toggles the head view anywhere.

## InventoryView
Always-on top-left mini head: a small plain disc (cyan rim) with the same ring in the same order (DNA as short ladders,
the loaded one lit; stores as bars), "05 // HEAD" + the loaded strand beside it; read only, one `FlatMesh` into a 256px
texture; folded away while the head view is open.

## Open items
`ResourceField` draw / reach / step loops are O(chunks) per frame (fine into the thousands); past ~10k give them a
spatial grid and step them in a TransformAccessArray job. Kinematic chunks don't collide with each other (a bump can
push one into another), and a rope-dragged cell stops dead against one. Chunks at 1 km are sub-pixel in the far field.
