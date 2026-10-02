# Cells: interiors, organelles, metabolism, the focus view of a cell

Files: `CellProfile.cs`, `OrganelleType.cs`, `CellInterior.cs`, `CellInteriorView.cs`, `CellReadout.cs`, `NucleusView.cs`,
`CellInterior.hlsl/.shader`, `CellInteriorMotes.shader`; assets `RedBloodCell.asset` (profile), `Mitochondrion.asset`.
Stores use `Substances/SubstanceStore.cs` (`Stores`, `StoreSlot`, `SubstanceCatalog`), shared with VirusInventory.

## Data
- Two classes of things: cells (storage units / factories that may fight you) and small things (viruses).
- `OrganelleType` (SO): inputs -> outputs per cycle, `cycleSeconds` per organelle, `shape` (drawing), colour, `size`.
  Mitochondrion: GLUCOSE 1 + OXYGEN 6 -> ATP 10 per 10 s. **ATP is the universal energy.** New organelle = new asset.
- `CellProfile` (SO, one per cell type): nucleus rule (Never / Chance / Always + `nucleusChance`), `organelleLimit`
  (rolled), allowed organelles + start counts, store `slots` x `capacity`, `startWith`, `uptake` from the blood
  (rate, `upTo`: uptake stops there, or oxygen would fill every empty slot). `upkeep`: what the cell itself spends
  per second (red cell: ATP 1.5/s), out through the membrane as a negative port. **Every output needs a sink:** with
  none, ATP filled the free slots in ~1 min and the mitochondria stopped for good (room for outputs = 0). **A nucleus is what allows organelles**
  (no nucleus: limit 0). Red blood cell: 3% nucleated (then limit 5, 2-3 mitochondria), else no organelles; carries
  oxygen + glucose.
- `CellInterior` (on the Cell prefab root; `For(transform)` adds one with `ViralBuildAssets.defaultCellProfile` to a
  cell without): rolled once in Awake from `UnityEngine.Random` (the streamer seeds it per entity, so a cell comes back
  the same). API: `GiveNucleus` (fills starting organelles), `RaiseLimit` (mutation), `AddOrganelle` /
  `RemoveOrganelle`, `Total` / `Room` / `Add` / `Take`, `Transfer(from, to, substance, amount, dt)` (tubes, later),
  `Flow` (report any membrane flow for the view), `Receive(gene, took)` (ImmuneSystem.Deliver: every gene delivered
  into a cell is kept in `dna`, <= `MaxDna` (8), oldest dropped; saved). Context menus: Give Nucleus, Raise Limit, Add Organelle.
- `IWorldState`: JSON (nucleus, limit, seed, organelle names + counts, store, dna, vessel clock). On load it catches up
  the time away (<= 600 s, in <= 16 sub-steps).

## Metabolism
One static tick in the player loop's Update (like Surface's): `CellsPerFrame` (32) cells round-robin, each handed the
time since its turn; shown cells every frame (`s_shown`). Per turn: uptake, upkeep, then each organelle kind runs
min(count x dt / cycle, inputs available, room for outputs) cycles. `activity` (0..1, eased) and `Port`s (eased
membrane flows, closed 0.5 s after reports stop) are what the view reads. Cost O(CellsPerFrame) a frame at any cell
count; per turn O(uptakes + organelle kinds) x slots.

## CellInteriorView (focus mode)
VirusMovement's `UpdateCell` calls `Show` for the cell the virus stands on each frame. Made on demand.
- **Frame:** a unit ball stretched over the mesh bounds (`inset` across, `thinInset` through the thin side, z = the
  thin axis), so a red cell's insides lie in its disc. Nucleus in the middle (`nucleusSize`), organelles on a
  golden-angle spiral from the cell's seed (adding one never moves the others). Everything grows out from the middle
  as focus starts (`appearTime`).
- Four draws: motes (`Custom/CellInteriorMotes` mode 0, Overlay+6, under the bodies so swallowing hides), honey
  streams (mode 2, same material / queue), bodies
  (`Custom/CellInterior`, instanced ball, Overlay+7), energy rings off working organelles (motes shader mode 1,
  Overlay+8). **Motion is all in the vertex stages** (`CellInterior.hlsl`: `CellDrift` shared, so trips stay tied to
  drifting bodies). Shown cells hold fixed slots (0..3): frame index = slot, motes at slot x 256 in one buffer.
