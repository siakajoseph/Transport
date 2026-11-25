'use strict';
const byId=id=>document.getElementById(id);
const slot='transport.smsTest.line';
let busy=false;

function showLine(line){
  byId('inbox').hidden=!line;
  byId('create').hidden=!!line;
  byId('phone').textContent=line?.phone??'';
  byId('expiry').textContent=line?'Valable jusqu’au '+new Date(line.expiresAt).toLocaleString('fr-FR')+'.':'';
  const list=byId('messages');
  list.replaceChildren();
  for(const message of line?.messages??[]){
    const item=document.createElement('li'), code=document.createElement('strong');
    code.textContent=message.code;
    item.append('Colis '+message.reference+' · '+message.destination+' · '+
      new Date(message.arrivedAt).toLocaleString('fr-FR')+' · À régler : '+message.due+' FCFA · Code de retrait : ',code);
    list.append(item);
  }
  byId('status').textContent=line&&!line.messages.length?'Aucun colis en attente de retrait.':'';
}

async function request(path,method,key){
  return fetch('/api/sms-test/'+path,{method,headers:{'X-Transport-Telephone':key},cache:'no-store',redirect:'error'});
}

async function loadMessages(key){
  const response=await request('messages','GET',key);
  if(response.status===401){
    sessionStorage.removeItem(slot);
    showLine(null);
    byId('status').textContent='Cette ligne est fermée ou expirée.';
    return;
  }
  if(!response.ok) throw new Error('Lecture des messages indisponible.');
  showLine(await response.json());
}

async function run(action){
  if(busy) return;
  busy=true;
  byId('create').disabled=true;
  byId('inbox').inert=true;
  try{await action();}
  catch(error){byId('messages').replaceChildren();byId('status').textContent=error.message;}
  finally{busy=false;byId('create').disabled=false;byId('inbox').inert=false;}
}

function refresh(){
  return run(async()=>{
    const key=sessionStorage.getItem(slot);
    if(key) await loadMessages(key);
  });
}
byId('create').onclick=()=>run(async()=>{
  let key=sessionStorage.getItem(slot);
  if(!key){
    key=Array.from(crypto.getRandomValues(new Uint8Array(32)),x=>x.toString(16).padStart(2,'0')).join('');
    sessionStorage.setItem(slot,key);
  }
  const result=await request('ligne','POST',key);
  if(!result.ok) throw new Error(result.status===404?'Le simulateur SMS est désactivé sur ce serveur.':'Création indisponible. Réessayez.');
  await loadMessages(key);
});
byId('refresh').onclick=refresh;
byId('close').onclick=()=>run(async()=>{
  if(!confirm('Fermer ce numéro et perdre l’accès à ses codes de retrait ?')) return;
  const response=await request('ligne','DELETE',sessionStorage.getItem(slot));
  if(!response.ok) throw new Error('Fermeture non confirmée. Réessayez.');
  sessionStorage.removeItem(slot);
  showLine(null);
  byId('status').textContent='Ligne fermée.';
});
if(window.isSecureContext){
  refresh();
  setInterval(()=>{if(!document.hidden && !byId('inbox').hidden) refresh();},15000);
  window.addEventListener('pageshow',e=>{if(e.persisted) refresh();});
}else{
  byId('status').textContent='Ouvrez le téléphone de recette en HTTPS ou sur la boucle locale.';
}
