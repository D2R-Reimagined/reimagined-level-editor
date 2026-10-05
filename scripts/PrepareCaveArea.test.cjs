'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const {validatePresetOpenPath} = require('./PresetOpenPath.cjs');

test('preset open path counts native prefix and rejects the reproduced crash path', () => {
  validatePresetOpenPath('data/global/tiles/act1/rlecave/cave.ds1');
  validatePresetOpenPath('data/global/tiles/' + 'a'.repeat(37) + '.ds1');
  assert.throws(() => validatePresetOpenPath('data/global/tiles/' + 'a'.repeat(38) + '.ds1'), /59 bytes/);
  assert.throws(() => validatePresetOpenPath('data/global/tiles/act1/rle_cave_growth_oct1/cave_growth_oct1.ds1'), /59 bytes/);
});

test('area registration rejects overlong generated cave paths before publishing', () => withFixture(current => {
  updateJson(current.recipeFile, recipe => { recipe.slug = 'cave_growth_oct1'; });
  assert.throws(() => prepare(current.source, current.candidate, current.recipeFile, current.output), /59 bytes/);
  assert.equal(fs.existsSync(current.output), false);
}));
const {prepare, validateCandidate, AUTOMAP_CELLS} = require('./PrepareCaveArea.cjs');
const {parseTable, serializeTable, writeTable, ENCODING} = require('./lib/tsv.cjs');

const LEVEL_HEADERS = [
  'Name', '*StringName', 'Id', 'Act', 'Layer', 'SizeX', 'SizeY', 'SizeX(N)', 'SizeY(N)',
  'SizeX(H)', 'SizeY(H)', 'OffsetX', 'OffsetY', 'Depend', 'DrlgType', 'LevelType',
  ...Array.from({length: 8}, (_, index) => `Vis${index}`),
  ...Array.from({length: 8}, (_, index) => `Warp${index}`),
  'QuestFlag', 'QuestFlagEx', 'Portal', 'Position', 'SaveMonsters', 'Quest', 'MonDen', 'MonDen(N)', 'MonDen(H)',
  'MonUMin', 'MonUMax', 'MonUMin(N)', 'MonUMax(N)', 'MonUMin(H)', 'MonUMax(H)',
  'MonWndr', 'MonSpcWalk', 'NumMon',
  ...['mon'].flatMap(prefix => Array.from({length: 25}, (_, index) => `${prefix}${index + 1}`)),
  'rangedspawn',
  ...['nmon', 'umon'].flatMap(prefix => Array.from({length: 25}, (_, index) => `${prefix}${index + 1}`)),
  ...['cmon', 'cpct', 'camt'].flatMap(prefix => Array.from({length: 4}, (_, index) => `${prefix}${index + 1}`)),
  'Themes', 'Waypoint', 'LevelName', 'LevelWarp', 'LevelEntry',
  ...Array.from({length: 8}, (_, index) => `ObjGrp${index}`),
  ...Array.from({length: 8}, (_, index) => `ObjPrb${index}`),
  'LevelGroup', 'PreventTownPortal',
];
const PRESET_HEADERS = ['Name', 'Def', 'LevelId', 'Populate', 'Logicals', 'Outdoors', 'Animate',
  'KillEdge', 'FillBlanks', 'SizeX', 'SizeY', 'AutoMap', 'Scan', 'Pops', 'PopPad', 'Files',
  'File1', 'File2', 'File3', 'File4', 'File5', 'File6', 'Dt1Mask'];
const TYPE_HEADERS = ['Name', 'Id', ...Array.from({length: 32}, (_, index) => `File ${index + 1}`), 'Act'];
const AUTOMAP_HEADERS = ['LevelName', 'TileName', 'Style', 'StartSequence', 'EndSequence',
  '*Type1', 'Cel1', '*Type2', 'Cel2', '*Type3', 'Cel3', '*Type4', 'Cel4'];

function mkdir(file) {
  fs.mkdirSync(path.dirname(file), {recursive: true});
  return file;
}

function writeJson(file, value) {
  fs.writeFileSync(mkdir(file), `${JSON.stringify(value, null, 2)}\r\n`, 'utf8');
}

