'use strict';
// Called only inside the connection fixture's fresh offline output copy.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const {parseTable,serializeTable,writeTable,ENCODING}=require('./lib/tsv.cjs');
if(process.argv[2]==='--verify'){
  assert.equal(process.argv.length,5,'Expected registered data and fresh fixture data');
  const baseline=path.resolve(process.argv[3]),candidate=path.resolve(process.argv[4]);
  const expected=[['levels','Id',1,'Vis7','0','138'],['levels','Id',1,'Warp7','-1','6'],
    ['levels','Id',138,'Vis7','0','1'],['levels','Id',138,'Warp7','-1','6'],['lvlprest','Def',1092,'Scan','0','1']];
  for(const name of ['levels','lvlprest','lvltypes','automap']){
    const beforeFile=path.join(baseline,'global/excel',name+'.txt'),afterFile=path.join(candidate,'global/excel',name+'.txt');
    const before=parseTable(beforeFile),after=parseTable(afterFile);
    assert.equal(serializeTable(before),fs.readFileSync(beforeFile,ENCODING));
    assert.equal(serializeTable(after),fs.readFileSync(afterFile,ENCODING));
    for(const [table,key,id,header,from,to] of expected.filter(e=>e[0]===name)){
      const col=after.headers.indexOf(header),keyCol=after.headers.indexOf(key);
      const rows=after.rows.filter(r=>r[keyCol]===String(id));assert.equal(rows.length,1);
      assert.equal(before.rows.find(r=>r[before.headers.indexOf(key)]===String(id))[before.headers.indexOf(header)],from);
      assert.equal(rows[0][col],to);rows[0][col]=from;
    }
    assert.equal(serializeTable(after),fs.readFileSync(beforeFile,ENCODING),'Unrelated table bytes changed: '+name);
  }
  fs.writeFileSync(path.join(candidate,'..','connection-table-changes.json'),JSON.stringify({changes:expected.map(([table,key,id,header,before,after])=>({table,key,id,header,before,after})),unrelatedBytesExact:true,native:'not run'},null,2));
  process.exit(0);
}
const root=path.resolve(process.argv[2]||'');
assert(process.argv.length===3,'Expected fresh fixture data root');
const changes=[];
for(const [table,key,updates] of [
  ['levels','Id',[[1,{Vis7:'0',Warp7:'6'}],[138,{Vis7:'0',Warp7:'6'}]]],
  ['lvlprest','Def',[[1092,{Scan:'1'}]]]
]){
  const file=path.join(root,'global/excel',table+'.txt'),raw=fs.readFileSync(file,ENCODING),t=parseTable(file);
  assert.equal(serializeTable(t),raw);
  assert.equal(t.eol,'\r\n','Expected governed CRLF table');
  if(table==='lvlprest'){
    const areas=t.rows.filter(r=>r[t.headers.indexOf('LevelId')]==='1');
    assert(areas.length>0,'Missing town presets');
    assert(areas.every(r=>r[t.headers.indexOf('Scan')]==='1'),'Town preset scan must already be enabled');
  }
  const edits=[];
  for(const [id,cells] of updates){
    const rows=t.rows.filter(r=>r[t.headers.indexOf(key)]===String(id));assert.equal(rows.length,1);
    for(const [header,value] of Object.entries(cells)){
      const col=t.headers.indexOf(header);assert(col>=0);
      const before=rows[0][col];
      assert.equal(before,header==='Warp7'?'-1':'0',`Unexpected ${table}/${id}/${header}`);
      rows[0][col]=value;edits.push({table,id,header,before,after:value});
    }
  }
  const check=structuredClone(t);
  for(const edit of edits)check.rows.find(r=>r[check.headers.indexOf(key)]===String(edit.id))[check.headers.indexOf(edit.header)]=edit.before;
  assert.equal(serializeTable(check),raw,'Unrelated table bytes changed');
  writeTable(file,t);assert.equal(serializeTable(parseTable(file)),fs.readFileSync(file,ENCODING));changes.push(...edits);
}
fs.writeFileSync(path.join(root,'..','endpoint-table-changes.json'),JSON.stringify({changes,unrelatedBytesExact:true,native:'not run'},null,2));
