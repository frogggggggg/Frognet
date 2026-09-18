# Frognet Data — VS Code support

Highlighting and formatting for the two text formats the data system reads:

| File | Language | Holds |
|---|---|---|
| `*.fschema` | Frognet Schema | the shape of a folder of records |
| `*.fdata` | Frognet Data | the records themselves |

Both live under `Assets/StreamingAssets/<folder>/`.

## Installing

The extension is already linked into `%USERPROFILE%\.vscode\extensions\frognet-data`
as a directory junction, so edits made here apply after a window reload
(`Ctrl+Shift+P` → *Developer: Reload Window*).

To recreate the link:

```powershell
New-Item -ItemType Junction `
  -Path  "$env:USERPROFILE\.vscode\extensions\frognet-data" `
  -Target "<repo>\Tools\vscode-frognet-data"
```

Nothing needs installing or building; the extension is plain JavaScript with no
dependencies.

## What you get

**Colour.** Types (`float`, `int`, `bool`, `string`), keys, chosen branches,
references, numbers, brackets and comments are each scoped separately, so they
pick up distinct colours from whatever theme is in use.

**Editing.** `//` comment toggling with `Ctrl+/`, bracket matching, auto-closing
`[`, `{` and `(`, and automatic indent after an opening bracket or a section
header.

**Formatting.** `Shift+Alt+F`, or on save — the workspace turns that on for
these two languages only.

- Schema files are re-indented to follow their brackets, and spacing around `=`
  is evened out.
- Data files are re-indented to follow their sections, and call arguments and
  number lists get consistent spacing.
- Prose values are left exactly as written, so a description keeps its commas
  and spacing.
- A comment keeps the depth it was written at, and never opens or closes a
  section.

## Browsing every item

`Ctrl+Shift+P` -> **Frognet Data: Browse All Items** opens every record in the
workspace as a notebook: one cell per item, with a heading before each file.

Each cell is its own little editor, so **line numbers restart at 1 for every
item** rather than running to four digits, and items stay visually separate no
matter how many there are. Cells are editable and `Ctrl+S` writes each one back
to the file it came from.

A record starts at every `name:` written at the left margin; an indented `name:`
is just a value. Anything above the first record in a file, usually a comment
block, becomes its own cell.

There is a matching **Browse All Schemas**.

## Seeing everything as one document

`Ctrl+Shift+P` → **Frognet Data: Open All Data Files** opens every `.fdata` in
the workspace as one document, each preceded by a `// ==== path ====` separator.
There is a matching **Open All Schemas**.

It is a virtual file, not a real one, so nothing lands on disk: the registry
cannot pick it up as a duplicate and git never sees it.

- Because the separators are comments the whole thing is still valid syntax, so
  it highlights normally and `Ctrl+F` works across the lot.
- `Ctrl+Click` or `F12` on any line jumps to that exact line in its own file.
- **`Ctrl+S` writes your edits back**, each block to the file it came from.
- The view refreshes when a data file changes on disk, unless it has unsaved
  edits, in which case it leaves them alone.

Saving is deliberately careful:

- Only files whose block actually changed are written, so Unity does not reimport
  the rest. Whitespace-only and line-ending-only differences do not count.
- Each file keeps the line endings it already had.
- If a file changed on disk after the view opened, the save is refused rather
  than clobbering it.
- Deleting a block does **not** delete the file. It is reported and the file is
  left alone; delete the file itself to remove it.
- Renaming a header, or typing above the first one, is refused with an
  explanation instead of guessing.

For plain searching, the built-in `Ctrl+Shift+F` with *files to include* set to
`**/*.fdata` also works, and its "Open in Editor" button gives a persistent
result document.

## Tests

The formatter is plain functions, so it runs without VS Code and without
installing anything:

```powershell
cd Tools\vscode-frognet-data
node test-format.js
```

The grammars are checked against the real VS Code tokeniser, which needs the two
dev dependencies first:

```powershell
npm install
node test-grammar.js
```

Between them they cover both formatters, check formatting is idempotent and
already a no-op on `Assets/StreamingAssets/Items`, and assert the scope of every
kind of token in both formats.
