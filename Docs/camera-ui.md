# Camera and shared UI: UniversalCamera, TerminalUI, WorldButton, ControlsHint

Pause menu is in `Assets/Viral/World/CLAUDE.md`; the head view in `Docs/head-genome.md`; command mode in
`Docs/command-mode.md`.

## UniversalCamera (`UniversalCamera.cs`)
Modular rig (behaviour list per mode).
- **Transition momentum:** on a mode switch the pose difference decays on a damped spring seeded with the camera's own
  velocity, so it carries its motion instead of easing from a standstill (`transitionMomentum`, `transitionDamping`).
- `StandIn(root, standIn)`: a camera whose target is `root` or under it follows `standIn` instead (WhiteBloodCells pins
  a swallowed creature's camera at the cell's surface, never deeper, so it doesn't clip).
- `SpeedFOV` measures speed against the movement's top speed, but that reference only rises as fast as the body really
  speeds up (a Burst raised it instantly and the FOV dipped).
- `PointerCaptured`: gameplay owns the pointer (rope bases, command mode), drag-to-look ignores that click.
  `FreeCursor`: unlock / show the cursor whatever the mode says (look that needs a locked cursor stops).
- `FrameSurface` step (focus mode): orthographic, slid across the view onto the centre of the surface the target stands
  on (ISurfaceContact, mesh local bounds), size = surface radius + margin, grown to keep the target in frame; backs off
  so the planet isn't near-clipped. Focus mode's drag-look is LMB.

## TerminalUI (`TerminalUI.cs`)
The focus-mode terminal look shared by every screen: palette (cyan, acid green = engaged, `Target` blue, blood red),
generated sprites (chamfer, dial, disc, scanlines, `BoxSprite`, `PillSprite` keycaps, `MouseSprite`), OS terminal
font, canvas/rect builders, `Typed` text. **Use it for any new screen** instead of copying from the rope menu.

## WorldButton (`WorldButton.cs`)
Screen-space terminal prompt over `followPoint`: keycap with the bound key, filling ring, dial, typed
"HOLD // INJECT" tag. `Show/Hide` fade it (`IsVisible`, not `enabled`); a new hold needs a fresh press. Static `All`
(HoldTickAudio). Input action `Player/Inject` (F). UI built in `Start`, hidden (built on first show it was a ~220 ms
hitch in a build: OS font list, painted sprites, canvas, JIT).

## ControlsHint (`ControlsHint.cs`)
Bottom-right terminal panel with the controls for the current mode (flight / surface / focus / seized by a white cell:
aim away + Burst to tear free). Lists are data at the top: "[RMB][RMB]" = two prompt icons, plain words are tags.
Retyped on change, H folds it. Creates itself on play.

## Gotchas
- Unity UI draws a material's **first pass only**.
- A fully clear UI `Image` click catcher is culled (`CanvasRenderer.cullTransparentMesh`) and the EventSystem doesn't
  hit it: set `cullTransparentMesh = false`, or test the shape directly.
