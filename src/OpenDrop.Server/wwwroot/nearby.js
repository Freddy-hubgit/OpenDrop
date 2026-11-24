const visible = document.querySelector('#visible');
const discoveryStatus = document.querySelector('#discovery-status');
const devices = document.querySelector('#nearby-devices');
const connect = document.querySelector('#connect-peer');
const inviteInput = document.querySelector('#peer-invite');
const connectStatus = document.querySelector('#connect-status');
let selected;
let updating = false;
let previous = '';
let generation = 0;

function draw({ visible: active, error, devices: peers }) {
  visible.checked = active;
  discoveryStatus.textContent = !active ? 'Schalte die Sichtbarkeit auf beiden Geräten ein.'
    : error ?? (peers.length ? '' : 'Suche nach OpenDrop-Geräten im gleichen WLAN …');
  if (selected && !peers.some(peer => peer.address === selected.address && peer.origin === selected.origin)) {
    selected = null;
    connect.hidden = true;
  }
  const key = JSON.stringify(peers.map(({ name, address, origin, pin }) => [name, address, origin, pin]));
  if (previous === key) return;
  previous = key;
  devices.replaceChildren(...peers.map(peer => {
    const item = document.createElement('li');
    const name = document.createElement('strong');
    const detail = document.createElement('small');
    name.textContent = peer.name;
    detail.textContent = peer.origin ? peer.address : peer.address + ' · HTTPS-Empfang aus';
    item.append(name, detail);
    if (peer.origin) {
      const choose = document.createElement('button');
      if (peer.pin && window.chrome?.webview) {
        choose.textContent = 'Verbinden';
        choose.onclick = () => askDevice(peer);
        item.append(choose);
        return item;
      }
      choose.textContent = 'Einladung eingeben';
      choose.onclick = () => {
        selected = peer;
        document.querySelector('#peer-name').textContent = peer.name;
        connect.hidden = false;
        inviteInput.value = '';
        connectStatus.textContent = '';
        inviteInput.focus();
      };
      item.append(choose);
    }
    return item;
  }));
}

async function fetchPeers(options) {
  const response = await fetch('/owner/nearby', options);
  if (!response.ok) throw new Error();
  return response.json();
}

async function pollNearby() {
  if (!updating) {
    try {
      const current = generation;
      const data = await fetchPeers();
      if (!updating && current === generation) draw(data);
    } catch {
      discoveryStatus.textContent = 'Die Gerätesuche ist momentan nicht erreichbar.';
    }
  }
  setTimeout(pollNearby, 2000);
}
visible.addEventListener('change', async () => {
  generation++;
  updating = true;
  visible.disabled = true;
  try {
    draw(await fetchPeers({
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-OpenDrop-UI': 'nearby' },
      body: JSON.stringify({ enabled: visible.checked })
    }));
  } catch {
    visible.checked = !visible.checked;
    discoveryStatus.textContent = 'Die Sichtbarkeit konnte nicht geändert werden.';
  } finally {
    updating = false;
    visible.disabled = false;
  }
});
connect.addEventListener('submit', event => {
  event.preventDefault();
  let link;
  try { link = new URL(inviteInput.value.trim()); } catch { }
  if (!selected || !link || link.origin !== selected.origin || link.pathname !== '/' ||
      link.username || link.password || link.search || !/^#pair=[0-9A-F]{64}(?:&pin=[0-9A-F]{64})?$/.test(link.hash)) {
    connectStatus.textContent = 'Füge den vollständigen HTTPS-Einladungslink dieses Geräts ein.';
    return;
  }
  if (window.chrome?.webview) window.chrome.webview.postMessage(link.href);
  else window.open(link.href, '_blank', 'noopener,noreferrer');
});
document.querySelector('#copy-invite').addEventListener('click', async () => {
  const status = document.querySelector('#copy-status');
  try {
    await navigator.clipboard.writeText(document.querySelector('#copy-invite').dataset.invitation);
    status.textContent = 'Einladungslink kopiert.';
  } catch {
    status.textContent = 'Link konnte nicht kopiert werden.';
  }
});
pollNearby();
