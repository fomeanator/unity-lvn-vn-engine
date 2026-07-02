import { forwardRef, useEffect, useImperativeHandle, useMemo, useRef, useState } from "react";
import { getManifest, putAsset } from "../lib/api.js";
import { ensureWasm, compileLvns } from "../lib/wasm.js";
import { highlightLvns } from "../lib/highlight.js";
import DocsPanel from "./DocsPanel.jsx";
import ExamplesPanel from "./ExamplesPanel.jsx";
import ExportPanel from "./ExportPanel.jsx";
import ThemePanel from "./ThemePanel.jsx";
import TranslatePanel from "./TranslatePanel.jsx";
import ResizeHandle from "./ResizeHandle.jsx";
import MonacoEditor from "./MonacoEditor.jsx";
import { OP_DOCS } from "lvn-lang/grammar.js";
import {
  labelsIn, labelInfo, varInfo, wordAt, speakerEntity,
  completionAt, predictGhost, describeLine,
} from "lvn-lang/analyze.js";

const splitLines = (s) => (s ? s.split("\n").map((x) => x.trim()).filter(Boolean) : []);

// Banner shown in the editor for a server-side compiled chapter (an articy import).
// Plain .lvns comments, so it never breaks anything if the chapter is later edited.
function importedBanner(id, n) {
  return `# ─────────────────────────────────────────────────────────────
# «${id}» — импортировано из articy:draft (.adpd)
#
# Глава скомпилирована напрямую в .lvn (${n} команд) и лежит на
# сервере — она уже играбельна в движке и видна в библиотеке.
# Редактор здесь read-only: формат слишком большой для ручного
# .lvns. Справа — реальный компилированный .lvn. Перевод строк —
# через кнопку «🌐 Languages».
# ─────────────────────────────────────────────────────────────
`;
}

function defaultSrc(scene) {
  return `scene ${scene || "chapter"}

The chapter opens here.
Mara: Hello.

- Continue -> next
- Leave -> __end

:next
Mara [smile]: Glad you stayed.
goto __end
`;
}

// "New file" templates. `code: null` means a blank chapter (defaultSrc).
const SAMPLES = [
  { label: "Blank chapter", code: null },
  {
    label: "Narration & speech",
    code: `scene intro
actor_map Mara=mara

This is narration — no speaker.
Mara: This is a speech line.
Mara [happy]: I am smiling now!
goto __end
`,
  },
  {
    label: "Branching & variables",
    code: `scene branching
set key="friendship" value=0

:start
Mara: Have we met?
- Yes -> met
- No -> first

:met
inc key="friendship" by=5
goto check
:first
Mara: Nice to meet you!
goto check

:check
if expr="friendship >= 5" then="friends" else="strangers"
:friends
Mara [smile]: Already great friends!
goto __end
:strangers
Mara: Let's get to know each other.
goto __end
`,
  },
  {
    label: "Gated choices",
    code: `scene gates
:room
Mara: Try the forbidden door?
- Break it -> enter min=5 requires_stat="courage"
- Pay the lockpick -> enter cost="50 gold"
- Walk away -> leave

:enter
You step through.
goto __end
:leave
You walk away.
goto __end
`,
  },
];

