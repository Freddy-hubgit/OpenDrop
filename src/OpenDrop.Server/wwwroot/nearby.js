const visible = document.querySelector('#visible');
const friendsVisible = document.querySelector('#friends-visible');
const savedFriends = document.querySelector('#saved-friends');
const friendsStatus = document.querySelector('#friends-status');
let previousFriends;
const discoveryStatus = document.querySelector('#discovery-status');
const devices = document.querySelector('#nearby-devices');
const connect = document.querySelector('#connect-peer');
const inviteInput = document.querySelector('#peer-invite');
const connectStatus = document.querySelector('#connect-status');
let selected;
let updating = false;
let previous = '';
let generation = 0;

function draw({ visible: active, friendsVisible: friendsActive, error, devices: peers, friends }) {
  friendsVisible.checked = friendsActive;
  friendsStatus.textContent = !friends.length ? 'Noch keine gespeicherten Freunde.'
    : friendsActive ? 'Deine Freunde können dich im WLAN finden.' : 'Freundes-Sichtbarkeit ist aus.';
  const friendSnapshot = JSON.stringify(friends);
  if (previousFriends !== friendSnapshot) {
    previousFriends = friendSnapshot;
    savedFriends.replaceChildren(...friends.map(friend => {
      const row = document.createElement('li');
      const name = document.createElement('span');
      const remove = document.createElement('button');
      name.textContent = friend.name;
      remove.textContent = 'Entfernen';
      remove.setAttribute('aria-label', friend.name + ' entfernen');
      remove.onclick = () => mutateNearby('/owner/friends/remove', { id: friend.id });
      row.append(name, remove);
      return row;
    }));
  }
  visible.checked = active;
  discoveryStatus.textContent = !active && !friendsActive ? 'Schalte die Sichtbarkeit auf beiden Geräten ein.'
    : error ?? (peers.length ? '' : 'Suche nach OpenDrop-Geräten im gleichen WLAN …');
  if (selected && !peers.some(peer => peer.address === selected.address && peer.origin === selected.origin)) {
    selected = null;
    connect.hidden = true;
  }
  const key = JSON.stringify(peers.map(({ name, address, origin, pin, isFriend }) => [name, address, origin, pin, isFriend]));
  if (previous === key) return;
  previous = key;
  devices.replaceChildren(...peers.map(peer => {
    const item = document.createElement('li');
    const name = document.createElement('strong');
    const detail = document.createElement('small');
    name.textContent = peer.name;
    detail.textContent = peer.address + (peer.isFriend ? ' · Freund' : '') + (peer.origin ? '' : ' · HTTPS-Empfang aus');
    item.append(name, detail);
    if (peer.origin) {
      const choose = document.createElement('button');
      if (peer.pin && window.chrome?.webview) {
        choose.textContent = 'Verbinden';
        choose.onclick = () => askDevice(peer);
        item.append(choose);
        if (!peer.isFriend) {
          const add = document.createElement('button');
          add.textContent = 'Als Freund hinzufügen';
          add.onclick = () => askDevice(peer, true);
          item.append(add);
        }
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
async function mutateNearby(path, payload) {
  if (updating) return;
  generation++;
  updating = true;
  visible.disabled = friendsVisible.disabled = true;
  try {
    const response = await fetch(path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-OpenDrop-UI': 'nearby' },
      body: JSON.stringify(payload)
    });
    if (!response.ok) throw new Error();
    draw(await response.json());
  } catch {
    discoveryStatus.textContent = 'Die Änderung konnte nicht gespeichert werden.';
  } finally {
    updating = false;
    visible.disabled = friendsVisible.disabled = false;
  }
}
visible.addEventListener('change', () => mutateNearby('/owner/nearby', { enabled: visible.checked }));
friendsVisible.addEventListener('change', () =>
  mutateNearby('/owner/friends/visible', { enabled: friendsVisible.checked }));
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
