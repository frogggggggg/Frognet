# Command mode (RTS): CommandMode, CommandBoard, Selectable

Files: `CommandMode.cs`, `CommandBoard.cs`, `Selectable.cs`. Agents obey via `ICommandable.Order` (VirusAI:
`Assets/Viral/Movement/CLAUDE.md`). All hit-testing is in screen space in CommandMode, not the EventSystem.

## Mode
Q toggles (`toggleKey`; Escape leaves). `UniversalCamera.FreeCursor` frees the cursor; `CommandMode.Active` makes
VirusMovement drop its mouse handling (keys still move) and sets `PointerCaptured`. Returns early while the pause menu
is open.

## Selectable
Category Agent / Target, `kind`, `GroupName`, `affords`, static `All`, screen box. VirusAI viruses (agents, "Virus")
and Surfaces (targets, "Cell") get one when the mode opens (only those present then). Chunks are targets named after
their substance, antibodies "Antibodies", white cells "White Cell".
- Boxes fitted to the shape: `ScreenRect` projects, per renderer, the mesh's extreme vertex along ~40 directions (found
  once; unreadable meshes use their bounds' corners), not the world AABB. Computed once a frame (`FitBoxes`).
- No idle boxes (a gray box on everything was spam): hovered or inside the drag box pulses `TerminalUI.Target` blue;
  selected is bold yellow (`selected`, stays yellow when hovered); every saved group's members keep a box in the group's
  `color` (CommandBoard palette, no blue / yellow; a ring per group stacked outward; bold while the group is pointed at).
  Full outlines (`TerminalUI.BoxSprite`), drawn in a layer in the order used.

## Selecting and groups
- Click selects, drag boxes (box centre inside); Shift toggles (adds, or removes if all of it was selected; no Ctrl).
- A selection opens a radial menu (`_pick`) that follows the selection's middle in the world (`CentreOnCanvas`: mean of
  members' bounds centres, projected, kept on screen), split by kind; an entry saves it: agents ->
  `CommandBoard.Squad` ("Agents 1"), targets -> `CommandBoard.Task` ("Cells 1", numbered per group name).
- Each group gets a world **tag** (`PlaceTags`: name + count + an X, in its colour, on the middle of *all* its members,
  off-screen ones included, kept on screen; overlapping tags pushed apart): click selects the members, X or RMB removes
  the group. Clicking a *squad's* tag (or node) opens its link menu (again: its next link; no links: a hint to drag it
  onto targets).
- **Drag straight onto things:** a link line can be dropped on anything in the world, and dragged from a *selected*
  agent (`Press.Agents`; a press on anything else still box-selects). The dropped-on thing (with the rest of the
  selection of its kind, if selected) and the dragged agents are saved on the way (`CommandBoard.FindOrSave` reuses a
  group with exactly those members). Squad -> target, task -> agent, task -> target chains.

## Links (`CommandBoard.Link`: squad, task, `job`, `oneAtATime`)
- Drag a squad onto a task (tags or board nodes, either way round) -> `Connect` (default job: extract, else attack,
  else move) and the **link menu** (`_linkMenu`, compact radial in rows (`LinkSlot`), read top to bottom like a form,
  "SQUAD > TASK" header, follows the middle of the link's world line): ATTACK | EXTRACT | MOVE TO as one segmented row
  over the dial (the one in force lit; greyed unless some target's `Selectable.affords` allows it: cells MoveTo,
  antibodies Attack|MoveTo), SPREAD | FOCUS under it (`CommandBoard.ModeName`; code says `oneAtATime`), UNLINK below.
- Lines join the tags in the world (`DrawWorldLines`, job-coloured, flow dots, "EXTRACT // SPREAD" label; chains orange
  "THEN") and the nodes on the board; click either to reopen the menu, RMB removes it.
- Spread = agents shared over the open targets; focus (one at a time) = all on the target nearest the squad
  (`Link.active`, kept until complete, then the next nearest); for MOVE TO just the nearest.
- Task -> task drag chains (`CommandBoard.Chain`): when `Task.Done(link)` (attack / extract: every target gone or
  `Selectable.Complete`, an `ICompletable` next to it, `CellSignal` = converted; move to: every agent within
  `ArriveDistance` of its goal's bounds) the squad moves on to the next tasks not yet `Complete` (`Advance`, run by
  `Dispatch`; job kept where allowed, and the mode).
- `Dispatch` (on change + every second) shares each squad's agents over its links and asks the task
  `GoalFor(link, i, n)`. New work types (guard an area...) subclass `Task` (`GoalFor`, `Done`, `Complete`, `Affords`).
  Agents get `ICommandable.Order(Transform, Job)` (the job doesn't change VirusAI's behaviour yet).

## Tasking board (bottom middle)
A web: a node per group, re-laid out on every change (`Layout`: layered left to right, tasks by chain depth, each squad
one column before its earliest task, columns ordered by neighbours' average height, swept back and forth to cut
crossings), nodes and board easing to their new places.

## Open items
Selectables only for cells present when the mode opens. Saved: `CommandBoard.Capture` / `Restore` (groups with members
by SaveRef + their Selectable settings, links, chains, numbering, colours; a member without a Selectable gets one;
`Dispatch` re-gives orders). Members that streamed out are gone (as before).
