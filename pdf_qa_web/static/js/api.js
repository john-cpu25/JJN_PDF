// Thin client for the FastAPI backend.
import { downloadBlob } from './ui.js';

async function errText(res) {
  try {
    const j = await res.json();
    return typeof j.detail === 'string' ? j.detail : JSON.stringify(j.detail);
  } catch { return `${res.status} ${res.statusText}`; }
}

export async function getJSON(url) {
  const res = await fetch(url);
  if (!res.ok) throw new Error(await errText(res));
  return res.json();
}

export async function postJSON(url, body) {
  const res = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  if (!res.ok) throw new Error(await errText(res));
  return res.json();
}

export async function del(url) {
  try { await fetch(url, { method: 'DELETE' }); } catch { /* ignore */ }
}

/** POST JSON, save the returned file. */
export async function postDownload(url, body, fallbackName = 'download') {
  const res = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  if (!res.ok) throw new Error(await errText(res));
  const name = decodeURIComponent(res.headers.get('X-Filename') || fallbackName);
  downloadBlob(await res.blob(), name);
  return name;
}

/** multipart upload with progress callback (0..1). */
export function upload(url, fields, onProgress) {
  return new Promise((resolve, reject) => {
    const fd = new FormData();
    for (const [k, v] of Object.entries(fields)) fd.append(k, v);
    const xhr = new XMLHttpRequest();
    xhr.open('POST', url);
    xhr.responseType = 'json';
    xhr.upload.onprogress = e => e.lengthComputable && onProgress?.(e.loaded / e.total);
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) resolve(xhr.response);
      else reject(new Error(xhr.response?.detail || `${xhr.status} ${xhr.statusText}`));
    };
    xhr.onerror = () => reject(new Error('Mất kết nối tới server'));
    xhr.send(fd);
  });
}

export function excel(filename, sheets) {
  return postDownload('/api/excel', { filename, sheets }, filename);
}
