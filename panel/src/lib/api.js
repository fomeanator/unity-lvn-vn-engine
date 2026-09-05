// Thin client over the Go LVN server's content + admin endpoints.
// Paths are proxied by Vite to the running server (see vite.config.js).

export async function getManifest() {
  const r = await fetch("/v1/content/manifest", { cache: "no-store" });
  if (!r.ok) throw new Error("manifest " + r.status);
  return r.json();
}

// The project's optional host-op declaration (content/ext-grammar.json) — the
// same file the validator's -ext-grammar auto-detects. Absent → null (the
// closed core grammar applies); present-but-broken → throws, callers surface it.
export async function getExtGrammar() {
  const r = await fetch("/content/ext-grammar.json", { cache: "no-store" });
  if (r.status === 404) return null;
  if (!r.ok) throw new Error("ext-grammar " + r.status);
  return r.json();
}

// encodePath URL-encodes each segment of a content-relative path while keeping
// the '/' separators — a filename with '#', '?' or '%' must not break the URL.
const encodePath = (rel) => String(rel).split("/").map(encodeURIComponent).join("/");

// PUT a file through the token-gated admin route. `body` is a string (script /
// manifest JSON) or a File/Blob (uploaded art). Returns { path, bytes, etag }.
//
// ifMatch — версия файла, НА КОТОРОЙ правил автор (ETag, полученный при
// чтении). Сервер запишет, только если на диске всё ещё она; иначе ответит 409,
// и здесь это становится ошибкой с признаком `conflict`, чтобы вызывающий не
// разбирал текст сообщения. Без аргумента запись идёт как раньше — этим
// пользуются загрузка арта и скрипты сборки, которые версий не читают.
export async function putAsset(path, body, token, contentType, ifMatch) {
  const rel = encodePath(String(path).replace(/^\/+content\/+/, "").replace(/^\/+/, ""));
  const headers = {
    Authorization: "Bearer " + (token || ""),
    "Content-Type": contentType || "application/octet-stream",
  };
  if (ifMatch) headers["If-Match"] = ifMatch;
  const r = await fetch("/v1/admin/assets/" + rel, {
    credentials: "include",
    method: "PUT",
    headers,
    body,
  });
  if (!r.ok) {
    const e = new Error(await errorMessage(r, r.status + ": "));
    if (r.status === 409) e.conflict = true;
    throw e;
  }
  return r.json();
}

// Текущая серверная редакция файла контента: текст и его версия. Нужна перед
// сохранением — чтобы увидеть чужую правку ДО того, как своя ляжет поверх.
// Файла нет → { text: null }, и это не ошибка: так выглядит новая глава.
export async function readSource(path) {
  const rel = encodePath(String(path).replace(/^\/+content\/+/, "").replace(/^\/+/, ""));
  const r = await fetch("/content/" + rel + "?v=" + Date.now(), { cache: "no-store" });
  if (!r.ok) return { text: null, etag: null };
  return { text: await r.text(), etag: r.headers.get("ETag") };
}

// uploadStaged PUTs a File to the server in chunks, resuming from wherever the
// server says it left off — a dropped connection re-queries the offset and
// continues instead of restarting the whole (possibly multi-hundred-MB)
// upload from zero. `id` should be stable for the same logical file (name +
// size) so re-picking it after a reload resumes rather than reuploading.
// Resolves to the staged file's absolute server path (fed straight into
// import-bundle's JSON {dir} fields — see importBundleFromPaths below).
export async function uploadStaged(file, id, token, onProgress, chunkSize = 8 * 1024 * 1024) {
  const headers = { Authorization: "Bearer " + (token || "") };
  const url = "/v1/admin/staged-upload/" + encodeURIComponent(id);
  let offset = 0, path = null;
  {
    const r = await fetch(url, { headers, credentials: "include" });
    if (r.ok) {
      const b = await r.json();
      offset = b.offset || 0;
      path = b.path || null; // already-staged from an earlier run — no PUT needed at all
    }
  }
  if (offset > file.size) { offset = 0; path = null; } // stale/mismatched staged file — start over
  if (offset === file.size && path) { if (onProgress) onProgress(1); return path; }
  while (offset < file.size) {
    const end = Math.min(offset + chunkSize, file.size);
    const chunk = file.slice(offset, end);
    const r = await fetch(url, {
      credentials: "include",
      method: "PUT",
      headers: { ...headers, "Content-Range": `bytes ${offset}-${end - 1}/${file.size}` },
      body: chunk,
    });
    const body = await r.json().catch(() => ({}));
    if (r.status === 409 && typeof body.offset === "number") {
      offset = body.offset; // server resynced us — resume from where it actually is
      continue;
    }
    if (!r.ok) throw new Error(r.status + ": server rejected chunk at " + offset);
    offset = body.offset;
    path = body.path;
    if (onProgress) onProgress(offset / file.size);
  }
  if (!path) throw new Error("server didn't confirm the staged path"); // defensive — should be unreachable
  return path;
}