function writeFixtureTable(file, headers, objects) {
  const rows = objects.map(object => headers.map(header => String(object[header] ?? '')));
  fs.mkdirSync(path.dirname(file), {recursive: true});
  writeTable(file, {headers, rows, eol: '\r\n', hasFinalEol: true});
}

function baseLevel(id, changes = {}) {
  const object = {
    Name: `Level ${id}`, '*StringName': 'Cathedral', Id: id, Act: 0, Layer: 0,
    SizeX: 28, SizeY: 34, 'SizeX(N)': 28, 'SizeY(N)': 34, 'SizeX(H)': 28, 'SizeY(H)': 34,
    OffsetX: 100, OffsetY: 100, Depend: 0, DrlgType: 2, LevelType: 9,
    Portal: 1, Position: 0, SaveMonsters: 1, Quest: 0,
    MonDen: 680, 'MonDen(N)': 850, 'MonDen(H)': 1250,
    MonUMin: 1, MonUMax: 2, 'MonUMin(N)': 3, 'MonUMax(N)': 4, 'MonUMin(H)': 5, 'MonUMax(H)': 6,
    MonWndr: 1, NumMon: 1, mon1: 'fallen', nmon1: 'fallen', umon1: 'fallen', cmon1: 'rat', cpct1: 30,
    Themes: 0, Waypoint: 255, LevelName: 'Cathedral', LevelWarp: 'To The Cathedral',
    LevelEntry: 'EnteringTheCathedral', LevelGroup: 'Act 1 - Cathedral', PreventTownPortal: 0,
  };
  for (let index = 0; index < 8; index++) Object.assign(object,
    {[`Vis${index}`]: 0, [`Warp${index}`]: -1, [`ObjGrp${index}`]: 0, [`ObjPrb${index}`]: 0});
  return {...object, ...changes};
}

function writeDc6(file, frames = 200) {
  const tableEnd = 24 + frames * 4;
  const bytes = Buffer.alloc(tableEnd + 1);
  bytes.writeUInt32LE(6, 0);
  bytes.writeUInt32LE(1, 16);
  bytes.writeUInt32LE(frames, 20);
  for (let index = 0; index < frames; index++) bytes.writeUInt32LE(tableEnd, 24 + index * 4);
  fs.writeFileSync(mkdir(file), bytes);
}

function writeDs1(file, widthMinusOne = 40, heightMinusOne = 24) {
  const bytes = Buffer.alloc(64);
  bytes.writeInt32LE(16, 0);
  bytes.writeInt32LE(widthMinusOne, 4);
  bytes.writeInt32LE(heightMinusOne, 8);
  fs.writeFileSync(mkdir(file), bytes);
}

function modelCatalogKey(value) {
  let key = 2166136261;
  for (const character of value.toLowerCase()) key = Math.imul((key ^ character.charCodeAt(0)) >>> 0, 16777619) >>> 0;
  return key;
}

function writeModelCatalog(file, logicalModel) {
  const bytes = Buffer.alloc(24 + 44 + 12 + 32);
  bytes.writeUInt32LE(1, 0); bytes.writeUInt32LE(20, 4);
  bytes.writeUInt32LE(1, 8); bytes.writeUInt32LE(56, 12);
  bytes.writeUInt32LE(1, 16); bytes.writeUInt32LE(60, 20);
  bytes.writeUInt32LE(modelCatalogKey(logicalModel), 24);
  fs.writeFileSync(mkdir(file), bytes);
}

