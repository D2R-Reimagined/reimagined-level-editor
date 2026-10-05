"use strict";
const fs = require("node:fs");
const ENCODING = "latin1";

// Latin-1 is a reversible byte transport, not an assumption about table encoding.
function parseTable(filePath) {
  const raw = fs.readFileSync(filePath, ENCODING);
  const eol = raw.includes("\r\n") ? "\r\n" : "\n";
  const hasFinalEol = raw.endsWith(eol);
  const body = hasFinalEol ? raw.slice(0, -eol.length) : raw;
  const lines = body.length ? body.split(eol) : [];
  return {headers: lines.length ? lines[0].split("\t") : [],
    rows: lines.slice(1).map(line => line.split("\t")), eol, hasFinalEol};
}
function serializeTable({headers, rows, eol, hasFinalEol}) {
  return [headers.join("\t"), ...rows.map(row => row.join("\t"))].join(eol)
    + (hasFinalEol ? eol : "");
}
function writeTable(filePath, table) {
  const temporary = filePath + ".tmp";
  fs.writeFileSync(temporary, serializeTable(table), ENCODING);
  fs.renameSync(temporary, filePath);
}
module.exports = {parseTable, serializeTable, writeTable, ENCODING};