// uploadStagedWithRetry wraps uploadStaged with resume-on-failure: a network
// drop (wifi hiccup, tab throttled in the background) throws mid-chunk —
// catch it, back off, and call uploadStaged again, which re-queries the
// server's offset and continues rather than starting over.
export async function uploadStagedWithRetry(file, id, token, onProgress, maxAttempts = 20) {
  let lastErr;
  for (let attempt = 1; attempt <= maxAttempts; attempt++) {
    try {
      return await uploadStaged(file, id, token, onProgress);
    } catch (e) {
      lastErr = e;
      await new Promise((res) => setTimeout(res, Math.min(1000 * attempt, 10000)));
    }
  }
  throw lastErr;
}

// Run a bundle import from files ALREADY staged on the server (see
// uploadStaged) — hits import-bundle's JSON {dir} mode instead of a fresh
// multipart upload. Near-instant: the server just reads paths it already has.
export async function importBundleFromPaths(paths, meta, token) {
  const r = await fetch("/v1/admin/import-bundle", {
    credentials: "include",
    method: "POST",
    headers: { Authorization: "Bearer " + (token || ""), "Content-Type": "application/json" },
    body: JSON.stringify({
      articy: paths.articy || "", backgrounds: paths.backgrounds || "", heroine: paths.heroine || "",
      characters: paths.characters || "", vars: paths.vars || "",
      id: meta.id || "", name: meta.name || "", subtitle: meta.subtitle || "", template: meta.template || "",
    }),
  });
  const body = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(r.status + ": " + (body.error || JSON.stringify(body)));
  return body;
}

// Register a Spine character from its editor export: the three files land in
// content/spine/<id>/ and the entity is spliced into manifest.sprites.
export async function uploadSpine(meta, files, token) {
  const fd = new FormData();
  fd.append("id", meta.id);
  if (meta.name) fd.append("name", meta.name);
  if (meta.auto) fd.append("auto", meta.auto);
  if (meta.scale) fd.append("scale", String(meta.scale));
  fd.append("json", files.json);
  fd.append("atlas", files.atlas);
  fd.append("texture", files.texture);
  const res = await fetch("/v1/admin/spine", {
    credentials: "include",
    method: "POST",
    headers: { Authorization: "Bearer " + (token || "") },
    body: fd,
  });
  if (!res.ok) throw new Error(await res.text());
  return res.json();
}

// ── Admin dashboard API ──────────────────────────────────────────────────────
// The product-backend endpoints the old raw /admin/ page used, now called from
// the unified React app. Every call is token-gated; a 401 is surfaced so the UI
// can prompt for the admin token.

// adminFetch is the shared request helper: attaches the bearer token, throws a
// typed error on failure (message "401" on auth so callers can special-case), and
// returns parsed JSON (or text for non-JSON responses).
export async function adminFetch(path, token, opt = {}) {
  opt.headers = Object.assign({ Authorization: "Bearer " + (token || "") }, opt.headers || {});
  opt.credentials = "include";
  const r = await fetch(path, opt);
  if (r.status === 401) throw new Error("401");
  if (!r.ok) throw new Error(await errorMessage(r));
  const ct = r.headers.get("content-type") || "";
  return ct.includes("json") ? r.json() : r.text();
}

// errorMessage turns a failed response into something an author can act on.
// Structured rejections (the server's .lvn write gate answers 422 with
// {errors:[…]}) would otherwise land in the toast as a raw JSON blob — the
// list itself IS the message, one issue per line.
async function errorMessage(r, prefix = "") {
  const text = ((await r.text()) || r.status).toString().trim();
  try {
    const body = JSON.parse(text);
    const errs = (body && body.errors) || [];
    if (errs.length) return errs.join("\n");
    if (body && body.error) return prefix + String(body.error);
  } catch { /* not JSON — the raw text is the message */ }
  return prefix + text;
}

// GET /v1/admin/users → { users: [{ user_id, name, created, providers, balances }] }
export const adminUsers = (token) =>
  adminFetch("/v1/admin/users", token).then((d) => d.users || []);

