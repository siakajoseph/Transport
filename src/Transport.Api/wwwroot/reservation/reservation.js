const form = document.querySelector('form');
const trip = document.querySelector('#trip');
const seat = document.querySelector('#seat');
const submit = document.querySelector('button');
const statusText = document.querySelector('[role=status]');
let routes = [];
let lastRequest;
const pay = document.querySelector('#pay');
const proof = document.querySelector('#proof');
let orderRef;

async function paySandbox() {
    pay.disabled = true;
    try {
        const url = 'reservations/' + orderRef + '/simulation';
        sessionStorage.setItem('transport.pendingSandboxPayment', orderRef);
        const receipt = await api(url, {method: 'POST'});
        const result = await api(url + '/' + receipt.payment, {method: 'POST'});
        pay.hidden = true;
        document.querySelector('#finish').hidden = false;
        if (result.outcome === 'issued') {
            document.querySelector('#qr').src = 'data:image/png;base64,' + result.qr;
            proof.querySelector('code').textContent = result.token;
            proof.hidden = false;
            statusText.textContent = 'Paiement de test confirmé. Conservez ce code pour l’embarquement.';
        } else {
            statusText.textContent = 'Paiement de test à rapprocher. Référence : ' + receipt.payment;
        }
        // Keep the reference in this tab so the ticket can be recovered after a reload.
    } catch (error) {
        statusText.textContent = error.message;
        if (error.status === 404 || error.status === 409) {
            sessionStorage.removeItem('transport.pendingSandboxPayment');
            pay.hidden = true;
            document.querySelector('#finish').hidden = false;
        }
    } finally { pay.disabled = false; }
}
pay.onclick = paySandbox;
document.querySelector('#finish').onclick = () => {
    sessionStorage.removeItem('transport.pendingSandboxPayment');
    location.reload();
};


async function api(path, options) {
    const response = await fetch('/api/' + path, options);
    if (!response.ok) {
        const detail = await response.json().catch(() => ({}));
        const error = new Error(detail.detail || 'Service momentanément indisponible.');
        error.status = response.status;
        throw error;
    }
    return response.json();
}

async function seats() {
    const index = trip.value;
    const route = routes[index];
    seat.replaceChildren();
    submit.disabled = true;
    if (!route) return;
    try {
        const query = new URLSearchParams({tripId: route.tripId, origin: route.origin, destination: route.destination});
        const choices = await api('sieges?' + query);
        if (index !== trip.value) return;
        for (const item of choices.filter(item => item.available))
            seat.add(new Option(item.number, item.number));
        submit.disabled = !seat.length;
        statusText.textContent = seat.length ? '' : 'Ce trajet est complet.';
    } catch (error) { statusText.textContent = error.message; }
}

trip.onchange = seats;
form.onsubmit = async event => {
    event.preventDefault();
    const {tripId, origin, destination} = routes[trip.value];
    const fields = {tripId, origin, destination, number: Number(seat.value),
        passenger: document.querySelector('#passenger').value.trim()};
    const key = JSON.stringify(fields);
    if (lastRequest?.key !== key) lastRequest = {key, reference: crypto.randomUUID()};
    for (const field of form.elements) field.disabled = true;
    try {
        const order = await api('reservations', {method: 'POST', headers: {'Content-Type': 'application/json'},
            body: JSON.stringify({...fields, reference: lastRequest.reference})});
        statusText.textContent = order.company + ' · Siège ' + fields.number + ' réservé jusqu’à ' +
            new Date(order.expiresAt).toLocaleTimeString('fr') + '. Montant à payer : ' + order.xof +
            ' FCFA. Référence : ' + order.reference + '. Aucun paiement effectué.';
        orderRef = order.reference;
        sessionStorage.removeItem('transport.pendingSandboxPayment');
        proof.hidden = true;
        pay.textContent = 'Payer sur le compte marchand de test';
        pay.hidden = !order.sandbox;
    } catch (error) {
        for (const field of form.elements) field.disabled = false;
        if (error.status === 403) form.hidden = true;
        if (error.status === 409) {
            lastRequest = undefined;
            await seats();
        }
        statusText.textContent = error.message;
    }
};

(async () => {
    try {
        orderRef = sessionStorage.getItem('transport.pendingSandboxPayment');
        if (orderRef) {
            statusText.textContent = 'Retrouvez votre paiement de test dans cet onglet.';
            pay.textContent = 'Retrouver mon paiement de test';
            pay.hidden = false;
            for (const field of form.elements) field.disabled = true;
            document.querySelector('#finish').hidden = false;
            return;
        }
        const availability = await api('reservations/disponibilite');
        if (!availability.open) {
            form.hidden = true;
            statusText.textContent = 'Les nouvelles réservations web sont fermées pour cette compagnie.';
            return;
        }
        routes = await api('trajets');
        routes.forEach((route, i) => trip.add(new Option(route.label + ' · ' +
            new Date(route.departure).toLocaleString('fr') + ' · ' + route.xof + ' FCFA', i)));
        if (routes.length) await seats();
        else statusText.textContent = 'Aucun départ ouvert.';
    } catch (error) { statusText.textContent = error.message; }
})();