function fixture() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'rle-cave-area-'));
  const source = path.join(root, 'source');
  const candidate = path.join(root, 'candidate');
  const output = path.join(root, 'output');
  const recipeFile = path.join(root, 'recipe.json');
  const excel = path.join(source, 'global', 'excel');
  const levels = Array.from({length: 34}, (_, id) => baseLevel(id, {
    Name: `Filler ${id}`, Act: 1, OffsetX: 0, OffsetY: 0,
  }));
  levels[0] = baseLevel(0, {Name: 'Root', '*StringName': 'Root', OffsetX: 0, OffsetY: 0,
      SizeX: 50, SizeY: 50, 'SizeX(N)': 50, 'SizeY(N)': 50, 'SizeX(H)': 50, 'SizeY(H)': 50,
      LevelName: 'Root', LevelWarp: 'Root', LevelEntry: 'Root'});
  levels[33] = baseLevel(33);
  levels.splice(10, 0, {Name: 'Expansion', PreventTownPortal: 0});
  writeFixtureTable(path.join(excel, 'levels.txt'), LEVEL_HEADERS, levels);
  const presets = Array.from({length: 100}, (_, def) => ({Name: `Filler ${def}`, Def: def}));
  presets[99] = {
    Name: 'Act 1 - Cave Coldcrow W', Def: 99, LevelId: 0, Populate: 1, Logicals: 0,
    Outdoors: 0, Animate: 0, KillEdge: 1, FillBlanks: 1, SizeX: 24, SizeY: 24,
    AutoMap: 0, Scan: 0, Pops: 0, PopPad: 0, Files: 1, File1: 'Act1/Caves/CaveWCrow.ds1',
    File2: 0, File3: 0, File4: 0, File5: 0, File6: 0, Dt1Mask: 1,
  };
  presets.splice(50, 0, {Name: 'Expansion'});
  writeFixtureTable(path.join(excel, 'lvlprest.txt'), PRESET_HEADERS, presets);
  const type3 = {Name: 'Act 1 - Cave', Id: 3, 'File 1': 'Act1/Caves/cave.dt1', Act: 1};
  for (let index = 2; index <= 32; index++) type3[`File ${index}`] = 0;
  const type9 = {Name: 'Act 1 - Cathedral', Id: 9, 'File 1': 'Act1/Cathedrl/floor.dt1', Act: 1};
  for (let index = 2; index <= 32; index++) type9[`File ${index}`] = 0;
  const types = Array.from({length: 10}, (_, id) => ({Name: `Filler ${id}`, Id: id, Act: 1}));
  types[3] = type3;
  types[9] = type9;
  types.splice(6, 0, {Name: 'Expansion'});
  writeFixtureTable(path.join(excel, 'lvltypes.txt'), TYPE_HEADERS, types);
  writeFixtureTable(path.join(excel, 'automap.txt'), AUTOMAP_HEADERS, [{
    LevelName: '1 Cave', TileName: 'fl', Style: 2, StartSequence: 1, EndSequence: 1,
    '*Type1': 'Cave LA', Cel1: 120, Cel2: -1, Cel3: -1, Cel4: -1,
  }, {LevelName: '2 Desert', TileName: 'fl', Style: 0, StartSequence: 0, EndSequence: 0,
    Cel1: 1, Cel2: -1, Cel3: -1, Cel4: -1}]);
  fs.writeFileSync(mkdir(path.join(source, 'global', 'tiles', 'act1', 'caves', 'cave.dt1')), 'stock cave');
  writeDc6(path.join(source, 'global', 'ui', 'automap', 'maximap.dc6'));
  const languages = {enUS: 'Cathedral', frFR: 'Cathédrale'};
  writeJson(path.join(source, 'local', 'lng', 'strings', 'levels.json'), [
    {id: 1, Key: 'Cathedral', ...languages},
    {id: 2, Key: 'To The Cathedral', enUS: 'To The Cathedral', frFR: 'Vers la cathédrale'},
    {id: 3, Key: 'EnteringTheCathedral', enUS: 'Entering The Cathedral', frFR: 'Entrée dans la cathédrale'},
    {id: 4, Key: 'Root', enUS: 'Root', frFR: 'Racine'},
  ]);
  writeJson(path.join(source, 'local', 'lng', 'strings', 'other.json'), [{id: 10, Key: 'Other', ...languages}]);

  const presetRelative = path.join('data', 'hd', 'env', 'preset', 'act1', 'caves', 'cavewcrow.json');
  const mapRelative = path.join('data', 'global', 'tiles', 'act1', 'caves', 'cavewcrow.ds1');
  const preset = path.join(candidate, presetRelative);
  writeJson(preset, {
    dependencies: {},
    terrain: {components: [
      {type: 'ModelDefinitionComponent', filename: 'data/hd/env/preset/act1/caves/cavewcrow_ground_hash/terrain.model'},
      {type: 'PhysicsBodyDefinitionComponent', filename: 'data/hd/env/preset/act1/caves/caveWcrow/terrain.physics'},
    ]},
  });
  const terrainLogical = 'data/hd/env/preset/act1/caves/cavewcrow_ground_hash/terrain.model';
  for (let lod = 0; lod < 5; lod++) fs.writeFileSync(mkdir(path.join(candidate, 'data', 'hd', 'env',
    'preset', 'act1', 'caves', 'cavewcrow_ground_hash', `terrain_lod${lod}.model`)), `terrain ${lod}`);
  writeModelCatalog(path.join(candidate, 'data', 'hd', 'model_lod_desc.bin'), terrainLogical);
  fs.writeFileSync(mkdir(path.join(candidate, 'data', 'hd', 'env', 'preset', 'act1', 'caves',
    'caveWcrow', 'terrain.physics')), 'physics');
  fs.writeFileSync(mkdir(path.join(candidate, 'data', 'hd', 'env', 'model', 'act1', 'caves',
    'fixture_collision.physics')), 'local physics');
  writeDs1(path.join(candidate, mapRelative));
  fs.writeFileSync(mkdir(path.join(candidate, 'data', 'global', 'tiles', 'act1', 'caves',
    'rle_ground_contour.dt1')), 'contour');
  writeJson(`${preset}.rle-project.json`, {
    Version: 1, Id: 'fixture', Name: 'fixture', Preset: 'data/hd/env/preset/act1/caves/cavewcrow.json',
    Map: 'data/global/tiles/act1/caves/cavewcrow.ds1', Width: 41, Height: 25, Act: 1,
    Tileset: {Mask: 1, Files: ['data/global/tiles/act1/caves/cave.dt1',
      'data/global/tiles/act1/caves/rle_ground_contour.dt1']},
    Template: 'data/hd/env/preset/act1/caves/cavewcrow.json', TemplateSha256: 'OLD', Placements: [],
  });
  writeJson(`${preset}.rle-links.json`, {Version: 2, Ds1Path: 'old.ds1', Fingerprint: 'STRUCTURAL',
    Calibration: {TileWidth: 10, TileHeight: 10}, Links: [], Baselines: []});
  writeJson(path.join(candidate, 'ground-extension.json'), {
    Preset: 'hd/env/preset/act1/caves/cavewcrow.json', Map: 'global/tiles/act1/caves/cavewcrow.ds1',
    Terrain: 'hd/env/preset/act1/caves/cavewcrow_ground_hash/terrain_lod0.model',
    Region: {Growth: {Width: 41, Height: 25}}, Gameplay: 'not run',
  });
  writeJson(path.join(candidate, 'cave-contour.json'), {
    Style: 63,
    Cells: Object.keys(AUTOMAP_CELLS).map((mask, index) => ({X: index + 1, Y: 1, Mask: Number(mask)})),
    Tile: 'data/global/tiles/act1/caves/rle_ground_contour.dt1', Gameplay: 'not run',
  });
  const recipe = {
    version: 1, slug: 'fixture', name: 'Act 1 - RLE Fixture',
    levelId: 34, presetDef: 100, levelTypeId: 10,
    baseLevelId: 33, basePresetDef: 99, baseLevelTypeId: 3,
    placement: {offsetX: 1000, offsetY: 1000, depend: 0, layer: 0},
    connections: [], reservedIds: [], reservedPresetDefs: [],
    localization: {
      mode: 'add',
      keys: {stringName: 'RLEFixture', levelName: 'RLEFixture',
        levelWarp: 'ToRLEFixture', levelEntry: 'EnteringRLEFixture'},
      entries: [
        {id: 100, key: 'RLEFixture', text: 'RLE Fixture'},
        {id: 101, key: 'ToRLEFixture', text: 'To RLE Fixture'},
        {id: 102, key: 'EnteringRLEFixture', text: 'Entering RLE Fixture'},
      ],
    },
  };
  writeJson(recipeFile, recipe);
  return {root, source, candidate, output, recipeFile, recipe};
}

