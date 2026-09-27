# Rendering: cell shaders, focus sweep, sky, misc visuals

Files: `BloodCellCore.hlsl`, `BloodCellForward.hlsl`, `BloodCellTriplanar.shader`, `ScreenInvertTest.cs`,
`ScreenInvertSweep.*`, `ScreenInvertTransparentDepthFeature.cs`, `TransparentDepthForPost*`,
`AstrophageCrystalTop.shader`, `SkyboxCache.cs`, `StylizedCellSky*`, `AmbientParticles.*`, `HoloMap*`.
Stream fade / atmosphere (`World/StreamFade.hlsl`) is in `Assets/Viral/World/CLAUDE.md`.

## Style
**Stylized, cel shaded; the user doesn't want lighting or shadows.** Real-time shadows are off in every URP tier
(Laptop / PC / Mobile RP assets): on the laptop iGPU they were ~15 ms of a ~24 ms frame, nearly all the cells'
tessellated ShadowCaster. If they come back: `EdgeFactor` caps tessellation at 4 in orthographic views (a directional
light's cascades sat a few metres from everything and tessellated every patch round the player to `_TessMax`).

## Cell shader family
`BloodCellCore.hlsl` (properties, noise, ripple, bump, depth fragments) and `BloodCellForward.hlsl` (cel/PBR lighting,
`CellShade`) were cut verbatim out of `BloodCellTriplanar.shader` so cells, legs, rope beads and white cells share one
code path. `BloodCellTriplanar` keeps tessellation (for planets); legs use `_TessMax = 1`. Displaces along UV3
(`Docs/surfaces.md`). `CELL_TENDRILS` (blight, `Docs/head-genome.md`) and `STREAM_FADE_OBJECTS` live here too.
- **Noise LOD by pixel footprint:** `SurfaceHeight(p, fade, PixelMetres(posWS))` (`FBM3DLod`): each fBm octave fades to
  its mean (0.5, flat) between 0.15 and 0.4 cycles per pixel, in every stage (distance-based, no derivatives), so far
  cells go smooth instead of speckled / moire and displacement stops crawling. Used by cells (frag + domain), legs,
  white cells; rope beads use the unfiltered overload (their map isn't metres). `DetailFade` still applies on top.

## Focus sweep (`ScreenInvertTest.cs`)
- Sweep starts from the impact under the virus (`fromImpact`, surface point captured on trigger, pinned in the
  surface's space); ripple echoes trail the front. Drawn with the `ScreenInvertSweep.mat` asset itself (edit it live;
  per-frame values via a property block). Outlines within `highlightRadius` of the player (nearest outline tap, world
  position from depth) use the material's Player Outline colour. Fades depth of field while the sweep covers the
  screen (its outlines were blurred into a glow). Global `_InvertSweep` = centre + eased progress, zeroed at the end.
- **Backs:** while sweeping (play mode), cell materials go `_Cull` Off (restored after; shared assets) and global
  keyword `INVERT_BACKFACES` makes BloodCellTriplanar's colour and depth passes drop front faces inside the sweep
  circle (`InvertSweepClip` in BloodCellCore, same clip-space maths as ScreenInvertSweep), so depth outlines trace the
  insides. Shadow pass untouched.
- **Plain outlines:** inside the circle (`InvertSweepCover`) cells drop texture displacement and bump but keep ripples
  (the user wants ripples outlined). The sweep compares the scene normals texture (requested by
  ScreenInvertTransparentDepthFeature), not normals rebuilt from depth (faceted every triangle). Rule: smooth-shaded
  surfaces show no geometry, only silhouettes and real hard edges.
- X-ray things only inside the circle (Overlay+10, ZTest Always): InjectionDrillXRay, ResourceCore, rope base cores.

## Other
- `AstrophageCrystalTop.shader`: virus shell. DNA frames built once per pixel, DNA faded and march shortened with
  distance, wire noise skipped away from edges. Transparent, no depth (so the sweep doesn't show it).
- `SkyboxCache.cs`: the cell sky (`StylizedCellSky.hlsl`, 30 noise layers per pixel) drawn from crossfaded cubemaps
  baked a strip at a time. Face size fits the camera's *rendered* height (render scale included; `faceSize` 0 = auto,
  ~1280 at 810p), bakes every `refreshSeconds` (2). (Old fixed 2048 / 1 s baked about as many pixels as drawing live and
  hitched seconds at start.) Stops baking while the vessel wall hides the sky (`Vessel.HidesSky`).
- `AmbientParticles.cs`: GPU speck field around the camera, wrapped into a box, smeared by the **player's** velocity,
  visible only above a speed; carried by the blood flow (`_FlowOffset`), shows only when moving *through* the blood.
- `HoloMap.cs` + shaders: hologram map, top-right. Real scene geometry (grouped by mesh, instanced) + virus dots into a
  render texture, viewed from the camera's direction, faded toward the range sphere. Creates itself on play. Doesn't
  show the vessel loop or regions yet. Rescan (1 s): `OverlapSphereNonAlloc` round the player (1.5 x range + margin) ->
  bodies -> their MeshRenderers; only bodies with colliders are mapped. Don't go back to `FindObjectsByType` over the
  scene (30-65 ms spikes with the streamed world, thousands of far transforms walked per frame).
- `TransparentDepthForPostFeature.cs`: stamps transparent objects into depth before post so DOF stops blurring them.
- **GPU costs** (build, PC tier 1920x1200 x1, frozen frame, F9 `PerfBenchmark`; ~11 ms total): SSAO 3 (its
  depth-normals prepass redraws every opaque object, cells re-tessellated), vessel wall 3, cells 2.3, post 1.2, holo map
  0.9, white cells 0.8; the rest under ~0.6 (noise ~0.5). SSAO runs half-res with the low (Kawase) blur: -1.6 ms, frame
  mean diff ~0.1/255. **Depth priming breaks the look** (the wall has no DepthNormals pass, so it vanished; outlines
  too). The wall draws at `Geometry+100`, far-field stand-ins at `Geometry+50`: after the near opaques, so early-Z
  skips what cells cover (one huge-bounds draw each, distance sorting can't place them). The holo map draws chunks with
  `ChunkMesh.Get(shape, false)`, not their 1280-triangle walk hull. After those: ~10.3 ms (cells 2.75, wall 2.0, SSAO
  1.5, post 0.85). PC tier's opaque texture is off: nothing samples `_CameraOpaqueTexture` (turn it back on for a shader
  that does). PerfOverlay's "CPU main" excludes the present wait (GPU-bound frames read as CPU-bound before).

## Open items
- **DOF still blurs transparent objects / world-space UI.** The stamp pass runs (log-confirmed) but its debug view drew
  nothing, so its renderer lists come back empty. Uses an override *material* (override *shader* drew nothing). Unresolved.
