// Loaded lazily so the formatting helpers below can also be exercised by a plain node script.
let vscode = null;

try {
    vscode = require('vscode');
} catch (error) {
    vscode = null;
}

/** Splits a line into its content and any trailing `//` comment. */
function splitComment(line) {
    const at = line.indexOf('//');

    if (at < 0) {
        return { body: line, comment: '' };
    }

    return { body: line.slice(0, at), comment: line.slice(at).trim() };
}

/** `speed( 1,2 )` becomes `speed(1, 2)`, and a bare number list gets even spacing. */
function normalizeValue(value) {
    const call = value.match(/^([A-Za-z_][A-Za-z0-9_]*)\s*\(([\s\S]*)\)$/);

    if (call) {
        const args = call[2]
            .split(',')
            .map(part => part.trim())
            .filter(part => part.length > 0)
            .join(', ');

        return `${call[1]}(${args})`;
    }

    if (/^-?\d+(\.\d+)?(\s*,\s*-?\d+(\.\d+)?)*$/.test(value)) {
        return value.split(',').map(part => part.trim()).join(', ');
    }

    // Anything else is prose and is left exactly as written.
    return value;
}

function normalizeDataLine(line) {
    const { body, comment } = splitComment(line);
    const colon = body.indexOf(':');

    if (colon < 0) {
        return comment ? `${body.trim()} ${comment}`.trim() : body.trim();
    }

    const key = body.slice(0, colon).trim();
    const value = normalizeValue(body.slice(colon + 1).trim());
    let result = value.length > 0 ? `${key}: ${value}` : `${key}:`;

    if (comment) {
        result += ` ${comment}`;
    }

    return result;
}

function normalizeSchemaLine(line) {
    const { body, comment } = splitComment(line);
    let result = body.trim();
    const equals = result.indexOf('=');

    if (equals >= 0) {
        const name = result.slice(0, equals).trim();
        const right = result.slice(equals + 1).trim().replace(/\s+/g, ' ');
        result = right.length > 0 ? `${name} = ${right}` : `${name} =`;
    }

    if (comment) {
        result = result.length > 0 ? `${result} ${comment}` : comment;
    }

    return result;
}

/** Indent follows the brackets, which is what actually nests a schema. */
function formatSchema(text, unit) {
    const out = [];
    let depth = 0;
    let blanks = 0;

    for (const raw of text.split(/\r?\n/)) {
        const trimmed = raw.trim();

        if (trimmed.length === 0) {
            blanks++;
            continue;
        }

        if (blanks > 0 && out.length > 0) {
            out.push('');
        }

        blanks = 0;
        const body = splitComment(trimmed).body.trim();

        if (/^[\]}]/.test(body)) {
            depth = Math.max(0, depth - 1);
        }

        out.push(unit.repeat(depth) + normalizeSchemaLine(trimmed));

        if (/[[{]$/.test(body)) {
            depth++;
        }
    }

    return out.join('\n') + '\n';
}

/** Indent follows the sections, which is what actually nests a data file. */
function formatData(text, unit) {
    const out = [];
    const open = [];
    let blanks = 0;

    for (const raw of text.split(/\r?\n/)) {
        const trimmed = raw.trim();

        if (trimmed.length === 0) {
            blanks++;
            continue;
        }

        if (blanks > 0 && out.length > 0) {
            out.push('');
        }

        blanks = 0;
        const indent = raw.length - raw.replace(/^[ \t]*/, '').length;

        if (trimmed.startsWith('//')) {
            // A comment sits where it was written, but never opens or closes a section itself.
            let depth = open.length;

            while (depth > 0 && indent <= open[depth - 1]) {
                depth--;
            }

            out.push(unit.repeat(depth) + trimmed);
            continue;
        }

        while (open.length > 0 && indent <= open[open.length - 1]) {
            open.pop();
        }

        out.push(unit.repeat(open.length) + normalizeDataLine(trimmed));

        // A key with nothing after the colon opens a block for the lines below it.
        if (/:\s*$/.test(splitComment(trimmed).body)) {
            open.push(indent);
        }
    }

    return out.join('\n') + '\n';
}

