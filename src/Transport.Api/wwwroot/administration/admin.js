'use strict';
const $=id=>document.getElementById(id);
const labels={vente:'Vente',fret:'Fret',embarquement:'Embarquement',depart:'Départ'};
let token='', expiry, generation=0, agencies=[], staff=[], selected=null, draft=null;
const hex=bytes=>Array.from(bytes,b=>b.toString(16).padStart(2,'0')).join('');
const status=message=>{$('status').textContent=message;};
function hideKey(){ $('personal').textContent=''; $('delivery').hidden=true; }
function reset(){
  token=''; generation++; clearTimeout(expiry); agencies=[]; staff=[]; selected=null; draft=null;
  $('staff').replaceChildren(); $('events').replaceChildren(); $('agency').replaceChildren(); $('company').textContent='';
  $('support').open=false; $('supportList').replaceChildren();
  $('goods').open=false; $('goodsList').replaceChildren(); $('newGoods').reset();
  $('refunds').open=false; $('refunds').hidden=true; $('cases').replaceChildren();
  $('edit').reset(); $('login').reset(); $('search').value=''; hideKey();
  $('office').hidden=true; $('logout').hidden=true; $('editor').hidden=true; $('signin').hidden=false; $('audit').open=false;
}
async function api(path,method='GET',data){
  const version=generation, headers={};
  if(token) headers.Authorization='Bearer '+token;
  if(data) headers['Content-Type']='application/json';
  let response;
  try{response=await fetch('/api/admin/'+path,{method,headers,body:data?JSON.stringify(data):undefined,cache:'no-store'});}
  catch{throw new Error('Serveur injoignable. Vérifiez la connexion avant de réessayer.');}
  const body=response.status===204?null:await response.json().catch(()=>null);
  if(version!==generation) throw new Error('Session terminée.');
  if(!response.ok){
    if(response.status===401 && token){ reset(); throw new Error('Session expirée. Reconnectez-vous.'); }
    throw new Error(body?.detail || (response.status===429?'Trop de tentatives. Patientez avant de réessayer.':
      response.status===404?'Administration indisponible sur ce serveur.':'Demande refusée.'));
  }
  return body;
}
function render(){
  const q=$('search').value.trim().toLocaleLowerCase('fr');
  const root=$('staff'); root.replaceChildren();
  for(const agent of staff){
    const agency=agencies.find(a=>a.id===agent.agency);
    if(!(agent.name+' '+(agency?.name??'')).toLocaleLowerCase('fr').includes(q)) continue;
    const row=document.createElement('article'), info=document.createElement('div'), detail=document.createElement('small');
    info.textContent=agent.name; detail.textContent=[agency?.name,agent.enabled?'Actif':'Désactivé',agent.rights.map(r=>labels[r]).join(', ')].join(' · ');
    info.append(detail);
    const button=document.createElement('button'); button.className='quiet'; button.textContent='Modifier';
    button.addEventListener('click',()=>openEditor(agent)); row.append(info,button); root.append(row);
  }
  if(!root.childElementCount) root.textContent='Aucun agent correspondant.';
}
async function load(){
  const data=await api('annuaire'); agencies=data.agencies; staff=data.staff;
  $('company').textContent=data.company;
  $('refunds').hidden=!data.canRefund;
  if (!data.canRefund) { $('refunds').open=false; $('cases').replaceChildren(); }
  $('agency').replaceChildren(...agencies.map(a=>new Option(a.entity+' · '+a.name,a.id)));
  $('add').disabled=!agencies.length; render();
}
function openEditor(agent){
  selected=agent; draft=null; hideKey(); $('editor').hidden=false; $('edit').reset();
  $('editTitle').textContent=agent?'Modifier le compte':'Créer un agent'; $('name').value=agent?.name??'';
  $('agency').value=agent?.agency??agencies[0]?.id??''; $('agency').disabled=!!agent;
  $('activeRow').hidden=!agent; $('active').checked=agent?.enabled??true;
  for(const box of $('edit').querySelectorAll('[name=rights]')) box.checked=agent?.rights.includes(box.value)??false;
  $('name').focus(); status('');
}
async function history(){
  const events=await api('historique'); $('events').replaceChildren();
  for(const item of events){
    const row=document.createElement('li'), before=item.previous, after=item.current;
    const describe=a=>a.name+' · '+a.rights.map(r=>labels[r]).join(', ')+' · '+(a.enabled?'actif':'désactivé');
    row.textContent=new Date(item.at).toLocaleString('fr-FR')+' · '+item.admin+' · '+
      (before?describe(before)+' → ':'Création · ')+describe(after); $('events').append(row);
  }
  if(!events.length){const li=document.createElement('li');li.textContent='Aucune modification enregistrée.';$('events').append(li);}
}
async function save(){
  const name=$('name').value.trim(), agency=$('agency').value;
  const rights=[...$('edit').querySelectorAll('[name=rights]:checked')].map(x=>x.value);
  if(!name || !rights.length) throw new Error('Renseignez le nom et sélectionnez au moins une habilitation.');
  if(selected){
    const enabled=$('active').checked;
    if(selected.enabled && !enabled && !confirm('Désactiver le compte de '+selected.name+' ?')) return;
    await api('agents/'+selected.id,'PUT',{name,rights,enabled,revision:selected.revision});
    status('Compte mis à jour.');
  }else{
    if(!draft){
      const key=hex(crypto.getRandomValues(new Uint8Array(32)));
      const keyHash=hex(new Uint8Array(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(key))));
      draft={key,keyHash,id:crypto.randomUUID()};
    }
    await api('agents','POST',{id:draft.id,agency,name,rights,keyHash:draft.keyHash});
    $('personal').textContent=draft.key; $('delivery').hidden=false; draft=null; status('Compte créé.');
  }
  $('editor').hidden=true; selected=null; await load(); if($('audit').open) await history();
}
async function loadRefunds(){
  const cases=$('cases');
  cases.replaceChildren();
  const rows=await api('rapprochement');
  if(!rows.length) { cases.textContent='Aucun écart en attente.'; return; }
  for(const item of rows){
    const form=document.createElement('form');
    const details=document.createElement('p');
    details.textContent='Paiement '+item.payment+' · Reçu '+item.amount+' '+item.currency+
      ' · Attendu '+item.expectedXof+' XOF · '+new Date(item.receivedAt).toLocaleString('fr');
    const label=document.createElement('label');
    label.textContent='Motif du remboursement simulé';
    const reason=document.createElement('input');
    reason.required=true; reason.maxLength=200; label.append(reason);
    const button=document.createElement('button');
    button.textContent='Enregistrer le remboursement simulé';
    form.append(details,label,button); cases.append(form);
    form.addEventListener('submit',event=>{
      event.preventDefault();
      perform(async()=>{
        const refund=await api('rapprochement/'+item.payment+'/remboursement','POST',{reason:reason.value.trim()});
        status('Remboursement simulé enregistré : '+refund.id);
        await loadRefunds();
      });
    });
  }
}
$('refunds').ontoggle=()=>{if($('refunds').open && token) perform(loadRefunds);};
$('reloadRefunds').onclick=()=>perform(loadRefunds);
// Serialize user actions so a reply always applies to the form that submitted it.
let busy=false;
async function perform(action){
  if(busy) return;
  busy=true; $('office').inert=true; $('signin').inert=true;
  try{await action();}catch(error){status(error.message);}finally{busy=false; $('office').inert=false; $('signin').inert=false;}
}
$('login').addEventListener('submit',event=>{
  event.preventDefault(); perform(async()=>{
    const values=Object.fromEntries(new FormData($('login')));
    const reply=await api('session','POST',values); token=reply.token; $('login').reset();
    expiry=setTimeout(()=>{reset();status('Session expirée. Reconnectez-vous.');},20*60*1000);
    $('signin').hidden=true; $('office').hidden=false; $('logout').hidden=false;
    await load(); status(agencies.length?'':'Créez une agence avant d’inscrire un agent.');
  });
});
$('logout').onclick=()=>perform(async()=>{try{await api('session','DELETE');}finally{reset();status('Session fermée sur ce navigateur.');}});
$('add').onclick=()=>openEditor(null); $('dismiss').onclick=hideKey; $('search').oninput=render;
$('refresh').onclick=()=>perform(async()=>{await load(); $('editor').hidden=true; selected=null; draft=null; if($('audit').open) await history(); status('Annuaire actualisé.');});
$('edit').onsubmit=event=>{event.preventDefault(); perform(save);};
$('audit').ontoggle=()=>{if($('audit').open && token) perform(history);};
if(!window.isSecureContext){$('login').hidden=true;status('Ouvrez l’administration en HTTPS ou sur la boucle locale.');}