function withFixture(action) {
  const current = fixture();
  try {
    return action(current);
  } finally {
    fs.rmSync(current.root, {recursive: true, force: true});
  }
}

function updateJson(file, edit) {
  const value = JSON.parse(fs.readFileSync(file, 'utf8'));
  edit(value);
  writeJson(file, value);
}

test('prepares a private candidate and preserves every existing TSV row byte-exact', () => withFixture(current => {
  const before = Object.fromEntries(['levels', 'lvlprest', 'lvltypes', 'automap'].map(name => {
    const file = path.join(current.source, 'global', 'excel', `${name}.txt`);
    return [name, fs.readFileSync(file, ENCODING)];
  }));
  const result = prepare(current.source, current.candidate, current.recipeFile, current.output);
  assert.equal(result.receipt.entry, 'not run');
  assert.match(result.receipt.connectivity, /connection authoring required/);
  assert.equal(result.receipt.gameplay, 'not run');
  assert.deepEqual(result.receipt.dimensions.logical, [40, 24]);
  assert.deepEqual(result.receipt.dimensions.stored, [41, 25]);
  for (const proof of Object.values(result.receipt.untouchedRowProof)) assert.equal(proof.existingRowsByteExact, true);
  for (const [name, raw] of Object.entries(before)) {
    assert.equal(fs.readFileSync(path.join(current.source, 'global', 'excel', `${name}.txt`), ENCODING), raw,
      `${name} source changed`);
  }
  const outputPreset = path.join(current.output, 'data', 'hd', 'env', 'preset', 'act1', 'rle_fixture', 'fixture.json');
  const outputDs1 = path.join(current.output, 'data', 'global', 'tiles', 'act1', 'rle_fixture', 'fixture.ds1');
  assert.ok(fs.existsSync(outputPreset));
  assert.ok(fs.existsSync(outputDs1));
  assert.ok(!fs.existsSync(path.join(current.output, 'data', 'hd', 'env', 'preset', 'act1', 'caves', 'cavewcrow.json')));
  assert.ok(!fs.existsSync(path.join(current.output, 'data', 'global', 'tiles', 'act1', 'caves', 'cavewcrow.ds1')));
  const privatePreset = JSON.parse(fs.readFileSync(outputPreset, 'utf8'));
  assert.ok(JSON.stringify(privatePreset).includes('data/hd/env/preset/act1/caves/cavewcrow_ground_hash/terrain.model'));
  const sourceCatalog = fs.readFileSync(path.join(current.candidate, 'data', 'hd', 'model_lod_desc.bin'));
  assert.deepEqual(fs.readFileSync(path.join(current.output, 'data', 'hd', 'model_lod_desc.bin')), sourceCatalog);
  for (let lod = 0; lod < 5; lod++) assert.ok(fs.existsSync(path.join(current.output, 'data', 'hd', 'env',
    'preset', 'act1', 'caves', 'cavewcrow_ground_hash', `terrain_lod${lod}.model`)));
  assert.ok(fs.existsSync(path.join(current.output, 'data', 'hd', 'env', 'model', 'act1', 'caves',
    'fixture_collision.physics')));
  assert.ok(!fs.existsSync(path.join(current.output, 'data', 'hd', 'env', 'preset', 'act1',
    'rle_fixture', 'fixture_ground_hash')));
  const project = JSON.parse(fs.readFileSync(`${outputPreset}.rle-project.json`, 'utf8'));
  assert.deepEqual(project.Tileset.Files, ['data/global/tiles/act1/caves/cave.dt1',
    'data/global/tiles/act1/caves/rle_ground_contour.dt1']);
  const links = JSON.parse(fs.readFileSync(`${outputPreset}.rle-links.json`, 'utf8'));
  assert.deepEqual(links.Links, []);
  assert.deepEqual(links.Baselines, []);
  const sourceLinks = JSON.parse(fs.readFileSync(path.join(current.candidate, 'data', 'hd', 'env',
    'preset', 'act1', 'caves', 'cavewcrow.json.rle-links.json'), 'utf8'));
  assert.deepEqual({...links, Ds1Path: sourceLinks.Ds1Path}, sourceLinks,
    'An unchanged DS1 retains its structural fingerprint and all calibration metadata');
  assert.match(links.Ds1Path, /fixture\.ds1$/);
  assert.equal(result.receipt.nativeAssets.registeredModelPathsRekeyed, false);
  assert.equal(result.receipt.nativeAssets.generatedTerrainLogical,
    'data/hd/env/preset/act1/caves/cavewcrow_ground_hash/terrain.model');
  for (const name of Object.keys(before)) {
    const table = parseTable(path.join(current.output, 'data', 'global', 'excel', `${name}.txt`));
    assert.equal(serializeTable(table), fs.readFileSync(path.join(current.output, 'data', 'global', 'excel', `${name}.txt`), ENCODING));
  }
  const types = parseTable(path.join(current.output, 'data', 'global', 'excel', 'lvltypes.txt'));
  const newType = types.rows.find(row => row[types.headers.indexOf('Id')] === '10');
  assert.equal(newType[types.headers.indexOf('File 2')], 'Act1/Caves/rle_ground_contour.dt1');
  const presets = parseTable(path.join(current.output, 'data', 'global', 'excel', 'lvlprest.txt'));
  const newPreset = presets.rows.find(row => row[presets.headers.indexOf('Def')] === '100');
  assert.equal(newPreset[presets.headers.indexOf('Dt1Mask')], '3');
  const automap = parseTable(path.join(current.output, 'data', 'global', 'excel', 'automap.txt'));
  assert.equal(automap.rows.filter(row => row[automap.headers.indexOf('LevelName')] === '1 Cave' &&
    row[automap.headers.indexOf('Style')] === '63').length, 8);
}));

