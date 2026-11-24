const requestDialog = document.querySelector('#incoming-request');
const requestText = document.querySelector('#incoming-text');
const transferStatus = document.querySelector('#request-status');
let incomingId;
let sendingRequest = false;
let asking = false;

async function connectionApi(path, payload) {
  const response = await fetch(path, payload === undefined ? undefined : {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-OpenDrop-UI': 'nearby' },
    body: JSON.stringify(payload)
  });
  if (!response.ok) throw new Error();
  return response.json();
}

function explainResult(state) {
  return {
    declined: 'Die Anfrage wurde abgelehnt.',
    cooldown: 'Nach drei Ablehnungen sind weitere Anfragen fünf Minuten gesperrt.',
    busy: 'Eine Anfrage für dieses Gerät ist bereits offen oder der Empfänger ist beschäftigt.',
    expired: 'Die Anfrage ist abgelaufen.',
    unreachable: 'Das Gerät ist für Verbindungsanfragen nicht erreichbar.'
  }[state] ?? 'Die Anfrage ist fehlgeschlagen.';
}

async function askDevice(peer) {
  if (sendingRequest) return;
  sendingRequest = true;
  transferStatus.textContent = 'Verbindungsanfrage wird gesendet …';
  const secret = Array.from(crypto.getRandomValues(new Uint8Array(32)),
    byte => byte.toString(16).padStart(2, '0')).join('').toUpperCase();
  try {
    const offer = await connectionApi('/owner/request', { address: peer.address, secret });
    if (offer.state !== 'pending') {
      transferStatus.textContent = explainResult(offer.state);
      return;
    }
    transferStatus.textContent = 'Warte auf ' + peer.name + '. Vergleicht den Code ' + offer.code + '.';
    for (let i = 0; i < 32; i++) {
      await new Promise(resolve => setTimeout(resolve, 3000));
      const result = await connectionApi('/owner/request/status', { address: peer.address, id: offer.id, secret });
      if (result.state === 'pending') continue;
      if (result.state === 'accepted' && result.invitation) {
        const link = new URL(result.invitation);
        if (link.origin !== peer.origin || !/^#pair=[0-9A-F]{64}&pin=[0-9A-F]{64}$/.test(link.hash))
          throw new Error();
        transferStatus.textContent = 'Anfrage angenommen.';
        window.chrome.webview.postMessage(link.href);
        return;
      }
      transferStatus.textContent = explainResult(result.state);
      return;
    }
    transferStatus.textContent = explainResult('expired');
  } catch {
    transferStatus.textContent = explainResult('unreachable');
  } finally {
    sendingRequest = false;
  }
}

async function pollIncoming() {
  if (!asking) {
    try {
      const requests = await connectionApi('/owner/requests');
      const request = requests.find(item => item.id === incomingId) ?? requests[0];
      if (asking) { setTimeout(pollIncoming, 1800); return; }
      if (!request) {
        if (requestDialog.open) requestDialog.close();
        incomingId = undefined;
      } else {
        incomingId = request.id;
        requestText.textContent = request.name + ' möchte sich verbinden. Vergleicht den Code ' +
          request.code + ' persönlich oder telefonisch, bevor du annimmst.';
        if (!requestDialog.open) requestDialog.showModal();
      }
    } catch { }
  }
  setTimeout(pollIncoming, 1800);
}

async function respond(accept) {
  if (!incomingId || asking) return;
  asking = true;
  try {
    const result = await connectionApi('/owner/requests/respond', { id: incomingId, accept });
    transferStatus.textContent = result.state === 'accepted' ? 'Verbindung angenommen.'
      : result.state === 'declined' ? 'Verbindung abgelehnt.' : explainResult(result.state);
    incomingId = undefined;
    requestDialog.close();
  } catch {
    transferStatus.textContent = 'Die Entscheidung konnte nicht gespeichert werden.';
  } finally {
    asking = false;
  }
}
document.querySelector('#request-accept').onclick = () => respond(true);
document.querySelector('#request-decline').onclick = () => respond(false);
requestDialog.addEventListener('cancel', event => { event.preventDefault(); respond(false); });
pollIncoming();