async function loadGoods(){
  const entries=await api('marchandises');
  $('goodsList').replaceChildren();
  for(const item of entries){
    const row=document.createElement('li'), button=document.createElement('button');
    row.append(item.label+(item.permitted?' · Autorisée ':' · Interdite '));
    button.textContent=item.permitted?'Interdire':'Autoriser';
    button.onclick=()=>perform(()=>saveGoods({...item,permitted:!item.permitted}));
    const audit=document.createElement('button'), history=document.createElement('ol');
    audit.textContent='Historique'; audit.className='quiet';
    let before=null;
    audit.onclick=()=>perform(async()=>{
      const params=new URLSearchParams({label:item.label});
      if(before!==null) params.set('before',before);
      const page=await api('marchandises/historique?'+params);
      for(const event of page.events){
        const entry=document.createElement('li');
        const state=allowed=>allowed?'autorisée':'interdite';
        const previous=event.previous===null?'Création':state(event.previous);
        entry.textContent='Rév. '+event.revision+' · '+new Date(event.at).toLocaleString('fr')+
          ' · '+event.actor+' · '+previous+' → '+state(event.permitted);
        history.append(entry); before=event.revision;
      }
      if(!history.children.length) history.textContent='Aucune modification enregistrée depuis le portail.';
      audit.hidden=!page.more; audit.textContent='Modifications précédentes';
    });
    row.append(button,audit,history); $('goodsList').append(row);
  }
}
async function saveGoods(change){
  await api('marchandises','POST',change);
  $('newGoods').reset();
  await loadGoods(); status('Catalogue fret mis à jour.');
}
$('goods').ontoggle=()=>{if($('goods').open && token) perform(loadGoods);};
$('loadGoods').onclick=()=>perform(loadGoods);
$('newGoods').onsubmit=event=>{
  event.preventDefault();
  perform(()=>saveGoods({label:$('goodsName').value.trim(),permitted:$('goodsAllowed').checked,revision:0}));
};