test('can explicitly reuse verified donor localization keys and records the limitation', () => withFixture(current => {
  updateJson(current.recipeFile, recipe => {
    recipe.localization = {
      mode: 'reuse-donor',
      keys: {stringName: 'Cathedral', levelName: 'Cathedral',
        levelWarp: 'To The Cathedral', levelEntry: 'EnteringTheCathedral'},
    };
  });
  const result = prepare(current.source, current.candidate, current.recipeFile, current.output);
  assert.match(result.receipt.localizationLimitation, /no unique area text/);
  assert.equal(fs.existsSync(path.join(current.output, 'data', 'local', 'lng', 'strings', 'levels.json')), false);
}));

test('rejects duplicate requested IDs before publishing', () => withFixture(current => {
  updateJson(current.recipeFile, recipe => { recipe.levelId = 33; });
  assert.throws(() => prepare(current.source, current.candidate, current.recipeFile, current.output), /already used/);
  assert.equal(fs.existsSync(current.output), false);
}));

test('rejects sparse or reordered keyed tables before publishing', () => {
  for (const [name, key] of [['levels', 'Id'], ['lvlprest', 'Def'], ['lvltypes', 'Id']]) {
    for (const mutation of ['sparse', 'reordered']) withFixture(current => {
      const file = path.join(current.source, 'global', 'excel', `${name}.txt`);
      const table = parseTable(file);
      const idAt = table.headers.indexOf(key);
      const numeric = table.rows.filter(row => /^\d+$/.test(row[idAt]));
      if (mutation === 'sparse') {
        table.rows.splice(table.rows.indexOf(numeric[5]), 1);
      } else {
        const first = table.rows.indexOf(numeric[1]);
        const second = table.rows.indexOf(numeric[2]);
        [table.rows[first], table.rows[second]] = [table.rows[second], table.rows[first]];
      }
      writeTable(file, table);
      assert.throws(() => prepare(current.source, current.candidate, current.recipeFile, current.output),
        /physical data-row index/);
      assert.equal(fs.existsSync(current.output), false);
    });
  }
});

