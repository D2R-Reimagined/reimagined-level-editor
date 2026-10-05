'use strict';

// Offline registration of a portable boundary export. Never deploys or starts the game.
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),assert=require('node:assert/strict');
const {serializeTable,writeTable,parseTable,ENCODING}=require('./lib/tsv.cjs');
const {tables:T,AUTOMAP_CELLS,readDs1Header}=require('./PrepareCaveArea.cjs');
const {validatePresetOpenPath}=require('./PresetOpenPath.cjs');
const digest=b=>crypto.createHash('sha256').update(b).digest('hex');
const norm=p=>p.replaceAll('\\','/').toLowerCase();
const integer=(n,max)=>assert(Number.isSafeInteger(n)&&n>=0&&n<=max,'Invalid bounded integer: '+n);
const json=f=>JSON.parse(fs.readFileSync(f,'utf8').replace(/^\uFEFF/,''));

function plan(source,candidate){
 source=fs.realpathSync(source);candidate=fs.realpathSync(candidate);
 const boundary=json(path.join(candidate,'boundary.json'));
 assert([2,3,4].includes(boundary.schemaVersion),'Expected portable boundary receipt schema 2, 3 or 4');
 assert.equal(boundary.act,1,'Only built-in Act I cave automap art is qualified for this planner');
 const data=path.join(candidate,'data'),files=T.listFiles(data);
 assert(files.length<=256&&files.reduce((n,f)=>n+fs.statSync(f).size,0)<=256*1024*1024,'Candidate exceeds export bounds');
 const projects=files.filter(f=>f.endsWith('.rle-project.json'));
 assert.equal(projects.length,1,'Choose a candidate with exactly one project');
 const project=json(projects[0]);assert.equal(project.Act,1);
 assert(typeof project.Name==='string'&&project.Name.length>0&&project.Name.length<=100&&!/[\t\r\n]/.test(project.Name),'Invalid table-safe project name');
 const asset=p=>{assert(p.startsWith('data/'),'Expected game data path');return T.contained(data,p.slice(5),'project asset');};
 const preset=asset(project.Preset),map=asset(project.Map),dimensions=readDs1Header(map);
 assert.equal(projects[0],preset+'.rle-project.json');validatePresetOpenPath(project.Map);
 assert.equal(dimensions.storedWidth,project.Width);assert.equal(dimensions.storedHeight,project.Height);
 assert.equal(dimensions.version,18,'Registration currently requires DS1 v18');assert.equal(dimensions.bytes.readInt32LE(12),0,'DS1 act differs from project');
 assert.deepEqual(boundary.dimensions,[project.Width,project.Height]);
 assert.deepEqual(project.Tileset.Files,boundary.tileset.Files,'Project and export tileset differ');
 assert(project.Tileset.Files.length>0&&project.Tileset.Files.length<=32);
 assert.equal(new Set(project.Tileset.Files.map(norm)).size,project.Tileset.Files.length);
 assert(project.Tileset.Files.some(f=>norm(f)==='data/global/tiles/act1/caves/cave.dt1'),'Cave floor tileset required');
 assert(project.Tileset.Files.includes(boundary.contourTiles),'Missing generated contour tileset');
 for(const f of project.Tileset.Files)assert(fs.statSync(asset(f)).isFile(),'Missing exported tileset');
 const contour=fs.readFileSync(asset(boundary.contourTiles));
 assert(contour.length>=276&&contour.readInt32LE(0)===7&&contour.readInt32LE(4)===6,'Invalid generated DT1');
 const count=contour.readInt32LE(268),start=contour.readInt32LE(272);
 assert(count>0&&count<=100000&&start>=276&&start+count*96<=contour.length,'Invalid DT1 record table');
 const actual=new Set();for(let i=0;i<count;i++){const at=start+i*96;assert.equal(contour.readInt32LE(at+20),0,'Generated DT1 must contain only floor records');actual.add(contour.readInt32LE(at+24)+':'+contour.readInt32LE(at+28));}
 assert(Array.isArray(boundary.identities)&&boundary.identities.length>0,'Missing contour identities');
 assert.equal(new Set(boundary.identities.map(i=>i.Style)).size,boundary.identities.length,'Duplicate contour styles');
 const expected=new Set(boundary.identities.flatMap(i=>i.Masks.map(m=>i.Style+':'+m)));
 assert.deepEqual(actual,expected,'Contour receipt does not match DT1 identities');
 const loaded=Object.fromEntries(['levels','lvlprest','lvltypes','automap'].map(n=>[n,T.loadTable(source,n)]));
 const tables=Object.fromEntries(Object.entries(loaded).map(([n,v])=>[n,T.cloneTable(v)]));
 const levelId=T.assertDenseNumericIds(tables.levels,'levels','Id').nextId;
 const presetDef=T.assertDenseNumericIds(tables.lvlprest,'lvlprest','Def').nextId;
 T.assertDenseNumericIds(tables.lvltypes,'lvltypes','Id');integer(levelId,65535);integer(presetDef,65535);
 const levels=T.objectRows(tables.levels),presets=T.objectRows(tables.lvlprest);
 const donor=levels.find(l=>l.Act==='0'&&l.LevelType==='3'&&l.DrlgType==='2'&&presets.some(p=>p.LevelId===l.Id));
 assert(donor,'No fixed Act I cave donor');
 const donorPreset=presets.find(p=>p.LevelId===donor.Id);
 const type=T.uniqueRow(tables.lvltypes,'lvltypes','Id',3);
 assert.equal(type[T.column(tables.lvltypes,'Act','lvltypes')],'1');
 const mapRelative=project.Map.slice('data/global/tiles/'.length);
 assert(!presets.some(p=>Array.from({length:6},(_,i)=>p['File'+(i+1)]).some(f=>f&&norm(f)===norm(mapRelative))),'Map path already registered');
 // Include cave entrance presets with LevelId 0. Conservatively reserve every preset mask.
 let used=0n;for(const p of presets){const mask=BigInt(p.Dt1Mask||0);assert(mask>=0n&&mask<=0xffffffffn,'Invalid preset mask');used|=mask;}
 const changedSlots=[],slots=[];
 for(const logical of project.Tileset.Files){
  const relative=logical.slice('data/global/tiles/'.length);
  assert(logical.startsWith('data/global/tiles/'),'Expected tileset path');
  let slot=Array.from({length:32},(_,i)=>i+1).find(s=>norm(type[T.column(tables.lvltypes,'File '+s,'lvltypes')])===norm(relative));
  if(!slot){slot=Array.from({length:32},(_,i)=>i+1).find(s=>['','0'].includes(type[T.column(tables.lvltypes,'File '+s,'lvltypes')])&&!(used&(1n<<BigInt(s-1))));
   assert(slot,'No unused DT1 slot outside all existing preset masks');
   const col=T.column(tables.lvltypes,'File '+slot,'lvltypes');changedSlots.push({slot,before:type[col],after:relative});type[col]=relative;
  }
  slots.push(slot);
 }
 const mask=slots.reduce((m,s)=>m|(1n<<BigInt(s-1)),0n);
 const origins=T.resolveOrigins(levels.filter(l=>/^\d+$/.test(l.Id)));
 const act=levels.filter(l=>l.Act==='0'&&/^\d+$/.test(l.Id));
 const bottom=Math.max(0,...act.flatMap(l=>['','(N)','(H)'].map(s=>origins.resolve(+l.Id)[1]+Number(l['SizeY'+s]||0))));
 const placement={depend:0,offsetX:64,offsetY:bottom+64,layer:Math.max(0,...act.map(l=>Number(l.Layer||0)))+1};
 const world=T.validatePlacement(tables.levels,{placement},dimensions);
 const level=[...T.uniqueRow(tables.levels,'levels','Id',donor.Id)];
 const changes={Name:project.Name+' boundary',Id:levelId,Act:0,DrlgType:2,LevelType:3,Depend:0,OffsetX:placement.offsetX,OffsetY:placement.offsetY,Layer:placement.layer,
  QuestFlag:0,QuestFlagEx:0,Portal:0,Position:1,SaveMonsters:0,Quest:0,Waypoint:255,NumMon:0,MonWndr:0,MonSpcWalk:0,Themes:0,LevelGroup:'',PreventTownPortal:0};
 for(const s of ['','(N)','(H)'])Object.assign(changes,{['SizeX'+s]:dimensions.logicalWidth,['SizeY'+s]:dimensions.logicalHeight,['MonDen'+s]:0,['MonUMin'+s]:0,['MonUMax'+s]:0});
 for(let i=0;i<8;i++)Object.assign(changes,{['Vis'+i]:0,['Warp'+i]:-1,['ObjGrp'+i]:0,['ObjPrb'+i]:0});
 for(const h of tables.levels.headers)if(/^(?:mon|nmon|umon|cmon|cpct|camt)\d+$/.test(h))changes[h]='';
 T.setCells(tables.levels,'levels',level,changes);T.appendBeforeBlank(tables.levels,level);
 const presetRow=[...T.uniqueRow(tables.lvlprest,'lvlprest','Def',donorPreset.Def)];
 const pchanges={Name:project.Name+' boundary',Def:presetDef,LevelId:levelId,Populate:0,Files:1,File1:mapRelative,Dt1Mask:mask.toString(),SizeX:dimensions.logicalWidth,SizeY:dimensions.logicalHeight};
 for(let i=2;i<=6;i++)pchanges['File'+i]='0';T.setCells(tables.lvlprest,'lvlprest',presetRow,pchanges);T.appendBeforeBlank(tables.lvlprest,presetRow);
 const a=tables.automap,c=h=>T.column(a,h,'automap');
 const group=a.rows.map((r,i)=>r[c('LevelName')]==='1 Cave'?i:-1).filter(i=>i>=0);
 assert(group.length&&group.at(-1)-group[0]+1===group.length,'Cave automap group must be contiguous');
 assert(Array.isArray(boundary.identities)&&boundary.identities.length>0);
 const additions=[];let reused=0;
 for(const identity of boundary.identities){integer(identity.Style,63);assert(Array.isArray(identity.Masks));
  for(const m of new Set(identity.Masks)){integer(m,15);assert(m===0||Object.hasOwn(AUTOMAP_CELLS,m),'Unsupported contour art mask');
   const cell=m===0?-1:AUTOMAP_CELLS[m];
   const old=a.rows.filter(r=>r[c('LevelName')]==='1 Cave'&&r[c('TileName')]==='fl'&&+r[c('Style')]===identity.Style&&+r[c('StartSequence')]<=m&&+r[c('EndSequence')]>=m);
   if(old.length){assert.equal(old.length,1,'Ambiguous automap style');assert.equal(+old[0][c('Cel1')],cell,'Automap style conflicts with generated art');for(const k of ['Cel2','Cel3','Cel4'])assert.equal(+old[0][c(k)],-1,'Automap variant conflict');reused++;continue;}
   const row=a.headers.map(()=> '');T.setCells(a,'automap',row,{LevelName:'1 Cave',TileName:'fl',Style:identity.Style,StartSequence:m,EndSequence:m,Cel1:cell,Cel2:-1,Cel3:-1,Cel4:-1});additions.push(row);
  }
 }
 a.rows.splice(group.at(-1)+1,0,...additions);
 // Remove only planned additions/changes and demand an exact original round trip.
 for(const n of Object.keys(tables)){const t={...tables[n],rows:tables[n].rows.map(r=>r.slice())};
  if(n==='levels')t.rows=t.rows.filter(r=>r[T.column(t,'Id',n)]!==String(levelId));
  if(n==='lvlprest')t.rows=t.rows.filter(r=>r[T.column(t,'Def',n)]!==String(presetDef));
  if(n==='lvltypes'){const r=T.uniqueRow(t,n,'Id',3);for(const s of changedSlots)r[T.column(t,'File '+s.slot,n)]=s.before;}
  if(n==='automap')t.rows.splice(group.at(-1)+1,additions.length);
  assert.equal(serializeTable(t),loaded[n].raw.toString(ENCODING),n+' changed unrelated cells');
 }
 T.assertDenseNumericIds(tables.levels,'levels','Id');T.assertDenseNumericIds(tables.lvlprest,'lvlprest','Def');
 const receiptFile=path.join(candidate,'boundary.json');
 const hashes=[{file:receiptFile,hash:digest(fs.readFileSync(receiptFile))},...files.map(f=>({file:f,hash:digest(fs.readFileSync(f))})),...Object.values(loaded).map(l=>({file:l.file,hash:digest(l.raw)}))];
 for(const f of files){const relative=path.relative(data,f);if(norm(relative).startsWith('global/excel/'))continue;
  const existing=path.join(source,relative);assert(!fs.existsSync(existing)||digest(fs.readFileSync(existing))===digest(fs.readFileSync(f)),'Candidate would replace a different baseline asset: '+relative);}
 return {source,candidate,data,files,project,projectFile:projects[0],tables,hashes,mask:Number(mask),report:{levelId,presetDef,levelType:3,group:'1 Cave',slots,changedSlots,dt1Mask:mask.toString(),world,dimensions:[dimensions.logicalWidth,dimensions.logicalHeight],automapAdded:additions.length,automapReused:reused,existingPresetMasksChecked:presets.length,unrelatedTableCells:'byte-exact',localization:'donor labels reused',connectivity:'not authored',hdTerrain:'not generated or qualified',gameplay:'not run'}};
}