// GET /v1/admin/users/<id> → { name, wallet: { balances, inventory, history } }
export const adminUserDetail = (id, token) =>
  adminFetch("/v1/admin/users/" + encodeURIComponent(id), token);

// POST /v1/admin/grant — credit/debit a wallet currency (amount may be negative).
export const adminGrant = (body, token) =>
  adminFetch("/v1/admin/grant", token, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });

// GET /v1/admin/orders → { orders: [{ ts, user_id, type, sku, amount, currency, reason }] }
export const adminOrders = (token) =>
  adminFetch("/v1/admin/orders", token).then((d) => d.orders || []);

// GET /v1/admin/saves → { saves: [{ key, size, modified }] }
export const adminSaves = (token) =>
  adminFetch("/v1/admin/saves", token).then((d) => d.saves || []);

// GET /v1/admin/saves/<key> → the raw save blob (JSON).
export const adminSaveDetail = (key, token) =>
  adminFetch("/v1/admin/saves/" + encodeURIComponent(key), token);

// DELETE /v1/admin/saves/<key> — irreversible.
export const adminDeleteSave = (key, token) =>
  adminFetch("/v1/admin/saves/" + encodeURIComponent(key), token, { method: "DELETE" });

// GET/PUT /v1/admin/config/<name> — a live-reloaded server config (iap-catalog,
// ads, daily-rewards). PUT validates JSON server-side and applies immediately.
export const adminConfig = (name, token) =>
  adminFetch("/v1/admin/config/" + encodeURIComponent(name), token);
export const adminPutConfig = (name, doc, token) =>
  adminFetch("/v1/admin/config/" + encodeURIComponent(name), token, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(doc),
  });

// GET /v1/admin/history?file=<rel> → { versions: [{ ts, size }] } (newest first).
export const adminHistory = (file, token) =>
  adminFetch("/v1/admin/history?file=" + encodeURIComponent(file), token);

// POST /v1/admin/rollback {file, ts} — restore a saved version (the rollback
// itself is versioned too, so it's always reversible).
export const adminRollback = (file, ts, token) =>
  adminFetch("/v1/admin/rollback", token, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ file, ts }),
  });

// GET /v1/admin/files?dir=<rel> → { files: [{ name, size, dir }] } (dirs first).
export const adminFiles = (dir, token) =>
  adminFetch("/v1/admin/files?dir=" + encodeURIComponent(dir || ""), token);

// POST /v1/admin/rebuild — пересобрать главы, подключающие изменённый общий
// файл. Без этого правка механик не доезжает до игры: играется скомпилированный
// .lvn, а он остаётся прежним, и автор перестаёт понимать, почему правки «не
// работают».
export const rebuildDependents = (path, token) =>
  adminFetch("/v1/admin/rebuild", token, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ path }),
  });

// DELETE /v1/admin/assets/<path> — scripts go to history, art is gone for good.
export const adminDeleteAsset = (path, token) =>
  adminFetch("/v1/admin/assets/" + encodePath(path), token, { method: "DELETE" });

// GET /v1/admin/manifest[?draft=1] — the manifest (or its unpublished draft).
export const adminManifest = (token, draft) =>
  adminFetch("/v1/admin/manifest" + (draft ? "?draft=1" : ""), token);

// PUT /v1/admin/manifest[?draft=1] — save (players see it live unless draft).
export const adminPutManifest = (doc, token, draft) =>
  adminFetch("/v1/admin/manifest" + (draft ? "?draft=1" : ""), token, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(doc),
  });

// POST /v1/admin/manifest/publish — the draft becomes the live manifest.
export const adminPublishManifest = (token) =>
  adminFetch("/v1/admin/manifest/publish", token, { method: "POST" });

// DELETE /v1/admin/manifest?draft=1 — discard the draft.
export const adminDiscardDraft = (token) =>
  adminFetch("/v1/admin/manifest?draft=1", token, { method: "DELETE" });

// GET /v1/analytics/summary?day=YYYY-MM-DD → { total, unique_users, by_name }.
// The one-day legacy shape, still what Обзор asks for. Wider windows and the
// cuts/funnel/health reports go through the three helpers below.
export const adminAnalytics = (day, token) =>
  adminFetch("/v1/analytics/summary?day=" + encodeURIComponent(day), token);

// ── Analytics reports (server/analytics_report.go) ──────────────────────────
// `query` is the window string built by windowQuery() in lib/analytics.js —
// "day=…", "days=N" or "from=…&to=…". Empty means "today" (the server default).