test('rejects an ordinary non-data row with a blank key', () => withFixture(current => {
  const file = path.join(current.source, 'global', 'excel', 'levels.txt');
  const table = parseTable(file);
  const idAt = table.headers.indexOf('Id');
  const row = table.rows.find(value => value[idAt] === '5');
  row[idAt] = '';
  writeTable(file, table);
  assert.throws(() => prepare(current.source, current.candidate, current.recipeFile, current.output),
    /unsupported non-data row/);
  assert.equal(fs.existsSync(current.output), false);
}));

test('rejects an interior blank row that would split later keyed records', () => withFixture(current => {
  const file = path.join(current.source, 'global', 'excel', 'levels.txt');
  const table = parseTable(file);
  const idAt = table.headers.indexOf('Id');
  const later = table.rows.findIndex(row => row[idAt] === '17');
  table.rows.splice(later, 0, table.headers.map(() => ''));
  writeTable(file, table);
  assert.throws(() => prepare(current.source, current.candidate, current.recipeFile, current.output),
    /interior blank row/);
  assert.equal(fs.existsSync(current.output), false);
}));

test('requires the immediate append ID for levels, presets, and level types', () => {
  for (const [field, value, message] of [
    ['levelId', 35, /levelId must be the next append-only ID 34/],
    ['presetDef', 101, /presetDef must be the next append-only ID 100/],
    ['levelTypeId', 11, /levelTypeId must be the next append-only ID 10/],
  ]) withFixture(current => {
    updateJson(current.recipeFile, recipe => { recipe[field] = value; });
    assert.throws(() => prepare(current.source, current.candidate, current.recipeFile, current.output), message);
    assert.equal(fs.existsSync(current.output), false);
  });
});

