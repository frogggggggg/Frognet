# Console: the command line and its language

Files: `CmdLang.cs` (static `Cmd`: lexer, parser, evaluator, completion; game-agnostic), `CmdWorld.cs` (Frognet's words:
`Thing`, `Amount`, `GeneItem`, `Prefab`, registrations), `CommandLine.cs` (the UI). Replaces the old project's
`Assets/Command.cs` + `MathExpression.cs` (leave those alone).

## Language (`Cmd`)
- Everything is a value: number, bool, text, Vector3, `Many` (a list), a thing, `Space` (dna), world objects.
  `a.b` on a list: list op (`ListOps`: count mix random(n) first(n) last(n) reverse sort(key) min/max(key) sum mean
  where(c) map(e) do(stmts) print kinds, + world's nearest(n) farthest(n)) > verb (kill, delete) > kind (filter) >
  each element's property (a list). On a single value: property > verb > list op (as a list of one) > kind.
- `.( cond )` filters; inside it (and in per-element op args: sort, min, max, where, map, do) bare names are the
  element's properties, `index`, `it`, and kinds as true/false; then variables, then words. Scopes nest.
- Statements: `target (=|+=|-=|*=|/=) value`, `;` splits. Target = a variable, a property of a thing or a list of them
  (elementwise when the right side is a list of the same count), or a value's member (`me.pos.y = 5`: the changed
  vector is written back up the chain, `WriteBack`). A property with `add` / `remove` (inventory, genes) takes `+=` /
  `-=` as put in / take out, one per list element.
- Items (things, genes, amounts; not numbers / vectors / text) and lists of them: `+` joins, `-` removes, `* n`
  repeats (`dna-kill*5`, `all.cell - all.redbloodcell`); checked before broadcasting, after the world's `Operators`
  (so `glucose*50` scales). A collection's `+=` reports each distinct result once with a count.
- Arithmetic broadcasts over lists and number x vector; `|x|` = abs / length; `Operators` hook for world types
  (`50*glucose`). `a-b` lexes as one word only when a word by that name is registered (`dna-kill`).
- **Dry runs** (`Env.dry`): completion and the live preview evaluate the line with nothing happening: verbs, spawn,
  do, print and assignments `Say` what they would do into `Env.note`; assigned variables go to `Env.dryVars`. Anything
  new that changes the world must check `e.dry` (Fns get the Env; verbs / prop setters are never called when dry).
- Completion (`Complete`): word at the caret; after a dot, the value before it is found by scanning back over the dot
  chain (`ChainStart`) and dry-evaluated; open `.( ` / per-element `op(` scopes above the caret are rebuilt with the
  first element as `it` (`ScopeAt`). Lists offer the kinds actually present (with counts).
- Registries are static; a script reload wipes them: `CmdWorld.Ensure()` (every frame, one lookup) re-registers.

## World (`CmdWorld`)
- `all` = `Organism.All` + `WhiteBloodCells.All` + `ResourceField.All` + `ImmuneSystem.Antibodies` + `Surface.All`
  (mapped to their chunk / white cell / creature / WorldEntity root) + `WorldStreamer.Live`, deduped, gathered once a
  frame. No scene search. A `Thing` per GameObject is kept (so `==` works), dead ones pruned on gather.
- Kinds: main `kind` (virus, whitebloodcell, chunk, antibody, a cell's `CellProfile.displayName` squashed:
  redbloodcell) + extras (player, ai, cell, wbc, immune, resource, the substance, the profile code rbc, streamed, the
  WorldEntity key). `Norm` = lower case letters/digits.
- Properties: pos (teleport: a creature is freed from white cells, detached, its ropes cleared, cameras `Teleport`;
  a chunk re-homes via `ResourceChunk.Place`), vel, forward (a creature: `aimForward`), up, right, rot, scale (a
  chunk: `Resize`), dist, name, kind, kinds, id, onscreen; creatures: speed (Thrust), crawlspeed, dashspeed, state,
  grounded, inventory, genes; chunks: amount, substance; vectors x y z len norm.
- `inventory += 50*glucose` -> `VirusInventory.Add`; `+= dna.kill` / `dna-kill` adds the gene into the first empty
  DNA mount (else `Ring` gives it one, even past `maxSlots`); `-=` takes / `Consume`s (+ `GeneRemoved`). Cells: their
  `CellInterior.store`. `dna` members come from the player's `Crafting.recipes` (non-liquid), rebuilt on open.
- kill: cells / white cells burst (`CellBurst.Kill`, from the side facing the player, **no alarm**), else Destroy.
  Never the player. delete: Destroy.
- `aim` / `target`: raycast from the screen centre (the mouse when the cursor is free), first hit not under the player.
- `spawn(what, pos = aim, count, size)` -> `WorldStreamer.SpawnNew` (a fresh record stamped with the vessel clock /
  frame: streams, saves, fades like generated ones). Prefab words = the streamer's catalog keys, squashed.

## UI (`CommandLine`, creates itself, execution order -300)
- / or ` opens, Enter runs (empty line closes), Escape closes, Tab / Right-at-end takes a suggestion, Up/Down pick a
  suggestion (history when none show; history kept in PlayerPrefs), PgUp/PgDn scroll, Ctrl+V/C/L/Backspace/arrows.
- Selection = `_anchor`..`_caret` (equal: none). Shift+arrows/Home/End extend, Ctrl+A all, Ctrl+X cut, Ctrl+C copies
  it (the whole line when none), typing / paste / Backspace / Delete replace it. Mouse on the input line: click,
  Shift+click, drag, double-click word, triple-click line (`IndexAt` = nearest glyph boundary via the font advances).
  Anything that sets `_caret` must also set `_anchor` (`Edited` / `Move(to, extend)` do). Opening sets
  `UniversalCamera.FreeCursor` (cleared on close only if it set it, like PauseMenu); a press on the line holds
  `PointerCaptured` until release so it doesn't drag-look.
- Text via `Keyboard.onTextInput`. **Gameplay keys read `CommandLine.Keys`** (null while typing and on the frame it
  closes) instead of `Keyboard.current`: VirusMovement, CommandMode, ControlsHint, PauseMenu, PerfOverlay; WorldButton
  checks `CommandLine.Typing`. A new keyboard shortcut must do the same.
- Legacy UI Text, one canvas (order 900), monospace OS font; caret / ghost / popup placed by summing the font's glyph
  advances (`Measure`). Log redraws only when what shows changes (a key of visible range + fade steps).
- Cost: nothing while closed but a short fade loop; per keystroke one completion + one preview (a gather of all is
  O(things), once a frame). Commands on thousands of things are O(n) per step; `sort` O(n log n).

## Open items
- Not run in play mode. Guessed: font advance matching the rendered text (caret drift if not), raycast for aim in
  third person (it skips the player, so it hits what's behind them).
- `me.pos` moves the root; if Organism's own state fights a teleport mid-state, add a proper `Organism.Teleport`.
- Not saved: variables. No `select` into command mode, no `if`/loops beyond `do`, no user functions.