// GET /v1/analytics/summary → cuts by title / author / day / hour + signals.
// Сегмент подмешивается к окну: сервер понимает его во ВСЕХ отчётах, поэтому
// панели достаточно дописать один параметр, а не заводить отдельные вызовы.
export const withSegment = (query, segment) =>
  segment ? (query ? query + "&" : "") + "segment=" + encodeURIComponent(segment) : query;

// Эксперименты: конфиг (доли, таргет, слои) и отчёт сравнения вариантов.
export const adminExperiments = (token) => adminFetch("/v1/admin/experiments", token);
export const adminSaveExperiments = (list, token) =>
  adminFetch("/v1/admin/experiments", token, { method: "PUT", body: JSON.stringify(list) });
export const analyticsExperiment = (query, name, token) =>
  adminFetch("/v1/analytics/experiment?" + (query ? query + "&" : "") +
    "name=" + encodeURIComponent(name || ""), token);

// GET /v1/admin/crashes — падения, сгруппированные по сути, а не по строкам.
export const adminCrashes = (query, token) =>
  adminFetch("/v1/admin/crashes" + (query ? "?" + query : ""), token);

// GET /v1/admin/feedback — отзывы из игры с их контекстом.
export const adminFeedback = (query, token) =>
  adminFetch("/v1/admin/feedback" + (query ? "?" + query : ""), token);

export const analyticsSummary = (query, token) =>
  adminFetch("/v1/analytics/summary" + (query ? "?" + query : ""), token);

// GET /v1/analytics/funnel[&title=…] — with a title: that novel's chapter
// funnel ({funnel, chapters}); without: the cross-title drop-off leaderboard
// ({dropoffs, stop_points, titles}).
export const analyticsFunnel = (query, title, token) =>
  adminFetch("/v1/analytics/funnel?" + (query ? query + "&" : "") +
    "title=" + encodeURIComponent(title || ""), token);

// GET /v1/analytics/health — the engineering view: failures, unknown ops,
// asset misses, and the gaps the log cannot answer yet.
export const analyticsHealth = (query, token) =>
  adminFetch("/v1/analytics/health" + (query ? "?" + query : ""), token);

// GET /v1/analytics/slides — воронка ВНУТРИ главы: метки и развилки.
export const analyticsSlides = (query, title, chapter, token) =>
  adminFetch("/v1/analytics/slides?" + (query ? query + "&" : "") +
    "title=" + encodeURIComponent(title || "") +
    "&chapter=" + encodeURIComponent(chapter || ""), token);

// GET /v1/analytics/money — конверсия в платящего, ARPU, ARPPU, разбивка по
// пакам и когортам. Сумма — оценка по прайсу каталога, не выручка из стора.
// GET /v1/admin/stats/spend?from&to&bucket=day|month — траты ВАЛЮТ по
// контенту (TR-28): серии для графика + тайтлы с главами и типами трат.
export const adminSpendStats = (query, token) =>
  adminFetch("/v1/admin/stats/spend" + (query ? "?" + query : ""), token);

export const analyticsMoney = (query, token) =>
  adminFetch("/v1/analytics/money" + (query ? "?" + query : ""), token);

// ── Import conflicts (server/import_conflicts.go) ───────────────────────────
// A re-import never overwrites a hand edit: the new version is parked as
// <file>.incoming and the pair waits here for a decision.

// GET /v1/admin/import-conflicts[?rel=…][&diff=0] →
//   { count, conflicts: [{ rel, incoming_rel, mine, incoming, text, titles,
//                          diff, diff_note, undoable }] }
// `rel` narrows to one conflict AND raises the diff budget 400 → 4000 lines,
// so the drawer refetches per file instead of trusting the listing's excerpt.
// `diff:false` skips diffing entirely — the cheap "is anything waiting?" poll.
export const adminConflicts = (token, opt = {}) => {
  const q = [];
  if (opt.rel) q.push("rel=" + encodeURIComponent(opt.rel));
  if (opt.diff === false) q.push("diff=0");
  if (opt.maxLines) q.push("max_lines=" + Number(opt.maxLines));
  return adminFetch("/v1/admin/import-conflicts" + (q.length ? "?" + q.join("&") : ""), token);
};