- Organelles: golden-angle spiral, each snapped to the nearest home deep enough for it (inside the real mesh).
- Looks: `Substances/SubstanceLook.hlsl` (shared with resource blobs; see the Substances notes). **Energy (Honey)
  pools in a band round the nucleus** (user), so ATP travels from the mitochondria into it.
- **Honey (ATP) is a liquid** (user: golden honey, drops joining like liquid, "ooh shiny"): each honey quad is its
  field's reach (`HONEY_REACH` x size); its fragment sums a metaball kernel (1 - r^2/R^2)^3 over the cell's honey
  motes (`_CellHoney` list, count in the frame's `axisZ.w`, rebuilt on dirty), evaluating each one's trip with the
  same `MoteAt` (maths, not a simulation), and draws only if its own drop is the strongest there (so the joined
  surface is blended once; others discard). Shaded by `HoneyShade` (Substances notes). **A stream + a pool, not particles**
  (user sketch: a ring of liquid round the nucleus, a thin wavy thread from each mitochondrion into it):
  - Pool: near the nucleus a drop's field is stretched x2.4 along the band (`PoolStretch`, its quad oriented to
    match), so pooled drops merge into a lumpy ring. Nucleus flag = frac of the frame's `axisZ.w` (+0.25).
  - Streams: draw mode 2, one quad per honey-making body's wave (`to` = `PoolPoint`, its direction at
    nucleusSize + 0.1), **drawn only while honey rides it** (user: permanent lines from every mitochondrion looked
    wrong): `StreamSpans` (CPU, O(waves x 256)) gives the span [tail, head] of the path its riding drops cover
    (`info.xy`, `to.w` 1 = show); a drop's strand reaches back to the maker until `StrandLetGo` (0.35) of its trip,
    then the tail follows it into the pool. The curve: `HoneyPath` / `HoneyBow` in CellInterior.hlsl (small slow arc
    + S), 6 segments, reach `STREAM_REACH`. Part of the same field; owner id `STREAM | wave`.
  - Made honey rides that same curve (MoteAt branch; `Where` mirrors `HoneyPath` on the CPU) to the free near-band
    home nearest the pool point (`NearestFree`) and **stays** (user: the liquid moved around too much): honey never
    wanders, wobbles 0.006 (not 0.02), and spent honey with no sink (the cell's upkeep, ~1 drop / 1.7 s) drains away
    where it lies (end 3, `DrainSeconds`) instead of flying to the membrane.
  - Don't go back to per-drop straight strands (user: straight, popping, too busy).
  Cost per honey pixel: O(honey motes in the cell + waves x 6 segments) (bounded 256 + 64; typically tens).
- **Motes (`CellInteriorView.Motes.cs`)** (user: resources spread through the cell's volume, flowing to what uses
  them; nothing appearing / disappearing): persistent, 256 per shown cell, count = store amount x `motesPerSlot` /
  capacity per substance.
  - **Homes** sampled once per mesh inside its real shape via its `SurfaceMap` (behind the nearest point's normal, at
    least 4.5% of the size deep; 384, cached per map); no Surface: the frame ball. Lists: `open` (outside the nucleus),
    `near` (band 0.03-0.2 round the nucleus: honey with a nucleus), `edge` (shallowest third: membrane crossings).
  - **Driven by flows, not the store's net change** (user: mitochondria didn't visibly use what they made; uptake
    refills oxygen as fast as they burn it, so the total barely moves). `CellInterior` keeps a ledger: per organelle
    kind `used[]` / `made[]`, per `Port` `moved` (capped at 50 units unshown; `ForgetFlows` on first look). Each frame
    the view claims whole units (capacity / `motesPerSlot` each, <= `movesPerFrame`): used = the mote nearest one of
    that kind's bodies *now* is sucked into it (resting, or settling past growing in: `Where` works a moving one's
    position out from its trip, mirroring the shader's `MoteAt`; it's sent on from there); made = a mote grows out of
    one to a free home; port in = grows in at the membrane spot facing it; port out = the mote nearest that spot is
    sucked out through it.
  - **Drained = sucked in, never vanishing** (user): a leaving trip (`Pull`, faster than drifting) is slow to let go,
    then rushes (e = k^2 (0.35 + 0.65 k)), curls in, stretches along the pull on screen, and is swallowed (end 1:
    shrinks inside the body) or thins out through the membrane (end 2). Picking the nearest is O(256) per event,
    <= `movesPerFrame` events per kind a frame.
  - Leftover mismatch with the store (> 1.5 motes: stores set directly, rounding): from a working maker / into a
    working user, else the mote nearest the membrane (shallowest) is sucked out through it. First look: placed resting. Resting motes drift to a free home within
    0.35 every ~`restSeconds`.
  - CPU writes a mote only on those events (80 B; the cell's block uploaded when dirty); the shader eases the trip
    (`time.x` = Time.time = `_Time.y`), bows it, wobbles. Cost O(256 x substances) a frame per shown cell.
  - Don't go back to stateless per-emitter motes (tried: resources visibly spawned / vanished at organelles).
- **ZTest LEqual, not Always:** inside the sweep cells keep only back faces in depth (ScreenInvertTest), so the
  insides show and the virus on top hides them. Clipped to the sweep circle (`FocusSweep.hlsl`).
- `CellReadout`: the stats, always under the cell in focus (below its bounds' lowest point on screen, kept on
  screen): type, nucleus + organelles n / limit, code x count + % active per kind, a thin bar per store in its
  colour. Plain centred text, no panel, nothing to click (the user wanted it simple). Text 5x a second. Hidden while
  the nucleus is open.

## NucleusView (click the nucleus)
The user wanted the nucleus itself to expand into a UI mode, **not like the player's inventory** (the head view's
bubble + tube + mounts round the rim was tried and read as the inventory). The nucleus swells on screen in place into a
disc (`size`; centre eased from the nucleus to a spot that fits the screen, then follows): its tinted body darkening to
the sweep's fill, chromatin threads + nucleolus fading behind, a double membrane (solid outer ring in `nucleusColor`,
inner ring broken by pores). Inside, top to bottom: "NUCLEUS" + name / id; a horizontal bar per store (code left,
% of capacity right, quarter ticks, pulsing lip); "ORGANELLES n / limit" and a small circle per slot the limit allows
(filled in the organelle's colour with a ring pulsing out by activity; hollow = free; wraps past 10); the foreign DNA
(`CellInterior.dna`) as small upright helices in the gene's colour (turning = took; dim + still = inert, `*` after its
code), codes under them, else "NO FOREIGN DNA". One FlatMesh (`Hidden/GenomeStrand`) into an MSAA RT on a RawImage
sized to the disc, only while open; text is pooled UI Text, rewritten 5x a second (only once visible). Read only.
`VirusMovement.UpdateCell`: hover lights the nucleus (`CellInteriorView.Hovered` / `Opened` -> frame `axisY.w` ->
glow + swell in `Custom/CellInterior`), click toggles, click elsewhere / Escape / leaving the cell closes; it and the
head view / rope menu close each other; `PointerOverUI` includes `Covers` (the disc).

## Open items (nothing run in play mode)
- Tubes between cells don't exist yet: `Transfer` + ports are ready for them. Nothing gives a nucleus or raises the
  limit in play yet (gene effects later). The nucleus view takes no clicks yet (later: programming the
  cell's DNA); injected DNA does nothing over time yet beyond its gene effect.
- Only the red cell profile exists (the only cell prefab); white cells aren't `CellInterior`s. Substances are a
  static catalog in code (`SubstanceCatalog`), not assets.
- Visual sizes / speeds / rates are guesses (view + assets). Red cell economy: 2-3 mitochondria make 2-3 ATP/s from
  0.2-0.3 glucose/s; glucose uptake 0.15/s, so glucose runs down while they work flat out and becomes the limit.
  One mote = capacity / `motesPerSlot` = 2.5 units: a mitochondrion swallows a glucose mote every ~25 s, oxygen ~4 s. Cells without a nucleus only show motes + uptake.
- Far / streamed-out cells don't simulate; they catch up on load (capped).
