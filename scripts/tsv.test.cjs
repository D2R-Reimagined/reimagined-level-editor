'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {parseTable, serializeTable, writeTable, ENCODING} = require('./lib/tsv.cjs');

test('table editing preserves non-ASCII bytes, empty cells and line endings', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'rle-tsv-'));
  t.after(() => fs.rmSync(root, {recursive: true, force: true}));
  for (const eol of ['\r\n', '\n']) {
    for (const finalEol of ['', eol]) {
      const original = Buffer.from(`Id\tName\tValue${eol}0\t\x80\xff\t${eol}1\tkeep\told${finalEol}`, ENCODING);
      const input = path.join(root, 'input.txt');
      fs.writeFileSync(input, original);
      const table = parseTable(input);
      assert.deepEqual(Buffer.from(serializeTable(table), ENCODING), original);
      table.rows[1][2] = 'new';
      writeTable(input, table);
      assert.deepEqual(fs.readFileSync(input), Buffer.from(original.toString(ENCODING).replace('old', 'new'), ENCODING));
    }
  }
});