/**
 * Joins several files into one document, remembering where every line came from.
 * `files` is `[{ label, text }]`; the returned `map` is parallel to the content's
 * lines, holding `{ file, line }` for each or null for padding.
 */
function combine(files) {
    const lines = [];
    const map = [];

    files.forEach((file, index) => {
        if (lines.length > 0) {
            lines.push('');
            map.push(null);
        }

        const rule = '='.repeat(Math.max(4, 74 - file.label.length));
        lines.push(`// ==== ${file.label} ${rule}`);
        map.push({ file: index, line: 0 });

        const body = file.text.replace(/\r\n/g, '\n').split('\n');

        while (body.length > 0 && body[body.length - 1] === '') {
            body.pop();
        }

        body.forEach((text, line) => {
            lines.push(text);
            map.push({ file: index, line: line });
        });
    });

    if (files.length === 0) {
        lines.push('// Nothing found.');
        map.push(null);
    }

    return { content: lines.join('\n') + '\n', map: map };
}

const HEADER = /^\/\/ ==== (.*?) =+\s*$/;

/**
 * The inverse of `combine`: cuts a joined document back into per file blocks.
 * Returns the blocks and whether anything appeared before the first header.
 */
function split(text) {
    const lines = text.replace(/\r\n/g, '\n').split('\n');
    const blocks = [];
    const before = [];
    let current = null;

    for (const line of lines) {
        const header = HEADER.exec(line);

        if (header) {
            current = { label: header[1].trim(), lines: [] };
            blocks.push(current);
            continue;
        }

        if (current) {
            current.lines.push(line);
        } else {
            before.push(line);
        }
    }

    return {
        blocks: blocks.map(block => {
            const body = block.lines.slice();

            while (body.length > 0 && body[body.length - 1] === '') {
                body.pop();
            }

            return { label: block.label, text: body.join('\n') + '\n' };
        }),
        stray: before.some(line => line.trim() !== '')
    };
}

/** Line endings and trailing blanks are not differences worth rewriting a file over. */
function settle(text) {
    return text.replace(/\r\n/g, '\n').replace(/\s*$/, '') + '\n';
}

/**
 * Cuts one file's text into its records: everything before the first `name:` becomes a
 * preamble chunk, then one chunk per record. Blank lines between records are dropped,
 * because `weave` puts exactly one back.
 */
function slice(text) {
    const lines = text.replace(/\r\n/g, '\n').split('\n');
    const chunks = [];
    let current = [];

    const flush = () => {
        while (current.length > 0 && current[current.length - 1].trim() === '') {
            current.pop();
        }

        if (current.length > 0) {
            chunks.push(current.join('\n'));
        }

        current = [];
    };

    for (const line of lines) {
        // Only a `name:` at the left margin starts a record; an indented one is a value.
        if (/^name\s*:/.test(line) && current.some(seen => seen.trim() !== '')) {
            flush();
        }

        current.push(line);
    }

    flush();
    return chunks;
}

/** The inverse of `slice`. */
function weave(chunks) {
    const kept = chunks
        .map(chunk => chunk.replace(/\r\n/g, '\n').replace(/\s*$/, ''))
        .filter(chunk => chunk.length > 0);

    return kept.length > 0 ? kept.join('\n\n') + '\n' : '';
}

/**
 * Turns a combined document into notebook cells: a heading per file, then one cell per
 * record. Cells are plain objects here so the conversion can be tested without VS Code.
 */
function toCells(text) {
    const parsed = split(text);
    const cells = [];

    for (const block of parsed.blocks) {
        cells.push({ kind: 'markdown', file: block.label, text: `#### ${block.label}` });

        for (const chunk of slice(block.text)) {
            cells.push({ kind: 'code', file: block.label, text: chunk });
        }
    }

    return cells;
}

