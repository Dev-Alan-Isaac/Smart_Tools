'use strict';
/* Send to Phone: the page the phone opens. Plain JavaScript, no libraries, nothing loaded from the internet.
   Encrypted mode: the key is read from the "#k=" part of the link (the browser never sends it anywhere), then every
   file is cut into chunks sealed with AES-256-GCM exactly like PhoneCrypto.cs (keep both in step):
     file key  = HKDF-SHA256(sessionKey, salt = file id, info = "stp-v1-dl" | "stp-v1-ul")
     nonce     = 4 zero bytes + chunk number (8 bytes, big-endian)
     aad       = file id (16) + chunk number (8) + last-chunk flag (1) + total size (8)
     state     = AES-GCM(HKDF(sessionKey, salt "state", info "stp-v1-state"), random nonce in front, aad "state") */
(function () {
  const base = location.pathname.replace(/\/+$/, '');
  const enc = new TextEncoder();
  const dec = new TextDecoder();
  const $ = (id) => document.getElementById(id);

  const STR = {
    en: {
      title: 'Send to Phone', connecting: 'Connecting...', waiting: 'Waiting for you to allow this phone on the PC...',
      denied: 'The PC did not allow this phone.', busy: 'Another phone is already connected to this PC.',
      expired: 'This link is no longer valid. Scan the QR code on the PC again.', lost: 'Lost the connection to the PC.',
      noKey: 'This link has no key. Scan the QR code on the PC again.',
      noCrypto: 'This browser does not allow encryption on this page. On the PC, switch to "TLS only" and scan the new QR code.',
      connected: 'Connected to the PC.', fromPc: 'From the PC', nothing: 'The PC is not sharing anything yet.',
      download: 'Download', copy: 'Copy', copied: 'Copied.', sendToPc: 'Send to the PC', pick: 'Choose files', sendText: 'Send text',
      textHint: 'Type or paste text to send', sent: 'Sent.', failed: 'Failed: ', working: 'Working...', savedPhone: 'Saved on this phone.',
      noteE2ee: 'End-to-end encrypted. The key is only in the QR code you scanned and in this page: if you reload, scan the QR code again.',
      noteTls: 'Protected by HTTPS, not end-to-end encrypted.',
      big: 'Large files are held in this phone\'s memory until they finish. If it fails, ask for "TLS only" on the PC.',
      tampered: 'It failed the security check.', refused: 'The PC refused it.', size: 'The size did not match.', order: 'Out of order.',
    },
    es: {
      title: 'Enviar al teléfono', connecting: 'Conectando...', waiting: 'Esperando que permitas este teléfono en el PC...',
      denied: 'El PC no permitió este teléfono.', busy: 'Ya hay otro teléfono conectado a este PC.',
      expired: 'Este enlace ya no es válido. Escanea de nuevo el código QR en el PC.', lost: 'Se perdió la conexión con el PC.',
      noKey: 'Este enlace no tiene clave. Escanea de nuevo el código QR en el PC.',
      noCrypto: 'Este navegador no permite el cifrado en esta página. En el PC cambia a "Solo TLS" y escanea el nuevo código QR.',
      connected: 'Conectado al PC.', fromPc: 'Desde el PC', nothing: 'El PC todavía no comparte nada.',
      download: 'Descargar', copy: 'Copiar', copied: 'Copiado.', sendToPc: 'Enviar al PC', pick: 'Elegir archivos', sendText: 'Enviar texto',
      textHint: 'Escribe o pega el texto a enviar', sent: 'Enviado.', failed: 'Falló: ', working: 'Trabajando...', savedPhone: 'Guardado en este teléfono.',
      noteE2ee: 'Cifrado de extremo a extremo. La clave solo está en el código QR que escaneaste y en esta página: si recargas, escanea el código QR de nuevo.',
      noteTls: 'Protegido por HTTPS, sin cifrado de extremo a extremo.',
      big: 'Los archivos grandes se guardan en la memoria de este teléfono hasta terminar. Si falla, pide "Solo TLS" en el PC.',
      tampered: 'No superó la comprobación de seguridad.', refused: 'El PC lo rechazó.', size: 'El tamaño no coincide.', order: 'Fuera de orden.',
    },
  };
  const lang = (navigator.language || 'en').toLowerCase().startsWith('es') ? 'es' : 'en';
  const T = (k) => STR[lang][k] || STR.en[k] || k;

  // ---- helpers ----
  const hex = (u8) => Array.from(u8, (b) => b.toString(16).padStart(2, '0')).join('');
  const fromHex = (s) => Uint8Array.from(s.match(/../g) || [], (h) => parseInt(h, 16));
  const b64 = (u8) => { let s = ''; for (let i = 0; i < u8.length; i += 0x8000) s += String.fromCharCode.apply(null, u8.subarray(i, i + 0x8000)); return btoa(s); };
  const unb64 = (s) => Uint8Array.from(atob(s), (c) => c.charCodeAt(0));
  const unb64url = (s) => unb64(s.replace(/-/g, '+').replace(/_/g, '/') + '==='.slice((s.length + 3) % 4));
  const fmtSize = (n) => n < 1024 ? n + ' B' : n < 1048576 ? (n / 1024).toFixed(1) + ' KB' : n < 1073741824 ? (n / 1048576).toFixed(1) + ' MB' : (n / 1073741824).toFixed(2) + ' GB';
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  // ---- device id and key ----
  let deviceId = sessionStorage.getItem('stp-device');
  if (!deviceId) { deviceId = hex(crypto.getRandomValues(new Uint8Array(32))); sessionStorage.setItem('stp-device', deviceId); }
  let sessionKey = null;
  const m = /(?:^|&)k=([A-Za-z0-9_-]+)/.exec(location.hash.slice(1));
  if (m) { try { sessionKey = unb64url(m[1]); } catch (e) { sessionKey = null; } history.replaceState(null, '', location.pathname); }

  const H = () => ({ 'X-Device': deviceId });
  const subtle = (window.crypto && window.crypto.subtle) || null;

  // ---- crypto (same layout as PhoneCrypto.cs) ----
  async function hkdf(salt, info) {
    const root = await subtle.importKey('raw', sessionKey, 'HKDF', false, ['deriveKey']);
    return subtle.deriveKey({ name: 'HKDF', hash: 'SHA-256', salt: salt, info: enc.encode(info) }, root, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
  }
  function nonce(i) { const n = new Uint8Array(12); const dv = new DataView(n.buffer); dv.setUint32(4, Math.floor(i / 4294967296)); dv.setUint32(8, i >>> 0); return n; }
  function aad(idBytes, i, last, total) {
    const a = new Uint8Array(idBytes.length + 17); a.set(idBytes, 0);
    const dv = new DataView(a.buffer); const p = idBytes.length;
    dv.setUint32(p, Math.floor(i / 4294967296)); dv.setUint32(p + 4, i >>> 0);
    a[p + 8] = last ? 1 : 0;
    dv.setUint32(p + 9, Math.floor(total / 4294967296)); dv.setUint32(p + 13, total >>> 0);
    return a;
  }
  async function openState(b) {
    const key = await hkdf(enc.encode('state'), 'stp-v1-state');
    const blob = unb64(b);
    const plain = await subtle.decrypt({ name: 'AES-GCM', iv: blob.subarray(0, 12), additionalData: enc.encode('state') }, key, blob.subarray(12));
    return JSON.parse(dec.decode(plain));
  }

  // ---- connection ----
  let mode = null, chunk = 1048576, maxUpload = 0;

  function setStatus(text, kind) { const s = $('status'); s.textContent = text; s.className = 'status' + (kind ? ' ' + kind : ''); }
  const post = (path, body) => fetch(base + path, { method: 'POST', headers: Object.assign({ 'Content-Type': 'application/json' }, H()), body: JSON.stringify(body) });

  async function hello() {
    const r = await post('/api/hello', { device: deviceId, name: navigator.userAgent });
    if (r.status === 404) throw new Error('expired');
    const j = await r.json().catch(() => ({}));
    if (r.status === 403 || j.status === 'denied') throw new Error('denied');
    if (r.status === 409 || j.status === 'busy') throw new Error('busy');
  }

  async function getState() {
    const r = await fetch(base + '/api/state', { headers: H(), cache: 'no-store' });
    if (r.status === 404) throw new Error('expired');
    const j = await r.json().catch(() => ({}));
    if (r.status === 403 || j.status === 'denied') throw new Error('denied');
    if (j.status === 'pending' || r.status === 401) return null;
    if (j.mode === 'e2ee') {
      if (!sessionKey) throw new Error('noKey');
      if (!subtle) throw new Error('noCrypto');
      const inner = await openState(j.sealedState);
      return { mode: 'e2ee', items: inner.items, maxUpload: inner.maxUpload, chunk: inner.chunk };
    }
    return { mode: 'tls', items: j.items, maxUpload: j.maxUpload, chunk: j.chunk };
  }

  // ---- downloads ----
  async function fetchItem(item, onProgress) {
    if (mode !== 'e2ee') throw new Error('mode');
    const idBytes = fromHex(item.id);
    const key = await hkdf(idBytes, 'stp-v1-dl');
    const chunks = Math.max(1, Math.ceil(item.size / chunk));
    const parts = [];
    for (let i = 0; i < chunks; i++) {
      const r = await fetch(base + '/api/dl/' + item.id + '/' + i, { headers: H(), cache: 'no-store' });
      if (!r.ok) throw new Error('http' + r.status);
      const buf = new Uint8Array(await r.arrayBuffer());
      parts.push(new Uint8Array(await subtle.decrypt({ name: 'AES-GCM', iv: nonce(i), additionalData: aad(idBytes, i, i === chunks - 1, item.size) }, key, buf)));
      if (onProgress) onProgress((i + 1) / chunks);
    }
    return new Blob(parts, { type: 'application/octet-stream' });
  }

  function saveBlob(blob, name) {
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob); a.download = name;
    document.body.appendChild(a); a.click();
    setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 60000);
  }

  // ---- uploads ----
  async function sealMeta(key, uid, meta) {
    const iv = crypto.getRandomValues(new Uint8Array(12));
    const ct = new Uint8Array(await subtle.encrypt({ name: 'AES-GCM', iv: iv, additionalData: enc.encode('meta' + uid) }, key, enc.encode(JSON.stringify(meta))));
    const out = new Uint8Array(12 + ct.length); out.set(iv, 0); out.set(ct, 12);
    return b64(out);
  }

  async function errorText(r) {
    const j = await r.json().catch(() => ({}));
    const e = j.error || ('HTTP ' + r.status);
    return STR[lang][e] ? T(e) : e;
  }

  // Sends bytes (a Blob/File, or text bytes) as one file or one text. onProgress(0..1).
  async function sendE2ee(blob, name, kind, onProgress) {
    const uid = hex(crypto.getRandomValues(new Uint8Array(16)));
    const idBytes = fromHex(uid);
    const key = await hkdf(idBytes, 'stp-v1-ul');
    const total = blob.size;
    const chunks = Math.max(1, Math.ceil(total / chunk));
    for (let i = 0; i < chunks; i++) {
      const last = i === chunks - 1;
      const plain = new Uint8Array(await blob.slice(i * chunk, Math.min(total, (i + 1) * chunk)).arrayBuffer());
      const ct = await subtle.encrypt({ name: 'AES-GCM', iv: nonce(i), additionalData: aad(idBytes, i, last, total) }, key, plain);
      const headers = Object.assign({ 'Content-Type': 'application/octet-stream', 'X-Final': last ? '1' : '0' }, H());
      if (i === 0) headers['X-Meta'] = await sealMeta(key, uid, { name: name, size: total, kind: kind });
      const r = await fetch(base + '/api/ul/' + uid + '/' + i, { method: 'POST', headers: headers, body: ct });
      if (!r.ok) throw new Error(await errorText(r));
      if (onProgress) onProgress((i + 1) / chunks);
    }
  }

  function sendTls(blob, name, onProgress) {
    return new Promise((resolve, reject) => {
      const uid = hex(crypto.getRandomValues(new Uint8Array(16)));
      const x = new XMLHttpRequest();
      x.open('POST', base + '/api/rawul?uid=' + uid + '&name=' + encodeURIComponent(name));
      x.setRequestHeader('X-Device', deviceId);
      x.upload.onprogress = (e) => { if (e.lengthComputable && onProgress) onProgress(e.loaded / e.total); };
      x.onload = () => {
        if (x.status === 200) return resolve();
        let msg = 'HTTP ' + x.status; try { const j = JSON.parse(x.responseText); if (j.error) msg = STR[lang][j.error] ? T(j.error) : j.error; } catch (e) { /* keep the status */ }
        reject(new Error(msg));
      };
      x.onerror = () => reject(new Error(T('lost')));
      x.send(blob);
    });
  }

  async function sendTextTls(text) {
    const r = await fetch(base + '/api/rawtext', { method: 'POST', headers: H(), body: text });
    if (!r.ok) throw new Error(await errorText(r));
  }

  // ---- page ----
  const known = new Map();

  function renderOffers(items) {
    $('no-offers').textContent = items.length ? '' : T('nothing');
    const ul = $('offers');
    const ids = new Set(items.map((i) => i.id));
    for (const [id, li] of known) if (!ids.has(id)) { li.remove(); known.delete(id); }
    for (const item of items) {
      if (known.has(item.id)) continue;
      const li = document.createElement('li');
      const row = document.createElement('div'); row.className = 'row';
      const name = document.createElement('span'); name.className = 'name'; name.textContent = item.kind === 'text' ? item.text.slice(0, 60) : item.name;
      const size = document.createElement('span'); size.className = 'size'; size.textContent = fmtSize(item.size);
      const btn = document.createElement('button'); btn.type = 'button'; btn.className = 'btn';
      btn.textContent = item.kind === 'text' ? T('copy') : T('download');
      row.append(name, size, btn); li.append(row);
      const msg = document.createElement('div'); msg.className = 'msg'; li.append(msg);
      if (item.kind === 'text') {
        const pre = document.createElement('pre'); pre.className = 'text'; pre.textContent = item.text; li.append(pre);
        btn.onclick = async () => {
          try { await navigator.clipboard.writeText(item.text); }
          catch (e) { const r = document.createRange(); r.selectNodeContents(pre); const s = getSelection(); s.removeAllRanges(); s.addRange(r); document.execCommand('copy'); }
          msg.className = 'msg ok'; msg.textContent = T('copied');
        };
      } else {
        const bar = document.createElement('progress'); bar.max = 1; bar.value = 0; bar.hidden = true; li.append(bar);
        btn.onclick = async () => {
          if (mode === 'tls') {
            const a = document.createElement('a'); a.href = base + '/api/raw/' + item.id + '?d=' + deviceId; a.download = item.name;
            document.body.appendChild(a); a.click(); a.remove(); return;
          }
          btn.disabled = true; bar.hidden = false; msg.className = 'msg'; msg.textContent = T('working');
          try {
            const blob = await fetchItem(item, (p) => { bar.value = p; });
            saveBlob(blob, item.name); msg.className = 'msg ok'; msg.textContent = T('savedPhone');
          } catch (e) { msg.className = 'msg bad'; msg.textContent = T('failed') + (e.name === 'OperationError' ? T('tampered') : e.message); }
          btn.disabled = false; bar.hidden = true;
        };
      }
      ul.append(li); known.set(item.id, li);
    }
  }

  function addUploadRow(name) {
    const li = document.createElement('li');
    const row = document.createElement('div'); row.className = 'row';
    const n = document.createElement('span'); n.className = 'name'; n.textContent = name; row.append(n);
    const bar = document.createElement('progress'); bar.max = 1; bar.value = 0;
    const msg = document.createElement('div'); msg.className = 'msg';
    li.append(row, bar, msg); $('uploads').prepend(li);
    return {
      progress: (p) => { bar.value = p; },
      ok: () => { bar.value = 1; msg.className = 'msg ok'; msg.textContent = T('sent'); },
      fail: (e) => { bar.hidden = true; msg.className = 'msg bad'; msg.textContent = T('failed') + e; },
    };
  }

  async function uploadFiles(files) {
    for (const file of files) {
      const row = addUploadRow(file.name + ' · ' + fmtSize(file.size));
      try {
        if (maxUpload > 0 && file.size > maxUpload) throw new Error(STR[lang].refused);
        if (mode === 'e2ee') await sendE2ee(file, file.name, 'file', row.progress); else await sendTls(file, file.name, row.progress);
        row.ok();
      } catch (e) { row.fail(e.message); }
    }
  }

  async function uploadText(text) {
    const row = addUploadRow(text.slice(0, 40));
    try {
      if (mode === 'e2ee') await sendE2ee(new Blob([enc.encode(text)]), 'text', 'text', row.progress); else await sendTextTls(text);
      row.ok();
    } catch (e) { row.fail(e.message); }
  }

  async function main() {
    document.title = T('title'); $('title').textContent = T('title');
    $('h-pc').textContent = T('fromPc'); $('h-send').textContent = T('sendToPc'); $('t-pick').textContent = T('pick');
    $('send-text').textContent = T('sendText'); $('text').placeholder = T('textHint');
    setStatus(T('connecting'));
    try {
      await hello();
      let state = null;
      setStatus(T('waiting'));
      while (!state) { state = await getState(); if (!state) await sleep(1500); }
      mode = state.mode; chunk = state.chunk || chunk; maxUpload = state.maxUpload || 0;
      $('pane-main').hidden = false;
      $('note').textContent = (mode === 'e2ee' ? T('noteE2ee') : T('noteTls')) + ' ' + T('big');
      setStatus(T('connected'), 'ok');
      renderOffers(state.items);
      $('pick').onchange = (e) => { uploadFiles(Array.from(e.target.files)); e.target.value = ''; };
      $('send-text').onclick = () => { const t = $('text').value.trim(); if (t) { $('text').value = ''; uploadText(t); } };
      // keep the list of what the PC offers fresh
      for (;;) {
        await sleep(4000);
        const s = await getState().catch((e) => { throw e; });
        if (s) renderOffers(s.items);
      }
    } catch (e) {
      const key = ['expired', 'denied', 'busy', 'noKey', 'noCrypto'].includes(e.message) ? e.message : 'lost';
      setStatus(T(key), 'bad');
    }
  }

  window.stp = { fetchItem: fetchItem, sendE2ee: sendE2ee, sendTls: sendTls, sendTextTls: sendTextTls, getState: getState, hello: hello, ready: () => mode, setMode: (m) => { mode = m; } };
  main();
})();