function prepare(source,candidate,output){
 output=path.resolve(output);T.safeOutputRoots(source,candidate,output);
 const p=plan(source,candidate),stage=output+'.preparing-'+crypto.randomUUID();fs.mkdirSync(stage);
 try{
  for(const f of p.files){const target=path.join(stage,'data',path.relative(p.data,f));fs.mkdirSync(path.dirname(target),{recursive:true});fs.copyFileSync(f,target);}
  for(const [n,t] of Object.entries(p.tables)){const target=path.join(stage,'data/global/excel',n+'.txt');fs.mkdirSync(path.dirname(target),{recursive:true});writeTable(target,t);assert.equal(serializeTable(parseTable(target)),serializeTable(t));}
  const project={...p.project,Tileset:{...p.project.Tileset,Mask:p.mask},LevelType:3,LevelTypeName:'Act 1 - Cave'};
  fs.writeFileSync(path.join(stage,'data',path.relative(p.data,p.projectFile)),JSON.stringify(project));
  const boundary=json(path.join(candidate,'boundary.json'));boundary.tileset=project.Tileset;
  fs.writeFileSync(path.join(stage,'boundary.json'),JSON.stringify(boundary,null,2)+'\n');
  for(const h of p.hashes)assert.equal(digest(fs.readFileSync(h.file)),h.hash,'Registration input drift');
  const report={...p.report,source:p.source,candidate:p.candidate,inputHashes:p.hashes,runtimeModified:false};
  fs.writeFileSync(path.join(stage,'registration.json'),JSON.stringify(report,null,2)+'\n');
  assert(!fs.existsSync(output),'Output appeared during preparation');fs.renameSync(stage,output);return report;
 }finally{if(fs.existsSync(stage)){
  assert(path.dirname(path.resolve(stage))===path.dirname(output)&&path.basename(stage).startsWith(path.basename(output)+'.preparing-'),'Unsafe staging cleanup path');
  fs.rmSync(stage,{recursive:true,force:true});
 }}
}
if(require.main===module){assert.equal(process.argv.length,5,'Usage: node PrepareBoundaryArea.cjs <baseline-data> <boundary-export> <fresh-output>');console.log(JSON.stringify(prepare(...process.argv.slice(2)),null,2));}
module.exports={plan,prepare};