/**
 * The inverse of `toCells`. Headings are ignored; a code cell belongs to whichever file
 * it says, or to the one above it when someone adds a fresh cell.
 */
function fromCells(cells) {
    const order = [];
    const byFile = new Map();
    let last = null;

    for (const cell of cells) {
        if (cell.kind !== 'code') {
            // A heading still moves the cursor, so a cell added under it lands in that file.
            if (cell.file) {
                last = cell.file;
            }

            continue;
        }

        const file = cell.file || last;

        if (!file) {
            continue;
        }

        last = file;

        if (!byFile.has(file)) {
            byFile.set(file, []);
            order.push(file);
        }

        byFile.get(file).push(cell.text);
    }

    return combine(order.map(file => ({ label: file, text: weave(byFile.get(file)) }))).content;
}

/**
 * Decides what a save should do. Pure, so the risky part can be tested without touching disk.
 * `parsed` comes from `split`, `files` is the snapshot the view was built from.
 */
function plan(parsed, files) {
    if (parsed.stray) {
        return { error: 'Everything must sit under a "// ==== path ====" header.' };
    }

    const known = new Map(files.map(file => [file.label, file]));
    const writes = [];
    const unknown = [];

    for (const block of parsed.blocks) {
        const file = known.get(block.label);

        if (!file) {
            unknown.push(block.label);
            continue;
        }

        known.delete(block.label);

        // Only files that actually differ get rewritten; Unity reimports whatever it is handed.
        if (settle(block.text) !== settle(file.text)) {
            writes.push({ label: block.label, text: block.text });
        }
    }

    if (unknown.length > 0) {
        return {
            error: `No file named ${unknown.map(name => `'${name}'`).join(', ')}. `
                + 'Headers name existing files; new ones have to be created normally.'
        };
    }

    return { writes: writes, missing: [...known.keys()] };
}

const COMBINED_SCHEME = 'frognet-combined';
const NOTEBOOK_TYPE = 'frognet-data-notebook';
const NOTEBOOK_SUFFIX = '-all';

