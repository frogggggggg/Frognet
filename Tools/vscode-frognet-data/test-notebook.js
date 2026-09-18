// Checks the notebook view: one cell per record, and an exact trip back to the files.
//   node test-notebook.js
const fs = require('fs');
const path = require('path');
const {
    combine, split, settle, plan, slice, weave, toCells, fromCells
} = require('./extension.js');

const folder = path.join(__dirname, '..', '..', 'Assets', 'StreamingAssets', 'Items');

let failures = 0;

function check(label, actual, expected) {
    const ok = actual === expected;

    if (!ok) {
        failures++;
        console.log(`  FAIL ${label}`);
        console.log('    expected: ' + JSON.stringify(expected));
        console.log('    actual:   ' + JSON.stringify(actual));
    } else {
        console.log(`  ok   ${label}`);
    }
}

console.log('== cutting a file into records ==');

const two = '// header\n\nname: one\nv: 1\n\nname: two\nv: 2\n';
const cut = slice(two);

check('preamble plus one cell per record', cut.length, 3);
check('preamble is the comment block', cut[0], '// header');
check('first record', cut[1], 'name: one\nv: 1');
check('second record', cut[2], 'name: two\nv: 2');
check('blank lines are not their own cell', slice('name: a\n\n\n\nname: b\n').length, 2);
check('a file of only comments is one cell', slice('// just this\n').length, 1);
check('an empty file makes no cells', slice('').length, 0);

check('an indented name is a value, not a new record',
    slice('name: a\ndata:\n    name: inner\nv: 1\n').length, 1);

console.log('== putting them back ==');

check('weave undoes slice', weave(slice(two)), two);
check('weave drops empty cells', weave(['a', '', '   ', 'b']), 'a\n\nb\n');
check('weave of nothing', weave([]), '');
check('one blank line between records',
    weave(['name: a', 'name: b']), 'name: a\n\nname: b\n');

console.log('== cells from a combined document ==');

const files = [
    { label: 'a.fdata', text: '// note\n\nname: one\nv: 1\n\nname: two\nv: 2\n' },
    { label: 'b.fdata', text: 'name: three\n' }
];
const joined = combine(files).content;
const cells = toCells(joined);

check('a heading per file plus a cell per chunk', cells.length, 6);
check('first is a heading', cells[0].kind, 'markdown');
check('heading names the file', cells[0].text, '#### a.fdata');
check('code cells are code', cells[1].kind, 'code');
check('every cell knows its file',
    cells.map(c => c.file).join(','),
    'a.fdata,a.fdata,a.fdata,a.fdata,b.fdata,b.fdata');
check('records are separate cells',
    cells.filter(c => c.kind === 'code').map(c => c.text.split('\n')[0]).join(' | '),
    '// note | name: one | name: two | name: three');

console.log('== back to a document ==');

check('cells round trip to the same document', fromCells(cells), joined);

const edited = cells.map(cell =>
    cell.text === 'name: one\nv: 1' ? { ...cell, text: 'name: one\nv: 99' } : cell);
check('an edit reaches the right file',
    split(fromCells(edited)).blocks[0].text,
    '// note\n\nname: one\nv: 99\n\nname: two\nv: 2\n');
check('and leaves the other file alone',
    split(fromCells(edited)).blocks[1].text, 'name: three\n');

const added = cells.slice();
added.splice(3, 0, { kind: 'code', text: 'name: fresh\nv: 7' });
check('a new cell with no file joins the one above it',
    split(fromCells(added)).blocks[0].text,
    '// note\n\nname: one\nv: 1\n\nname: fresh\nv: 7\n\nname: two\nv: 2\n');

check('headings are ignored on the way back',
    fromCells(cells.filter(c => c.kind === 'code')), joined);

const removed = cells.filter(c => c.text !== 'name: two\nv: 2');
check('deleting a cell removes just that record',
    split(fromCells(removed)).blocks[0].text, '// note\n\nname: one\nv: 1\n');

console.log('== the real files ==');

const real = fs.readdirSync(folder)
    .filter(f => f.endsWith('.fdata'))
    .map(f => ({ label: f, text: fs.readFileSync(path.join(folder, f), 'utf8') }));
const realJoined = combine(real).content;
const realCells = toCells(realJoined);

check('one cell per real record',
    realCells.filter(c => c.kind === 'code').length, 5);
check('cells are the records and preambles',
    realCells.filter(c => c.kind === 'code')
        .map(c => (c.text.match(/^name:\s*(.*)$/m) || [, 'preamble'])[1]).join(' | '),
    'preamble | meat helmet | Pebble | preamble | test item');

const back = split(fromCells(realCells));
real.forEach((file, i) => {
    check(`${file.label} survives the notebook untouched`,
        settle(back.blocks[i].text), settle(file.text));
});

check('an untouched notebook writes no files',
    plan(split(fromCells(realCells)), real).writes.length, 0);

const bumped = realCells.map(cell =>
    cell.text.startsWith('name: Pebble')
        ? { ...cell, text: cell.text.replace('maxStack: 5', 'maxStack: 6') }
        : cell);
const decided = plan(split(fromCells(bumped)), real);
check('editing one item writes one file', decided.writes.length, 1);
check('and it is the file that item lives in', decided.writes[0].label, 'items.fdata');

console.log();
console.log(failures === 0 ? 'ALL CHECKS PASSED' : `${failures} CHECK(S) FAILED`);
process.exit(failures === 0 ? 0 : 1);
