const form = document.querySelector('#send');
const fileInput = document.querySelector('#file');
const submit = document.querySelector('#submit');
const progress = document.querySelector('#progress');
const status = document.querySelector('#status');
const token = new URLSearchParams(location.hash.slice(1)).get('pair');

if (location.hash) history.replaceState(null, '', location.pathname);

if (token && /^[0-9A-F]{64}$/.test(token)) {
  fileInput.disabled = submit.disabled = false;
  status.textContent = 'Die Einladung wurde geladen.';
}

form.addEventListener('submit', event => {
  event.preventDefault();
  const file = fileInput.files[0];
  if (fileInput.disabled || !file) return;
  if (file.size > 100 * 1024 * 1024) {
    status.textContent = 'Die Datei darf höchstens 100 MiB groß sein.';
    return;
  }

  fileInput.disabled = submit.disabled = true;
  progress.hidden = false;
  progress.value = 0;
  status.textContent = 'Übertragung läuft …';

  const upload = new XMLHttpRequest();
  upload.open('POST', '/api/upload?name=' + encodeURIComponent(file.name));
  upload.timeout = 310000;
  upload.responseType = 'json';
  upload.setRequestHeader('Authorization', 'Bearer ' + token);
  upload.setRequestHeader('Content-Type', 'application/octet-stream');
  upload.upload.onprogress = event => {
    if (event.lengthComputable) progress.value = event.loaded / event.total * 100;
  };
  upload.onload = () => {
    if (upload.status === 201) {
      const receipt = upload.response;
      status.textContent = receipt.name + ' wurde empfangen (' + receipt.bytes.toLocaleString() + ' Byte).';
      form.reset();
    } else {
      status.textContent = upload.response?.detail ?? 'Der Upload ist fehlgeschlagen (HTTP ' + upload.status + ').';
    }
  };
  upload.onerror = () => { status.textContent = 'Die Netzwerkverbindung wurde unterbrochen.'; };
  upload.ontimeout = () => { status.textContent = 'Die Bestätigung hat zu lange gedauert.'; };
  upload.onloadend = () => {
    progress.hidden = true;
    fileInput.disabled = submit.disabled = false;
  };
  upload.send(file);
});
