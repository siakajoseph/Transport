'use strict';
const form=document.getElementById('access'), result=document.getElementById('result');
if(!window.isSecureContext){
  form.hidden=true;
  result.textContent='Ouvrez cette page en HTTPS ou sur la boucle locale.';
}
form.onsubmit=async event=>{
  event.preventDefault();
  if(form.inert || !window.isSecureContext) return;
  form.inert=true;
  result.textContent='';
  const key=document.getElementById('key').value;
  form.reset();
  try{
    const response=await fetch('/api/editeur/support/diagnostic',{method:'POST',redirect:'error',cache:'no-store',
      headers:{'X-Transport-Support':key}});
    if(!response.ok){
      result.textContent=response.status===401?'Vérifiez la clé et l’autorisation auprès de la compagnie.':'Diagnostic indisponible.';
      return;
    }
    const data=await response.json();
    result.textContent=data.company+' · Profil '+data.mode+' · Agences : '+data.agencies+
      ' · Postes actifs : '+data.enabledDesks+' · Référence : '+data.request;
  }catch{result.textContent='Réponse indisponible. Vérifiez dans le journal de la compagnie si la consultation a été enregistrée.';}
  finally{form.inert=false;}
};
