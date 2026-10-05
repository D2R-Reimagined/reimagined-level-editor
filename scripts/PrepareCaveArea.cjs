'use strict';
const {validatePresetOpenPath} = require('./PresetOpenPath.cjs');

// Prepare a new, offline Act I cave-area candidate. This command never writes
// to the selected source data root, exporter candidate, or an installed game.
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {
  parseTable,
  serializeTable,
  writeTable,
  ENCODING,
} = require('./lib/tsv.cjs');

const TABLE_NAMES = ['levels', 'lvlprest', 'lvltypes', 'automap'];
const AUTOMAP_CELLS = Object.freeze({1: 120, 2: 121, 4: 120, 8: 121, 3: 123, 6: 136, 9: 135, 12: 122});
const REQUIRED_MASKS = Object.freeze([1, 2, 4, 8, 3, 6, 9, 12]);
const MAX_U16 = 0xffff;
const MAX_U32 = 0xffffffffn;

function invariant(value, message) {
  if (!value) throw new Error(message);
}

function sha256(bytes) {
  return crypto.createHash('sha256').update(bytes).digest('hex');
}

function jsonBytes(value) {
  return Buffer.from(`${JSON.stringify(value, null, 2)}\n`, 'utf8');
}

function readJson(file, label = file) {
  let raw;
  try {
    raw = fs.readFileSync(file, 'utf8');
    return {raw, value: JSON.parse(raw.replace(/^\uFEFF/, ''))};
  } catch (error) {
    throw new Error(`Cannot read ${label} as JSON: ${error.message}`);
  }
}

function integer(value, label, maximum = Number.MAX_SAFE_INTEGER) {
  invariant(Number.isSafeInteger(value) && value >= 0 && value <= maximum,
    `${label} must be an unsigned integer no greater than ${maximum}`);
  return value;
}

