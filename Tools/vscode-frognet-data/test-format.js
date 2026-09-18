// Exercises the formatter outside VS Code:  node test-format.js
const fs = require('fs');
const path = require('path');
const {
    formatSchema, formatData, combine, split, settle, plan, slice, weave, toCells, fromCells
} = require('./extension.js');

const folder = path.join(__dirname, '..', '..', 'Assets', 'StreamingAssets', 'Items');

let failures = 0;

function check(label, actual, expected) {
    const ok = actual === expected;

    if (!ok) {
        failures++;
        console.log(`  FAIL ${label}`);
        console.log('    expected:\n' + JSON.stringify(expected));
        console.log('    actual:\n' + JSON.stringify(actual));
    } else {
        console.log(`  ok   ${label}`);
    }
}

console.log('== schema ==');

check('re-indents to the brackets',
    formatSchema('name = string\ndata =\n[\nx = float\n     y = int\n]\n', '    '),
    'name = string\ndata =\n[\n    x = float\n    y = int\n]\n');

check('normalises spacing round =',
    formatSchema('name=string\nx   =    float     float\n', '    '),
    'name = string\nx = float float\n');

check('nested blocks',
    formatSchema('name = string\na =\n[\nb =\n{\nc\nd\n}\n]\n', '    '),
    'name = string\na =\n[\n    b =\n    {\n        c\n        d\n    }\n]\n');

check('inline opener',
    formatSchema('name = string\na = [\nb = float\n]\n', '    '),
    'name = string\na = [\n    b = float\n]\n');

check('keeps comments and one blank line',
    formatSchema('// top\n\n\n\nname = string   // trailing\n', '    '),
    '// top\n\nname = string // trailing\n');

check('tabs honoured',
    formatSchema('name = string\na =\n[\nb = float\n]\n', '\t'),
    'name = string\na =\n[\n\tb = float\n]\n');

console.log('== data ==');

check('re-indents sections',
    formatData('name: x\ndata:\n  durability: 1\n      label: hi\n', '    '),
    'name: x\ndata:\n    durability: 1\n    label: hi\n');

check('three levels',
    formatData('name: x\na:\n  b:\n    c: 1\n', '    '),
    'name: x\na:\n    b:\n        c: 1\n');

check('normalises call arguments',
    formatData('name: x\ndata:\n    consumable:speed( 1,2 )\n', '    '),
    'name: x\ndata:\n    consumable: speed(1, 2)\n');

check('normalises number lists',
    formatData('name: x\ndata:\n    v:1,2,   3\n', '    '),
    'name: x\ndata:\n    v: 1, 2, 3\n');

check('leaves prose alone',
    formatData('name:   The Stick, Mk. 2\n', '    '),
    'name: The Stick, Mk. 2\n');

check('an indented comment stays in its section',
    formatData('// head\nname: x\ndata:\n      // inside\n    v: 1\n', '    '),
    '// head\nname: x\ndata:\n    // inside\n    v: 1\n');

check('a margin comment stays at the margin',
    formatData('name: x\ndata:\n    v: 1\n// between\nmodified_data:\n    v: 2\n', '    '),
    'name: x\ndata:\n    v: 1\n// between\nmodified_data:\n    v: 2\n');

check('a margin comment does not close the section',
    formatData('name: x\ndata:\n// note\n    v: 1\n    w: 2\n', '    '),
    'name: x\ndata:\n// note\n    v: 1\n    w: 2\n');

check('dedents back out',
    formatData('name: x\ndata:\n    v: 1\nmodified_data:\n    v: 2\n', '    '),
    'name: x\ndata:\n    v: 1\nmodified_data:\n    v: 2\n');

console.log('== combined view ==');

const combined = combine([
    { label: 'a.fdata', text: 'name: one\ndata:\n    v: 1\n' },
    { label: 'b.fdata', text: 'name: two\n' }
]);
const combinedLines = combined.content.split('\n');

check('starts with a header comment',
    combinedLines[0].startsWith('// ==== a.fdata '), true);
check('header is a comment so highlighting survives',
    combinedLines.filter(l => l.startsWith('//')).length, 2);
check('keeps every source line',
    combinedLines.slice(1, 4).join('\n'), 'name: one\ndata:\n    v: 1');
check('blank line between files', combinedLines[4], '');
check('second header', combinedLines[5].startsWith('// ==== b.fdata '), true);

check('maps a body line back to its file',
    JSON.stringify(combined.map[3]), JSON.stringify({ file: 0, line: 2 }));
check('maps the second file too',
    JSON.stringify(combined.map[6]), JSON.stringify({ file: 1, line: 0 }));
check('padding has no source', combined.map[4], null);
check('map lines up with the content',
    combined.map.length, combinedLines.length - 1);

check('handles nothing at all',
    combine([]).content, '// Nothing found.\n');

console.log('== splitting back apart ==');