// POST /v1/admin/import-conflicts/resolve {rel, choice:"mine"|"incoming"}
// Commits one side: the bytes are written through the normal editorial path
// (history snapshot + atomic write) and stamped into the import baseline, so
// the next import compares against the decision. 422 → the chosen version is
// not a valid .lvn; adminFetch surfaces the parse errors as the message.
export const adminResolveConflict = (rel, choice, token, title) =>
  adminFetch("/v1/admin/import-conflicts/resolve", token, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ rel, choice, title: title || "" }),
  });

// ── Сборки (server/builds.go) ───────────────────────────────────────────────
// Готовый APK лежит у сервера, а не в мессенджере: команда забирает свежий
// сама. Байты приезжают тем же кусочным заливом, что и бандлы импорта, —
// uploadStaged выше, — а этот POST лишь регистрирует уже залитый файл.

// GET /v1/admin/builds → { builds: [...], latest }
export const adminBuilds = (token) => adminFetch("/v1/admin/builds", token);

// POST /v1/admin/builds {path, version, platform?, notes?} → карточка сборки.
export const adminRegisterBuild = (body, token) =>
  adminFetch("/v1/admin/builds", token, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });

// DELETE /v1/admin/builds/<id> — файл с диска и строка из описи.
export const adminDeleteBuild = (id, token) =>
  adminFetch("/v1/admin/builds/" + encodeURIComponent(id), token, { method: "DELETE" });

// Скачивание идёт через fetch, а не простой ссылкой: у входа по токену нет
// cookie, а заголовок к <a href> не привязать — ссылка молча вернула бы 401.
export async function downloadBuild(id, filename, token) {
  const r = await fetch("/v1/admin/builds/" + encodeURIComponent(id), {
    headers: { Authorization: "Bearer " + (token || "") },
    credentials: "include",
  });
  if (!r.ok) throw new Error(await errorMessage(r, r.status + ": "));
  const url = URL.createObjectURL(await r.blob());
  const a = document.createElement("a");
  a.href = url;
  a.download = filename || "build";
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

// ── Import mapper: Template CRUD + pre-import detect preview ────────────────
// See tools/lvnconv/importer/template.go (Template) and detect.go
// (DetectRoles) — the panel's import-mapper screen (ImportMapper.jsx) is the
// UI for both.

// GET /v1/admin/import-templates → { templates: [name, …] } — "default" (the
// built-in default) is always included even with no file on disk.
export const listImportTemplates = (token) =>
  adminFetch("/v1/admin/import-templates", token).then((d) => d.templates || []);

// GET /v1/admin/import-templates/<name> → the raw Template JSON (overlay-by-
// presence — a partial file stays partial, it is NOT inflated to the full
// resolved template).
export const getImportTemplate = (name, token) =>
  adminFetch("/v1/admin/import-templates/" + encodeURIComponent(name), token);

// PUT /v1/admin/import-templates/<name> — validates server-side (rejects e.g.
// a broken scene_marker_regex) before writing; versioned through the same
// editorial history as manifest.json.
export const putImportTemplate = (name, doc, token) =>
  adminFetch("/v1/admin/import-templates/" + encodeURIComponent(name), token, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(doc),
  });

// DELETE /v1/admin/import-templates/<name> — refused for "default"/"default".
export const deleteImportTemplate = (name, token) =>
  adminFetch("/v1/admin/import-templates/" + encodeURIComponent(name), token, { method: "DELETE" });

// POST /v1/admin/stage-extract {path} → { dir } — unpacks a staged articy
// archive ONCE (a no-op if it's already a directory) so detectRoles can read
// it; the same `path` (still the archive) is what import-bundle takes later.
export const stageExtractArticy = (path, token) =>
  adminFetch("/v1/admin/stage-extract", token, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ path }),
  });

// POST /v1/admin/detect-roles {dir, template?, draft?, vars?} → a DetectReport:
// every speaker's role/art/line-count, scene-marker + emotion hit rates,
// alias-collision suggestions. Pass `draft` (an unsaved Template object) to
// preview edits before saving them with putImportTemplate.
//
// `vars` is the staged -vars.xlsx path of the same bundle. The real import
// reads the spreadsheet for the emotion legend and the protagonist's roster
// art; a preview computed without it warns about problems the import doesn't
// have (and inflates emotion_color_misses), so pass it whenever it exists.
// The response then also carries xlsx_protagonist / xlsx_emotion_colors.
export const detectRoles = (dir, opt, token) =>
  adminFetch("/v1/admin/detect-roles", token, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      dir,
      template: (opt && opt.template) || "",
      draft: (opt && opt.draft) || undefined,
      vars: (opt && opt.vars) || undefined,
    }),
  });
