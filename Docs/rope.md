# Rope: VirusRope, RopeBlood, RopeRadialMenu

Files: `VirusRope.cs`, `RopeBlood.shader/.mat`, `RopeRadialMenu.cs`. Audio: `Audio/RopeAudio.cs` (listens to events).

## VirusRope
XPBD rope, no input of its own. `PlaceAnchor()`, `reel`, `feed`. Breaks on *sustained* tension (`breakDelay`), not
spikes. Rope forces treat the body the player stands on as internal (no spinning your own planet). Events for audio:
`Anchored(point, normal)`, `Spooled(metres)`, `Stowed`.
- **Bases:** either anchored end. In focus mode it shows a glowing core (`DrawBaseCores`, `Custom/ResourceCore` like a
  chunk's so it reads as clickable: TerminalUI cyan, acid green straightened, blood red cauterizing; hidden behind cells
  by a raycast per base every 0.15 s; `baseCoreSize`, `ShowBaseCores` set by VirusMovement). Hover grows it; click
  opens the radial menu.
- **Straighten** (whole rope): with two bases = a rigid rod of the rope's own length, welded square to both surfaces
  (`rodStrength`), moves and turns the planets to fit, doesn't snap; rods sharing bodies settle as a network.
- Pulling up a base (grab or tug) marks the rope `suspended`: settings (straight, pending length) kept but dormant
  until the held end is placed (`FinishActiveRope`). Any spool length change (reel, pay out, auto payout) replaces a
  pending length.
- `SetLength`: eases every segment alike at feed speed, re-spaces particles past 0.6-1.6x spacing; rods push their
  bodies to fit. Handle API for the menu: `IsBase`, `BasePoint`, `IsStraight`, `ToggleStraight`, `LengthOf`, `SetLength`.

## Cauterize (`Cauterize(handle)`, rod only)
Blood front climbs from that base (`pump` metres, `cauterizeSpeed`), drawn as **beads that don't exist**:
- `BuildBlood` makes a see-through proxy per rope (a tube as wide as the beads can reach, `ProxySides`, one ring per
  rope ring plus one past each end; a disc over each base) on its own child renderer; `Custom/RopeBlood` ray-traces the
  beads inside it per pixel.
- Marching: steps of <= 3/4 bead through only the band the beads sit in (near side, then the far side if the ray
  passes beside the core), testing the ~4 nearest lattice beads per step, stepping a fixed distance from where the ray
  enters, never spread over the stretch (spread or fixed-count steps skipped beads at grazing / along-the-rope views =
  see-through). Skirts are closed pucks (lid + wall + floor) marched from lid to floor, not stopped at the surface plane
  (bead undersides hang below it; a flat stop cut them open from below). Writes SV_Depth and sphere normals, so
  shadows / depth normals / focus outlines see real spheres.
- Rules in the shader (per-rope `_Bead*` vectors via a property block): `beadStrands` strands in a 3-start helix,
  alternating shades, on the shell `ShellRadius(s)` (full swell behind, bolus at the front, lub-dub `Heartbeat` bursts,
  `baseFlare` into each base); each bead appears once the fill passes its hash (snaps in over `pumpBlend`). Skirts =
  `skirtRings` rings on each base's surface, sized from the tube's live `ShellRadius` at the base (a fixed size let the
  swelling tube lift off). Each bead is a tiny cell: the cells' `SurfaceHeight` lumps (`_BeadLumps`, mapped in the
  proxy's frame so they ride the rope, own noise patch per bead) with `BumpNormal` and `CellShade`; alternate beads
  shift toward the deep colour. Look = `beadLook` if set, else the material of the cell the blood came from
  (`SourceLook`), copied onto a runtime material per look (`_beadMats`). `_SHADING_*` is `multi_compile`.
- CPU cost per rope: a tiny proxy mesh + 5 vectors per frame. No loose beads ahead of the front (they'd be outside the
  proxy). The rope mesh itself stays a plain tube (the core).
- At the far base it **seals**: particles frozen in A's anchor space (`sealedLocal`), sim skipped, bodies welded with a
  `FixedJoint` on the dynamic one (none dynamic: nothing). Straighten / length / grab locked; losing a base unseals
  (joint destroyed).

## Controls (in VirusMovement)
- LMB pays out (`StartFromSpool`: anchored under you on a surface, a loose trailing end in the air); a grounded tap
  slams + `PlaceAnchor` (pins your end, keeps a loose far end loose).
- Nothing held: the nearest loose end within `grabRadius` (else anchored end within `baseGrabRadius`, both from the
  body surface) shows a phantom rope (`PhantomEnd`, drawn with the rope's tube builder); an LMB click `GrabEnd`s it
  (rope reversed so the held end is B, gap bridged with particles); that press is spent.
- RMB held reels; RMB click (released within `clickTime`) = `ReleaseHeld`, cut at your end, applied once the
  double-click window passes. Double RMB = `Tug`: far end torn loose (its body jerked toward you) and whipped in;
  already loose = slurp (`slurpSpeed` until gone).
- Out of focus / when the cursor is freed, the rope ignores the mouse.
- **Mouth** (`frontOnly`): the held end attaches on one side of the body (`mouthSide`, default Below =
  -`playerRopePoint.up`, which Organism keeps as the virus's up) at `bodyRadius`. Placement only: an earlier body-wrap
  solver pushed the rope, which shoved the player and the planet; removed. The curl round the body into the mouth is
  drawn only (`CurlToMouth` in BuildMesh, `curl` in body radii): keep it out of the solver.
- `LeanRequest` feeds `Organism.Lean` while reeling / paying out / slurping.

## RopeRadialMenu
Bio-lab terminal style (TerminalUI: navy/cyan, acid green = engaged, chamfered scanlined panels, rotating dial, typed
monospace text). Focus mode, clicking a rope base opens it on the base: Straighten toggle + Length field + Cauterize
(bottom panel, blood-red progress bar). Self-built uGUI (legacy font, generated disc/ring sprites); talks to the rope
only by handle. Click elsewhere or Escape closes it. It and the head view close each other.

## Open items
Ropes aren't saved (cleared on load).