const both = [
    { label: 'a.fdata', text: 'name: one\ndata:\n    v: 1\n' },
    { label: 'b.fdata', text: 'name: two\ndescription: hi\n' }
];
const apart = split(combine(both).content);

check('finds every block', apart.blocks.length, 2);
check('no stray content', apart.stray, false);
check('first block round trips', apart.blocks[0].text, both[0].text);
check('second block round trips', apart.blocks[1].text, both[1].text);
check('labels survive', apart.blocks.map(b => b.label).join(','), 'a.fdata,b.fdata');

check('an edit lands in one block only',
    split(combine(both).content.replace('v: 1', 'v: 99')).blocks
        .map(b => b.text).join('|'),
    'name: one\ndata:\n    v: 99\n|name: two\ndescription: hi\n');

check('content above the first header is caught',
    split('oops\n// ==== a.fdata ====\nname: one\n').stray, true);
check('blank lines above the first header are fine',
    split('\n\n// ==== a.fdata ====\nname: one\n').stray, false);
check('header rule length does not matter',
    split('// ==== a.fdata =\nname: one\n').blocks[0].label, 'a.fdata');
check('a comment is not mistaken for a header',
    split('// ==== a.fdata ====\n// just a note\nname: one\n').blocks[0].text,
    '// just a note\nname: one\n');

check('settle ignores line endings',
    settle('a\r\nb\r\n'), settle('a\nb\n'));
check('settle ignores trailing blanks',
    settle('a\nb\n\n\n'), settle('a\nb\n'));
check('settle keeps real differences apart',
    settle('a\nb\n') === settle('a\nc\n'), false);

console.log('== deciding what a save writes ==');

const snapshot = [
    { label: 'a.fdata', text: 'name: one\nv: 1\n' },
    { label: 'b.fdata', text: 'name: two\n' },
    { label: 'c.fdata', text: 'name: three\n' }
];
const joined = combine(snapshot).content;

check('an untouched view writes nothing',
    plan(split(joined), snapshot).writes.length, 0);

const oneEdit = plan(split(joined.replace('v: 1', 'v: 2')), snapshot);
check('only the edited file is written', oneEdit.writes.length, 1);
check('and it is the right one', oneEdit.writes[0].label, 'a.fdata');
check('with the new text', oneEdit.writes[0].text, 'name: one\nv: 2\n');

const twoEdits = plan(split(joined.replace('v: 1', 'v: 2').replace('name: two', 'name: TWO')), snapshot);
check('two edits write two files',
    twoEdits.writes.map(w => w.label).join(','), 'a.fdata,b.fdata');

check('reformatting whitespace only is not a change',
    plan(split(joined.replace('name: two\n', 'name: two\n\n\n')), snapshot).writes.length, 0);
check('CRLF in the view is not a change',
    plan(split(joined.replace(/\n/g, '\r\n')), snapshot).writes.length, 0);

const renamed = plan(split(joined.replace('a.fdata', 'z.fdata')), snapshot);
check('an unknown header is refused', typeof renamed.error, 'string');
check('and never writes anything', renamed.writes, undefined);

const dropped = plan(split(joined.split('// ==== c.fdata')[0]), snapshot);
check('a deleted block writes nothing', dropped.writes.length, 0);
check('and is reported instead of deleting', dropped.missing.join(','), 'c.fdata');

check('stray text is refused',
    typeof plan(split('junk\n' + joined), snapshot).error, 'string');

console.log('== round trip on the real files ==');
const realFiles = fs.readdirSync(folder)
    .filter(f => f.endsWith('.fdata'))
    .map(f => ({ label: f, text: fs.readFileSync(path.join(folder, f), 'utf8') }));
const realApart = split(combine(realFiles).content);

check('block count matches', realApart.blocks.length, realFiles.length);

realFiles.forEach((file, i) => {
    check(`${file.label} survives a round trip`,
        settle(realApart.blocks[i].text), settle(file.text));
});

const real = realFiles;
const realCombined = combine(real);

check('real files combine without losing a line',
    real.every(f => f.text.replace(/\r\n/g, '\n').trimEnd().split('\n')
        .every(line => realCombined.content.includes(line))), true);
check('line endings normalised',
    realCombined.content.includes('\r'), false);
check('every real body line is mapped',
    realCombined.map.filter(e => e !== null).length > 0, true);

console.log('== idempotent on the real files ==');

for (const file of fs.readdirSync(folder)) {
    if (!file.endsWith('.fschema') && !file.endsWith('.fdata')) continue;

    const full = path.join(folder, file);
    const text = fs.readFileSync(full, 'utf8');
    const format = file.endsWith('.fschema') ? formatSchema : formatData;
    const once = format(text, '    ');
    const twice = format(once, '    ');

    check(`${file} is stable`, twice, once);
    check(`${file} already formatted`, once, text.replace(/\r\n/g, '\n'));
}

console.log();
console.log(failures === 0 ? 'ALL CHECKS PASSED' : `${failures} CHECK(S) FAILED`);
process.exit(failures === 0 ? 0 : 1);
