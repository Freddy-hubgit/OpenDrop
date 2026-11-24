const status = document.querySelector('#status');
const invite = document.querySelector('#invite');
const qr = document.querySelector('#qr');
const receipts = document.querySelector('#receipts');
let lastReceipts = '';

async function refresh() {
  try {
    const response = await fetch('/owner/invitation');
    if (!response.ok) throw new Error();
    const data = await response.json();
    const expired = Date.parse(data.expiresAt) <= Date.now();
    invite.href = data.invitation;
    invite.hidden = qr.hidden = expired;
    if (!expired && !qr.hasAttribute('src')) qr.src = '/owner/qr.png';

    const expiry = new Date(data.expiresAt).toLocaleTimeString();
    const localOnly = new URL(data.invitation).hostname === '127.0.0.1';
    status.textContent = expired
      ? 'Einladung abgelaufen. Starte den Server neu, um eine neue zu erstellen.'
      : localOnly
        ? 'Nur auf diesem PC erreichbar. Einladung gültig bis ' + expiry + '.'
        : 'Im gleichen WLAN scannen. Einladung gültig bis ' + expiry + '.';

    document.querySelector('#folder').textContent = data.inbox;
    const snapshot = JSON.stringify(data.receipts);
    if (snapshot !== lastReceipts) {
      document.querySelector('#empty').hidden = data.receipts.length > 0;
      receipts.replaceChildren(...data.receipts.slice().reverse().map(receipt => {
        const item = document.createElement('li');
        item.textContent = receipt.name + ' · ' + receipt.bytes.toLocaleString() + ' Byte · ' +
          new Date(receipt.receivedAt).toLocaleTimeString() + ' · gespeichert als ' + receipt.storedName;
        return item;
      }));
      lastReceipts = snapshot;
    }
  } catch {
    invite.hidden = qr.hidden = true;
    status.textContent = 'Der Empfangsserver ist momentan nicht erreichbar.';
  } finally {
    window.setTimeout(refresh, 2500);
  }
}

refresh();

const checkAirDrop = document.querySelector('#check-airdrop');
const airDropStatus = document.querySelector('#airdrop-status');
const airDropAdapters = document.querySelector('#airdrop-adapters');

checkAirDrop.addEventListener('click', async () => {
  checkAirDrop.disabled = true;
  airDropStatus.textContent = 'WLAN-Adapter werden gesucht …';
  airDropAdapters.replaceChildren();
  try {
    const response = await fetch('/owner/airdrop', { signal: AbortSignal.timeout(5000) });
    if (!response.ok) throw new Error();
    const result = await response.json();
    airDropStatus.textContent = result.message;
    airDropAdapters.replaceChildren(...result.adapters.map(adapter => {
      const item = document.createElement('li');
      item.textContent = adapter;
      return item;
    }));
  } catch {
    airDropStatus.textContent = 'Der AirDrop-Status konnte nicht geladen werden.';
  } finally {
    checkAirDrop.disabled = false;
  }
});