function activate(context) {
    const formatter = {
        provideDocumentFormattingEdits(document, options) {
            const unit = options.insertSpaces ? ' '.repeat(options.tabSize) : '\t';
            const text = document.getText();
            const formatted = document.languageId === 'frognet-schema'
                ? formatSchema(text, unit)
                : formatData(text, unit);

            if (formatted === text) {
                return [];
            }

            const whole = new vscode.Range(
                document.positionAt(0),
                document.positionAt(text.length));

            return [vscode.TextEdit.replace(whole, formatted)];
        }
    };

    context.subscriptions.push(
        vscode.languages.registerDocumentFormattingEditProvider('frognet-schema', formatter),
        vscode.languages.registerDocumentFormattingEditProvider('frognet-data', formatter));

    // ---- combined view -------------------------------------------------
    //
    // A writable virtual file rather than a real one: nothing lands on disk, so the
    // registry cannot pick it up as a duplicate and git never sees it. Saving splits
    // the blocks back to the files they came from.

    const changed = new vscode.EventEmitter();
    const views = new Map();

    async function build(uri) {
        const extension = uri.path.includes('.fschema') ? '.fschema' : '.fdata';
        const found = await vscode.workspace.findFiles(`**/*${extension}`);
        found.sort((a, b) => a.path.localeCompare(b.path));

        const files = [];

        for (const file of found) {
            const bytes = await vscode.workspace.fs.readFile(file);
            files.push({
                uri: file,
                label: vscode.workspace.asRelativePath(file, false),
                text: Buffer.from(bytes).toString('utf8')
            });
        }

        const joined = combine(files);
        const view = {
            files: files,
            map: joined.map,
            content: joined.content,
            mtime: Date.now()
        };

        views.set(uri.toString(), view);
        return view;
    }

    function cached(uri) {
        return views.get(uri.toString());
    }

    async function writeBack(uri, text) {
        const view = cached(uri);

        if (!view) {
            throw vscode.FileSystemError.FileNotFound(uri);
        }

        const decided = plan(split(text), view.files);

        if (decided.error) {
            throw new Error(decided.error);
        }

        const byLabel = new Map(view.files.map(file => [file.label, file]));
        const written = [];

        for (const write of decided.writes) {
            const file = byLabel.get(write.label);

            // Refuse to clobber a file that moved underneath this view.
            const current = Buffer.from(await vscode.workspace.fs.readFile(file.uri)).toString('utf8');

            if (settle(current) !== settle(file.text)) {
                throw new Error(
                    `'${file.label}' changed on disk since this view opened. `
                    + 'Close it and run the command again to pick up the newer copy.');
            }

            // Keep whatever line endings the file already used.
            const body = file.text.includes('\r\n')
                ? write.text.replace(/\n/g, '\r\n')
                : write.text;

            await vscode.workspace.fs.writeFile(file.uri, Buffer.from(body, 'utf8'));
            written.push(file.label);
        }

        // A block someone deleted is far more likely a slip than a request to delete a file.
        if (decided.missing.length > 0) {
            vscode.window.showWarningMessage(
                `Left ${decided.missing.join(', ')} alone: no block for it. `
                + 'Delete the file itself to remove it.');
        }

        await build(uri);
        changed.fire([{ type: vscode.FileChangeType.Changed, uri: uri }]);

        vscode.window.setStatusBarMessage(
            written.length > 0
                ? `Frognet: saved ${written.length} file(s) — ${written.join(', ')}`
                : 'Frognet: nothing changed',
            4000);
    }

    const provider = {
        onDidChangeFile: changed.event,
        watch: () => new vscode.Disposable(() => { }),
        async stat(uri) {
            const view = cached(uri) || await build(uri);
            return {
                type: vscode.FileType.File,
                ctime: view.mtime,
                mtime: view.mtime,
                size: Buffer.byteLength(view.content, 'utf8')
            };
        },
        async readFile(uri) {
            const view = cached(uri) || await build(uri);
            return Buffer.from(view.content, 'utf8');
        },
        async writeFile(uri, content) {
            await writeBack(uri, Buffer.from(content).toString('utf8'));
        },
        readDirectory: () => [],
        createDirectory: () => { },
        delete: () => { throw vscode.FileSystemError.NoPermissions(); },
        rename: () => { throw vscode.FileSystemError.NoPermissions(); }
    };

    context.subscriptions.push(
        vscode.workspace.registerFileSystemProvider(COMBINED_SCHEME, provider, { isCaseSensitive: true }));

    // ---- notebook view --------------------------------------------------
    //
    // One cell per record, so line numbers restart at 1 for every item instead of running
    // to four digits. The serializer only converts between the combined text and cells;
    // reading and writing the real files is the file system provider's job above.

    const FILE_KEY = 'frognetFile';

    context.subscriptions.push(
        vscode.workspace.registerNotebookSerializer(NOTEBOOK_TYPE, {
            deserializeNotebook(content) {
                const text = Buffer.from(content).toString('utf8');
                const language = split(text).blocks.some(block => block.label.endsWith('.fschema'))
                    ? 'frognet-schema'
                    : 'frognet-data';

                const cells = toCells(text).map(cell => {
                    const made = new vscode.NotebookCellData(
                        cell.kind === 'markdown'
                            ? vscode.NotebookCellKind.Markup
                            : vscode.NotebookCellKind.Code,
                        cell.text,
                        cell.kind === 'markdown' ? 'markdown' : language);

                    made.metadata = { [FILE_KEY]: cell.file };
                    return made;
                });

                return new vscode.NotebookData(cells);
            },
            serializeNotebook(data) {
                const cells = data.cells.map(cell => ({
                    kind: cell.kind === vscode.NotebookCellKind.Markup ? 'markdown' : 'code',
                    file: cell.metadata ? cell.metadata[FILE_KEY] : undefined,
                    text: cell.value
                }));

                return Buffer.from(fromCells(cells), 'utf8');
            }
        }, { transientOutputs: true }));

    context.subscriptions.push(
        vscode.commands.registerCommand('frognet.browseAllData',
            () => browse('.fdata')),
        vscode.commands.registerCommand('frognet.browseAllSchemas',
            () => browse('.fschema')));

    async function browse(extension) {
        const uri = vscode.Uri.from({
            scheme: COMBINED_SCHEME,
            path: `/All ${extension} files${extension}${NOTEBOOK_SUFFIX}`
        });

        await build(uri);
        changed.fire([{ type: vscode.FileChangeType.Changed, uri: uri }]);

        const notebook = await vscode.workspace.openNotebookDocument(uri);
        await vscode.window.showNotebookDocument(notebook);
    }

    // Ctrl+Click or F12 on any line jumps to that line in the file it came from.
    context.subscriptions.push(
        vscode.languages.registerDefinitionProvider(
            [
                { scheme: COMBINED_SCHEME, language: 'frognet-data' },
                { scheme: COMBINED_SCHEME, language: 'frognet-schema' }
            ],
            {
                provideDefinition(document, position) {
                    const view = cached(document.uri);
                    const entry = view && view.map[position.line];

                    if (!entry) {
                        return null;
                    }

                    return new vscode.Location(
                        view.files[entry.file].uri,
                        new vscode.Position(entry.line, 0));
                }
            }));

    async function open(extension, language) {
        const uri = vscode.Uri.from({
            scheme: COMBINED_SCHEME,
            path: `/All ${extension} files${extension}`
        });

        await build(uri);
        changed.fire([{ type: vscode.FileChangeType.Changed, uri: uri }]);

        const document = await vscode.workspace.openTextDocument(uri);
        await vscode.languages.setTextDocumentLanguage(document, language);
        await vscode.window.showTextDocument(document, { preview: false });
    }

    context.subscriptions.push(
        vscode.commands.registerCommand('frognet.openAllData', () => open('.fdata', 'frognet-data')),
        vscode.commands.registerCommand('frognet.openAllSchemas', () => open('.fschema', 'frognet-schema')));

    // Follow changes made elsewhere, but never throw away edits in progress.
    const watcher = vscode.workspace.createFileSystemWatcher('**/*.{fdata,fschema}');

    /** True while the view has edits someone has not saved yet. */
    function dirty(key) {
        const text = vscode.workspace.textDocuments
            .find(document => document.uri.toString() === key);

        if (text && text.isDirty) {
            return true;
        }

        const notebook = vscode.workspace.notebookDocuments
            .find(document => document.uri.toString() === key);

        return Boolean(notebook && notebook.isDirty);
    }

    async function refresh() {
        for (const key of [...views.keys()]) {
            if (dirty(key)) {
                continue;
            }

            const uri = vscode.Uri.parse(key);
            const before = views.get(key);
            const after = await build(uri);

            // Saving rewrites the files this view is built from, so it would otherwise
            // reload itself and throw the cursor away for no reason.
            if (before && before.content === after.content) {
                continue;
            }

            changed.fire([{ type: vscode.FileChangeType.Changed, uri: uri }]);
        }
    }

    watcher.onDidChange(refresh);
    watcher.onDidCreate(refresh);
    watcher.onDidDelete(refresh);
    context.subscriptions.push(watcher, changed);
}

function deactivate() { }

module.exports = {
    activate, deactivate,
    formatSchema, formatData,
    combine, split, settle, plan,
    slice, weave, toCells, fromCells
};
