const form = document.querySelector('form');
const result = document.querySelector('output');
const route = document.querySelector('#route');
const buttons = [...form.querySelectorAll('button')];
async function loadRoutes() {
  const response = await fetch('/api/trajets');
  if (!response.ok) throw new Error();
  for (const trip of await response.json()) {
    const when = new Date(trip.departure).toLocaleString('fr-FR', {timeZone:'Africa/Abidjan'});
    route.add(new Option(trip.label+' · '+when+' · voyage '+trip.tripId, JSON.stringify(trip)));
  }
  if (!route.length) result.textContent = 'Aucun départ ouvert.';
}
loadRoutes().catch(()=>{result.textContent='Horaires indisponibles. Actualiser la page.';});
form.addEventListener('submit', async event=>{
  event.preventDefault();
  if (!route.value) return;
  buttons.forEach(b=>b.disabled=true);
  try {
    const trip=JSON.parse(route.value);
    const parcel={tripId:trip.tripId,origin:trip.origin,destination:trip.destination,
      grams:Math.round(form.querySelector('#weight').valueAsNumber*1000)};
    for (const name of ['lengthCm','widthCm','heightCm']) parcel[name]=document.getElementById(name).valueAsNumber;
    const book=event.submitter?.value==='book';
    if (book) {
      const fingerprint=JSON.stringify(parcel);
      const saved=JSON.parse(sessionStorage.getItem('transport-fret') || 'null');
      parcel.requestId=saved?.fingerprint===fingerprint?saved.key:crypto.randomUUID();
      sessionStorage.setItem('transport-fret',JSON.stringify({fingerprint,key:parcel.requestId}));
    }
    const response=await fetch('/api/fret/'+(book?'preinscriptions':'devis'),{
      method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(parcel)});
    const data=await response.json();
    if (!response.ok) throw new Error(data.detail || 'Demande refusée.');
    result.textContent='Poids facturable : '+data.kg+' kg · Tarif : '+data.xof.toLocaleString('fr-FR')+' FCFA.'+
      (book?' Référence à présenter en agence : '+data.reference:'');
    if (book) { document.querySelector('#tracking-ref').value=data.reference; form.reset(); sessionStorage.removeItem('transport-fret'); }
  } catch(error) { result.textContent=error.message+' Vérifier les informations puis réessayer.'; }
  finally {buttons.forEach(b=>b.disabled=false);}
});

const tracking = document.querySelector('#tracking');
tracking.addEventListener('submit', async event => {
  event.preventDefault();
  const reference = document.querySelector('#tracking-ref').value.trim();
  const output = document.querySelector('#tracking-result');
  const button = tracking.querySelector('button');
  if (!reference || button.disabled) return;
  button.disabled = true;
  output.textContent = 'Recherche en cours…';
  try {
    const response = await fetch('/api/fret/suivi/' + encodeURIComponent(reference));
    if (response.status === 429) output.textContent = 'Trop de recherches. Réessayez dans une minute.';
    else if (response.status === 404) output.textContent = 'Référence de colis introuvable.';
    else {
      if (!response.ok) throw new Error();
      const parcel = await response.json();
      output.textContent = [parcel.state, parcel.area].filter(Boolean).join(' · ');
    }
  } catch { output.textContent = 'Suivi indisponible. Réessayez dans un instant.'; }
  finally { button.disabled = false; }
});
