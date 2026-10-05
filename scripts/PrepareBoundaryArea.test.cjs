'use strict';
const fs=require('node:fs'),os=require('node:os'),path=require('node:path'),test=require('node:test'),assert=require('node:assert/strict');
const {writeTable,parseTable}=require('./lib/tsv.cjs');
const {plan,prepare}=require('./PrepareBoundaryArea.cjs');
function fixture(run){
 const root=fs.mkdtempSync(path.join(os.tmpdir(),'boundary-area-')),source=path.join(root,'source'),candidate=path.join(root,'candidate'),data=path.join(candidate,'data');
 const table=(n,headers,objects)=>{const f=path.join(source,'global/excel',n+'.txt');fs.mkdirSync(path.dirname(f),{recursive:true});writeTable(f,{headers,rows:objects.map(o=>headers.map(h=>String(o[h]??''))),eol:'\r\n',hasFinalEol:true});};
 const scalar='Name Id Act DrlgType LevelType Depend OffsetX OffsetY Layer QuestFlag QuestFlagEx Portal Position SaveMonsters Quest Waypoint NumMon MonWndr MonSpcWalk Themes LevelGroup PreventTownPortal LevelName LevelWarp LevelEntry'.split(' ');
 const sizes=['','(N)','(H)'].flatMap(s=>['SizeX','SizeY','MonDen','MonUMin','MonUMax'].map(h=>h+s));
 const exits=Array.from({length:8},(_,i)=>['Vis','Warp','ObjGrp','ObjPrb'].map(h=>h+i)).flat();
 table('levels',[...scalar,...sizes,...exits],[{Name:'None',Id:0,Act:0,OffsetX:0,OffsetY:0},{Name:'Cave',Id:1,Act:0,DrlgType:2,LevelType:3,OffsetX:100,OffsetY:100,Layer:1,SizeX:20,SizeY:20,'SizeX(N)':20,'SizeY(N)':20,'SizeX(H)':20,'SizeY(H)':20,LevelName:'cave-label'}]);
 table('lvlprest',['Name','Def','LevelId','Populate','Files',...Array.from({length:6},(_,i)=>'File'+(i+1)),'Dt1Mask','SizeX','SizeY'],[{Name:'None',Def:0,LevelId:0,Dt1Mask:0},{Name:'Cave',Def:1,LevelId:1,Files:1,File1:'act1/caves/old.ds1',Dt1Mask:1}]);
 const slots=Array.from({length:32},(_,i)=>'File '+(i+1));
 table('lvltypes',['Name','Id','Act',...slots],Array.from({length:4},(_,i)=>({Name:'Type '+i,Id:i,Act:1,...Object.fromEntries(slots.map(s=>[s,'0'])),...(i===3?{'File 1':'act1/caves/cave.dt1'}:{})})));
 table('automap',['LevelName','TileName','Style','StartSequence','EndSequence','Cel1','Cel2','Cel3','Cel4'],[{LevelName:'1 Cave',TileName:'fl',Style:0,StartSequence:0,EndSequence:0,Cel1:1,Cel2:-1,Cel3:-1,Cel4:-1},{LevelName:'1 Crypt',Style:0}]);
 const put=(r,b)=>{const f=path.join(data,r);fs.mkdirSync(path.dirname(f),{recursive:true});fs.writeFileSync(f,b);};
 const map=Buffer.alloc(16);map.writeInt32LE(18,0);map.writeInt32LE(25,4);map.writeInt32LE(23,8);put('global/tiles/act1/test/map.ds1',map);
 const masks=[0,1,2,3,4,6,8,9,12],dt=Buffer.alloc(276+96*masks.length);dt.writeInt32LE(7,0);dt.writeInt32LE(6,4);dt.writeInt32LE(masks.length,268);dt.writeInt32LE(276,272);masks.forEach((m,i)=>{dt.writeInt32LE(63,276+i*96+24);dt.writeInt32LE(m,276+i*96+28);});
 put('global/tiles/act1/test/map.boundary.dt1',dt);put('global/tiles/act1/caves/cave.dt1',Buffer.from('test donor'));
 const project={Version:1,Name:'Test',Act:1,Width:26,Height:24,Preset:'data/hd/env/preset/act1/test/map.json',Map:'data/global/tiles/act1/test/map.ds1',Tileset:{Mask:0,Files:['data/global/tiles/act1/caves/cave.dt1','data/global/tiles/act1/test/map.boundary.dt1']}};
 put(project.Preset.slice(5),'{}');put(project.Preset.slice(5)+'.rle-project.json',JSON.stringify(project));
 const boundary={schemaVersion:4,act:1,dimensions:[26,24],tileset:project.Tileset,contourTiles:project.Tileset.Files[1],identities:[{SourceMain:0,SourceSub:0,Style:63,Masks:masks}]};fs.writeFileSync(path.join(candidate,'boundary.json'),JSON.stringify(boundary));
 function edit(n,fn){const f=path.join(source,'global/excel',n+'.txt'),t=parseTable(f);fn(t,h=>t.headers.indexOf(h));writeTable(f,t);}
 try{run({root,source,candidate,data,project,boundary,edit});}finally{assert(path.dirname(root)===path.resolve(os.tmpdir())&&path.basename(root).startsWith('boundary-area-'));fs.rmSync(root,{recursive:true,force:true});}
}
test('allocate dense IDs, geometry, safe slots and complete contour rows',()=>fixture(({source,candidate})=>{const p=plan(source,candidate);assert.equal(p.report.levelId,2);assert.equal(p.report.presetDef,2);assert.deepEqual(p.report.slots,[1,2]);assert.equal(p.report.dt1Mask,'3');assert.deepEqual(p.report.dimensions,[25,23]);assert.equal(p.report.automapAdded,9);assert.equal(p.report.unrelatedTableCells,'byte-exact');assert(p.report.world.origin[1]>=184);}));
test('all existing preset masks protect slots including unassigned entrances',()=>fixture(({source,candidate,edit})=>{edit('lvlprest',(t,c)=>t.rows[0][c('Dt1Mask')]='3');assert.deepEqual(plan(source,candidate).report.slots,[1,3]);}));
test('32nd slot remains unsigned',()=>fixture(({source,candidate,edit})=>{edit('lvlprest',(t,c)=>t.rows[0][c('Dt1Mask')]='2147483647');assert.equal(plan(source,candidate).report.dt1Mask,'2147483649');}));
test('no free slot rejects',()=>fixture(({source,candidate,edit})=>{edit('lvlprest',(t,c)=>t.rows[0][c('Dt1Mask')]='4294967295');assert.throws(()=>plan(source,candidate),/No unused DT1/);}));
test('sparse native IDs reject',()=>fixture(({source,candidate,edit})=>{edit('levels',(t,c)=>t.rows[1][c('Id')]='7');assert.throws(()=>plan(source,candidate),/physical data-row/);}));
test('registered DS1 path rejects duplicate area',()=>fixture(({source,candidate,edit})=>{edit('lvlprest',(t,c)=>t.rows[1][c('File1')]='act1/test/map.ds1');assert.throws(()=>plan(source,candidate),/already registered/);}));
test('conflicting automap art rejects',()=>fixture(({source,candidate,edit})=>{edit('automap',(t,c)=>{t.rows[0][c('Style')]='63';t.rows[0][c('StartSequence')]='1';t.rows[0][c('EndSequence')]='1';});assert.throws(()=>plan(source,candidate),/conflicts/);}));
test('matching automap art is reused',()=>fixture(({source,candidate,edit})=>{edit('automap',(t,c)=>{t.rows[0][c('Style')]='63';t.rows[0][c('StartSequence')]='1';t.rows[0][c('EndSequence')]='1';t.rows[0][c('Cel1')]='120';});const p=plan(source,candidate);assert.equal(p.report.automapReused,1);assert.equal(p.report.automapAdded,8);}));
test('fragmented automap group rejects',()=>fixture(({source,candidate,edit})=>{edit('automap',t=>t.rows.push(t.rows[0].slice()));assert.throws(()=>plan(source,candidate),/contiguous/);}));
test('receipt must match generated DT1',()=>fixture(({source,candidate,boundary})=>{boundary.identities[0].Style=62;fs.writeFileSync(path.join(candidate,'boundary.json'),JSON.stringify(boundary));assert.throws(()=>plan(source,candidate),/DT1 identities/);}));
test('new output preserves candidate and updates editor tileset context',()=>fixture(({source,candidate,root,project})=>{const before=fs.readFileSync(path.join(candidate,'boundary.json')),out=path.join(root,'out');const r=prepare(source,candidate,out);assert.equal(r.runtimeModified,false);assert.deepEqual(fs.readFileSync(path.join(candidate,'boundary.json')),before);const p=JSON.parse(fs.readFileSync(path.join(out,project.Preset+'.rle-project.json')));assert.equal(p.Tileset.Mask,3);assert.equal(p.LevelType,3);assert.throws(()=>prepare(source,candidate,out),/already exists/);}));
test('source-contained output rejects before writing',()=>fixture(({source,candidate})=>{const out=path.join(source,'bad');assert.throws(()=>prepare(source,candidate,out),/inside an input/);assert(!fs.existsSync(out));}));