// A novel's Script, as a small web IDE: an Explorer of chapters, a gutter+syntax
// editor, a compiled-.lvn preview, a Problems dock and a status bar. No local
// drafts — the server is the single source of truth: open re-reads the chapter's
// .lvns, "Save to app" writes both the .lvns source and the compiled .lvn back.
export default function ScriptSection({ creds, notify, titleId, setStatus }) {
  const [title, setTitle] = useState(null);
  const [published, setPublished] = useState(() => new Set()); // chapter ids live on the server
  const [selId, setSelId] = useState(null);
  const [catalog, setCatalog] = useState({}); // manifest.sprites — for id/axes autocomplete
  const [bust, setBust] = useState(() => Date.now());

  const [src, setSrc] = useState("");
  const [output, setOutput] = useState("");
  const [imported, setImported] = useState(false); // chapter is a read-only server-side .lvn (articy import)
  const [error, setError] = useState(false);
  const [diags, setDiags] = useState([]); // [{ sev, line, op, msg }]
  const [jump, setJump] = useState({ line: 0, n: 0 });
  const [stat, setStat] = useState({ kind: "warn", text: "…", title: "" });
  const [showPreview, setShowPreview] = useState(true);
  const [showProblems, setShowProblems] = useState(true);
  const [showDocs, setShowDocs] = useState(false);
  const [showExamples, setShowExamples] = useState(false);
  const [showExport, setShowExport] = useState(false);
  const [showTheme, setShowTheme] = useState(false);
  const [showTranslate, setShowTranslate] = useState(false);
  const [newMenu, setNewMenu] = useState(false);
  const [caretPos, setCaretPos] = useState({ line: 1, col: 1 });
  const lastJson = useRef("");
  const importedRef = useRef(false); // sync mirror of `imported` for the editor's mount-echo guard
  const openEpoch = useRef(0); // bumped per openChapter call; a stale async open bails out
  const editorRef = useRef(null);
  const wasmReady = useRef(false);
  const saveRef = useRef(null);

  // Global shortcuts: Ctrl/Cmd+S saves to the app; Ctrl/Cmd+P opens the
  // chapter quick-open; Ctrl/Cmd+Shift+F searches across every chapter.
  const [quickOpen, setQuickOpen] = useState(false);
  const [searchAll, setSearchAll] = useState(false);
  useEffect(() => {
    const h = (e) => {
      if ((e.metaKey || e.ctrlKey) && (e.key === "s" || e.key === "S")) {
        e.preventDefault();
        saveRef.current && saveRef.current();
      }
      if ((e.metaKey || e.ctrlKey) && (e.key === "p" || e.key === "P") && !e.shiftKey) {
        e.preventDefault();
        setQuickOpen(true);
      }
      if ((e.metaKey || e.ctrlKey) && e.shiftKey && (e.key === "f" || e.key === "F")) {
        e.preventDefault();
        setSearchAll(true);
      }
      if (e.key === "Escape") { setQuickOpen(false); setSearchAll(false); }
    };
    window.addEventListener("keydown", h);
    return () => window.removeEventListener("keydown", h);
  }, []);

  // ── chapters ──────────────────────────────────────────────────────────
  useEffect(() => {
    (async () => {
      let t = null;
      try {
        const m = await getManifest();
        setCatalog(m.sprites || {});
        t = (m.titles || []).find((x) => x.id === titleId) || null;
      } catch {}
      if (!t) t = { id: titleId, seasons: [{ chapters: [] }] };
      if (!t.seasons || t.seasons.length === 0) t.seasons = [{ chapters: [] }];
      setTitle(t);
      // everything that came from the manifest is already live
      const ids = [];
      (t.seasons || []).forEach((s) => (s.chapters || []).forEach((c) => ids.push(c.id)));
      setPublished(new Set(ids));
      const first = (t.seasons[0].chapters || [])[0];
      await ensureWasm().then(() => (wasmReady.current = true)).catch(() => {});
      if (first) openChapter(first); else { setSrc(""); compile(""); }
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [titleId]);

  const chapters = useMemo(() => {
    if (!title) return [];
    const out = [];
    (title.seasons || []).forEach((s) => (s.chapters || []).forEach((c) => out.push(c)));
    out.sort((a, b) => (a.number || 0) - (b.number || 0));
    return out;
  }, [title]);

  // ── unsaved-work safety ───────────────────────────────────────────────
  // Every IDE keeps your typing safe; "the server is the single source of
  // truth" must not mean "a closed tab eats an hour of writing". The editor
  // keeps a per-chapter DRAFT in localStorage while the text differs from the
  // last server copy; opening the chapter restores the draft (Reload from
  // server discards it), saving clears it, and closing the tab with unsaved
  // changes asks first.
  const savedSrc = useRef(""); // the last server-agreed source for this chapter
  const draftKey = (chapterId) => `lvn_draft_${titleId}_${chapterId}`;
  const dirty = !imported && !!selId && src !== savedSrc.current;

  useEffect(() => {
    document.title = (dirty ? "● " : "") + "ELVIN IDE";
    if (!dirty) return;
    const h = (e) => { e.preventDefault(); e.returnValue = ""; };
    window.addEventListener("beforeunload", h);
    return () => window.removeEventListener("beforeunload", h);
  }, [dirty]);

  // Adopt server text as the agreed baseline, then let a stashed draft win.
  function adoptSource(chapterId, serverText) {
    savedSrc.current = serverText;
    const draft = localStorage.getItem(draftKey(chapterId));
    if (draft != null && draft !== serverText) {
      setSrc(draft);
      compile(draft);
      notify("Restored an unsaved draft — «Reload from server» discards it", "");
      return true;
    }
    return false;
  }

  async function openChapter(c) {
    // Guard against a slow fetch from a previous open clobbering the chapter the
    // user has since switched to (and leaving importedRef stuck → dropped keys).
    const epoch = ++openEpoch.current;
    setSelId(c.id);
    if (c.script_url) creds.setPath(String(c.script_url).replace(/^\/+content\/+/, "").replace(/^\/+/, ""));
    // Read the source fresh from the server; a local unsaved draft, when one
    // exists, wins over it (see adoptSource).
    // Prefer a sibling .lvns SOURCE next to the compiled .lvn; it's editable, so
    // hand-made novels open as language, never as read-only bytecode.
    if (c.script_url && /\.lvn$/.test(c.script_url)) {
      const lvnsUrl = c.script_url.replace(/\.lvn$/, ".lvns");
      try {
        const r = await fetch(lvnsUrl + "?v=" + Date.now(), { cache: "no-store" });
        if (openEpoch.current !== epoch) return; // a newer openChapter is in charge
        if (r.ok) {
          const txt = await r.text();
          if (openEpoch.current !== epoch) return;
          // .lvns is plain text; guard against a static server falling back to
          // the compiled .lvn (JSON) when the source is missing.
          if (txt && !txt.trimStart().startsWith("{")) {
            importedRef.current = false;
            setImported(false);
            if (adoptSource(c.id, txt)) return;
            setSrc(txt);
            compile(txt);
            return;
          }
        }
      } catch { /* no .lvns source — fall through to the compiled .lvn */ }
    }
    // No .lvns source either. If the server holds a compiled .lvn for this chapter
    // (e.g. an articy:draft import), show that real content rather than a blank
    // template — read-only, since it isn't .lvns source.
    if (c.script_url) {
      try {
        const r = await fetch(c.script_url + "?v=" + Date.now(), { cache: "no-store" });
        if (openEpoch.current !== epoch) return;
        if (r.ok) {
          const txt = await r.text();
          if (openEpoch.current !== epoch) return;
          const obj = JSON.parse(txt);
          if (obj && Array.isArray(obj.script)) {
            const n = obj.script.length;
            const pretty = JSON.stringify(obj, null, 2);
            lastJson.current = pretty;
            importedRef.current = true;
            setImported(true);
            setOutput(pretty);
            setError(false);
            setDiags([]);
            setSrc(importedBanner(c.id, n));
            const s = { kind: "success", text: `✓ Imported · ${n} commands (read-only)` };
            setStat(s); setStatus?.(s);
            return;
          }
        }
      } catch { /* not a compiled import — fall through to a fresh template */ }
    }
    importedRef.current = false;
    setImported(false);
    if (adoptSource(c.id, "")) return; // a draft of a never-published chapter
    const text = defaultSrc(c.id);
    savedSrc.current = text; // a fresh template is "clean" until edited
    setSrc(text);
    compile(text);
  }

  // ── compile (WASM) ────────────────────────────────────────────────────
  function compile(text) {
    const r = compileLvns(text);
    setDiags(Array.isArray(r && r.diags) ? r.diags : []);
    if (!r || !r.ok) {
      const first = r && r.errors ? r.errors.split("\n")[0] : "Compilation error";
      setOutput(r && r.errors ? r.errors : "Compilation error");
      setError(true);
      const s = { kind: "error", text: "✗ " + first, title: r?.errors || "" };
      setStat(s); setStatus?.(s);
      lastJson.current = "";
      return;
    }
    lastJson.current = r.json;
    setOutput(r.json);
    setError(false);
    let s;
    if (r.warnings) {
      const n = splitLines(r.warnings).length;
      s = { kind: "warn", text: `⚠ ${n} warning${n > 1 ? "s" : ""}`, title: r.warnings };
    } else {
      s = { kind: "success", text: "✓ Compiled" };
    }
    setStat(s); setStatus?.(s);
  }

  // Compiling on EVERY keystroke froze the editor on real chapters (a 1.5k-line
  // articy episode = a full WASM compile + a giant JSON re-render per key).
  // The text state updates immediately (typing stays instant); the compile —
  // diagnostics, Problems, the Compiled pane — settles ~200ms after the pause.
  const compileTimer = useRef(0);
  useEffect(() => () => clearTimeout(compileTimer.current), []);

  function onEdit(text) {
    // Imported chapters are read-only — ignore the editor's mount-time echo so we
    // don't clobber the server .lvn shown in the Compiled pane.
    // Use the ref (not state) — the echo can fire before the state commit lands.
    if (importedRef.current) return;
    setSrc(text);
    // Draft stash: unsaved typing survives a closed tab / crashed browser.
    if (selId) {
      try {
        if (text !== savedSrc.current) localStorage.setItem(draftKey(selId), text);
        else localStorage.removeItem(draftKey(selId));
      } catch { /* quota — the beforeunload guard still protects */ }
    }
    if (!wasmReady.current) return;
    clearTimeout(compileTimer.current);
    compileTimer.current = setTimeout(() => compile(text), 200);
  }

  // ── chapter CRUD / meta ───────────────────────────────────────────────
  async function persist(nextTitle) {
    setTitle(nextTitle);
    try {
      const m = await getManifest();
      const titles = m.titles || [];
      const idx = titles.findIndex((t) => t.id === nextTitle.id);
      if (idx >= 0) titles[idx] = nextTitle; else titles.push(nextTitle);
      m.titles = titles;
      await putAsset("manifest.json", JSON.stringify(m, null, 2), creds.token, "application/json");
      notify("✓ Chapters saved — live in ~2s", "ok");
    } catch (e) { notify("✗ " + e.message, "err"); }
  }
  function addToSeasonOne(ch) {
    return { ...title, seasons: title.seasons.map((s, i) => (i === 0 ? { ...s, chapters: [...(s.chapters || []), ch] } : s)) };
  }
  function uniqueId(base) {
    let id = base, k = 1;
    while (chapters.some((x) => x.id === id)) id = base + "-" + ++k;
    return id;
  }
  // Create a new chapter file, optionally seeding its draft from a sample. A new
  // file never touches the file you're editing — picking a sample can't erase
  // your code.
  // New files are LOCAL DRAFTS — they never touch the live game until you
  // "Save to app". So creating a file can't break a running game, and you can
  // throw drafts away freely.
  // Load a brand-new chapter straight into the editor (no server file yet, no
  // drafts) — it becomes real on "Save to app", which writes its .lvns + .lvn.
  function seedNewChapter(id, text, bg) {
    const ch = { id, number: (chapters.length ? Math.max(...chapters.map((x) => x.number || 0)) : 0) + 1, script_url: `/content/scripts/${id}.lvn`, bg_url: bg || "" };
    setTitle(addToSeasonOne(ch));
    setSelId(id);
    creds.setPath(`scripts/${id}.lvn`);
    importedRef.current = false; setImported(false);
    setSrc(text);
    if (wasmReady.current) compile(text);
    notify("New chapter — Save to app to publish", "");
  }
  function createChapter(seed) {
    setNewMenu(false);
    const id = uniqueId(`${titleId}-ch${(chapters.length ? Math.max(...chapters.map((x) => x.number || 0)) : 0) + 1}`);
    seedNewChapter(id, seed != null ? seed : defaultSrc(id), "");
  }
  const addChapter = () => createChapter(null);

  function duplicateChapter(c) {
    const text = c.id === selId ? src : defaultSrc(c.id);
    seedNewChapter(uniqueId(`${c.id}-copy`), text, c.bg_url || "");
  }
  function patchChapter(id, patch) {
    setTitle((t) => ({ ...t, seasons: t.seasons.map((s) => ({ ...s, chapters: (s.chapters || []).map((c) => (c.id === id ? { ...c, ...patch } : c)) })) }));
  }
  function commitChapter(id, patch) {
    const next = { ...title, seasons: title.seasons.map((s) => ({ ...s, chapters: (s.chapters || []).map((c) => (c.id === id ? { ...c, ...patch } : c)) })) };
    // a draft's metadata edits stay local until it's published
    if (published.has(id)) persist(next); else setTitle(next);
  }
  function removeChapter(id) {
    const next = { ...title, seasons: title.seasons.map((s) => ({ ...s, chapters: (s.chapters || []).filter((c) => c.id !== id) })) };
    // an unpublished chapter just vanishes; a published one is removed on the server
    if (published.has(id)) {
      persist(next);
      setPublished((p) => { const q = new Set(p); q.delete(id); return q; });
    } else {
      setTitle(next);
    }
    if (selId === id) { const first = (next.seasons[0].chapters || [])[0]; if (first) openChapter(first); else { setSelId(null); setSrc(""); compile(""); } }
  }
  async function uploadBg(ch) {
    const target = ch.bg_url || `/content/ui/loading/${ch.id}.png`;
    const picker = document.createElement("input");
    picker.type = "file"; picker.accept = "image/*";
    picker.onchange = async () => {
      const f = picker.files && picker.files[0];
      if (!f) return;
      notify("Uploading loading screen…");
      try {
        await putAsset(target, f, creds.token, f.type || "application/octet-stream");
        setBust(Date.now());
        if (!ch.bg_url) commitChapter(ch.id, { bg_url: target });
        notify("✓ Loading bg uploaded", "ok");
      } catch (e) { notify("✗ " + e.message, "err"); }
    };
    picker.click();
  }

  async function save() {
    // The compile is debounced behind typing — flush it so we never save a
    // stale .lvn against fresh .lvns source.
    clearTimeout(compileTimer.current);
    if (wasmReady.current && !importedRef.current) compile(src);
    if (!lastJson.current) { notify("Fix the errors before saving.", "err"); return; }
    const lvnPath = (creds.path || "scripts/ch1.lvn").trim();
    const lvnsPath = lvnPath.replace(/\.lvn$/, ".lvns");
    notify("Saving…");
    try {
      // Persist BOTH the editable source (.lvns) and the compiled bytecode (.lvn)
      // to the server — the source is what the editor re-reads on open, so this is
      // what makes the no-drafts model work.
      await putAsset(lvnsPath, src, creds.token, "text/plain; charset=utf-8");
      await putAsset(lvnPath, lastJson.current, creds.token, "application/json");
      // a new chapter is published on first save: push its manifest entry too.
      if (selId && !published.has(selId)) {
        await persist(title);
        setPublished((p) => new Set(p).add(selId));
        notify(`✓ Published ${lvnsPath} (+ .lvn) — live in ~2s`, "ok");
      } else {
        notify(`✓ Saved ${lvnsPath} (+ .lvn) — live in ~2s`, "ok");
      }
      savedSrc.current = src; // the server now agrees — clean
      if (selId) try { localStorage.removeItem(draftKey(selId)); } catch { }
    } catch (e) { notify("✗ " + e.message, "err"); }
  }

  saveRef.current = save;

  const sel = chapters.find((c) => c.id === selId) || null;

  // Re-read the current chapter's .lvns from the server (drops in-editor unsaved
  // changes) — handy when the source was edited out-of-band, e.g. on disk.
  function reloadFromServer() {
    if (!sel) return;
    try { localStorage.removeItem(draftKey(sel.id)); } catch { } // an explicit reload discards the draft
    openChapter(sel);
    notify("Перечитано с сервера (черновик сброшен)", "ok");
  }
  const cmdCount = (output.match(/"op":/g) || []).length;
  const errCount = diags.filter((d) => d.sev === "error").length;
  const warnCount = diags.filter((d) => d.sev === "warning").length;
  const markers = diags.filter((d) => d.line > 0).map((d) => ({ line: d.line, sev: d.sev }));
  const goLine = (line) => { if (line > 0) setJump((j) => ({ line, n: j.n + 1 })); };
  const outline = useMemo(() => {
    const items = [];
    src.split("\n").forEach((l, i) => {
      let m;
      if ((m = l.match(/^\s*scene\s+(\S+)/))) items.push({ kind: "scene", name: m[1], line: i + 1 });
      else if ((m = l.match(/^\s*:(\S+)/))) items.push({ kind: "label", name: m[1], line: i + 1 });
    });
    return items;
  }, [src]);
  const curOutline = (() => {
    let cur = -1;
    for (let i = 0; i < outline.length; i++) { if (outline[i].line <= caretPos.line) cur = i; else break; }
    return cur;
  })();

  return (
    <div className="ide">
      {quickOpen && (
        <QuickOpen
          chapters={chapters}
          currentId={selId}
          onPick={(c) => { setQuickOpen(false); openChapter(c); }}
          onClose={() => setQuickOpen(false)}
        />
      )}
      {searchAll && (
        <SearchAll
          chapters={chapters}
          onPick={async (c, line) => {
            setSearchAll(false);
            await openChapter(c);
            if (line > 0) goLine(line);
          }}
          onClose={() => setSearchAll(false)}
        />
      )}
      <div className="ide-top">
        <div className="ide-file">
          <span className={"ide-file-dot" + (dirty ? " dirty" : "")} title={dirty ? "Unsaved changes (drafted locally)" : "Saved"} />
          <span className="ide-file-name">{sel ? sel.id : "—"}<em>.lvns</em>{dirty ? " •" : ""}</span>
        </div>
        <div className="ide-top-actions">
          <button className={"btn-ghost sm" + (showExamples ? " on" : "")} onClick={() => { setShowExamples((v) => !v); setShowDocs(false); }}>❖ Examples</button>
          <button className={"btn-ghost sm" + (showDocs ? " on" : "")} onClick={() => { setShowDocs((v) => !v); setShowExamples(false); }}>✦ Reference</button>
          <button className={"btn-ghost sm" + (showPreview ? " on" : "")} onClick={() => setShowPreview((v) => !v)}>⌗ Compiled</button>
          <button className={"btn-ghost sm" + (showTheme ? " on" : "")} onClick={() => { setShowTheme((v) => !v); setShowDocs(false); setShowExamples(false); setShowExport(false); }}>◐ Theme</button>
          <button className={"btn-ghost sm" + (showExport ? " on" : "")} onClick={() => { setShowExport((v) => !v); setShowDocs(false); setShowExamples(false); setShowTheme(false); }}>⤓ Export</button>
          <button className={"btn-ghost sm" + (showTranslate ? " on" : "")} onClick={() => setShowTranslate((v) => !v)}>🌐 Languages</button>
          <button className="btn-ghost sm" onClick={reloadFromServer} title="Перечитать .lvns с сервера (сбросить несохранённые правки)">↻ Reload</button>
          <button className="btn-ghost sm" onClick={() => navigator.clipboard.writeText(output)}>Copy .lvn</button>
          <button className="btn btn-primary" onClick={save} disabled={!!error}>{selId && !published.has(selId) ? "Publish to app ▸" : "Save to app ▸"}</button>
        </div>
      </div>

      <div className="ide-body">
        <aside className="ide-explorer enter">
          <ResizeHandle storageKey="ide-w-explorer" side="right" min={190} max={900} />
          <div className="ide-explorer-head">
            <span className="section-label">Files</span>
            <div className="ide-new">
              <button className="btn-ghost sm" onClick={() => setNewMenu((v) => !v)}>+ New ▾</button>
              {newMenu && (
                <div className="ide-new-menu" onMouseLeave={() => setNewMenu(false)}>
                  {SAMPLES.map((s) => (
                    <button key={s.label} onClick={() => createChapter(s.code)}>{s.label}</button>
                  ))}
                </div>
              )}
            </div>
          </div>
          <div className="ide-files">
            {chapters.length === 0 && <div className="ide-empty">No files.<br />+ New →</div>}
            {chapters.map((c) => {
              const isDraft = !published.has(c.id);
              const hasError = c.id === selId && errCount > 0;
              const status = hasError ? "error" : isDraft ? "draft" : "live";
              return (
              <div key={c.id} className={"ide-file-row" + (c.id === selId ? " active" : "")}>
                <button className="ide-file-open" onClick={() => openChapter(c)} title={c.id + ".lvns"}>
                  <span className={"ide-file-ico st-" + status} title={status === "error" ? "has errors" : status === "draft" ? "draft — not in the game yet" : "live in the game"} />
                  <span className="ide-file-num">{c.number}</span>
                  <span className="ide-file-label">{c.name ? c.name : <>{c.id}<em>.lvns</em></>}</span>
                  {isDraft && <span className="ide-file-tag">draft</span>}
                </button>
                <span className="ide-file-acts">
                  <button onClick={() => duplicateChapter(c)} title="Duplicate file">⧉</button>
                  <button onClick={() => removeChapter(c.id)} title="Delete file">✕</button>
                </span>
              </div>
              );
            })}
          </div>

          {sel && outline.length > 0 && (
            <div className="ide-outline">
              <div className="section-label">Outline</div>
              <div className="ide-outline-list">
                {outline.map((o, i) => (
                  <button key={i} className={"ide-out-row k-" + o.kind + (i === curOutline ? " cur" : "")}
                    onClick={() => goLine(o.line)} title={`line ${o.line}`}>
                    <span className="ide-out-ico">{o.kind === "scene" ? "▤" : "⌖"}</span>
                    <span className="ide-out-name">{o.name}</span>
                    <span className="ide-out-line">{o.line}</span>
                  </button>
                ))}
              </div>
            </div>
          )}

          {sel && (
            <div className="ide-chapter-settings">
              <div className="section-label">Chapter</div>
              <label className="ide-set-row">
                <span>Name</span>
                <input className="field" type="text" placeholder="Эпизод…" value={sel.name ?? ""}
                  onChange={(e) => patchChapter(sel.id, { name: e.target.value })}
                  onBlur={(e) => commitChapter(sel.id, { name: e.target.value })} />
              </label>
              <label className="ide-set-row">
                <span>Number</span>
                <input className="field" type="number" value={sel.number ?? 0}
                  onChange={(e) => patchChapter(sel.id, { number: parseInt(e.target.value, 10) || 0 })}
                  onBlur={(e) => commitChapter(sel.id, { number: parseInt(e.target.value, 10) || 0 })} />
              </label>
              <button className="ide-bg" onClick={() => uploadBg(sel)} title="Loading-screen background">
                {sel.bg_url ? <img src={sel.bg_url + "?v=" + bust} alt="" onError={(e) => { e.currentTarget.style.display = "none"; }} /> : <span>＋ loading bg</span>}
              </button>
              <code className="ide-set-path">{sel.script_url}</code>
              <button className="btn-ghost sm wide-btn" onClick={() => removeChapter(sel.id)}>Remove chapter</button>
            </div>
          )}
        </aside>

        {showExport && <ExportPanel defaultName={title ? title.name : ""} notify={notify} onClose={() => setShowExport(false)} />}
        {showTheme && <ThemePanel token={creds.token} notify={notify} titleId={titleId} onClose={() => setShowTheme(false)} />}
        {showTranslate && <TranslatePanel compiledJson={output} scriptUrl={sel ? sel.script_url : null} sourceLang="source" token={creds.token} notify={notify} onClose={() => setShowTranslate(false)} />}
        {showDocs && <DocsPanel onClose={() => setShowDocs(false)} />}
        {showExamples && (
          <ExamplesPanel
            onApply={(code) => editorRef.current && editorRef.current.applyText(code)}
            onClose={() => setShowExamples(false)}
          />
        )}

        <main className="ide-main">
          {sel ? (
            <>
              <div className="ide-editor-row">
                <section className="ide-pane">
                  <MonacoEditor ref={editorRef} key={selId} src={src} onChange={onEdit} diags={diags} jump={jump} catalog={catalog} onCaret={setCaretPos} readOnly={imported} />
                </section>
                {showPreview && (
                  <section className="ide-pane ide-preview">
                    <ResizeHandle storageKey="ide-w-preview" side="left" min={300} max={900} />
                    <div className="ide-pane-head"><span>Compiled · {sel.id}.lvn</span></div>
                    <pre className={"code-output" + (error ? " error" : "")}>{output}</pre>
                  </section>
                )}
              </div>
              {showProblems && (
                <ProblemsDock diags={diags} onJump={goLine} onClose={() => setShowProblems(false)} />
              )}
            </>
          ) : (
            <div className="ide-blank">
              <p>This novel has no chapters yet.</p>
              <button className="btn btn-primary" onClick={addChapter}>+ Add the first chapter</button>
            </div>
          )}
        </main>
      </div>

      <div className="ide-status">
        <span className={"ide-stat " + stat.kind} title={stat.title}>{stat.text}</span>
        <span className="ide-status-sep" />
        <span className="ide-status-dim">{cmdCount} command{cmdCount === 1 ? "" : "s"}</span>
        {sel && <span className="ide-status-dim mono">{creds.path}</span>}
        <span className="grow" />
        {sel && <span className="ide-status-dim mono">Ln {caretPos.line}, Col {caretPos.col}</span>}
        <span className="ide-status-sep" />
        <button className={"ide-status-toggle" + (showProblems ? " on" : "")} onClick={() => setShowProblems((v) => !v)}>
          {errCount > 0 && <span className="dot err" />}
          {warnCount > 0 && <span className="dot warn" />}
          Problems {errCount + warnCount > 0 ? `(${errCount + warnCount})` : ""}
        </button>
      </div>
    </div>
  );
}

/* ── editor: gutter line numbers + syntax highlight + diagnostic markers ── */
const LH = 22; // line height in px — must match .code-* / .gutter / .markers
const PAD = 18; // editor padding-top in px

// Grammar + analysis live in the shared LVNScript core (tools/lvn-lang), so the
// panel and the language server use the exact same brain. Panel-only helpers
// (entityThumb) stay here.
// The entity's display thumbnail (first layer filled with its defaults).
function entityThumb(catalog, id) {
  const e = catalog && catalog[id];
  if (!e) return null;
  const def = e.defaults || {};
  for (const l of e.layers || []) {
    let u = typeof l === "string" ? l : l && l.url;
    if (!u) continue;
    u = u.replace(/\{([^}]+)\}/g, (_, k) => def[k] || "");
    if (!u.includes("{")) return u;
  }
  return null;
}

const Editor = forwardRef(function Editor({ src, onChange, markers = [], jump, catalog = {}, onCaret }, ref) {
  const taRef = useRef(null);
  const hlRef = useRef(null);
  const gutRef = useRef(null);
  const markRef = useRef(null);
  const charW = useRef(7.8);
  const [comp, setComp] = useState(null); // { token, items, sel, x, y, nav }
  const [ghost, setGhost] = useState(null); // { at, text } — inline Copilot-style preview
  const [help, setHelp] = useState(null); // { line, info } — always-on help under the caret line
  const [scrollY, setScrollY] = useState(0); // editor scroll offset, so help tracks its line
  const [caret, setCaret] = useState({ line: 1, col: 1 }); // for the current-line band + status
  const [find, setFind] = useState(null); // { q, rep, showRep, matches, idx } — find/replace bar
  const findRef = useRef(null);
  const [hover, setHover] = useState(null); // { word, x, y }
  const hoverWord = useRef("");
  const hist = useRef(null); // own undo stack (a controlled textarea kills native undo)
  if (hist.current === null) hist.current = { stack: [{ value: src, caret: 0 }], idx: 0, t: 0 };
  const navRef = useRef(false); // did the user arrow-navigate the popup? (Enter accepts only then)
  const tabRef = useRef(null);  // active snippet tab-stop session { stops:[pos], i }
  const prevVal = useRef(src);  // previous value, for tab-stop shift tracking

  // Load an example into the editor as ONE undoable step — Ctrl+Z brings your
  // own code back, because it goes through the same onChange + history path.
  useImperativeHandle(ref, () => ({
    applyText: (text) => {
      hist.current.t = 0; // discrete history entry (no coalescing with prior typing)
      onChange(text);
      record(text, 0);
      setComp(null);
      requestAnimationFrame(() => {
        const ta = taRef.current;
        if (ta) { ta.focus(); ta.setSelectionRange(0, 0); ta.scrollTop = 0; }
      });
    },
  }));

  const lineCount = useMemo(() => Math.max(1, src.split("\n").length), [src]);
  const labels = useMemo(() => labelsIn(src), [src]);
  const actorMap = useMemo(() => {
    const m = {};
    src.split("\n").forEach((l) => { const mm = l.match(/^\s*actor_map\s+(\S+)\s*=\s*(\S+)/); if (mm) m[mm[1]] = mm[2]; });
    return m;
  }, [src]);

  const sevByLine = useMemo(() => {
    const m = {};
    markers.forEach((k) => { if (m[k.line] !== "error") m[k.line] = k.sev; });
    return m;
  }, [markers]);

  // measure monospace character width once (for caret-anchored popup)
  useEffect(() => {
    const s = document.createElement("span");
    s.style.cssText = "position:absolute;visibility:hidden;white-space:pre;font-family:var(--font-mono),monospace;font-size:13px;";
    s.textContent = "00000000000000000000";
    document.body.appendChild(s);
    charW.current = s.getBoundingClientRect().width / 20 || 7.8;
    document.body.removeChild(s);
  }, []);

  function syncScroll(t, l) {
    if (hlRef.current) { hlRef.current.scrollTop = t; hlRef.current.scrollLeft = l; }
    if (gutRef.current) gutRef.current.scrollTop = t;
    if (markRef.current) markRef.current.scrollTop = t;
  }
  function onScroll(e) { syncScroll(e.target.scrollTop, e.target.scrollLeft); setComp(null); setGhost(null); setScrollY(e.target.scrollTop); }

  // Compute the inline ghost preview: only at a collapsed caret sitting at the
  // end of its line, and only when no completion popup is showing.
  function updateGhost(ta) {
    if (tabRef.current || ta.selectionStart !== ta.selectionEnd) { setGhost(null); return; }
    const caret = ta.selectionStart;
    const after = ta.value.slice(caret);
    if (after.length && after[0] !== "\n") { setGhost(null); return; }
    const before = ta.value.slice(0, caret);
    const lineToCaret = before.slice(before.lastIndexOf("\n") + 1);
    const g = predictGhost(lineToCaret, { catalog, actorMap });
    if (!g) { setGhost(null); return; }
    const lineNo = before.split("\n").length; // 1-based
    const x = 20 + lineToCaret.length * charW.current - ta.scrollLeft;
    const y = PAD + (lineNo - 1) * LH - ta.scrollTop;
    setGhost({ at: caret, text: g, x, y });
  }

  // Always-on signature help: describe the line the caret is on, anchored just
  // beneath it. Updates whenever the caret moves (type / click / arrow).
  function updateHelp(ta) {
    const caretPos = ta.selectionStart;
    const before = ta.value.slice(0, caretPos);
    const lineStart = before.lastIndexOf("\n") + 1;
    const lineNo = before.split("\n").length; // 1-based
    const fullLine = ta.value.slice(lineStart).split("\n")[0];
    const col = caretPos - lineStart;
    setCaret({ line: lineNo, col: col + 1 });
    if (onCaret) onCaret({ line: lineNo, col: col + 1 });
    const info = describeLine(fullLine, col, { catalog, actorMap, labels, src });
    if (!info || info.kind === "prose") { setHelp(null); return; }
    setHelp({ line: lineNo, info });
  }

  function refresh(ta) {
    updateHelp(ta);
    if (ta.selectionStart !== ta.selectionEnd) { setComp(null); setGhost(null); return; }
    const start = ta.selectionStart;
    const before = ta.value.slice(0, start);
    const lineToCaret = before.slice(before.lastIndexOf("\n") + 1);
    const r = completionAt(lineToCaret, labels, catalog, actorMap);
    if (!r) { setComp(null); updateGhost(ta); return; }
    setGhost(null);
    const lineNo = before.split("\n").length;
    const x = 20 + lineToCaret.length * charW.current - ta.scrollLeft; // inside .code-editor (after the gutter)
    const y = PAD + lineNo * LH - ta.scrollTop;
    navRef.current = false;
    setComp({ token: r.token, items: r.items.slice(0, 9), sel: 0, x, y });
  }

  function accept(item) {
    const ta = taRef.current;
    if (!ta || !comp || !item) return;
    const start = ta.selectionStart;
    const tokenStart = start - comp.token.length;
    if (item.kind === "snippet") {
      // Expand $1,$2,…,$0 into tab-stops; caret lands on the first.
      const re = /\$([0-9])/g;
      let plain = "", last = 0, mm;
      const found = [];
      while ((mm = re.exec(item.body)) !== null) {
        plain += item.body.slice(last, mm.index);
        found.push({ n: +mm[1], pos: tokenStart + plain.length });
        last = mm.index + mm[0].length;
      }
      plain += item.body.slice(last);
      const next = ta.value.slice(0, tokenStart) + plain + ta.value.slice(start);
      found.sort((a, b) => (a.n === 0 ? 99 : a.n) - (b.n === 0 ? 99 : b.n));
      const stops = found.map((f) => f.pos);
      const caret = stops.length ? stops[0] : tokenStart + plain.length;
      tabRef.current = stops.length > 1 ? { stops, i: 0 } : null; // Tab jumps between stops
      setComp(null);
      onChange(next); record(next, caret); prevVal.current = next;
      requestAnimationFrame(() => { ta.setSelectionRange(caret, caret); ta.focus(); });
      return;
    }
    let insert = item.text;
    if (item.kind === "op" || item.kind === "directive") insert += " ";
    else if (item.kind === "attr") insert += "=";
    else if (item.kind === "emotion") insert = item.text + (ta.value[start] === "]" ? "" : "]");
    else if (item.kind === "value" || item.kind === "entity") {
      const isBool = item.text === "true" || item.text === "false";
      const close = ta.value[start] === '"' ? "" : '"'; // don't add a 2nd closing quote
      if (isBool && !item.quoted) insert = item.text;            // bare bool
      else if (item.quoted) insert = item.text + close;          // opening quote already typed
      else insert = '"' + item.text + close;                     // wrap a string value
    }
    const next = ta.value.slice(0, tokenStart) + insert + ta.value.slice(start);
    const caret = tokenStart + insert.length;
    setComp(null);
    onChange(next); record(next, caret); prevVal.current = next;
    requestAnimationFrame(() => { ta.setSelectionRange(caret, caret); ta.focus(); });
  }

  // Accept the inline ghost: insert its text at the caret. If the text carries
  // its own `$`/quote tab-stops we set up a Tab-stop session like a snippet.
  function acceptGhost() {
    const ta = taRef.current;
    if (!ta || !ghost) return false;
    const at = ghost.at;
    if (ta.selectionStart !== at || ta.selectionEnd !== at) return false;
    let text = ghost.text;
    // figure out useful caret/tab-stops: land on the first empty "" / after =
    const stops = [];
    const re = /""/g; let mm;
    while ((mm = re.exec(text)) !== null) stops.push(at + mm.index + 1); // inside the quotes
    const trailing = /=$/.test(text) ? at + text.length : null;
    const next = ta.value.slice(0, at) + text + ta.value.slice(at);
    let caret;
    if (stops.length) { caret = stops[0]; tabRef.current = stops.length > 1 ? { stops, i: 0 } : null; }
    else if (trailing != null) caret = trailing;
    else caret = at + text.length;
    setGhost(null); setComp(null);
    onChange(next); record(next, caret); prevVal.current = next;
    requestAnimationFrame(() => { ta.setSelectionRange(caret, caret); ta.focus(); });
    return true;
  }

  // ── own undo/redo history (a controlled <textarea> wipes native undo) ──
  function record(value, caret) {
    const h = hist.current;
    if (h.stack[h.idx] && h.stack[h.idx].value === value) return;
    if (h.idx < h.stack.length - 1) h.stack.length = h.idx + 1; // drop redo tail
    const now = performance.now();
    if (now - h.t < 350 && h.idx > 0) h.stack[h.idx] = { value, caret }; // coalesce a fast burst
    else { h.stack.push({ value, caret }); h.idx = h.stack.length - 1; }
    h.t = now;
    if (h.stack.length > 300) { h.stack.shift(); h.idx--; }
  }
  function applyHist(en) { onChange(en.value); requestAnimationFrame(() => { const ta = taRef.current; if (ta) { ta.focus(); ta.setSelectionRange(en.caret, en.caret); } }); }
  function undo() { const h = hist.current; if (h.idx > 0) { h.idx--; applyHist(h.stack[h.idx]); } }
  function redo() { const h = hist.current; if (h.idx < h.stack.length - 1) { h.idx++; applyHist(h.stack[h.idx]); } }

  // ── find / replace ──
  function matchesOf(q) {
    if (!q) return [];
    const out = []; const hay = src.toLowerCase(), needle = q.toLowerCase();
    let i = hay.indexOf(needle);
    while (i !== -1) { out.push({ start: i, end: i + q.length }); i = hay.indexOf(needle, i + Math.max(1, q.length)); }
    return out;
  }
  function selectRange(start, end, focusTa = true) {
    const ta = taRef.current; if (!ta) return;
    ta.setSelectionRange(start, end);
    if (focusTa) { ta.focus(); updateHelp(ta); }
    const line = ta.value.slice(0, start).split("\n").length;
    const top = Math.max(0, (line - 1) * LH - 60);
    ta.scrollTop = top; syncScroll(top, 0); setScrollY(top);
  }
  // pixel rects for every match, for the highlight overlay (single-line matches)
  function matchRects(matches, activeIdx) {
    return matches.map((m, i) => {
      const before = src.slice(0, m.start);
      const line = before.split("\n").length;
      const col = m.start - (before.lastIndexOf("\n") + 1);
      const len = m.end - m.start;
      return { left: 20 + col * charW.current, top: PAD + (line - 1) * LH - scrollY, width: len * charW.current, active: i === activeIdx };
    });
  }
  function openFind(showRep) {
    const ta = taRef.current;
    const seed = ta && ta.selectionStart !== ta.selectionEnd ? ta.value.slice(ta.selectionStart, ta.selectionEnd) : (find ? find.q : "");
    const matches = matchesOf(seed);
    setFind({ q: seed, rep: find ? find.rep : "", showRep, matches, idx: matches.length ? 0 : -1 });
    setComp(null); setGhost(null);
    requestAnimationFrame(() => { if (findRef.current) { findRef.current.focus(); findRef.current.select(); } });
  }
  function setQuery(q) {
    const matches = matchesOf(q);
    setFind((f) => ({ ...f, q, matches, idx: matches.length ? 0 : -1 }));
    if (matches.length) selectRange(matches[0].start, matches[0].end, false); // keep focus in the find box
  }
  function step(dir) {
    setFind((f) => {
      if (!f || !f.matches.length) return f;
      const idx = (f.idx + dir + f.matches.length) % f.matches.length;
      const m = f.matches[idx]; selectRange(m.start, m.end);
      return { ...f, idx };
    });
  }
  function replaceOne() {
    const f = find; if (!f || f.idx < 0 || !f.matches.length) return;
    const m = f.matches[f.idx];
    const next = src.slice(0, m.start) + f.rep + src.slice(m.end);
    const caret = m.start + f.rep.length;
    onChange(next); record(next, caret); prevVal.current = next;
    requestAnimationFrame(() => {
      const matches = (() => { const out = []; const hay = next.toLowerCase(), needle = f.q.toLowerCase(); if (!needle) return out; let i = hay.indexOf(needle); while (i !== -1) { out.push({ start: i, end: i + f.q.length }); i = hay.indexOf(needle, i + Math.max(1, f.q.length)); } return out; })();
      const idx = matches.findIndex((x) => x.start >= m.start);
      setFind((ff) => ({ ...ff, matches, idx: matches.length ? (idx === -1 ? 0 : idx) : -1 }));
      if (matches.length) { const t = matches[idx === -1 ? 0 : idx]; selectRange(t.start, t.end); }
    });
  }
  function replaceAll() {
    const f = find; if (!f || !f.q || !f.matches.length) return;
    const re = new RegExp(f.q.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), "gi");
    const next = src.replace(re, f.rep);
    onChange(next); record(next, 0); prevVal.current = next;
    setFind((ff) => ({ ...ff, matches: [], idx: -1 }));
  }
  function closeFind() { setFind(null); requestAnimationFrame(() => taRef.current && taRef.current.focus()); }

  // ── small editor conveniences: comment toggle & auto-closing pairs ──
  const PAIR = { '"': '"', "[": "]", "{": "}", "(": ")" };
  const CLOSERS = new Set(['"', "]", "}", ")"]);
  function applyEdit(ta, value, caretStart, caretEnd) {
    onChange(value); record(value, caretStart); prevVal.current = value;
    requestAnimationFrame(() => { ta.setSelectionRange(caretStart, caretEnd == null ? caretStart : caretEnd); ta.focus(); refresh(ta); });
  }
  function toggleComment(ta) {
    const v = ta.value, s = ta.selectionStart, e2 = ta.selectionEnd;
    const from = v.lastIndexOf("\n", s - 1) + 1;
    let to = v.indexOf("\n", e2); if (to === -1) to = v.length;
    const block = v.slice(from, to);
    const lines = block.split("\n");
    const allOff = lines.every((l) => !l.trim() || /^\s*\/\//.test(l));
    const out = lines.map((l) => {
      if (!l.trim()) return l;
      if (allOff) return l.replace(/^(\s*)\/\/ ?/, "$1");           // uncomment
      return l.replace(/^(\s*)/, "$1// ");                          // comment
    }).join("\n");
    const next = v.slice(0, from) + out + v.slice(to);
    applyEdit(ta, next, from, from + out.length);
  }
  function autoPair(e, ta) {
    const k = e.key, v = ta.value, s = ta.selectionStart, en = ta.selectionEnd;
    // type a closing char that's already there → step over it
    if (CLOSERS.has(k) && s === en && v[s] === k) { e.preventDefault(); applyEdit(ta, v, s + 1); return true; }
    if (PAIR[k]) {
      e.preventDefault();
      const close = PAIR[k];
      if (s !== en) { const next = v.slice(0, s) + k + v.slice(s, en) + close + v.slice(en); applyEdit(ta, next, s + 1, en + 1); return true; }
      const next = v.slice(0, s) + k + close + v.slice(s);
      applyEdit(ta, next, s + 1); return true;
    }
    // Backspace between an empty pair → remove both chars
    if (k === "Backspace" && s === en && s > 0 && PAIR[v[s - 1]] === v[s]) {
      e.preventDefault(); const next = v.slice(0, s - 1) + v.slice(s + 1); applyEdit(ta, next, s - 1); return true;
    }
    return false;
  }

  function onKeyDown(e) {
    if ((e.metaKey || e.ctrlKey) && (e.key === "z" || e.key === "Z")) { e.preventDefault(); setComp(null); e.shiftKey ? redo() : undo(); return; }
    if ((e.metaKey || e.ctrlKey) && (e.key === "y" || e.key === "Y")) { e.preventDefault(); setComp(null); redo(); return; }
    if ((e.metaKey || e.ctrlKey) && e.key === "/") { e.preventDefault(); setComp(null); setGhost(null); toggleComment(e.target); return; }
    if ((e.metaKey || e.ctrlKey) && (e.key === "f" || e.key === "F")) { e.preventDefault(); openFind(false); return; }
    if ((e.metaKey || e.ctrlKey) && (e.key === "h" || e.key === "H")) { e.preventDefault(); openFind(true); return; }
    // auto-closing pairs (skip when a popup is actively driving the keys)
    if (!e.metaKey && !e.ctrlKey && !e.altKey && !(comp && navRef.current)) {
      if ((PAIR[e.key] || CLOSERS.has(e.key) || e.key === "Backspace") && autoPair(e, e.target)) return;
    }
    if (comp) {
      if (e.key === "ArrowDown") { e.preventDefault(); navRef.current = true; setComp((c) => (c ? { ...c, sel: (c.sel + 1) % c.items.length } : c)); return; }
      if (e.key === "ArrowUp") { e.preventDefault(); navRef.current = true; setComp((c) => (c ? { ...c, sel: (c.sel - 1 + c.items.length) % c.items.length } : c)); return; }
      if (e.key === "Tab") { e.preventDefault(); accept(comp.items[comp.sel]); return; }
      if (e.key === "Enter") {
        // accept only if you actually navigated the list; otherwise it's a newline
        if (navRef.current) { e.preventDefault(); accept(comp.items[comp.sel]); return; }
        setComp(null); return;
      }
      if (e.key === "Escape") { e.preventDefault(); setComp(null); tabRef.current = null; return; }
    }
    if (e.key === "Escape" && ghost) { setGhost(null); return; }
    // moving the caret away ends a snippet tab-stop session
    if (tabRef.current && ["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Home", "End", "Escape"].includes(e.key)) {
      tabRef.current = null;
    }
    // an arrow press dismisses the ghost (it'll recompute on caret move)
    if (ghost && ["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].includes(e.key)) setGhost(null);
    if (e.key === "Tab") {
      e.preventDefault();
      // jump to the next snippet tab-stop, if a session is active
      if (tabRef.current) {
        const sess = tabRef.current;
        sess.i++;
        if (sess.i < sess.stops.length) {
          const pos = sess.stops[sess.i];
          const ta = e.target;
          if (sess.i >= sess.stops.length - 1) tabRef.current = null; // landed on the last stop
          requestAnimationFrame(() => ta.setSelectionRange(pos, pos));
          return;
        }
        tabRef.current = null;
      }
      // accept the inline ghost preview (Copilot-style)
      if (ghost && acceptGhost()) return;
      const ta = e.target, s = ta.selectionStart, en = ta.selectionEnd;
      const next = ta.value.slice(0, s) + "  " + ta.value.slice(en);
      onChange(next); record(next, s + 2); prevVal.current = next;
      requestAnimationFrame(() => ta.setSelectionRange(s + 2, s + 2));
    }
  }

  // Ctrl/Cmd+click a label reference (goto/call/->/then/else target) → jump to
  // its `:definition`. Returns true if it navigated.
  function gotoDef(e) {
    const ta = e.target, rect = ta.getBoundingClientRect();
    const x = e.clientX - rect.left - 20 + ta.scrollLeft;
    const y = e.clientY - rect.top - PAD + ta.scrollTop;
    if (x < 0 || y < 0) return false;
    const line = Math.floor(y / LH) + 1, col = Math.round(x / charW.current);
    const w = wordAt(src, line, col);
    if (!w) return false;
    const lineText = src.split("\n")[line - 1] || "";
    const isRef = /(?:goto|call|->|then\s*=|else\s*=)/.test(lineText) && /^[A-Za-z0-9_]+$/.test(w);
    if (!isRef || w === "__end") return false;
    const lines = src.split("\n");
    const defLine = lines.findIndex((l) => new RegExp("^\\s*:" + w + "\\b").test(l));
    if (defLine < 0) return false;
    let off = 0; for (let i = 0; i < defLine; i++) off += lines[i].length + 1;
    selectRange(off, off + lines[defLine].length);
    return true;
  }

  // Hover docs: the op, character or emotion under the cursor.
  function onMove(e) {
    const ta = e.target;
    const rect = ta.getBoundingClientRect();
    const x = e.clientX - rect.left - 20 + ta.scrollLeft;
    const y = e.clientY - rect.top - PAD + ta.scrollTop;
    if (x < 0 || y < 0) { if (hoverWord.current) { hoverWord.current = ""; setHover(null); } return; }
    const line = Math.floor(y / LH) + 1;
    const col = Math.round(x / charW.current);
    const w = wordAt(src, line, col);
    if (w === hoverWord.current) return;
    hoverWord.current = w || "";
    if (!w) { setHover(null); return; }
    const at = { x: e.clientX, y: e.clientY };
    const lineText = src.split("\n")[line - 1] || "";

    if (OP_DOCS[w]) { setHover({ kind: "op", word: w, ...at }); return; }

    // speaker line `Name: …` or `Name [emo]: …` — resolve name → character, and
    // tell a valid emotion from an invalid one.
    const sm = lineText.match(/^([^:[\]]+?)(?:\s*\[([^\]]*)\])?\s*:/);
    if (sm) {
      const name = sm[1].trim(), emo = (sm[2] || "").trim();
      const { id, ent } = speakerEntity(name, catalog, actorMap);
      if (emo && (w === emo || emo.split(/\s+/).includes(w))) {
        const ok = !!(ent && ent.axes && ent.axes.emotion && ent.axes.emotion.includes(w));
        setHover({ kind: "emotion", emo: w, charId: id, ok, ...at }); return;
      }
      if (name.split(/\s+/).includes(w)) { setHover(ent ? { kind: "entity", id, ...at } : null); return; }
    }
    // a bare catalog id (e.g. id="mara", bg id="porch"), case-insensitive
    const lw = w.toLowerCase();
    const direct = catalog ? (catalog[w] ? w : (catalog[lw] ? lw : null)) : null;
    if (direct) { setHover({ kind: "entity", id: direct, ...at }); return; }

    // a label — either its `:def` or a `goto/call/->/then/else` reference
    const isLabelCtx = /^\s*:/.test(lineText) || /(?:goto|call|->|then\s*=|else\s*=)/.test(lineText);
    if (isLabelCtx && /^[A-Za-z0-9_]+$/.test(w) && (labels.includes(w) || w === "__end" || /(?:goto|call|->)\s*"?$/.test(lineText.slice(0, lineText.indexOf(w))))) {
      const info = labelInfo(src, w);
      setHover({ kind: "label", name: w, ...info, ...at }); return;
    }

    // a variable — inside {interpolation}, an expr="…", or a set/inc key.
    // Case-insensitive so hovering prose "Score" still resolves var {score}.
    const vi = varInfo(src, w);
    const inInterp = new RegExp("\\{[^}]*\\b" + w + "\\b[^}]*\\}", "i").test(lineText);
    const inSetKey = new RegExp('(?:set|inc)\\s+key="' + w + '"', "i").test(lineText);
    const inExpr = new RegExp('(?:expr|value)="[^"]*\\b' + w + '\\b', "i").test(lineText);
    if (vi.known && (inInterp || inSetKey || inExpr)) { setHover({ kind: "var", name: w, ...vi, ...at }); return; }

    setHover(null);
  }

  // Jump to a source line: select it and scroll it into view.
  useEffect(() => {
    if (!jump || !jump.line || !taRef.current) return;
    const ta = taRef.current;
    const lines = src.split("\n");
    let off = 0;
    for (let i = 0; i < jump.line - 1 && i < lines.length; i++) off += lines[i].length + 1;
    const end = off + (lines[jump.line - 1]?.length || 0);
    ta.focus();
    ta.setSelectionRange(off, end);
    const top = Math.max(0, (jump.line - 1) * LH - 60);
    ta.scrollTop = top;
    syncScroll(top, 0); setScrollY(top); updateHelp(ta);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [jump?.n]);

  // keep find matches in sync when the document changes under an open find bar
  useEffect(() => {
    if (!find) return;
    const matches = matchesOf(find.q);
    setFind((f) => (f ? { ...f, matches, idx: matches.length ? Math.min(f.idx < 0 ? 0 : f.idx, matches.length - 1) : -1 } : f));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [src]);

  return (
    <div className="editor-wrap">
      <div className="gutter" ref={gutRef}>
        {Array.from({ length: lineCount }, (_, i) => (
          <div key={i} className={"gline" + (sevByLine[i + 1] ? " g-" + (sevByLine[i + 1] === "error" ? "error" : "warn") : "")}>{i + 1}</div>
        ))}
      </div>
      <div className="code-editor">
        {find && (
          <div className="find-bar" onKeyDown={(e) => {
            if (e.key === "Escape") { e.preventDefault(); closeFind(); }
            else if (e.key === "Enter") { e.preventDefault(); step(e.shiftKey ? -1 : 1); }
          }}>
            <div className="find-row">
              <input ref={findRef} className="find-input" placeholder="Find" value={find.q}
                onChange={(e) => setQuery(e.target.value)} />
              <span className="find-count">{find.matches.length ? `${find.idx + 1}/${find.matches.length}` : (find.q ? "0/0" : "")}</span>
              <button className="find-btn" title="Previous (⇧⏎)" onClick={() => step(-1)} disabled={!find.matches.length}>↑</button>
              <button className="find-btn" title="Next (⏎)" onClick={() => step(1)} disabled={!find.matches.length}>↓</button>
              <button className={"find-btn" + (find.showRep ? " on" : "")} title="Toggle replace" onClick={() => setFind((f) => ({ ...f, showRep: !f.showRep }))}>⇄</button>
              <button className="find-btn" title="Close (Esc)" onClick={closeFind}>✕</button>
            </div>
            {find.showRep && (
              <div className="find-row">
                <input className="find-input" placeholder="Replace" value={find.rep}
                  onChange={(e) => setFind((f) => ({ ...f, rep: e.target.value }))} />
                <button className="find-btn wide" onClick={replaceOne} disabled={find.idx < 0}>Replace</button>
                <button className="find-btn wide" onClick={replaceAll} disabled={!find.matches.length}>All</button>
              </div>
            )}
          </div>
        )}
        <div className="current-line" aria-hidden="true" style={{ top: PAD + (caret.line - 1) * LH - scrollY }} />
        <pre className="code-highlight" aria-hidden="true" ref={hlRef}>
          <code dangerouslySetInnerHTML={{ __html: highlightLvns(src) + "\n" }} />
        </pre>
        <div className="markers" aria-hidden="true" ref={markRef}>
          {markers.map((m, i) => (
            <div key={i} className={"marker " + (m.sev === "error" ? "error" : "warn")} style={{ top: PAD + (m.line - 1) * LH + "px" }} />
          ))}
        </div>
        {find && find.matches.length > 0 && (
          <div className="find-hits" aria-hidden="true">
            {matchRects(find.matches, find.idx).map((r, i) => (
              <div key={i} className={"find-hit" + (r.active ? " active" : "")} style={{ left: r.left, top: r.top, width: r.width }} />
            ))}
          </div>
        )}
        {ghost && !comp && (
          <div className="ghost-layer" aria-hidden="true">
            <span className="ghost" style={{ left: ghost.x, top: ghost.y }}>
              {ghost.text}
              <span className="ghost-tab">⇥ Tab</span>
            </span>
          </div>
        )}
        <textarea
          className="code-input"
          ref={taRef}
          spellCheck={false}
          wrap="off"
          value={src}
          onChange={(e) => {
            const v = e.target.value;
            // keep later tab-stops aligned as you type into the current one
            if (tabRef.current) {
              const delta = v.length - prevVal.current.length;
              if (delta !== 0) {
                const editPos = e.target.selectionStart - Math.max(0, delta);
                const s = tabRef.current;
                s.stops = s.stops.map((p, idx) => (idx > s.i && p >= editPos ? p + delta : p));
              }
            }
            prevVal.current = v;
            onChange(v); record(v, e.target.selectionStart); refresh(e.target);
          }}
          onKeyDown={onKeyDown}
          onKeyUp={(e) => { if (["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Home", "End"].includes(e.key)) refresh(e.target); }}
          onClick={(e) => {
            tabRef.current = null;
            if (e.metaKey || e.ctrlKey) { if (gotoDef(e)) return; }
            refresh(e.target);
          }}
          onBlur={() => setTimeout(() => { setComp(null); setGhost(null); setHelp(null); }, 150)}
          onScroll={onScroll}
          onMouseMove={onMove}
          onMouseLeave={() => { hoverWord.current = ""; setHover(null); }}
        />
        {comp && (() => {
          const selItem = comp.items[comp.sel];
          const doc = selItem && OP_DOCS[selItem.text];
          const isEntity = selItem && selItem.kind === "entity";
          const thumb = isEntity ? entityThumb(catalog, selItem.text) : null;
          const ent = isEntity ? catalog[selItem.text] : null;
          return (
            <div className="ac" style={{ left: comp.x, top: comp.y }}>
              <div className="ac-list">
                {comp.items.map((it, i) => (
                  <div
                    key={i}
                    className={"ac-item" + (i === comp.sel ? " sel" : "")}
                    onMouseEnter={() => setComp((c) => (c ? { ...c, sel: i } : c))}
                    onMouseDown={(e) => { e.preventDefault(); accept(it); }}
                  >
                    <span className={"ac-kind k-" + it.kind}>{it.kind[0]}</span>
                    <span className="ac-text">{it.label || it.text}</span>
                  </div>
                ))}
              </div>
              {isEntity ? (
                <div className="ac-doc ac-entity">
                  {thumb ? <img src={thumb} alt="" onError={(e) => { e.currentTarget.style.display = "none"; }} /> : <div className="ac-noimg">no art yet</div>}
                  <code>{ent && ent.name ? ent.name : selItem.text}</code>
                  <span>{ent && ent.axes ? Object.keys(ent.axes).join(" · ") : "object"}</span>
                </div>
              ) : doc ? (
                <div className="ac-doc">
                  <code>{doc[0]}</code>
                  <span>{doc[1]}</span>
                </div>
              ) : null}
            </div>
          );
        })()}
        {help && !comp && (() => {
          const top = PAD + help.line * LH - scrollY + 3; // just under the caret line
          if (top < PAD - LH) return null; // scrolled out of view above
          const i = help.info;
          if (i.kind === "op") {
            // split the signature so the active argument can be emphasised
            const parts = i.sig.split(/(\b[a-z_]+=)/g);
            return (
              <div className="line-help" style={{ top }}>
                <code className="lh-sig">
                  {parts.map((p, k) => {
                    const key = p.endsWith("=") ? p.slice(0, -1) : null;
                    const on = key && i.active === key;
                    return <span key={k} className={on ? "lh-arg on" : key ? "lh-arg" : ""}>{p}</span>;
                  })}
                </code>
                <span className="lh-desc">{i.desc}</span>
              </div>
            );
          }
          if (i.kind === "speaker") {
            return (
              <div className="line-help" style={{ top }}>
                <code className="lh-sig">{i.name}{i.emo ? ` [${i.emo}]` : ""}: …</code>
                <span className="lh-desc">
                  {i.id
                    ? (i.emo
                        ? (i.valid ? `“${i.emo}” ✓ emotion of ${i.id}` : `“${i.emo}” ⚠ unknown — try: ${(i.emos || []).join(", ")}`)
                        : (i.emos ? `dialogue · moods: ${i.emos.join(", ")}` : "dialogue"))
                    : "dialogue (no catalog match — add to cast or actor_map)"}
                </span>
              </div>
            );
          }
          if (i.kind === "choice") {
            return (
              <div className="line-help" style={{ top }}>
                <code className="lh-sig">- Text -&gt; {i.target || "target"}</code>
                <span className="lh-desc">
                  {!i.target ? "a choice — add “-> label” to set its destination"
                    : i.defined ? `jumps to ${i.target === "__end" ? "the end" : `:${i.target}`} ✓`
                    : `⚠ “${i.target}” is not a defined label`}
                </span>
              </div>
            );
          }
          if (i.kind === "labeldef") {
            return (
              <div className="line-help" style={{ top }}>
                <code className="lh-sig">:{i.name}</code>
                <span className="lh-desc">a jump target · {i.refs} jump{i.refs === 1 ? "" : "s"} here{i.refs === 0 ? " (unreachable)" : ""}</span>
              </div>
            );
          }
          return null;
        })()}
        {hover && (() => {
          const style = { left: hover.x + 14, top: hover.y + 18 };
          if (hover.kind === "op" && OP_DOCS[hover.word]) {
            return (
              <div className="op-doc" style={style}>
                <code>{OP_DOCS[hover.word][0]}</code>
                <span>{OP_DOCS[hover.word][1]}</span>
              </div>
            );
          }
          if (hover.kind === "entity") {
            const ent = catalog[hover.id];
            const thumb = entityThumb(catalog, hover.id);
            return (
              <div className="op-doc op-char" style={style}>
                {thumb ? <img src={thumb} alt="" onError={(e) => { e.currentTarget.style.display = "none"; }} /> : <div className="ac-noimg">no art yet</div>}
                <code>{ent && ent.name ? ent.name : hover.id}</code>
                <span>{ent && ent.axes ? Object.keys(ent.axes).map((a) => a + ": " + (ent.axes[a] || []).join("/")).join(" · ") : "object"}</span>
              </div>
            );
          }
          if (hover.kind === "emotion") {
            const ent = catalog[hover.charId];
            const list = ent && ent.axes && ent.axes.emotion ? ent.axes.emotion : [];
            return (
              <div className="op-doc" style={style}>
                <code>{hover.emo}{hover.ok ? " ✓" : " ⚠"}</code>
                <span>{hover.ok ? `emotion of ${hover.charId}` : (list.length ? `not an emotion of ${hover.charId} — try: ${list.join(", ")}` : `emotion (no catalog for ${hover.charId})`)}</span>
              </div>
            );
          }
          if (hover.kind === "label") {
            return (
              <div className="op-doc" style={style}>
                <code>:{hover.name}{hover.defined ? " ✓" : " ⚠"}</code>
                <span>
                  {hover.name === "__end" ? "ends the chapter"
                    : hover.defined
                      ? `label · defined at line ${hover.defLine} · ${hover.refs} jump${hover.refs === 1 ? "" : "s"}${hover.target ? ` → ${hover.target}` : ""}`
                      : "undefined label — no matching :definition"}
                </span>
              </div>
            );
          }
          if (hover.kind === "var") {
            return (
              <div className="op-doc" style={style}>
                <code>{hover.name}{hover.lastVal ? " " + hover.lastVal : ""}</code>
                <span>variable · set {hover.sets}× · read {hover.uses}×{hover.sets === 0 ? " · never assigned" : ""}</span>
              </div>
            );
          }
          return null;
        })()}
      </div>
    </div>
  );
});

/* ── Problems dock — click a row to jump to its source line ────────────── */
function ProblemsDock({ diags, onJump, onClose }) {
  const errCount = diags.filter((d) => d.sev === "error").length;
  const warnCount = diags.filter((d) => d.sev === "warning").length;
  const rows = [...diags].sort((a, b) => (a.sev === b.sev ? (a.line || 0) - (b.line || 0) : a.sev === "error" ? -1 : 1));
  return (
    <div className="diagnostics">
      <div className="diag-head">
        <span className="diag-title">Problems</span>
        {errCount > 0 && <span className="diag-count err">{errCount} error{errCount > 1 ? "s" : ""}</span>}
        {warnCount > 0 && <span className="diag-count warn">{warnCount} warning{warnCount > 1 ? "s" : ""}</span>}
        {diags.length === 0 && <span className="diag-count ok">no problems</span>}
        <span className="grow" />
        <button className="btn-ghost sm" onClick={onClose}>✕</button>
      </div>
      <div className="diag-list">
        {diags.length === 0 && <div className="diag-clean">Nothing to fix — the chapter compiles clean.</div>}
        {rows.map((d, i) => (
          <button
            key={i}
            className={"diag-row " + (d.sev === "error" ? "error" : "warn")}
            onClick={() => d.line > 0 && onJump(d.line)}
            title={d.line > 0 ? "Go to line " + d.line : ""}
          >
            <span className="diag-dot" />
            <span className={"diag-loc" + (d.line > 0 ? "" : " dim")}>{d.line > 0 ? "line " + d.line : "—"}</span>
            <span className="diag-msg">{d.op ? <em>{d.op}</em> : null}{d.op ? " · " : ""}{d.msg}</span>
          </button>
        ))}
      </div>
    </div>
  );
}

// Quick Open (Ctrl/Cmd+P): fuzzy-jump to any chapter by id, episode name or
// number — the "go to file" every IDE has. Arrow keys + Enter, Esc closes.
function QuickOpen({ chapters, currentId, onPick, onClose }) {
  const [q, setQ] = useState("");
  const [idx, setIdx] = useState(0);

  const needle = q.trim().toLowerCase();
  const hits = chapters.filter((c) => {
    if (!needle) return true;
    const hay = `${c.id} ${c.name || ""} ${c.number || ""}`.toLowerCase();
    // every space-separated term must appear somewhere (order-free)
    return needle.split(/\s+/).every((t) => hay.includes(t));
  });
  const sel = Math.min(idx, Math.max(0, hits.length - 1));

  function onKey(e) {
    if (e.key === "ArrowDown") { e.preventDefault(); setIdx((i) => Math.min(i + 1, hits.length - 1)); }
    else if (e.key === "ArrowUp") { e.preventDefault(); setIdx((i) => Math.max(i - 1, 0)); }
    else if (e.key === "Enter") { e.preventDefault(); if (hits[sel]) onPick(hits[sel]); }
    else if (e.key === "Escape") { e.preventDefault(); onClose(); }
    e.stopPropagation();
  }

  return (
    <div className="qo-overlay" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div className="qo-box">
        <input
          autoFocus
          className="qo-input"
          placeholder="Chapter… (id, name or number)"
          value={q}
          onChange={(e) => { setQ(e.target.value); setIdx(0); }}
          onKeyDown={onKey}
        />
        <div className="qo-list">
          {hits.map((c, i) => (
            <button
              key={c.id}
              className={"qo-item" + (i === sel ? " active" : "") + (c.id === currentId ? " current" : "")}
              onMouseEnter={() => setIdx(i)}
              onClick={() => onPick(c)}
            >
              <span className="qo-item-id">{c.id}</span>
              {c.name ? <span className="qo-item-name">{c.name}</span> : null}
            </button>
          ))}
          {hits.length === 0 && <div className="qo-empty">No chapters match</div>}
        </div>
      </div>
    </div>
  );
}

// Search across every chapter (Ctrl/Cmd+Shift+F): fetches each chapter's
// .lvns source once, greps case-insensitively, and jumps straight to the
// matched line in the right chapter — the workspace search every IDE has.
function SearchAll({ chapters, onPick, onClose }) {
  const [q, setQ] = useState("");
  const [hits, setHits] = useState([]);
  const [busy, setBusy] = useState(false);
  const cache = useRef({}); // chapter id → source text (per overlay session)
  const runTimer = useRef(0);

  useEffect(() => () => clearTimeout(runTimer.current), []);

  function schedule(text) {
    setQ(text);
    clearTimeout(runTimer.current);
    if (text.trim().length < 2) { setHits([]); return; }
    runTimer.current = setTimeout(() => run(text), 250);
  }

  async function run(text) {
    const needle = text.toLowerCase();
    setBusy(true);
    const out = [];
    for (const c of chapters) {
      if (out.length >= 200) break; // enough to act on
      let src = cache.current[c.id];
      if (src == null) {
        try {
          const url = String(c.script_url || "").replace(/\.lvn$/, ".lvns");
          const r = await fetch(url + "?v=" + Date.now(), { cache: "no-store" });
          src = r.ok ? await r.text() : "";
          if (src.trimStart().startsWith("{")) src = ""; // compiled import — no source to grep
        } catch { src = ""; }
        cache.current[c.id] = src;
      }
      if (!src) continue;
      const lines = src.split("\n");
      for (let i = 0; i < lines.length && out.length < 200; i++) {
        if (lines[i].toLowerCase().includes(needle))
          out.push({ ch: c, line: i + 1, text: lines[i].trim().slice(0, 120) });
      }
    }
    setHits(out);
    setBusy(false);
  }

  return (
    <div className="qo-overlay" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div className="qo-box">
        <input
          autoFocus
          className="qo-input"
          placeholder="Search in all chapters… (2+ characters)"
          value={q}
          onChange={(e) => schedule(e.target.value)}
          onKeyDown={(e) => { if (e.key === "Escape") { e.preventDefault(); onClose(); } e.stopPropagation(); }}
        />
        <div className="qo-list">
          {busy && <div className="qo-empty">Searching…</div>}
          {!busy && hits.map((h, i) => (
            <button key={i} className="qo-item" onClick={() => onPick(h.ch, h.line)}>
              <span className="qo-item-id">{h.ch.id}:{h.line}</span>
              <span className="qo-item-name">{h.text}</span>
            </button>
          ))}
          {!busy && q.trim().length >= 2 && hits.length === 0 && <div className="qo-empty">No matches</div>}
        </div>
      </div>
    </div>
  );
}
