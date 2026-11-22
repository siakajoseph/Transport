'use strict';
const access = document.querySelector('#access');
const opening = document.querySelector('#opening');
const identity = document.querySelector('#identity');
const exit = document.querySelector('#exit');
const statusText = document.querySelector('#status');
let key = '';
let stops = [];

function reset() {
    key = '';
    stops = [];
    access.reset();
    opening.reset();
    opening.elements.station.replaceChildren();
    identity.textContent = '';
    identity.hidden = opening.hidden = exit.hidden = true;
    access.hidden = false;
    statusText.textContent = '';
}

async function api(path, data) {
    const options = {headers: {Authorization: 'Bearer ' + key}, cache: 'no-store'};
    if (data) {
        options.method = 'POST';
        options.headers['Content-Type'] = 'application/json';
        options.body = JSON.stringify(data);
    }
    const response = await fetch('/api/editeur/' + path, options);
    if (response.status === 401) {
        reset();
        throw new Error('Mandat inconnu, expiré ou révoqué.');
    }
    const result = await response.json().catch(() => null);
    if (!response.ok) throw new Error(result?.detail || 'Demande refusée. Réessayez ultérieurement.');
    return result;
}

function receipt(id) {
    opening.hidden = true;
    statusText.textContent = 'Compagnie ouverte. Identifiant de la première agence : ' + id;
}

async function submit(event, action) {
    event.preventDefault();
    const form = event.currentTarget;
    if (form.inert) return;
    form.inert = true;
    exit.disabled = true;
    statusText.textContent = '';
    try { await action(); }
    catch (error) { statusText.textContent = error.message; }
    finally { form.inert = false; exit.disabled = false; }
}

access.onsubmit = event => submit(event, async () => {
    key = access.elements.key.value.trim();
    const grant = await api('mandat');
    access.reset();
    access.hidden = true;
    identity.hidden = exit.hidden = false;
    identity.textContent = grant.operatorName + ' · Compagnie ' + grant.companyId +
        (grant.webIncluded ? ' · Vente web souscrite' : ' · Vente web non souscrite') +
        ' · Mandat valable jusqu’à ' + new Date(grant.expiresAt).toLocaleTimeString('fr');
    if (grant.agencyId) { receipt(grant.agencyId); return; }
    stops = grant.stops;
    opening.elements.station.replaceChildren(...stops.map((s, i) =>
        new Option('Modèle ' + s.template + ' · ' + s.name, i)));
    opening.hidden = false;
});

opening.onsubmit = event => submit(event, async () => {
    const fields = Object.fromEntries(new FormData(opening));
    const stop = stops[fields.station];
    delete fields.station;
    const reply = await api('ouverture', {...fields, template: stop.template, stop: stop.stop});
    receipt(reply.agencyId);
});

exit.onclick = reset;
if (!window.isSecureContext) {
    access.hidden = true;
    statusText.textContent = 'Ouvrez cet assistant en HTTPS ou sur la boucle locale.';
}
