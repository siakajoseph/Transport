const form = document.querySelector('form');
const trip = document.querySelector('#trip');
const seat = document.querySelector('#seat');
const submit = document.querySelector('button');
const statusText = document.querySelector('[role=status]');
let routes = [];
let lastRequest;

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
    } catch (error) {
        for (const field of form.elements) field.disabled = false;
        if (error.status === 409) {
            lastRequest = undefined;
            await seats();
        }
        statusText.textContent = error.message;
    }
};

(async () => {
    try {
        routes = await api('trajets');
        routes.forEach((route, i) => trip.add(new Option(route.label + ' · ' +
            new Date(route.departure).toLocaleString('fr') + ' · ' + route.xof + ' FCFA', i)));
        if (routes.length) await seats();
        else statusText.textContent = 'Aucun départ ouvert.';
    } catch (error) { statusText.textContent = error.message; }
})();
