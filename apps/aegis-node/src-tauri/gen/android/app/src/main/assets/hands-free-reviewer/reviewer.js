/* Aegis offline reviewer. Dependency-free ZIP STORE writer (CRC32, UTF-8). */
(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const events = JSON.parse(new TextDecoder().decode(Uint8Array.from(atob(window.AEGIS_EVENTS_BASE64 || 'W10='), c => c.charCodeAt(0))));
  const names = {correct:'Correta', false_positive:'Falso positivo', uncertain:'Incerta'};
  const allowed = new Set(Object.keys(names));
  const storageKey = `aegis-activation-labels-v1:${events.map(e=>e.id).join(':')}`;
  let labels = {}, labelTimes = {}, position = 0, selectedFiles = new Map();
  function notice(text){ $('notice').textContent=text; }
  try { labels=JSON.parse(localStorage.getItem(storageKey)||'{}');labelTimes=JSON.parse(localStorage.getItem(storageKey+':times')||'{}'); } catch { notice('Este navegador não mantém rótulos locais. Exporte o dataset antes de fechar.'); }
  function current(){return events[position];}
  function render(){
    const e=current(), count=events.filter(x=>allowed.has(labels[x.id])).length;
    $('progress').textContent=`${count} / ${events.length} classificadas`;
    $('previous').disabled=!position; $('next').disabled=position>=events.length-1;
    $('export').disabled=!count;
    if(!e)return;
    $('date').textContent=e.activatedAt||'Data desconhecida';
    $('identity').textContent=`Modelo: ${e.modelVersion??'desconhecido'} · score instantâneo: ${e.instantaneousScore??e.score??'desconhecido'} · decisão: ${e.decisionScore??'desconhecido'}`;
    $('technical').textContent=`${e.finalState??'desconhecido'} · ${e.endReason??'motivo desconhecido'} · STT: ${e.transcriptionStatus??'desconhecido'}${e.snapshotInProgress?' · snapshot durante sessão ativa':''}`;
    $('event-id').textContent=`ID: ${e.id}`; $('position').textContent=`${position+1} / ${events.length}`;
    for(const [id,key] of [['context','triggerContext'],['post','postActivation']]) {const a=$(id);a.pause();if(e[key]?.path)a.src=e[key].path;else a.removeAttribute('src');a.load();}
    $('label').textContent=names[labels[e.id]]||'Não classificada';
    document.querySelectorAll('[data-label]').forEach(button=>button.setAttribute('aria-pressed',String(button.dataset.label===labels[e.id])));
  }
  function label(value){if(!current())return;if(value){labels[current().id]=value;labelTimes[current().id]=new Date().toISOString();}else{delete labels[current().id];delete labelTimes[current().id];}try{localStorage.setItem(storageKey,JSON.stringify(labels));localStorage.setItem(storageKey+':times',JSON.stringify(labelTimes));}catch{notice('Rótulos apenas em memória. Exporte antes de fechar.');}render();}
  function move(delta){position=Math.max(0,Math.min(events.length-1,position+delta));render();}
  document.querySelectorAll('[data-label]').forEach(b=>b.addEventListener('click',()=>label(b.dataset.label)));
  $('clear').addEventListener('click',()=>label(null));$('previous').addEventListener('click',()=>move(-1));$('next').addEventListener('click',()=>move(1));
  document.addEventListener('keydown',event=>{if(/INPUT|SELECT|TEXTAREA/.test(event.target.tagName))return;const actions={ArrowLeft:()=>move(-1),ArrowRight:()=>move(1),'1':()=>label('correct'),'2':()=>label('false_positive'),'3':()=>label('uncertain'),'0':()=>label(null)};if(actions[event.key]){event.preventDefault();actions[event.key]();}});
  $('files').addEventListener('change',event=>{selectedFiles=new Map();for(const file of event.target.files){const path=file.webkitRelativePath;const withoutRoot=path.slice(path.indexOf('/')+1);selectedFiles.set(withoutRoot,file);}notice(`${selectedFiles.size} arquivos locais selecionados.`);});
  const encoder=new TextEncoder(), table=Uint32Array.from({length:256},(_,n)=>{for(let k=0;k<8;k++)n=n&1?0xedb88320^(n>>>1):n>>>1;return n>>>0;});
  function crc(bytes){let value=0xffffffff;for(const b of bytes)value=table[(value^b)&255]^(value>>>8);return(value^0xffffffff)>>>0;}
  function zip(entries){if(entries.length>65535)throw new Error('ZIP32 suporta no máximo 65.535 arquivos. Exporte uma seleção menor.');let total=22;for(const entry of entries){const length=encoder.encode(entry.name).length;if(length>65535||entry.bytes.length>0xffffffff)throw new Error('Arquivo excede os limites ZIP32.');total+=76+length*2+entry.bytes.length;if(total>0xffffffff)throw new Error('Dataset excede o limite ZIP32 de 4 GiB.');}let offset=0;const local=[],central=[];for(const {name,bytes} of entries){const filename=encoder.encode(name),sum=crc(bytes),header=new Uint8Array(30+filename.length),view=new DataView(header.buffer);view.setUint32(0,0x04034b50,true);view.setUint16(4,20,true);view.setUint16(6,0x800,true);view.setUint32(14,sum,true);view.setUint32(18,bytes.length,true);view.setUint32(22,bytes.length,true);view.setUint16(26,filename.length,true);header.set(filename,30);local.push(header,bytes);const directory=new Uint8Array(46+filename.length),dv=new DataView(directory.buffer);dv.setUint32(0,0x02014b50,true);dv.setUint16(4,20,true);dv.setUint16(6,20,true);dv.setUint16(8,0x800,true);dv.setUint32(16,sum,true);dv.setUint32(20,bytes.length,true);dv.setUint32(24,bytes.length,true);dv.setUint16(28,filename.length,true);dv.setUint32(42,offset,true);directory.set(filename,46);central.push(directory);offset+=header.length+bytes.length;}const centralSize=central.reduce((n,b)=>n+b.length,0),end=new Uint8Array(22),ev=new DataView(end.buffer);ev.setUint32(0,0x06054b50,true);ev.setUint16(8,entries.length,true);ev.setUint16(10,entries.length,true);ev.setUint32(12,centralSize,true);ev.setUint32(16,offset,true);return new Blob([...local,...central,end],{type:'application/zip'});}
  function csvCell(value){return '"'+String(value??'').replaceAll('"','""')+'"';}
  function dated(date){const pad=n=>String(n).padStart(2,'0'),offset=-date.getTimezoneOffset(),sign=offset>=0?'+':'-';return `${date.getFullYear()}-${pad(date.getMonth()+1)}-${pad(date.getDate())}_${pad(date.getHours())}-${pad(date.getMinutes())}-${pad(date.getSeconds())}${sign}${pad(Math.floor(Math.abs(offset)/60))}${pad(Math.abs(offset)%60)}`;}
  async function buildDataset(){
    const rows=[],entries=[],generatedAt=new Date().toISOString(),seen=new Set();
    for(const event of events){const humanLabel=labels[event.id];if(!allowed.has(humanLabel))continue;
      const groupId=event.groupId??event.correlation?.sessionId??event.sessionId??event.id;
      const row={...event,humanLabel,labeledBy:'owner',labeledAt:labelTimes[event.id]??null,datasetGeneratedAt:generatedAt,provenance:{source:'Aegis Android local activation collection',sourceExportedAt:event.exportedAt??null,originalEventId:event.id},groupId,groupUnknown:!(event.groupId??event.correlation?.sessionId??event.sessionId),split:null};
      for(const key of ['triggerContext','postActivation']){const audio=event[key];if(!audio?.path)continue;const file=selectedFiles.get(audio.path);if(!file)throw new Error(`Selecione a pasta extraída contendo ${audio.path}.`);if(!seen.has(audio.path)){const bytes=new Uint8Array(await file.arrayBuffer());if(crypto.subtle&&audio.sha256){const digest=Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256',bytes)),b=>b.toString(16).padStart(2,'0')).join('');if(digest!==audio.sha256)throw new Error(`Hash diferente para ${audio.path}.`);}entries.push({name:audio.path,bytes});seen.add(audio.path);}}
      rows.push(row);
    }
    if(!rows.length)throw new Error('Classifique uma ocorrência antes de exportar.');
    entries.push({name:'dataset.jsonl',bytes:encoder.encode(rows.map(r=>JSON.stringify(r)).join('\n')+'\n')});
    const columns=['id','humanLabel','labeledAt','activatedAt','endedAt','groupId','groupUnknown','modelVersion','modelSha256','appVersion','build','triggerContextPath','triggerContextSha256','postActivationPath','postActivationSha256','sourceExportedAt','datasetGeneratedAt'];
    const csv=rows.map(r=>({...r,triggerContextPath:r.triggerContext?.path,triggerContextSha256:r.triggerContext?.sha256,postActivationPath:r.postActivation?.path,postActivationSha256:r.postActivation?.sha256,sourceExportedAt:r.exportedAt}));
    entries.push({name:'dataset.csv',bytes:encoder.encode(columns.join(',')+'\n'+csv.map(r=>columns.map(k=>csvCell(r[k])).join(',')).join('\n')+'\n')});
    entries.push({name:'README.txt',bytes:encoder.encode('Aegis dataset schema 1. Human labels only; uncertain is not a negative. No training performed. Keep all rows sharing groupId in the same future train/validation/test split. split=null means no assignment. Dataset JSONL preserves the complete source event and hashes. WAV paths remain relative. Private local recordings.\n')});
    return {blob:zip(entries),filename:`aegis-dataset-${dated(new Date())}.zip`,rows};
  }
  $('export').addEventListener('click',async()=>{try{$('export').disabled=true;notice('Preparando dataset local…');const result=await buildDataset(),url=URL.createObjectURL(result.blob),a=document.createElement('a');a.href=url;a.download=result.filename;a.click();setTimeout(()=>URL.revokeObjectURL(url),30000);notice(`${result.rows.length} ocorrências exportadas. Nenhum treinamento executado.`);}catch(error){notice(error.message);}finally{render();}});
  // Small pure surface for browser regression tests; no privileged/native interface.
  window.AegisReviewer={zip,buildDataset,events,get labels(){return {...labels};}};
  render();
})();