test('accepts only the observed byte-preserved Expansion separator shape', () => withFixture(current => {
  const file = path.join(current.source, 'global', 'excel', 'levels.txt');
  const table = parseTable(file);
  const row = table.rows.find(value => value[table.headers.indexOf('Name')] === 'Expansion');
  row[table.headers.indexOf('PreventTownPortal')] = '1';
  writeTable(file, table);
  assert.throws(() => prepare(current.source, current.candidate, current.recipeFile, current.output),
    /unsupported non-data row/);
  assert.equal(fs.existsSync(current.output), false);
}));

test('rejects reserved historical ID pairs independently', () => withFixture(current => {
  updateJson(current.recipeFile, recipe => {
    recipe.reservedIds = [34];
    recipe.reservedPresetDefs = [100];
  });
  assert.throws(() => prepare(current.source, current.candidate, current.recipeFile, current.output), /levelId 34 is reserved/);
  assert.equal(fs.existsSync(current.output), false);
}));

test('rejects project and DS1 dimension disagreement', () => withFixture(current => {
  const file = path.join(current.candidate, 'data', 'hd', 'env', 'preset', 'act1', 'caves',
    'cavewcrow.json.rle-project.json');
  updateJson(file, project => { project.Width = 99; });
  assert.throws(() => prepare(current.source, current.candidate, current.recipeFile, current.output), /do not match DS1/);
  assert.equal(fs.existsSync(current.output), false);
}));

test('rejects an overlapping fixed Act I placement', () => withFixture(current => {
  updateJson(current.recipeFile, recipe => {
    recipe.placement.offsetX = 110;
    recipe.placement.offsetY = 110;
  });
  assert.throws(() => prepare(current.source, current.candidate, current.recipeFile, current.output), /overlaps Act I level 33/);
  assert.equal(fs.existsSync(current.output), false);
}));

test('rejects candidate path escape before staging', () => withFixture(current => {
  updateJson(path.join(current.candidate, 'ground-extension.json'), ground => { ground.Map = '../escape.ds1'; });
  assert.throws(() => prepare(current.source, current.candidate, current.recipeFile, current.output), /must not escape/);
  assert.equal(fs.existsSync(current.output), false);
}));

test('rejects unsupported exporter links instead of inferring connections', () => withFixture(current => {
  const file = path.join(current.candidate, 'data', 'hd', 'env', 'preset', 'act1', 'caves',
    'cavewcrow.json.rle-links.json');
  updateJson(file, links => { links.Links.push({Id: 'unknown'}); });
  assert.throws(() => prepare(current.source, current.candidate, current.recipeFile, current.output),
    /Existing project Links are unsupported/);
  assert.equal(fs.existsSync(current.output), false);
}));

test('rejects a candidate without its matching native model catalog', () => withFixture(current => {
  fs.rmSync(path.join(current.candidate, 'data', 'hd', 'model_lod_desc.bin'));
  assert.throws(() => prepare(current.source, current.candidate, current.recipeFile, current.output),
    /Missing model_lod_desc\.bin/);
  assert.equal(fs.existsSync(current.output), false);
}));

test('refuses a destination that already exists', () => withFixture(current => {
  fs.mkdirSync(current.output);
  assert.throws(() => prepare(current.source, current.candidate, current.recipeFile, current.output), /already exists/);
}));
