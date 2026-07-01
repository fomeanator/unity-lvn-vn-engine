// Thin client over the Go LVN server's content + admin endpoints.
// Paths are proxied by Vite to the running server (see vite.config.js).

export async function getManifest() {
  const r = await fetch("/v1/content/manifest", { cache: "no-store" });
  if (!r.ok) throw new Error("manifest " + r.status);
  return r.json();
}

// PUT a file through the token-gated admin route. `body` is a string (script /
// manifest JSON) or a File/Blob (uploaded art). Returns { path, bytes }.
export async function putAsset(path, body, token, contentType) {
  const rel = String(path).replace(/^\/+content\/+/, "").replace(/^\/+/, "");
  const r = await fetch("/v1/admin/assets/" + rel, {
    method: "PUT",
    headers: {
      Authorization: "Bearer " + (token || ""),
      "Content-Type": contentType || "application/octet-stream",
    },
    body,
  });
  if (!r.ok) throw new Error(r.status + ": " + (await r.text()).trim());
  return r.json();
}

// One-click articy:draft import: upload every file of an extracted .adpd project
// (a browser folder pick) and let the server compile → auto-stage → matte art →
// add a manifest title. `files` is a FileList/array (each carries webkitRelativePath
// so the server can rebuild the tree). Returns the server's import summary.
export function importArticy(files, meta, token, onProgress) {
  return new Promise((resolve, reject) => {
    const fd = new FormData();
    // A single .zip goes as the "zip" part (the server unzips it); a picked folder
    // goes as many "f" parts carrying their relative paths so the tree rebuilds.
    for (const f of files) {
      const isZip = /\.zip$/i.test(f.name);
      fd.append(isZip ? "zip" : "f", f, f.webkitRelativePath || f.name);
    }
    const q = new URLSearchParams({
      id: meta.id || "", name: meta.name || "", subtitle: meta.subtitle || "",
    });
    const xhr = new XMLHttpRequest();
    xhr.open("POST", "/v1/admin/import-articy?" + q.toString());
    xhr.setRequestHeader("Authorization", "Bearer " + (token || ""));
    xhr.upload.onprogress = (e) => {
      if (e.lengthComputable && onProgress) onProgress(e.loaded / e.total);
    };
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        try { resolve(JSON.parse(xhr.responseText)); } catch { resolve({}); }
      } else reject(new Error(xhr.status + ": " + (xhr.responseText || "").trim()));
    };
    xhr.onerror = () => reject(new Error("network error"));
    xhr.send(fd);
  });
}
