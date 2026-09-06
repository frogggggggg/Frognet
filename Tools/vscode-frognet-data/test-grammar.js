// Tokenises with the real VS Code grammar engine and checks the scopes.
//   npm install        (once, pulls the two dev dependencies)
//   node test-grammar.js
const fs = require('fs');
const path = require('path');
const oniguruma = require('vscode-oniguruma');
const textmate = require('vscode-textmate');

const ITEMS = path.join(__dirname, '..', '..', 'Assets', 'StreamingAssets', 'Items');

let failures = 0;

function check(label, ok, detail) {
    if (!ok) failures++;
    console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${label}${detail ? '  -> ' + detail : ''}`);
}

async function main() {
    const wasm = fs.readFileSync(path.join(
        __dirname, 'node_modules/vscode-oniguruma/release/onig.wasm'));
    await oniguruma.loadWASM(wasm.buffer);

    const registry = new textmate.Registry({
        onigLib: Promise.resolve({
            createOnigScanner: s => new oniguruma.OnigScanner(s),
            createOnigString: s => new oniguruma.OnigString(s)
        }),
        loadGrammar: scope => {
            const file = scope === 'source.fschema'
                ? 'syntaxes/frognet-schema.tmLanguage.json'
                : 'syntaxes/frognet-data.tmLanguage.json';
            return Promise.resolve(textmate.parseRawGrammar(
                fs.readFileSync(path.join(__dirname, file), 'utf8'), file));
        }
    });

    const schema = await registry.loadGrammar('source.fschema');
    const data = await registry.loadGrammar('source.fdata');

    // The scope given to the first occurrence of `piece` at or after `from`.
    function expect(grammar, line, piece, wanted, from) {
        const tokens = grammar.tokenizeLine(line, textmate.INITIAL).tokens;
        const at = line.indexOf(piece, from || 0);
        let got = '(none)';

        for (const token of tokens) {
            if (token.startIndex <= at && at < token.endIndex) {
                got = token.scopes[token.scopes.length - 1];
                break;
            }
        }

        check(`${JSON.stringify(line)}  "${piece}"`, got.startsWith(wanted), got);
    }

    console.log('== schema ==');
    expect(schema, 'durability = float', 'durability', 'entity.name.tag');
    expect(schema, 'durability = float', 'float', 'support.type');
    expect(schema, 'durability = float', '=', 'keyword.operator');
    expect(schema, 'speed = float float', 'float float', 'support.type');
    expect(schema, 'maxStack = int', 'int', 'support.type');
    expect(schema, 'name = string', 'string', 'support.type');
    expect(schema, 'flag = bool', 'bool', 'support.type');
    expect(schema, 'modified_data = data', 'data', 'entity.name.type.reference', 14);
    expect(schema, 'data =', 'data', 'entity.name.tag');
    expect(schema, '[', '[', 'punctuation.section');
    expect(schema, ']', ']', 'punctuation.section');
    expect(schema, '    head', 'head', 'variable.other.enummember');
    expect(schema, '// a note', '// a note', 'comment.line');
    expect(schema, 'durability = float // why', '// why', 'comment.line');

    console.log('== data ==');
    expect(data, 'name: meat helmet', 'name', 'keyword.control');
    expect(data, 'name: meat helmet', 'meat helmet', 'string.unquoted');
    expect(data, 'description: a meaty helmet', 'a meaty helmet', 'string.unquoted');
    expect(data, 'maxStack: 99', 'maxStack', 'entity.name.tag');
    expect(data, 'maxStack: 99', '99', 'constant.numeric');
    expect(data, 'data:', 'data', 'entity.name.tag.section');
    expect(data, '    durability: 37.5', '37.5', 'constant.numeric');
    expect(data, '    consumable: speed(2.5, 30)', 'speed', 'entity.name.function');
    expect(data, '    consumable: speed(2.5, 30)', '2.5', 'constant.numeric');
    expect(data, '    consumable: speed(2.5, 30)', '30', 'constant.numeric');
    expect(data, '    equipable: torso', 'torso', 'variable.other.enummember');
    expect(data, '    flag: true', 'true', 'constant.language.boolean');
    expect(data, '    label: The Everything Stick, Mk. 2', 'The Everything', 'string.unquoted');
    expect(data, '// a note', '// a note', 'comment.line');
    expect(data, '    durability: 50 // worn', '// worn', 'comment.line');
    expect(data, 'data.durability: 100', 'data.durability', 'entity.name.tag');

    console.log('== every line of the real files is scoped ==');
    for (const file of fs.readdirSync(ITEMS)) {
        if (!file.endsWith('.fschema') && !file.endsWith('.fdata')) continue;

        const grammar = file.endsWith('.fschema') ? schema : data;
        const text = fs.readFileSync(path.join(ITEMS, file), 'utf8');
        const plain = [];

        text.split(/\r?\n/).forEach((line, i) => {
            if (line.trim() === '') return;
            const tokens = grammar.tokenizeLine(line, textmate.INITIAL).tokens;
            const scoped = tokens.some(t =>
                t.scopes.length > 1 && line.slice(t.startIndex, t.endIndex).trim() !== '');
            if (!scoped) plain.push(`${i + 1}: ${line}`);
        });

        check(`${file} fully scoped`, plain.length === 0, plain.length ? plain[0] : 'every line');
    }

    console.log('== combined view is still valid data syntax ==');
    {
        const { combine } = require('./extension.js');
        const files = fs.readdirSync(ITEMS)
            .filter(f => f.endsWith('.fdata'))
            .map(f => ({ label: f, text: fs.readFileSync(path.join(ITEMS, f), 'utf8') }));
        const joined = combine(files);
        const plain = [];
        let headers = 0;

        joined.content.split('\n').forEach((line, i) => {
            if (line.trim() === '') return;

            const tokens = data.tokenizeLine(line, textmate.INITIAL).tokens;

            if (line.startsWith('// ====')) {
                const scope = tokens[0].scopes[tokens[0].scopes.length - 1];
                if (scope.startsWith('comment.line')) headers++;
                else plain.push(`${i + 1}: header scoped ${scope}`);
                return;
            }

            const scoped = tokens.some(t =>
                t.scopes.length > 1 && line.slice(t.startIndex, t.endIndex).trim() !== '');
            if (!scoped) plain.push(`${i + 1}: ${line}`);
        });

        check('separators read as comments', headers === files.length, `${headers} header(s)`);
        check('every combined line is scoped', plain.length === 0, plain.length ? plain[0] : 'every line');
    }

    console.log();
    console.log(failures === 0 ? 'ALL CHECKS PASSED' : `${failures} CHECK(S) FAILED`);
    process.exit(failures === 0 ? 0 : 1);
}

main().catch(error => { console.error(error); process.exit(1); });