function normalizedRelative(value, label) {
  invariant(typeof value === 'string' && value.trim(), `${label} must be a non-empty relative path`);
  const slash = value.replaceAll('\\', '/').replace(/^\.\//, '');
  invariant(!path.win32.isAbsolute(value) && !path.posix.isAbsolute(slash), `${label} must be relative`);
  invariant(!slash.split('/').some(part => part === '..' || part === ''), `${label} must not escape its root`);
  return slash;
}

function withoutDataPrefix(value, label) {
  const relative = normalizedRelative(value, label);
  return relative.toLowerCase().startsWith('data/') ? relative.slice(5) : relative;
}

function contained(root, relative, label) {
  const clean = normalizedRelative(relative, label);
  const target = path.resolve(root, ...clean.split('/'));
  const relation = path.relative(path.resolve(root), target);
  invariant(relation && !relation.startsWith('..') && !path.isAbsolute(relation), `${label} escapes its root`);
  return target;
}

function isInside(parent, child) {
  const relative = path.relative(path.resolve(parent), path.resolve(child));
  return relative === '' || (!relative.startsWith('..') && !path.isAbsolute(relative));
}

function safeOutputRoots(source, candidate, output) {
  invariant(!fs.existsSync(output), 'Output folder already exists; choose a new destination');
  const parent = path.dirname(output);
  invariant(fs.existsSync(parent) && fs.statSync(parent).isDirectory(), 'Output parent folder must already exist');
  const sourceReal = fs.realpathSync(source);
  const candidateReal = fs.realpathSync(candidate);
  const parentReal = fs.realpathSync(parent);
  const projected = path.join(parentReal, path.basename(output));
  invariant(!isInside(sourceReal, projected) && !isInside(candidateReal, projected),
    'Output folder must not be inside an input root');
  invariant(!isInside(projected, sourceReal) && !isInside(projected, candidateReal),
    'Output folder must not contain an input root');
}

function sourceFile(root, relative, label = relative) {
  const file = contained(root, relative, label);
  invariant(fs.existsSync(file) && fs.statSync(file).isFile(), `Missing ${label}: ${file}`);
  return file;
}

function loadTable(source, name) {
  const relative = `global/excel/${name}.txt`;
  const file = sourceFile(source, relative, `${name}.txt`);
  const raw = fs.readFileSync(file, ENCODING);
  const table = parseTable(file);
  invariant(table.eol === '\r\n', `${name}.txt must use CRLF line endings`);
  invariant(serializeTable(table) === raw, `${name}.txt failed byte-exact TSV round-trip`);
  invariant(table.headers.length > 0, `${name}.txt has no headers`);
  for (const [index, row] of table.rows.entries()) {
    invariant(row.length === table.headers.length,
      `${name}.txt row ${index + 2} has ${row.length} columns; expected ${table.headers.length}`);
  }
  return {name, relative, file, raw: Buffer.from(raw, ENCODING), table};
}

function column(table, name, tableName) {
  const index = table.headers.indexOf(name);
  invariant(index >= 0, `${tableName} is missing header ${name}`);
  return index;
}

function objectRows(table) {
  return table.rows.filter(row => row.some(Boolean)).map(row =>
    Object.fromEntries(table.headers.map((header, index) => [header, row[index] ?? ''])));
}

function uniqueRow(table, tableName, key, value) {
  const at = column(table, key, tableName);
  const matches = table.rows.filter(row => row[at] === String(value));
  invariant(matches.length === 1, `${tableName}.${key}=${value} must resolve exactly once; found ${matches.length}`);
  return matches[0];
}

function assertDenseNumericIds(table, tableName, key) {
  const at = column(table, key, tableName);
  const seen = new Set();
  let separators = 0;
  let trailingBlanksStarted = false;
  for (const row of table.rows) {
    if (!row.some(Boolean)) {
      trailingBlanksStarted = true;
      continue;
    }
    invariant(!trailingBlanksStarted,
      `${tableName}.${key} contains an interior blank row before a later data row`);
    if (row[at] === '') {
      const allowed = row.every((value, index) => value === '' ||
        (table.headers[index] === 'Name' && value === 'Expansion') ||
        (tableName === 'levels' && ['PreventTownPortal', 'MonDen', 'MonDen(N)', 'MonDen(H)'].includes(table.headers[index]) && value === '0'));
      invariant(allowed && row[column(table, 'Name', tableName)] === 'Expansion',
        `${tableName}.${key} contains an unsupported non-data row`);
      separators++;
      invariant(separators === 1, `${tableName}.${key} contains more than one Expansion separator`);
      continue;
    }
    invariant(/^\d+$/.test(row[at] ?? ''), `${tableName}.${key} must be an unsigned integer`);
    invariant(!seen.has(row[at]), `Duplicate ${tableName}.${key}=${row[at]}`);
    const physicalIndex = seen.size;
    invariant(row[at] === String(physicalIndex),
      `${tableName}.${key}=${row[at]} is at physical data-row index ${physicalIndex}; ` +
      `native lookup requires ${tableName}.${key} to equal its physical data-row index`);
    seen.add(row[at]);
  }
  return {ids: seen, nextId: seen.size};
}

function setCells(table, tableName, row, changes) {
  for (const [name, value] of Object.entries(changes)) {
    row[column(table, name, tableName)] = String(value);
  }
}

function appendBeforeBlank(table, row) {
  const blank = table.rows.findIndex(existing => !existing.some(Boolean));
  table.rows.splice(blank < 0 ? table.rows.length : blank, 0, row);
}

function cloneTable(loaded) {
  return {
    headers: [...loaded.table.headers],
    rows: loaded.table.rows.map(row => [...row]),
    eol: loaded.table.eol,
    hasFinalEol: loaded.table.hasFinalEol,
  };
}

function readDs1Header(file) {
  const bytes = fs.readFileSync(file);
  invariant(bytes.length >= 12, 'Candidate DS1 is too short to contain its dimensions');
  const version = bytes.readInt32LE(0);
  const widthMinusOne = bytes.readInt32LE(4);
  const heightMinusOne = bytes.readInt32LE(8);
  invariant(version >= 1 && version <= 100, `Unsupported DS1 version ${version}`);
  integer(widthMinusOne, 'DS1 width-1', 511);
  integer(heightMinusOne, 'DS1 height-1', 511);
  invariant(widthMinusOne > 0 && heightMinusOne > 0, 'DS1 logical dimensions must be positive');
  return {
    version,
    logicalWidth: widthMinusOne,
    logicalHeight: heightMinusOne,
    storedWidth: widthMinusOne + 1,
    storedHeight: heightMinusOne + 1,
    bytes,
  };
}

function validateDc6(file) {
  const bytes = fs.readFileSync(file);
  invariant(bytes.length >= 24, 'MaxiMap.dc6 is too short');
  const version = bytes.readUInt32LE(0);
  const directions = bytes.readUInt32LE(16);
  const framesPerDirection = bytes.readUInt32LE(20);
  invariant(version === 6, `MaxiMap.dc6 version ${version} is unsupported`);
  invariant(directions > 0 && framesPerDirection > 0, 'MaxiMap.dc6 has no frames');
  const frameCount = directions * framesPerDirection;
  invariant(Number.isSafeInteger(frameCount) && frameCount <= 1_000_000, 'MaxiMap.dc6 frame count is invalid');
  const tableEnd = 24 + frameCount * 4;
  invariant(tableEnd <= bytes.length, 'MaxiMap.dc6 frame pointer table is truncated');
  for (const cell of new Set(Object.values(AUTOMAP_CELLS))) {
    invariant(cell < frameCount, `MaxiMap.dc6 does not contain required cell ${cell}`);
    const offset = bytes.readUInt32LE(24 + cell * 4);
    invariant(offset >= tableEnd && offset < bytes.length, `MaxiMap.dc6 cell ${cell} has an invalid frame offset`);
  }
  return {version, directions, framesPerDirection, frameCount, bytes};
}

function modelCatalogKey(value) {
  let key = 2166136261;
  for (const character of value.toLowerCase()) {
    key = Math.imul((key ^ character.charCodeAt(0)) >>> 0, 16777619) >>> 0;
  }
  return key;
}

function readModelCatalog(file) {
  const bytes = fs.readFileSync(file);
  invariant(bytes.length >= 24 && bytes.length <= 64 * 1024 * 1024, 'Invalid model_lod_desc.bin size');
  const countModels = bytes.readUInt32LE(0);
  const countLods = bytes.readUInt32LE(8);
  const countMeshes = bytes.readUInt32LE(16);
  const modelsStart = 4 + bytes.readUInt32LE(4);
  const lodsStart = 12 + bytes.readUInt32LE(12);
  const meshesStart = 20 + bytes.readUInt32LE(20);
  invariant(modelsStart === 24 && lodsStart === 24 + countModels * 44 &&
    meshesStart === lodsStart + countLods * 12 && bytes.length === meshesStart + countMeshes * 32,
  'Unsupported model_lod_desc.bin layout');
  const keys = new Set();
  for (let index = 0; index < countModels; index++) {
    const key = bytes.readUInt32LE(modelsStart + index * 44);
    invariant(!keys.has(key), `Duplicate model catalog key ${key}`);
    keys.add(key);
  }
  return {bytes, countModels, countLods, countMeshes, keys};
}

function stringValues(value, result = []) {
  if (typeof value === 'string') result.push(value.replaceAll('\\', '/'));
  else if (Array.isArray(value)) value.forEach(item => stringValues(item, result));
  else if (value && typeof value === 'object') Object.values(value).forEach(item => stringValues(item, result));
  return result;
}

function resolveOrigins(levelRows) {
  const byId = new Map(levelRows.map(row => [Number(row.Id), row]));
  const cache = new Map();
  function resolve(id, chain = []) {
    if (cache.has(id)) return cache.get(id);
    invariant(!chain.includes(id), `Cyclic Levels.Depend chain: ${[...chain, id].join(' -> ')}`);
    const row = byId.get(id);
    invariant(row, `Unresolved Levels.Depend=${id}`);
    const x = Number(row.OffsetX);
    const y = Number(row.OffsetY);
    const depend = Number(row.Depend || 0);
    invariant(Number.isSafeInteger(x) && Number.isSafeInteger(y) && Number.isSafeInteger(depend),
      `Level ${id} has invalid placement fields`);
    const parent = depend === 0 ? [0, 0] : resolve(depend, [...chain, id]);
    const result = [parent[0] + x, parent[1] + y];
    cache.set(id, result);
    return result;
  }
  return {byId, resolve};
}

function rectanglesOverlap(a, b) {
  return a.x < b.x + b.width && a.x + a.width > b.x && a.y < b.y + b.height && a.y + a.height > b.y;
}

function validatePlacement(levelTable, recipe, dimensions) {
  const rows = objectRows(levelTable).filter(row => /^\d+$/.test(row.Id));
  const origins = resolveOrigins(rows);
  const depend = integer(recipe.placement.depend, 'placement.depend', MAX_U16);
  const offsetX = integer(recipe.placement.offsetX, 'placement.offsetX', MAX_U16);
  const offsetY = integer(recipe.placement.offsetY, 'placement.offsetY', MAX_U16);
  integer(recipe.placement.layer, 'placement.layer', 255);
  const parent = depend === 0 ? [0, 0] : origins.resolve(depend);
  const origin = [parent[0] + offsetX, parent[1] + offsetY];
  for (const value of origin) integer(value, 'resolved placement coordinate', MAX_U16);
  const checked = [];
  for (const row of rows.filter(row => row.Act === '0')) {
    const existingOrigin = origins.resolve(Number(row.Id));
    for (const suffix of ['', '(N)', '(H)']) {
      const width = Number(row[`SizeX${suffix}`]);
      const height = Number(row[`SizeY${suffix}`]);
      invariant(Number.isSafeInteger(width) && Number.isSafeInteger(height) && width >= 0 && height >= 0,
        `Level ${row.Id} has invalid ${suffix || 'normal'} dimensions`);
      if (width === 0 || height === 0) continue;
      const existing = {x: existingOrigin[0], y: existingOrigin[1], width, height};
      const added = {x: origin[0], y: origin[1], width: dimensions.logicalWidth, height: dimensions.logicalHeight};
      invariant(!rectanglesOverlap(existing, added),
        `Requested area overlaps Act I level ${row.Id} in ${suffix || 'normal'} difficulty`);
    }
    checked.push(Number(row.Id));
  }
  invariant(origin[0] + dimensions.logicalWidth <= MAX_U16 && origin[1] + dimensions.logicalHeight <= MAX_U16,
    'Resolved area exceeds the supported unsigned world-coordinate bound');
  return {origin, checked};
}

function parseRecipe(file) {
  const {value} = readJson(file, 'recipe');
  invariant(value && typeof value === 'object' && !Array.isArray(value), 'Recipe must be a JSON object');
  invariant(value.version === 1, 'Recipe version must be 1');
  invariant(typeof value.slug === 'string' && /^[a-z0-9][a-z0-9_-]{0,47}$/.test(value.slug),
    'slug must contain only lowercase letters, digits, underscores, or hyphens');
  invariant(typeof value.name === 'string' && value.name.trim(), 'name is required');
  for (const field of ['levelId', 'presetDef', 'levelTypeId', 'baseLevelId', 'basePresetDef', 'baseLevelTypeId']) {
    integer(value[field], field, MAX_U16);
  }
  invariant(value.placement && typeof value.placement === 'object', 'placement is required');
  invariant(!value.connections || (Array.isArray(value.connections) && value.connections.length === 0),
    'Connections are unsupported here; author them later with the editor connection workflow');
  value.reservedIds ??= [];
  value.reservedPresetDefs ??= [];
  value.reservedLevelTypeIds ??= [];
  for (const [field, values] of [['reservedIds', value.reservedIds], ['reservedPresetDefs', value.reservedPresetDefs],
    ['reservedLevelTypeIds', value.reservedLevelTypeIds]]) {
    invariant(Array.isArray(values), `${field} must be an array`);
    values.forEach((entry, index) => integer(entry, `${field}[${index}]`, MAX_U16));
  }
  invariant(!value.reservedIds.includes(value.levelId), `levelId ${value.levelId} is reserved`);
  invariant(!value.reservedPresetDefs.includes(value.presetDef), `presetDef ${value.presetDef} is reserved`);
  invariant(!value.reservedLevelTypeIds.includes(value.levelTypeId), `levelTypeId ${value.levelTypeId} is reserved`);
  invariant(value.localization && ['add', 'reuse-donor'].includes(value.localization.mode),
    'localization.mode must be add or reuse-donor');
  return value;
}

function validateLocalization(source, recipe, baseLevel) {
  const root = contained(source, 'local/lng/strings', 'localization folder');
  invariant(fs.existsSync(root) && fs.statSync(root).isDirectory(), 'Missing local/lng/strings folder');
  const files = fs.readdirSync(root).filter(name => name.toLowerCase().endsWith('.json')).sort();
  invariant(files.includes('levels.json'), 'Missing local/lng/strings/levels.json');
  const records = [];
  const allEntries = [];
  for (const name of files) {
    const file = sourceFile(root, name, `localization ${name}`);
    const raw = fs.readFileSync(file);
    const parsed = JSON.parse(raw.toString('utf8').replace(/^\uFEFF/, ''));
    invariant(Array.isArray(parsed), `${name} must contain a JSON array`);
    records.push({name, file, raw, parsed});
    for (const entry of parsed) allEntries.push({file: name, entry});
  }
  const levelsRecord = records.find(record => record.name === 'levels.json');
  const keys = recipe.localization.keys;
  invariant(keys && typeof keys === 'object', 'localization.keys is required');
  for (const field of ['stringName', 'levelName', 'levelWarp', 'levelEntry']) {
    invariant(typeof keys[field] === 'string' && keys[field], `localization.keys.${field} is required`);
  }
  if (recipe.localization.mode === 'reuse-donor') {
    const expected = {
      stringName: baseLevel['*StringName'],
      levelName: baseLevel.LevelName,
      levelWarp: baseLevel.LevelWarp,
      levelEntry: baseLevel.LevelEntry,
    };
    for (const field of Object.keys(expected)) {
      invariant(typeof expected[field] === 'string' && expected[field], `Donor level is missing ${field}`);
      invariant(keys[field] === expected[field], `localization.keys.${field} must explicitly match the donor key`);
      invariant(allEntries.some(item => item.entry && item.entry.Key === keys[field]),
        `Donor localization key ${keys[field]} was not found`);
    }
    return {records, levelsRecord, keys, additions: [], limitation: 'Donor labels reused; this candidate has no unique area text.'};
  }
  invariant(Array.isArray(recipe.localization.entries) && recipe.localization.entries.length > 0,
    'localization.entries is required in add mode');
  const neededKeys = new Set(Object.values(keys));
  const entriesByKey = new Map();
  for (const entry of recipe.localization.entries) {
    invariant(entry && typeof entry === 'object' && typeof entry.key === 'string' && entry.key,
      'Each localization entry needs a key');
    integer(entry.id, `localization id for ${entry.key}`, Number.MAX_SAFE_INTEGER);
    invariant(!entriesByKey.has(entry.key), `Duplicate recipe localization key ${entry.key}`);
    invariant(!allEntries.some(item => item.entry && item.entry.Key === entry.key),
      `Localization key ${entry.key} already exists`);
    invariant(!allEntries.some(item => item.entry && item.entry.id === entry.id),
      `Localization id ${entry.id} already exists`);
    invariant(![...entriesByKey.values()].some(existing => existing.id === entry.id),
      `Duplicate recipe localization id ${entry.id}`);
    entriesByKey.set(entry.key, entry);
  }
  for (const key of neededKeys) invariant(entriesByKey.has(key), `Missing localization entry for key ${key}`);
  const sample = levelsRecord.parsed.find(entry => entry && typeof entry === 'object');
  invariant(sample, 'levels.json has no localization shape to copy');
  const languages = Object.keys(sample).filter(key => key !== 'id' && key !== 'Key');
  invariant(languages.length > 0, 'levels.json exposes no language fields');
  const additions = [...entriesByKey.values()].map(entry => {
    const values = entry.values && typeof entry.values === 'object' ? entry.values : {};
    invariant(typeof entry.text === 'string' || Object.keys(values).length > 0,
      `Localization entry ${entry.key} needs text or values`);
    const result = {id: entry.id, Key: entry.key};
    for (const language of languages) {
      const text = values[language] ?? entry.text;
      invariant(typeof text === 'string' && text, `Localization entry ${entry.key} is missing ${language}`);
      result[language] = text;
    }
    return result;
  });
  return {records, levelsRecord, keys, additions, limitation: null};
}

function appendJsonEntries(record, additions) {
  if (additions.length === 0) return record.raw;
  const text = record.raw.toString('utf8');
  const closing = text.lastIndexOf(']');
  invariant(closing >= 0, 'levels.json has no closing array bracket');
  const before = text.slice(0, closing).trimEnd();
  const prefix = record.parsed.length ? ',' : '';
  const eol = text.includes('\r\n') ? '\r\n' : '\n';
  const formatted = additions.map(value => JSON.stringify(value, null, 2).split('\n')
    .map(line => `  ${line}`).join(eol)).join(`,${eol}`);
  const result = `${before}${prefix}${eol}${formatted}${eol}${text.slice(closing)}`;
  const reparsed = JSON.parse(result.replace(/^\uFEFF/, ''));
  assert.deepEqual(reparsed.slice(0, record.parsed.length), record.parsed,
    'Existing levels.json entries changed while appending labels');
  return Buffer.from(result, 'utf8');
}

function listFiles(root) {
  const result = [];
  function visit(folder) {
    for (const entry of fs.readdirSync(folder, {withFileTypes: true})) {
      const file = path.join(folder, entry.name);
      invariant(!entry.isSymbolicLink(), `Symbolic links are unsupported in exporter candidates: ${file}`);
      if (entry.isDirectory()) visit(file);
      else if (entry.isFile()) result.push(file);
    }
  }
  visit(root);
  return result;
}

function relativeSlash(root, file) {
  return path.relative(root, file).replaceAll('\\', '/');
}

function validateCandidate(candidate, recipe) {
  const groundFile = sourceFile(candidate, 'ground-extension.json', 'ground-extension.json');
  const contourFile = sourceFile(candidate, 'cave-contour.json', 'cave-contour.json');
  const ground = readJson(groundFile).value;
  const contour = readJson(contourFile).value;
  invariant(ground && typeof ground === 'object', 'ground-extension.json must be an object');
  invariant(contour && typeof contour === 'object', 'cave-contour.json must be an object');
  const presetRelative = withoutDataPrefix(ground.Preset, 'ground-extension.Preset');
  const mapRelative = withoutDataPrefix(ground.Map, 'ground-extension.Map');
  const terrainRelative = withoutDataPrefix(ground.Terrain, 'ground-extension.Terrain');
  invariant(presetRelative.toLowerCase().startsWith('hd/env/preset/act1/caves/'),
    'Exporter preset must be an Act I cave preset');
  invariant(mapRelative.toLowerCase().startsWith('global/tiles/act1/caves/'),
    'Exporter map must be an Act I cave DS1');
  const presetFile = sourceFile(path.join(candidate, 'data'), presetRelative, 'exporter preset');
  const presetDocument = readJson(presetFile, 'exporter preset').value;
  const ds1File = sourceFile(path.join(candidate, 'data'), mapRelative, 'exporter DS1');
  sourceFile(path.join(candidate, 'data'), terrainRelative, 'exporter terrain');
  const dimensions = readDs1Header(ds1File);
  invariant(contour.Style === 63, 'cave-contour.Style must be 63');
  invariant(contour.Tile === 'data/global/tiles/act1/caves/rle_ground_contour.dt1',
    'cave-contour.Tile must be data/global/tiles/act1/caves/rle_ground_contour.dt1');
  invariant(Array.isArray(contour.Cells) && contour.Cells.length > 0, 'cave-contour.Cells must not be empty');
  const masks = new Set();
  for (const [index, cell] of contour.Cells.entries()) {
    integer(cell.X, `cave-contour.Cells[${index}].X`, dimensions.logicalWidth - 1);
    integer(cell.Y, `cave-contour.Cells[${index}].Y`, dimensions.logicalHeight - 1);
    integer(cell.Mask, `cave-contour.Cells[${index}].Mask`, 15);
    invariant(Object.hasOwn(AUTOMAP_CELLS, cell.Mask), `Unsupported contour mask ${cell.Mask}`);
    masks.add(cell.Mask);
  }
  const contourRelative = withoutDataPrefix(contour.Tile, 'cave-contour.Tile');
  const contourAsset = sourceFile(path.join(candidate, 'data'), contourRelative, 'contour DT1');
  const projectFile = `${presetFile}.rle-project.json`;
  const linksFile = `${presetFile}.rle-links.json`;
  invariant(fs.existsSync(projectFile), 'Missing exporter project sidecar');
  invariant(fs.existsSync(linksFile), 'Missing exporter links sidecar');
  const project = readJson(projectFile, 'project sidecar').value;
  const links = readJson(linksFile, 'links sidecar').value;
  invariant(project.Act === 1, 'Project sidecar Act must be 1');
  invariant(project.Width === dimensions.storedWidth && project.Height === dimensions.storedHeight,
    `Project dimensions ${project.Width}x${project.Height} do not match DS1 ${dimensions.storedWidth}x${dimensions.storedHeight}`);
  invariant(project.Tileset && Array.isArray(project.Tileset.Files), 'Project sidecar Tileset.Files is required');
  const files = project.Tileset.Files.map(value => normalizedRelative(value, 'Tileset file').toLowerCase());
  invariant(files.includes('data/global/tiles/act1/caves/cave.dt1'), 'Project sidecar must include cave.dt1');
  invariant(files.includes('data/global/tiles/act1/caves/rle_ground_contour.dt1'),
    'Project sidecar must include rle_ground_contour.dt1');
  invariant(Array.isArray(links.Links) && links.Links.length === 0, 'Existing project Links are unsupported');
  invariant(Array.isArray(links.Baselines) && links.Baselines.length === 0, 'Existing project Baselines are unsupported');
  const slugRoot = `rle_${recipe.slug}`;
  const privatePresetPrefix = `data/hd/env/preset/act1/${slugRoot}/${recipe.slug}`;
  const privateMap = `data/global/tiles/act1/${slugRoot}/${recipe.slug}.ds1`;
  validatePresetOpenPath(privateMap);
  const oldPresetPrefix = `data/${presetRelative.replace(/\.json$/i, '')}`;
  invariant(terrainRelative.toLowerCase().startsWith(oldPresetPrefix.slice(5).toLowerCase()),
    'Exporter terrain is not owned by the selected preset');
  invariant(/_lod0\.model$/i.test(terrainRelative), 'Exporter terrain must identify its generated LOD0 model');
  const generatedTerrainLogical = `data/${terrainRelative.replace(/_lod0\.model$/i, '.model')}`;
  invariant(stringValues(presetDocument).some(value => value.toLowerCase() === generatedTerrainLogical.toLowerCase()),
    'Exporter preset does not reference the generated terrain model identity');
  const generatedTerrainLods = Array.from({length: 5}, (_, lod) => sourceFile(path.join(candidate, 'data'),
    generatedTerrainLogical.slice(5, -6) + `_lod${lod}.model`, `generated terrain LOD${lod}`));
  const modelCatalogFile = sourceFile(path.join(candidate, 'data'), 'hd/model_lod_desc.bin', 'model_lod_desc.bin');
  const modelCatalog = readModelCatalog(modelCatalogFile);
  invariant(modelCatalog.keys.has(modelCatalogKey(generatedTerrainLogical)),
    `model_lod_desc.bin does not register ${generatedTerrainLogical}`);
  return {
    groundFile, contourFile, ground, contour, presetRelative, mapRelative, terrainRelative,
    presetFile, presetDocument, ds1File, dimensions, contourAsset, projectFile, project, linksFile, links,
    masks: [...masks].sort((a, b) => a - b), oldPresetPrefix, privatePresetPrefix, privateMap,
    generatedTerrainLogical, generatedTerrainLods, modelCatalogFile, modelCatalog,
  };
}

function validateInputs(sourceArg, candidateArg, recipeFileArg, outputArg) {
  const source = fs.realpathSync(path.resolve(sourceArg));
  const candidate = fs.realpathSync(path.resolve(candidateArg));
  const recipeFile = fs.realpathSync(path.resolve(recipeFileArg));
  const output = path.resolve(outputArg);
  invariant(fs.statSync(source).isDirectory(), 'Source data root must be a directory');
  invariant(fs.statSync(candidate).isDirectory(), 'Exporter candidate root must be a directory');
  invariant(fs.statSync(recipeFile).isFile(), 'Recipe must be a file');
  invariant(source !== candidate, 'Source data root and exporter candidate must be different');
  safeOutputRoots(source, candidate, output);
  const recipe = parseRecipe(recipeFile);
  const tables = Object.fromEntries(TABLE_NAMES.map(name => [name, loadTable(source, name)]));
  const levelIds = assertDenseNumericIds(tables.levels.table, 'levels', 'Id');
  const presetDefs = assertDenseNumericIds(tables.lvlprest.table, 'lvlprest', 'Def');
  const typeIds = assertDenseNumericIds(tables.lvltypes.table, 'lvltypes', 'Id');
  invariant(!levelIds.ids.has(String(recipe.levelId)), `levels.Id=${recipe.levelId} is already used`);
  invariant(!presetDefs.ids.has(String(recipe.presetDef)), `lvlprest.Def=${recipe.presetDef} is already used`);
  invariant(!typeIds.ids.has(String(recipe.levelTypeId)), `lvltypes.Id=${recipe.levelTypeId} is already used`);
  invariant(recipe.levelId === levelIds.nextId,
    `levelId must be the next append-only ID ${levelIds.nextId}`);
  invariant(recipe.presetDef === presetDefs.nextId,
    `presetDef must be the next append-only ID ${presetDefs.nextId}`);
  invariant(recipe.levelTypeId === typeIds.nextId,
    `levelTypeId must be the next append-only ID ${typeIds.nextId}`);
  const baseLevelRow = uniqueRow(tables.levels.table, 'levels', 'Id', recipe.baseLevelId);
  const basePresetRow = uniqueRow(tables.lvlprest.table, 'lvlprest', 'Def', recipe.basePresetDef);
  const baseTypeRow = uniqueRow(tables.lvltypes.table, 'lvltypes', 'Id', recipe.baseLevelTypeId);
  const baseLevel = Object.fromEntries(tables.levels.table.headers.map((header, index) => [header, baseLevelRow[index] ?? '']));
  const basePreset = Object.fromEntries(tables.lvlprest.table.headers.map((header, index) => [header, basePresetRow[index] ?? '']));
  const baseType = Object.fromEntries(tables.lvltypes.table.headers.map((header, index) => [header, baseTypeRow[index] ?? '']));
  invariant(baseLevel.Act === '0', `Base level ${recipe.baseLevelId} is not Act I`);
  invariant(baseLevel.DrlgType === '2', `Base level ${recipe.baseLevelId} is not a preset area`);
  invariant(baseType.Act === '1', `Base level type ${recipe.baseLevelTypeId} is not Act I`);
  const candidateInfo = validateCandidate(candidate, recipe);
  invariant(basePreset.File1.replaceAll('\\', '/').toLowerCase() === candidateInfo.mapRelative
    .replace(/^global\/tiles\//i, '').toLowerCase(),
  `Base preset ${recipe.basePresetDef} File1 does not match the exporter DS1`);
  const caveSlots = [];
  for (let slot = 1; slot <= 32; slot++) {
    const value = baseType[`File ${slot}`];
    if (typeof value === 'string' && value.replaceAll('\\', '/').toLowerCase() === 'act1/caves/cave.dt1') caveSlots.push(slot);
  }
  invariant(caveSlots.length === 1, `Base level type must contain cave.dt1 exactly once; found ${caveSlots.length}`);
  const contourSlot = Array.from({length: 32}, (_, index) => index + 1)
    .find(slot => !baseType[`File ${slot}`] || baseType[`File ${slot}`] === '0');
  invariant(contourSlot, 'Base level type has no free DT1 slot in its 32-bit mask');
  invariant(contourSlot !== caveSlots[0], 'Contour slot conflicts with cave.dt1');
  const baseMask = BigInt(basePreset.Dt1Mask);
  invariant(baseMask >= 0n && baseMask <= MAX_U32, 'Base preset Dt1Mask is outside 32 bits');
  const caveBit = 1n << BigInt(caveSlots[0] - 1);
  invariant(baseMask === caveBit,
    'Base preset Dt1Mask must select only cave.dt1 for this bounded workflow');
  const sourceCave = sourceFile(source, 'global/tiles/act1/caves/cave.dt1', 'source cave.dt1');
  const maxiRelative = recipe.maxiMapPath ?? 'global/ui/automap/maximap.dc6';
  const maxiMap = sourceFile(source, maxiRelative, 'MaxiMap.dc6');
  const dc6 = validateDc6(maxiMap);
  const placement = validatePlacement(tables.levels.table, recipe, candidateInfo.dimensions);
  const localization = validateLocalization(source, recipe, baseLevel);
  const styleAt = column(tables.automap.table, 'Style', 'automap');
  const groupAt = column(tables.automap.table, 'LevelName', 'automap');
  invariant(tables.automap.table.rows.some(row => row[groupAt] === '1 Cave'), 'automap group 1 Cave is missing');
  invariant(!tables.automap.table.rows.some(row => row[groupAt] === '1 Cave' && row[styleAt] === '63'),
    'automap group 1 Cave already defines style 63');
  return {
    source, candidate, recipeFile, output, recipe, tables, baseLevelRow, basePresetRow, baseTypeRow,
    baseLevel, basePreset, baseType, candidateInfo, caveSlot: caveSlots[0], contourSlot,
    resultMask: baseMask | (1n << BigInt(contourSlot - 1)), sourceCave, maxiMap, dc6, placement, localization,
  };
}

function buildTables(context) {
  const {recipe, tables, candidateInfo, contourSlot, resultMask, localization} = context;
  const outputTables = Object.fromEntries(TABLE_NAMES.map(name => [name, cloneTable(tables[name])]));
  const level = [...context.baseLevelRow];
  const levelChanges = {
    Name: recipe.name, '*StringName': localization.keys.stringName, Id: recipe.levelId, Act: 0,
    Layer: recipe.placement.layer, OffsetX: recipe.placement.offsetX, OffsetY: recipe.placement.offsetY,
    Depend: recipe.placement.depend, DrlgType: 2, LevelType: recipe.levelTypeId,
    QuestFlag: 0, QuestFlagEx: 0, Portal: 0, Position: 1, SaveMonsters: 0, Quest: 0,
    MonWndr: 0, MonSpcWalk: 0, NumMon: 0, rangedspawn: '', Themes: 0,
    Waypoint: 255, LevelName: localization.keys.levelName, LevelWarp: localization.keys.levelWarp,
    LevelEntry: localization.keys.levelEntry, LevelGroup: '', PreventTownPortal: 0,
  };
  for (const suffix of ['', '(N)', '(H)']) Object.assign(levelChanges, {
    [`SizeX${suffix}`]: candidateInfo.dimensions.logicalWidth,
    [`SizeY${suffix}`]: candidateInfo.dimensions.logicalHeight,
    [`MonDen${suffix}`]: 0, [`MonUMin${suffix}`]: 0, [`MonUMax${suffix}`]: 0,
  });
  for (let index = 0; index < 8; index++) Object.assign(levelChanges,
    {[`Vis${index}`]: 0, [`Warp${index}`]: -1, [`ObjGrp${index}`]: 0, [`ObjPrb${index}`]: 0});
  for (const prefix of ['mon', 'nmon', 'umon']) {
    for (let index = 1; index <= 25; index++) levelChanges[`${prefix}${index}`] = '';
  }
  for (const prefix of ['cmon', 'cpct', 'camt']) {
    for (let index = 1; index <= 4; index++) levelChanges[`${prefix}${index}`] = '';
  }
  setCells(outputTables.levels, 'levels', level, levelChanges);
  appendBeforeBlank(outputTables.levels, level);

  const preset = [...context.basePresetRow];
  const privateMapRelative = candidateInfo.privateMap.replace(/^data\/global\/tiles\//, '');
  const presetChanges = {
    Name: recipe.name, Def: recipe.presetDef, LevelId: recipe.levelId, Populate: 0,
    SizeX: candidateInfo.dimensions.logicalWidth, SizeY: candidateInfo.dimensions.logicalHeight,
    Files: 1, File1: privateMapRelative, Dt1Mask: resultMask.toString(),
  };
  for (let index = 2; index <= 6; index++) presetChanges[`File${index}`] = 0;
  setCells(outputTables.lvlprest, 'lvlprest', preset, presetChanges);
  appendBeforeBlank(outputTables.lvlprest, preset);

  const type = [...context.baseTypeRow];
  setCells(outputTables.lvltypes, 'lvltypes', type, {
    Name: recipe.name, Id: recipe.levelTypeId,
    [`File ${contourSlot}`]: 'Act1/Caves/rle_ground_contour.dt1', Act: 1,
  });
  appendBeforeBlank(outputTables.lvltypes, type);

  const automap = outputTables.automap;
  const groupAt = column(automap, 'LevelName', 'automap');
  let insertion = automap.rows.findLastIndex(row => row[groupAt] === '1 Cave') + 1;
  invariant(insertion > 0, 'Cannot locate automap group 1 Cave');
  for (const mask of REQUIRED_MASKS) {
    const row = automap.headers.map(() => '');
    setCells(automap, 'automap', row, {
      LevelName: '1 Cave', TileName: 'fl', Style: 63, StartSequence: mask, EndSequence: mask,
      Cel1: AUTOMAP_CELLS[mask], Cel2: -1, Cel3: -1, Cel4: -1,
    });
    automap.rows.splice(insertion++, 0, row);
  }
  return outputTables;
}

function proveTablePreservation(context, outputTables) {
  const identities = {levels: ['Id', context.recipe.levelId], lvlprest: ['Def', context.recipe.presetDef],
    lvltypes: ['Id', context.recipe.levelTypeId]};
  const proofs = {};
  for (const name of TABLE_NAMES) {
    const copy = {
      headers: [...outputTables[name].headers], rows: outputTables[name].rows.map(row => [...row]),
      eol: outputTables[name].eol, hasFinalEol: outputTables[name].hasFinalEol,
    };
    if (name === 'automap') {
      const groupAt = column(copy, 'LevelName', name);
      const styleAt = column(copy, 'Style', name);
      copy.rows = copy.rows.filter(row => !(row[groupAt] === '1 Cave' && row[styleAt] === '63'));
    } else {
      const [key, value] = identities[name];
      const at = column(copy, key, name);
      copy.rows = copy.rows.filter(row => row[at] !== String(value));
    }
    const restored = Buffer.from(serializeTable(copy), ENCODING);
    invariant(restored.equals(context.tables[name].raw), `${name}.txt changed existing rows`);
    proofs[name] = {sourceSha256: sha256(context.tables[name].raw),
      restoredSha256: sha256(restored), existingRowsByteExact: true};
  }
  assertDenseNumericIds(outputTables.levels, 'levels', 'Id');
  assertDenseNumericIds(outputTables.lvlprest, 'lvlprest', 'Def');
  assertDenseNumericIds(outputTables.lvltypes, 'lvltypes', 'Id');
  return proofs;
}

function writeNew(file, bytes) {
  fs.mkdirSync(path.dirname(file), {recursive: true});
  fs.writeFileSync(file, bytes, {flag: 'wx'});
}

function prepareAssets(context, stage) {
  const info = context.candidateInfo;
  const candidateData = path.join(context.candidate, 'data');
  const newPrefix = info.privatePresetPrefix;
  const privatePresetFile = contained(stage, `${newPrefix}.json`, 'private preset');
  const presetBytes = fs.readFileSync(info.presetFile);
  // The scene gets a private identity, while every model and physics reference
  // inside it retains the path registered by the exporter's model catalog.
  writeNew(privatePresetFile, presetBytes);
  const excluded = new Set([info.presetFile, info.projectFile, info.linksFile]
    .map(file => path.resolve(file).toLowerCase()));
  const copiedHd = listFiles(path.join(candidateData, 'hd')).filter(file =>
    !excluded.has(path.resolve(file).toLowerCase()));
  for (const source of copiedHd) {
    const relative = `data/${relativeSlash(candidateData, source)}`;
    writeNew(contained(stage, relative, 'local HD dependency'), fs.readFileSync(source));
  }
  const privateDs1 = contained(stage, info.privateMap, 'private DS1');
  writeNew(privateDs1, info.dimensions.bytes);
  const contourTarget = contained(stage, info.contour.Tile, 'contour DT1');
  writeNew(contourTarget, fs.readFileSync(info.contourAsset));
  const project = JSON.parse(JSON.stringify(info.project));
  project.Name = context.recipe.name;
  project.Preset = `${newPrefix}.json`;
  project.Map = info.privateMap;
  project.Width = info.dimensions.storedWidth;
  project.Height = info.dimensions.storedHeight;
  project.Act = 1;
  project.Tileset = {
    Mask: Number(context.resultMask),
    Files: ['data/global/tiles/act1/caves/cave.dt1', 'data/global/tiles/act1/caves/rle_ground_contour.dt1'],
  };
  project.Template = `${newPrefix}.json`;
  project.TemplateSha256 = sha256(presetBytes).toUpperCase();
  project.Placements = Array.isArray(project.Placements) ? project.Placements : [];
  const projectTarget = `${privatePresetFile}.rle-project.json`;
  writeNew(projectTarget, jsonBytes(project));
  const links = {
    ...info.links,
    Ds1Path: path.relative(path.dirname(privatePresetFile), privateDs1),
  };
  writeNew(`${privatePresetFile}.rle-links.json`, jsonBytes(links));
  const ground = JSON.parse(JSON.stringify(info.ground));
  ground.Preset = `${newPrefix.slice(5)}.json`;
  ground.Map = info.privateMap.slice(5);
  ground.Gameplay = 'not run';
  writeNew(path.join(stage, 'ground-extension.json'), jsonBytes(ground));
  writeNew(path.join(stage, 'cave-contour.json'), jsonBytes({...info.contour, Gameplay: 'not run'}));
  return {privatePresetFile, privateDs1, contourTarget, projectTarget,
    linksTarget: `${privatePresetFile}.rle-links.json`, modelCatalogTarget: contained(stage,
      'data/hd/model_lod_desc.bin', 'model catalog'),
    sourceFiles: [info.presetFile, info.projectFile, info.linksFile, ...copiedHd, info.ds1File, info.contourAsset]};
}

function prepare(sourceArg, candidateArg, recipeFileArg, outputArg) {
  const context = validateInputs(sourceArg, candidateArg, recipeFileArg, outputArg);
  const outputTables = buildTables(context);
  const preservation = proveTablePreservation(context, outputTables);
  const parent = path.dirname(context.output);
  const stage = fs.mkdtempSync(path.join(parent, '.prepare-cave-area-'));
  let published = false;
  try {
    const assets = prepareAssets(context, stage);
    for (const name of TABLE_NAMES) {
      const target = path.join(stage, 'data', 'global', 'excel', `${name}.txt`);
      fs.mkdirSync(path.dirname(target), {recursive: true});
      writeTable(target, outputTables[name]);
      const written = fs.readFileSync(target, ENCODING);
      invariant(serializeTable(parseTable(target)) === written, `${name}.txt failed output round-trip`);
    }
    if (context.localization.additions.length) {
      const target = path.join(stage, 'data', 'local', 'lng', 'strings', 'levels.json');
      writeNew(target, appendJsonEntries(context.localization.levelsRecord, context.localization.additions));
    }
    writeNew(path.join(stage, 'recipe.json'), jsonBytes(context.recipe));
    const sourceRecords = [
      ...TABLE_NAMES.map(name => ({path: context.tables[name].relative, sha256: sha256(context.tables[name].raw)})),
      {path: relativeSlash(context.source, context.sourceCave), sha256: sha256(fs.readFileSync(context.sourceCave))},
      {path: relativeSlash(context.source, context.maxiMap), sha256: sha256(context.dc6.bytes)},
      ...context.localization.records.map(record => ({path: `local/lng/strings/${record.name}`, sha256: sha256(record.raw)})),
    ];
    const candidateRecords = [...new Set([context.candidateInfo.groundFile, context.candidateInfo.contourFile,
      context.candidateInfo.projectFile, context.candidateInfo.linksFile, ...assets.sourceFiles])]
      .map(file => ({path: relativeSlash(context.candidate, file), sha256: sha256(fs.readFileSync(file))}));
    const receipt = {
      version: 1,
      preparedAt: new Date().toISOString(),
      sourceDataRoot: context.source,
      exporterCandidateRoot: context.candidate,
      recipeSha256: sha256(fs.readFileSync(context.recipeFile)),
      sourceHashes: sourceRecords,
      exporterHashes: candidateRecords,
      recipe: context.recipe,
      allocatedIds: {levelId: context.recipe.levelId, presetDef: context.recipe.presetDef,
        levelTypeId: context.recipe.levelTypeId, localizationIds: context.localization.additions.map(item => item.id)},
      donorIds: {baseLevelId: context.recipe.baseLevelId, basePresetDef: context.recipe.basePresetDef,
        baseLevelTypeId: context.recipe.baseLevelTypeId},
      dimensions: {logical: [context.candidateInfo.dimensions.logicalWidth, context.candidateInfo.dimensions.logicalHeight],
        stored: [context.candidateInfo.dimensions.storedWidth, context.candidateInfo.dimensions.storedHeight],
        ds1Version: context.candidateInfo.dimensions.version},
      placement: {origin: context.placement.origin, checkedAct1PresetLevels: context.placement.checked},
      tileset: {caveSlot: context.caveSlot, contourSlot: context.contourSlot,
        dt1Mask: context.resultMask.toString()},
      automap: {group: '1 Cave', style: 63, cells: AUTOMAP_CELLS,
        observedContourMasks: context.candidateInfo.masks, maxiMapFrameCount: context.dc6.frameCount,
        artCertifiedFromEditorPreview: false},
      nativeAssets: {
        modelCatalog: relativeSlash(stage, assets.modelCatalogTarget),
        modelCatalogSha256: sha256(context.candidateInfo.modelCatalog.bytes),
        generatedTerrainLogical: context.candidateInfo.generatedTerrainLogical,
        generatedTerrainLods: context.candidateInfo.generatedTerrainLods
          .map(file => `data/${relativeSlash(path.join(context.candidate, 'data'), file)}`),
        registeredModelPathsRekeyed: false,
      },
      privatePaths: {
        preset: relativeSlash(stage, assets.privatePresetFile), map: relativeSlash(stage, assets.privateDs1),
        projectSidecar: relativeSlash(stage, assets.projectTarget), linksSidecar: relativeSlash(stage, assets.linksTarget),
        contourTile: relativeSlash(stage, assets.contourTarget),
      },
      untouchedRowProof: preservation,
      localizationLimitation: context.localization.limitation,
      sourceMutated: false,
      runtimeAccessed: false,
      entry: 'not run',
      connectivity: 'not run - connection authoring required',
      connectionPreparation: {presetScan: Number(context.basePreset.Scan), warps: Array(8).fill(-1),
        endpointPrepared: false, required: 'Use the existing editor connection-authoring API as a separate step.'},
      gameplay: 'not run',
    };
    writeNew(path.join(stage, 'preparation-receipt.json'), jsonBytes(receipt));
    invariant(!fs.existsSync(context.output), 'Output destination appeared during preparation');
    fs.renameSync(stage, context.output);
    published = true;
    return {output: context.output, receipt};
  } finally {
    if (!published && fs.existsSync(stage)) fs.rmSync(stage, {recursive: true, force: true});
  }
}

function main(argv = process.argv.slice(2)) {
  invariant(argv.length === 4,
    'Usage: node PrepareCaveArea.cjs <source-data-root> <exporter-candidate-root> <recipe.json> <new-output-folder>');
  const result = prepare(...argv);
  process.stdout.write(`${JSON.stringify({prepared: result.output, allocatedIds: result.receipt.allocatedIds,
    entry: result.receipt.entry, connectivity: result.receipt.connectivity, gameplay: result.receipt.gameplay})}\n`);
}

if (require.main === module) {
  try {
    main();
  } catch (error) {
    process.stderr.write(`PrepareCaveArea: ${error.message}\n`);
    process.exitCode = 1;
  }
}

module.exports = {AUTOMAP_CELLS, REQUIRED_MASKS, prepare, validateInputs, validateCandidate, readDs1Header, validateDc6};
// Shared byte-preserving table and placement primitives for later registration workflows.
module.exports.tables = {loadTable, column, uniqueRow, assertDenseNumericIds, setCells,
  appendBeforeBlank, cloneTable, objectRows, resolveOrigins, validatePlacement, safeOutputRoots, contained, listFiles};