async function loadSupport(){
  const requests=await api('support');
  const list=$('supportList');
  list.replaceChildren();
  for(const request of requests){
    const row=document.createElement('li');
    const date=value=>new Date(value).toLocaleString('fr-FR');
    const peer=ip=>ip?' (IP '+ip+')':'';
    row.append(request.technician+' · Poste déclaré : '+(request.station??'Non renseigné')+' · '+request.reason+' · '+request.state+' · Échéance '+date(request.expiresAt));
    if(request.approvedBy) row.append(' · Accord de '+request.approvedBy+' le '+date(request.approvedAt)+peer(request.approvedIp));
    if(request.revokedBy) row.append(' · Retrait par '+request.revokedBy+' le '+date(request.revokedAt)+peer(request.revokedIp));
    if(request.consultedAt) row.append(' · Consultation le '+date(request.consultedAt)+peer(request.consultedIp));
    function action(label,decision){
      const button=document.createElement('button');
      button.textContent=label;
      button.onclick=()=>perform(async()=>{
        await api('support/'+request.id+'/'+decision,'POST');
        await loadSupport();
      });
      row.append(button);
    }
    if(request.state==='À examiner') action('Autoriser une consultation','autoriser');
    if(['À examiner','Autorisé'].includes(request.state)) action('Retirer l’accès','revoquer');
    list.append(row);
  }
  if(!requests.length) list.textContent='Aucune demande de diagnostic.';
}
$('support').ontoggle=()=>{if($('support').open && token) perform(loadSupport);};
$('loadSupport').onclick=()=>perform(loadSupport);
