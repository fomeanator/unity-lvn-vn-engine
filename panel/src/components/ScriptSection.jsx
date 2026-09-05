import { useEffect, useMemo, useRef, useState } from "react";
import { getManifest, getExtGrammar, putAsset, readSource, adminFiles, rebuildDependents } from "../lib/api.js";
import { ensureWasm, compileLvns } from "../lib/wasm.js";
import DocsPanel from "./DocsPanel.jsx";
import ExamplesPanel from "./ExamplesPanel.jsx";
import ExportPanel from "./ExportPanel.jsx";
import ThemePanel from "./ThemePanel.jsx";
import TranslatePanel from "./TranslatePanel.jsx";
import ResizeHandle from "./ResizeHandle.jsx";
import MonacoEditor from "./MonacoEditor.jsx";

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
// Ключ и адрес подключаемого файла. У ПАКЕТА (docs/packages.md) путь значим
// целиком: "@scope/pkg/duel.lvns" — не то же, что "duel.lvns" соседней
// новеллы, и на сервере он лежит в scripts/lvns_packages/. Обычный include
// как был: файл рядом, ключ — его имя.
function incKey(raw) {
  const s = String(raw);
  return s.startsWith("@") ? s : s.split("/").pop();
}
function incUrl(key) {
  return String(key).startsWith("@")
    ? "/content/scripts/lvns_packages/" + key
    : "/content/scripts/" + key;
}

// Все id глав манифеста — по ним отличаем главу от библиотеки в плоском
// каталоге scripts/. Ходит по всем титулам: файл главы принадлежит своей
// новелле, даже когда открыта соседняя.
function allChapterIds(manifest) {
  const out = [];
  (manifest?.titles || []).forEach((t) =>
    (t.seasons || []).forEach((s) => (s.chapters || []).forEach((c) => out.push(c.id))));
  return out;
}

export default function ScriptSection({ creds, notify, titleId, setStatus }) {
  const [title, setTitle] = useState(null);
  // The manifest fetch takes a beat — until it lands we genuinely don't know
  // whether the novel has chapters, so the blank "no chapters yet" screen must
  // not flash. booting=true → loader instead.
  const [booting, setBooting] = useState(true);
  const [published, setPublished] = useState(() => new Set()); // chapter ids live on the server
  const [selId, setSelId] = useState(null);
  const [catalog, setCatalog] = useState({}); // manifest.sprites — for id/axes autocomplete
  // The project's host-op declaration (content/ext-grammar.json): completion,
  // hover, ghost AND the wasm validator all read it. The ref keeps compile()
  // in sync without re-creating it; a late-arriving declaration recompiles.
  const [extGrammar, setExtGrammar] = useState(null);
  const extGrammarRef = useRef(null);
  const [bust, setBust] = useState(() => Date.now());

  const [src, setSrc] = useState("");
  const [output, setOutput] = useState("");
  const [imported, setImported] = useState(false); // chapter is a read-only server-side .lvn (articy import)
  const [error, setError] = useState(false);
  const [diags, setDiags] = useState([]); // [{ sev, line, op, msg }]
  const [jump, setJump] = useState({ line: 0, n: 0 });
  const [stat, setStat] = useState({ kind: "warn", text: "…", title: "" });

  // ИТОГ КОМПИЛЯЦИИ ЕДЕТ В ДВА МЕСТА: своя плашка и шапка родителя. Значение
  // одно, приёмника два, и держались они в согласии руками — три места, в
  // каждом по паре строк. Забудь вторую, и шапка покажет прошлый итог рядом со
  // свежим: автор чинит ошибку, ошибка исчезает из плашки и остаётся наверху.
  const report = (s) => { setStat(s); setStatus?.(s); };
  const [showPreview, setShowPreview] = useState(true);
  const [showProblems, setShowProblems] = useState(true);
  const [showDocs, setShowDocs] = useState(false);
  const [showExamples, setShowExamples] = useState(false);
  const [showExport, setShowExport] = useState(false);
  const [showTheme, setShowTheme] = useState(false);
  const [showTranslate, setShowTranslate] = useState(false);
  const [newMenu, setNewMenu] = useState(false);
  const [caretPos, setCaretPos] = useState({ line: 1, col: 1 });
  // Каретка нужна не только статус-бару: по ней Cmd/Ctrl+клик определяет, на
  // какой строке щёлкнули (см. onEditorClick). Из state её там не прочитать —
  // обработчик замкнут на прошлый рендер, поэтому дублируем в реф.
  const caretRef = useRef({ line: 1, col: 1 });
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
      setBooting(true);
      try {
        try { setExtGrammar(await getExtGrammar()); }
        catch (e) { notify("ext-grammar.json: " + ((e && e.message) || "не читается"), "err"); }
        let t = null;
        // Манифест нужен и ПОСЛЕ этого блока (главы всех титулов для фильтра
        // общих файлов), поэтому переменная живёт снаружи try, а не внутри.
        let man = null;
        try {
          man = await getManifest();
          setCatalog(man.sprites || {});
          t = (man.titles || []).find((x) => x.id === titleId) || null;
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
        await loadShared(ids, allChapterIds(man));
        if (first) openChapter(first); else { setSrc(""); compile(""); }
      } finally {
        setBooting(false);
      }
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [titleId]);

  // ── общие файлы новеллы (те, что подключают через include) ─────────────
  //
  // Список FILES строился ТОЛЬКО из глав манифеста, поэтому общий файл механик
  // в студии не было видно и нельзя было открыть — «файлами невозможно
  // управлять». А компилятор в браузере получал текст без соседей, и любая
  // глава с include падала с «подключение работает только при компиляции
  // файла». Обе беды лечит одно: знать все .lvns новеллы, а не только главы.
  const [shared, setShared] = useState([]);        // имена общих файлов
  const sourcesRef = useRef({});                    // имя.lvns -> текст (null = точно нет)
  const [sharedName, setSharedName] = useState(""); // открыт общий файл, а не глава
  // Имя открытого файла держим в РЕФЕ, а не только в state: compile() зовут
  // сразу после setSelId, и в том замыкании state ещё старый — из-за этого
  // компилятор получал пустое имя, уходил в путь «текст без файлов» и выдавал
  // «подключение работает только при компиляции файла» на живой главе.
  const curFileRef = useRef("");
  // Открыт ли БИБЛИОТЕЧНЫЙ файл — тоже в рефе, и по той же причине: compile()
  // зовут сразу после setSharedName, и в его замыкании state ещё старый.
  const libOpenRef = useRef(false);

  // Докачка подключаемых файлов ПРЯМО ИЗ СТАТИКИ. Каталог scripts/ отдаётся
  // публично, поэтому include не зависит ни от admin-токена, ни от листинга
  // каталога: раньше зависел, и с пустым полем токена в студии он не работал
  // вовсе. Транзитивно: подключённый файл сам может что-то подключать.
  async function ensureIncludes(text, depth = 0) {
    if (depth > 8) return; // цикл поймает компилятор, здесь только страховка
    const rx = /^[ \t]*include[ \t]+"([^"]+)"[ \t]*$/gm;
    const names = [];
    let m;
    while ((m = rx.exec(text || ""))) names.push(incKey(m[1]));
    const missing = names.filter((n) => sourcesRef.current[n] === undefined);
    if (!missing.length) return;
    await Promise.all(missing.map(async (n) => {
      try {
        const r = await fetch(incUrl(n) + "?v=" + Date.now(), { cache: "no-store" });
        const t = r.ok ? await r.text() : null;
        // Статика при отсутствии .lvns может отдать что-то другое — компилятору
        // нужен исходник, а не JSON скомпилированной главы.
        sourcesRef.current[n] = t && !t.trimStart().startsWith("{") ? t : null;
      } catch { sourcesRef.current[n] = null; }
    }));
    for (const n of missing) {
      if (sourcesRef.current[n]) await ensureIncludes(sourcesRef.current[n], depth + 1);
    }
  }

  // ── живой подхват правок с сервера ────────────────────────────────────
  //
  // Модель, ради которой всё делалось: правишь и сразу видишь. Но правит не
  // только человек за этим браузером — ИИ пишет в ТУ ЖЕ студию через API, и
  // соседняя глава могла измениться минуту назад. Без опроса ребёнок смотрел на
  // вчерашний текст и не понимал, почему «ИИ ничего не сделал», пока не нажмёт F5.
  //
  // Правила, которые здесь важнее самого опроса:
  //   - НИКОГДА не затирать несохранённую правку. Если в редакторе есть свои
  //     изменения, чужие только ОБЪЯВЛЯЮТСЯ, а решает автор;
  //   - сравнивать с savedSrc (последняя версия, о которой мы договорились с
  //     сервером), а не с текстом в редакторе: иначе собственное сохранение
  //     выглядело бы как чужая правка;
  //   - список файлов обновлять тоже, иначе созданная ИИ глава не появится.
  const [serverAhead, setServerAhead] = useState(false);
  // Автор увидел плашку и выбрал «Оставить мою» — значит следующее сохранение
  // идёт поверх чужой правки ОСОЗНАННО. Без этого флага проверка перед записью
  // отказывала бы вечно и работать стало бы нельзя.
  const overwriteOk = useRef(false);
  const srcRef = useRef("");
  useEffect(() => { srcRef.current = src; }, [src]);

  useEffect(() => {
    if (!titleId) return;
    let stop = false;
    const tick = async () => {
      const name = curFileRef.current;
      if (!name || importedRef.current) return;
      try {
        const r = await fetch("/content/scripts/" + name + "?v=" + Date.now(), { cache: "no-store" });
        if (!r.ok || stop) return;
        const txt = await r.text();
        if (stop || curFileRef.current !== name) return;      // успели переключить файл
        if (txt.trimStart().startsWith("{")) return;          // отдался .lvn, не исходник
        if (txt === savedSrc.current) { setServerAhead(false); return; }
        const mine = srcRef.current !== savedSrc.current;      // есть свои несохранённые правки
        if (mine) {
          setServerAhead(true);                               // молча не трогаем, показываем плашку
          return;
        }
        savedSrc.current = txt;
        setSrc(txt);
        compileWithIncludes(txt);
        setServerAhead(false);
        notify("Файл обновился на сервере — подхватил", "");
      } catch { /* сеть мигнула — просто следующий тик */ }
    };
    const id = setInterval(tick, 4000);
    return () => { stop = true; clearInterval(id); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [titleId]);

  // Список файлов тоже живой: ИИ мог создать новую главу или общий файл.
  useEffect(() => {
    if (!titleId || !creds.token) return;
    const id = setInterval(() => { refreshFileList(); }, 15000);
    return () => clearInterval(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [titleId, creds.token]);

  async function refreshFileList() {
    try {
      const m = await getManifest();
      const t = (m.titles || []).find((x) => x.id === titleId);
      if (t) setTitle((prev) => (JSON.stringify(prev) === JSON.stringify(t) ? prev : t));
      const ids = [];
      (t?.seasons || []).forEach((s) => (s.chapters || []).forEach((c) => ids.push(c.id)));
      if (ids.length) setPublished(new Set(ids));
      await loadShared(ids, allChapterIds(m));
    } catch { /* не смогли — попробуем на следующем тике */ }
  }

  // Компиляция «как надо»: сперва докачать включения, потом собрать.
  async function compileWithIncludes(text) {
    await ensureIncludes(text);
    compile(text);
  }

  async function loadShared(chapterIds, allChapterIds) {
    if (!creds.token) return;
    let names = [];
    try {
      const r = await adminFiles("scripts", creds.token);
      // «Общий файл» — это библиотека, а не глава. Скрывать надо главы ВСЕХ
      // титулов студии, а не только открытого: иначе главы соседней новеллы
      // (ec-ch01, ec-fedor-ch01 …) висят в списке общих, хотя ни один
      // include их не подключает и открывать их отсюда нечего.
      const chapterFiles = new Set((allChapterIds || chapterIds).map((id) => id + ".lvns"));
      names = (r.files || [])
        .filter((f) => !f.dir && /\.lvns$/.test(f.name) && !chapterFiles.has(f.name))
        .map((f) => f.name)
        .sort();
    } catch { return; } // нет прав/сети — студия работает как раньше
    setShared(names);
    // Тексты нужны компилятору, а не глазам: include резолвится в браузере.
    await Promise.all(names.map(async (n) => {
      try {
        const rr = await fetch("/content/scripts/" + n + "?v=" + Date.now(), { cache: "no-store" });
        if (rr.ok) sourcesRef.current[n] = await rr.text();
      } catch { /* нечитаемый файл просто не участвует в include */ }
    }));
  }

  const chapters = useMemo(() => {
    if (!title) return [];
    const out = [];
    (title.seasons || []).forEach((s) => (s.chapters || []).forEach((c) => out.push(c)));
    out.sort((a, b) => (a.number || 0) - (b.number || 0));
    return out;
  }, [title]);

  const sel = chapters.find((c) => c.id === selId) || null;
  // ИМЯ ОТКРЫТОГО ФАЙЛА — одна точка правды для плашки, ключа редактора,
  // черновика, статус-бара и самого факта «есть что показывать». Раньше это
  // выводили из selId, а у общего файла он пустой — поэтому редактор для него
  // не рендерился ВООБЩЕ, хотя файл был прочитан, скомпилирован и подсвечен в
  // списке как активный: «редачить файлы нельзя» — это ровно про то место.
  const openFile = sharedName || (sel ? sel.id + ".lvns" : "");

  // ── unsaved-work safety ───────────────────────────────────────────────
  // Every IDE keeps your typing safe; "the server is the single source of
  // truth" must not mean "a closed tab eats an hour of writing". The editor
  // keeps a per-chapter DRAFT in localStorage while the text differs from the
  // last server copy; opening the chapter restores the draft (Reload from
  // server discards it), saving clears it, and closing the tab with unsaved
  // changes asks first.
  const savedSrc = useRef(""); // the last server-agreed source for this chapter
  // Ключ черновика: у главы — её id (так черновики уже лежат у авторов, менять
  // ключ значило бы выбросить их несохранённый текст), у общего файла — имя
  // файла. Пространства не пересекаются.
  const draftKey = (fileKey) => `lvn_draft_${titleId}_${fileKey}`;
  const draftId = sharedName || selId || "";
  const dirty = !imported && !!openFile && src !== savedSrc.current;

  useEffect(() => {
    document.title = (dirty ? "● " : "") + "Elvin Studio";
    if (!dirty) return;
    const h = (e) => { e.preventDefault(); e.returnValue = ""; };
    window.addEventListener("beforeunload", h);
    return () => window.removeEventListener("beforeunload", h);
  }, [dirty]);

  // Adopt server text as the agreed baseline, then let a stashed draft win.
  function adoptSource(fileKey, serverText) {
    savedSrc.current = serverText;
    const draft = localStorage.getItem(draftKey(fileKey));
    if (draft != null && draft !== serverText) {
      setSrc(draft);
      compileWithIncludes(draft);
      notify("Restored an unsaved draft — «Reload from server» discards it", "");
      return true;
    }
    return false;
  }

  // Открыть ОБЩИЙ файл на правку. Это не глава: у неё нет номера, обложки и
  // записи в манифесте, и компилировать её отдельно нечего — у библиотеки нет
  // `scene`. Поэтому сохранение пишет только .lvns, а «Save» не публикует
  // главу. Проверит её первая же глава, которая её подключает.
  async function openShared(name) {
    const epoch = ++openEpoch.current;
    setSelId("");
    setSharedName(name);
    curFileRef.current = name;
    libOpenRef.current = true;
    setImported(false);
    importedRef.current = false;
    creds.setPath(String(name).startsWith("@") ? "scripts/lvns_packages/" + name : "scripts/" + name);
    let txt = sourcesRef.current[name] || "";
    let found = !!txt;
    try {
      const r = await fetch(incUrl(name) + "?v=" + Date.now(), { cache: "no-store" });
      if (openEpoch.current !== epoch) return;
      if (r.ok) { txt = await r.text(); found = true; }
    } catch { /* останется то, что уже прочитали для include */ }
    if (openEpoch.current !== epoch) return;
    sourcesRef.current[name] = txt;
    // Файл могли открыть по include, а в списке его может не быть (листинг
    // каталога требует токена). Дописываем — иначе открытый файл не подсвечен
    // как активный, то есть «открыт» ничем не подтверждено.
    if (found) setShared((s) => (s.includes(name) ? s : [...s, name].sort()));
    else notify(`Нет файла scripts/${name} — сохранение создаст его`, "err");
    setSrc(txt);
    if (adoptSource(name, txt)) return; // несохранённый черновик этого файла
    compileWithIncludes(txt);
  }

  // Переход ПО include. Владелец, увидев в тексте `include "ec-mechanics.lvns"`,
  // спросил «и как его открыть?» — и был прав: способа не было. Глава открывается
  // как глава (у неё номер и запись в манифесте), всё остальное — как общий файл.
  function openInclude(raw) {
    const name = incKey(raw);
    const ch = chapters.find((c) => c.id + ".lvns" === name);
    if (ch) { openChapter(ch); return; }
    // null = файл уже пробовали докачать для компиляции и его нет. Молча открыть
    // пустоту здесь хуже, чем сказать правду: строка include ссылается в никуда.
    if (sourcesRef.current[name] === null) { notify(`Нет файла scripts/${name} — include ссылается в пустоту`, "err"); return; }
    openShared(name);
  }

  async function openChapter(c) {
    setSharedName("");
    curFileRef.current = c.id + ".lvns";
    libOpenRef.current = false;
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
            compileWithIncludes(txt);
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
            report(s);
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
    compileWithIncludes(text);
  }

  // A declaration that arrives after the first compile re-validates the open
  // chapter, so `ext` warnings clear without a keystroke.
  useEffect(() => {
    extGrammarRef.current = extGrammar;
    if (extGrammar && src) compile(src);
    // compile/src are deliberately not deps — this only reacts to the grammar.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [extGrammar]);

  // ── compile (WASM) ────────────────────────────────────────────────────
  function compile(text) {
    // Соседи для include: все .lvns новеллы, КРОМЕ открытого — он и есть text,
    // и подсунуть его копию значило бы скрыть цикл «сам на себя».
    const self = curFileRef.current;
    const sib = {};
    for (const [n, t] of Object.entries(sourcesRef.current)) {
      if (n !== self && typeof t === "string") sib[n] = t;
    }
    const r = compileLvns(text, extGrammarRef.current, sib, self);
    let ds = Array.isArray(r && r.diags) ? r.diags : [];
    // Imported chapters: articy linearization merges branches by design, so
    // machine labels (n12_000000) legitimately mix jump-targets with
    // fall-through — 17 identical warnings per chapter drown real problems.
    // Hand-written labels keep the hint.
    ds = ds.filter((d) => !(String(d.msg || "").includes("fall-through")
      && /label "n\d+_\d+"/.test(String(d.msg || ""))));
    // Открыт ОБЩИЙ файл (библиотека для include). У неё нет и не будет `scene`
    // — она не глава, её подключают. Претензия к отсутствию заголовка тут не
    // дефект, а шум, а шум в культуре «ноль предупреждений» разъедает саму
    // привычку смотреть на предупреждения.
    const libOpen = libOpenRef.current;
    if (libOpen) ds = ds.filter((d) => !/no .?scene.? header/i.test(String(d.msg || "")));
    setDiags(ds);
    if (!r || !r.ok) {
      const first = r && r.errors ? r.errors.split("\n")[0] : "Compilation error";
      setOutput(r && r.errors ? r.errors : "Compilation error");
      setError(true);
      const s = { kind: "error", text: "✗ " + first, title: r?.errors || "" };
      report(s);
      lastJson.current = "";
      return;
    }
    lastJson.current = r.json;
    setOutput(r.json);
    setError(false);
    let s;
    const warnText = libOpen
      ? splitLines(r.warnings || "").filter((w) => !/no .?scene.? header/i.test(w)).join("\n")
      : (r.warnings || "");
    if (warnText) {
      const n = splitLines(warnText).length;
      s = { kind: "warn", text: `⚠ ${n} warning${n > 1 ? "s" : ""}`, title: warnText };
    } else {
      s = { kind: "success", text: libOpen ? "✓ Общий файл — проверяется в главе" : "✓ Compiled" };
    }
    report(s);
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
    // Ключ — открытый файл, а не глава: у общего файла черновик не писался
    // вовсе, то есть его правки теряла любая перезагрузка молча.
    if (draftId) {
      try {
        if (text !== savedSrc.current) localStorage.setItem(draftKey(draftId), text);
        else localStorage.removeItem(draftKey(draftId));
      } catch { /* quota — the beforeunload guard still protects */ }
    }
    if (!wasmReady.current) return;
    clearTimeout(compileTimer.current);
    compileTimer.current = setTimeout(() => compileWithIncludes(text), 200);
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
    } catch (e) {
      if (e && e.conflict) {
        setServerAhead(true);
        notify("✗ Кто-то сохранил эту главу прямо сейчас. Твой текст цел: возьми серверную версию или сохрани поверх", "err");
      } else notify("✗ " + e.message, "err");
    }
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
    // Новый файл — ГЛАВА, а не тот файл, что был открыт. Если открыт общий файл,
    // забыть о нём надо целиком и синхронно: иначе save() уходил в ветку общего
    // файла и писал текст новой главы ПОВЕРХ библиотеки механик, а недокачанный
    // fetch прошлого открытия ещё и подменял текст в редакторе.
    ++openEpoch.current;
    setSharedName("");
    curFileRef.current = id + ".lvns";
    libOpenRef.current = false;
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

  // ЧУЖАЯ ПРАВКА ДО ЗАПИСИ, А НЕ ПОСЛЕ. Спрашиваем сервер прямо перед
  // сохранением и сверяем с тем, что мы в последний раз видели у него сами.
  // Возвращает версию для If-Match, либо null, если писать нельзя.
  //
  // Плашка «файл ушёл вперёд» ловит то же самое раньше — но только пока автор
  // сидит в редакторе с открытой главой и только раз в четыре секунды. Здесь —
  // последняя проверка, общая для главы и для общего файла: правку механик,
  // которую подключают все главы, терять ещё дороже, чем правку одной.
  async function baseVersion(rel) {
    if (overwriteOk.current) return { ok: true, etag: null };
    const base = await readSource(rel);
    if (base.text !== null && savedSrc.current && base.text !== savedSrc.current && base.text !== srcRef.current) {
      setServerAhead(true);
      notify("✗ Файл изменил кто-то ещё, пока ты правил. Твой текст цел: сверься с плашкой над редактором", "err");
      return { ok: false, etag: null };
    }
    return { ok: true, etag: base.etag };
  }

  async function save() {
    // The compile is debounced behind typing — flush it so we never save a
    // stale .lvn against fresh .lvns source.
    clearTimeout(compileTimer.current);
    if (wasmReady.current && !importedRef.current) compile(src);

    // Импортированную главу сохранять НЕЛЬЗЯ: в редакторе не её исходник, а
    // баннер-заглушка, и Save записал бы эти девять строк комментария в
    // <глава>.lvns. При следующем открытии редактор принял бы их за исходник
    // (не начинается с «{»), и глава превратилась бы в пустой комментарий —
    // компилируется, валидатор пропускает, содержимое потеряно.
    if (importedRef.current) {
      notify("Импортированная глава — правится переимпортом, а не здесь", "err");
      return;
    }

    // Общий файл сохраняется САМ ПО СЕБЕ: компилировать его отдельно нечего
    // (нет `scene`), в манифест он не идёт. Ошибки в нём всплывут в главе,
    // которая его подключает — там сочетание и становится настоящим.
    //
    // Ветвление по РЕФУ, а не по состоянию: save() зовут и с горячей клавиши
    // сразу после «+ New», когда setSharedName ещё не применился, и тогда
    // текст новой главы уезжал в файл механик, который подключают все главы.
    const libName = libOpenRef.current ? curFileRef.current : "";
    if (libName) {
      notify("Saving…");
      try {
        const base = await baseVersion("scripts/" + libName);
        if (!base.ok) return;
        await putAsset("scripts/" + libName, src, creds.token, "text/plain; charset=utf-8", base.etag);
        overwriteOk.current = false;
        sourcesRef.current[libName] = src;
        savedSrc.current = src;
        localStorage.removeItem(draftKey(libName));
        // Сам файл сохранён — но игра играет СКОМПИЛИРОВАННЫЕ главы, и без
        // пересборки правка механик до телефона не доедет. Тост обязан говорить
        // о том, что реально произошло, а не «сохранено».
        const r = await rebuildDependents(libName, creds.token);
        const n = (r.rebuilt || []).length;
        const bad = Object.entries(r.failed || {});
        if (bad.length) {
          notify(`⚠ ${libName} сохранён, но ${bad.length} глав(ы) перестали собираться: `
            + bad.map(([f, e]) => `${f} — ${e}`).join("; ")
            + ". На сервере остались прошлые рабочие версии.", "err");
        } else if (n) {
          notify(`✓ ${libName} сохранён, пересобрано глав: ${n} — уже в игре`, "ok");
        } else {
          notify(`✓ ${libName} сохранён. Его пока не подключает ни одна глава`, "ok");
        }
      } catch (e) {
        if (e && e.conflict) {
          setServerAhead(true);
          notify("✗ Кто-то сохранил этот файл прямо сейчас. Твой текст цел: возьми серверную версию или сохрани поверх", "err");
        } else notify("Save failed: " + ((e && e.message) || "unknown"), "err");
      }
      return;
    }
    if (!lastJson.current) { notify("Fix the errors before saving.", "err"); return; }
    const lvnPath = (creds.path || "scripts/ch1.lvn").trim();
    const lvnsPath = lvnPath.replace(/\.lvn$/, ".lvns");
    notify("Saving…");
    try {
      // Persist BOTH the editable source (.lvns) and the compiled bytecode (.lvn)
      // to the server — the source is what the editor re-reads on open, so this is
      // what makes the no-drafts model work.
      //
      // The COMPILED script goes first, because it is the one the server
      // structurally validates (dangling jumps, duplicate labels — see
      // lvnguard.go). If it is refused, this throws before the .lvns is
      // touched, so source and bytecode can never end up describing different
      // stories; the author's text is still in the editor (and its local
      // draft) and the errors land in the toast.
      const base = await baseVersion(lvnsPath);
      if (!base.ok) return;
      const saved = await putAsset(lvnPath, lastJson.current, creds.token, "application/json");
      // Версию сторожит .lvns — исходник автора; байткод рядом с ним всегда
      // пересобирается из него же. If-Match здесь — против ГОНКИ: чужое
      // сохранение, успевшее пройти между проверкой выше и этой строкой.
      await putAsset(lvnsPath, src, creds.token, "text/plain; charset=utf-8", base.etag);
      overwriteOk.current = false;
      // a new chapter is published on first save: push its manifest entry too.
      if (selId && !published.has(selId)) {
        await persist(title);
        setPublished((p) => new Set(p).add(selId));
        notify(`✓ Published ${lvnsPath} (+ .lvn) — live in ~2s`, "ok");
      } else {
        notify(`✓ Saved ${lvnsPath} (+ .lvn) — live in ~2s`, "ok");
      }
      // Advisory findings from the server's gate (a host op nobody declared,
      // a label nothing reaches). The save DID go through, so they replace the
      // success toast rather than pretending to be a failure.
      const warns = (saved && saved.warnings) || [];
      if (warns.length) notify(`⚠ Сохранено, но ${warns.length}: ${warns[0]}`, "err");
      savedSrc.current = src; // the server now agrees — clean
      if (selId) try { localStorage.removeItem(draftKey(selId)); } catch { }
    } catch (e) { notify("✗ " + e.message, "err"); }
  }

  saveRef.current = save;

  // Re-read the current file's .lvns from the server (drops in-editor unsaved
  // changes) — handy when the source was edited out-of-band, e.g. on disk.
  function reloadFromServer() {
    if (!openFile) return;
    try { localStorage.removeItem(draftKey(draftId)); } catch { } // an explicit reload discards the draft
    if (sharedName) openShared(sharedName); else openChapter(sel);
    notify("Перечитано с сервера (черновик сброшен)", "ok");
  }
  // Считать по РАЗОБРАННОМУ документу, а не по вхождениям `"op":` в тексте:
  // у структурного `if` внутри есть свой cond с полем "op" ("gte" и т.п.), и
  // регулярка засчитывала его как отдельную команду — счётчик завышался ровно
  // на число таких условий и расходился с `lvnconv probe`.
  const cmdCount = useMemo(() => {
    try {
      const d = JSON.parse(output);
      if (Array.isArray(d?.script)) return d.script.length;
    } catch { /* во время печати JSON бывает неполным — падать нельзя */ }
    return (output.match(/"op":/g) || []).length;
  }, [output]);
  const errCount = diags.filter((d) => d.sev === "error").length;
  const warnCount = diags.filter((d) => d.sev === "warning").length;
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

  // Что подключает ОТКРЫТЫЙ файл — список строится из текста в редакторе, поэтому
  // он честен и для несохранённых правок. Это второй (и не зависящий от Monaco)
  // ответ на «и как его открыть?»: файл видно и он открывается кликом.
  const includes = useMemo(() => {
    const out = [];
    src.split("\n").forEach((l, i) => {
      const m = /^[ \t]*include[ \t]+"([^"]+)"/.exec(l);
      if (!m) return;
      const name = incKey(m[1]);
      if (!out.some((x) => x.name === name)) out.push({ name, line: i + 1 });
    });
    return out;
  }, [src]);

  function onCaretMove(p) { caretRef.current = p; setCaretPos(p); }

  // Cmd/Ctrl+клик по строке `include "…"` открывает подключённый файл. Строку
  // берём из позиции каретки: Monaco ставит её на mousedown, то есть ДО click,
  // — так переход не требует API редактора (MonacoEditor.jsx не меняется), а
  // если каретка вдруг не там, обработчик просто ничего не делает.
  function onEditorClick(e) {
    if (!(e.metaKey || e.ctrlKey)) return;
    // Только клик по САМОМУ ТЕКСТУ: по миникарте и полосе прокрутки каретка не
    // двигается, и переход сработал бы по строке, где каретка осталась с прошлого
    // раза — то есть уводил бы из файла по клику в стороне.
    if (!(e.target instanceof Element) || !e.target.closest(".view-lines")) return;
    const line = src.split("\n")[caretRef.current.line - 1] || "";
    const m = /^[ \t]*include[ \t]+"([^"]+)"/.exec(line);
    if (!m) return;
    e.preventDefault();
    openInclude(m[1]);
  }

  // Подсказка строки списка: путь как на сервере плюс что это такое. Раньше у
  // глав в title было имя файла без каталога, а у общих — с каталогом, и
  // одинаковые с виду строки объясняли себя по-разному.
  const rowTitle = (file, what) => "scripts/" + file + "\n" + what;

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
          {/* Настоящее имя открытого файла, а не «—.lvns»: общий файл раньше не
              назывался в плашке никак, хотя был открыт. Рядом — чем он является. */}
          <span className="ide-file-name">
            {openFile ? <>{openFile.replace(/\.lvns$/, "")}<em>.lvns</em></> : "—"}{dirty ? " •" : ""}
          </span>
          {sharedName
            ? <span className="ide-file-kind sh" title="Общий файл: не глава — его подключают в главах через include">общий файл</span>
            : sel ? <span className="ide-file-kind" title={"Глава " + (sel.number || "?") + (sel.name ? " · «" + sel.name + "»" : "")}>глава {sel.number || "?"}</span> : null}
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
          {/* У общего файла нет манифеста и главы, публиковать нечего — и обещать
              «to app» тоже: сохраняется он сам, а проверяет его первая же глава. */}
          <button className="btn btn-primary" onClick={save} disabled={!!error}
            title={sharedName ? "Записать scripts/" + sharedName + " на сервер" : ""}>
            {sharedName ? "Save file ▸" : selId && !published.has(selId) ? "Publish to app ▸" : "Save to app ▸"}
          </button>
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
            {chapters.length === 0 && shared.length === 0 && <div className="ide-empty">No files.<br />+ New →</div>}
            {chapters.map((c) => {
              const isDraft = !published.has(c.id);
              const hasError = c.id === selId && errCount > 0;
              const status = hasError ? "error" : isDraft ? "draft" : "live";
              return (
              <div key={c.id} className={"ide-file-row" + (c.id === selId ? " active" : "")}>
                {/* Главный текст строки — НАСТОЯЩЕЕ имя файла. Раньше здесь стояло
                    поле name из манифеста («01», «02»), то есть в списке файлов не
                    было видно ни одного имени файла. Номер главы — отдельный чип,
                    человеческое имя — второй строкой: одно больше не подменяет другое. */}
                <button className="ide-file-open" onClick={() => openChapter(c)}
                  title={rowTitle(c.id + ".lvns", "Глава " + (c.number || "?") + (c.name ? " · «" + c.name + "»" : ""))}>
                  <span className={"ide-file-ico st-" + status} title={status === "error" ? "has errors" : status === "draft" ? "draft — not in the game yet" : "live in the game"} />
                  <span className="ide-file-chip">гл. {c.number || "?"}</span>
                  <span className="ide-file-main">
                    <span className="ide-file-label">{c.id}<em>.lvns</em></span>
                    {c.name ? <span className="ide-file-sub">{c.name}</span> : null}
                  </span>
                  {isDraft && <span className="ide-file-tag">draft</span>}
                </button>
                <span className="ide-file-acts">
                  <button onClick={() => duplicateChapter(c)} title="Duplicate file">⧉</button>
                  <button onClick={() => removeChapter(c.id)} title="Delete file">✕</button>
                </span>
              </div>
              );
            })}
            {shared.length > 0 && (
              <>
                {/* Заголовок обещал «файлы, которые главы подключают через include»,
                    а список — это ВСЕ прочие .lvns каталога (в нём нашлась памятка
                    авторам, которую не подключает никто). Что подключает открытый
                    файл — в секции «Подключено» ниже, там это правда. */}
                <div className="ide-files-group" title="Прочие .lvns в scripts/ — не главы: без номера и не в манифесте. Что подключает открытый файл — см. «Подключено» ниже">
                  общие файлы
                </div>
                {shared.map((n) => (
                  <div key={n} className={"ide-file-row" + (n === sharedName ? " active" : "")}>
                    <button className="ide-file-open" onClick={() => openShared(n)}
                      title={rowTitle(n, "Общий файл — не глава, его подключают через include")}>
                      <span className="ide-file-ico st-live" title="файл есть на сервере" />
                      <span className="ide-file-chip sh">общ.</span>
                      <span className="ide-file-main">
                        <span className="ide-file-label">{n.replace(/\.lvns$/, "")}<em>.lvns</em></span>
                      </span>
                    </button>
                  </div>
                ))}
              </>
            )}
          </div>

          {includes.length > 0 && (
            <SideSection id="includes" title="Подключено" count={includes.length} defaultOpen>
              <div className="ide-inc-list">
                {includes.map((it) => (
                  <button key={it.name} className="ide-inc-row" onClick={() => openInclude(it.name)}
                    title={"Открыть scripts/" + it.name + "\nстрока include: " + it.line}>
                    <span className="ide-inc-ico">↗</span>
                    <span className="ide-inc-name">{it.name}</span>
                    <span className="ide-out-line">{it.line}</span>
                  </button>
                ))}
              </div>
              <p className="ide-inc-hint">Cmd/Ctrl+клик по строке <code>include</code> в редакторе тоже открывает файл.</p>
            </SideSection>
          )}

          {openFile && outline.length > 0 && (
            <SideSection id="outline" title="Outline" count={outline.length}>
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
            </SideSection>
          )}

          {sel && (
            <SideSection id="chapter" title="Chapter">
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
            </SideSection>
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
          {/* Гейт по ОТКРЫТОМУ ФАЙЛУ, а не по главе: с `sel` общий файл открывался
              «в никуда» — редактор не рендерился, и на его месте предлагали
              «+ Add the first chapter» у новеллы с шестью главами. */}
          {openFile ? (
            <>
              <div className="ide-editor-row">
                {/* onClick на обёртке, а не в MonacoEditor: переход по include не
                    должен зависеть от правок в чужом файле редактора. */}
                {serverAhead && (
                  <div className="ide-conflict">
                    <span>
                      Файл изменился на сервере, а у тебя есть несохранённые правки.
                      Молча перезаписывать твоё я не буду.
                    </span>
                    <button className="btn-ghost sm" onClick={() => {
                      // Явное решение автора: взять серверную версию и потерять свою.
                      setServerAhead(false);
                      if (sharedName) openShared(sharedName); else if (sel) openChapter(sel);
                    }}>Взять серверную</button>
                    <button className="btn-ghost sm" onClick={() => {
                      // Явное решение автора: следующее сохранение ложится
                      // поверх серверной редакции.
                      overwriteOk.current = true;
                      setServerAhead(false);
                    }}>
                      Оставить мою
                    </button>
                  </div>
                )}
                <section className="ide-pane" onClick={onEditorClick}>
                  <MonacoEditor ref={editorRef} key={openFile} src={src} onChange={onEdit} diags={diags} jump={jump} catalog={catalog} extGrammar={extGrammar} onCaret={onCaretMove} readOnly={imported} />
                </section>
                {showPreview && (
                  <section className="ide-pane ide-preview">
                    <ResizeHandle storageKey="ide-w-preview" side="left" min={300} max={900} />
                    {/* Общий файл в .lvn не собирается — эта колонка для него
                        только проверка сборки, обещать «Compiled · x.lvn» нельзя. */}
                    <div className="ide-pane-head"><span>{sel ? `Compiled · ${sel.id}.lvn` : `Проверка · ${sharedName}`}</span></div>
                    <pre className={"code-output" + (error ? " error" : "")}>{output}</pre>
                  </section>
                )}
              </div>
              {showProblems && (
                <ProblemsDock diags={diags} compileError={error ? output : ""} onJump={goLine} onClose={() => setShowProblems(false)} />
              )}
            </>
          ) : booting ? (
            <div className="ide-blank">
              <span className="ide-spinner" aria-label="Загрузка" />
              <p>Загружаю главы…</p>
            </div>
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
        {/* Путь ОТКРЫТОГО файла, а не скомпилированного: в creds.path у главы
            лежит .lvn, а в редакторе всегда исходник .lvns. Список файлов,
            плашка и заголовок окна зовут его «…lvns» — четыре места об одном
            файле обязаны говорить одно имя, иначе «файл» опять непонятно что. */}
        {openFile && <span className="ide-status-dim mono">{String(creds.path || "").replace(/\.lvn$/, ".lvns")}</span>}
        <span className="grow" />
        {openFile && <span className="ide-status-dim mono">Ln {caretPos.line}, Col {caretPos.col}</span>}
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

/* ── Problems dock — click a row to jump to its source line ────────────── */
// compileError — ошибка КОМПИЛЯЦИИ, а не валидации. Она не приходит списком
// диагностик, поэтому эта панель раньше писала «no problems — the chapter
// compiles clean» одновременно с красной ошибкой в строке состояния. Автор
// видел два противоположных утверждения и верил ближнему.
// Сворачиваемая секция сайдбара — как Explorer/Outline/Timeline в VS Code и
// Cursor. До этого Outline и Chapter были ФИКСИРОВАННЫМИ блоками и съедали
// сайдбар целиком: на главе с девятью метками список файлов сжимался до одной
// видимой строки, то есть панель файлов не выполняла свою единственную работу.
// Состояние запоминается: у автора свой рабочий режим, и заставлять его
// сворачивать одно и то же при каждом открытии — та же мелкая порча.
function SideSection({ id, title, count, children, defaultOpen = false }) {
  const key = "lvn_side_" + id;
  const [open, setOpen] = useState(() => {
    const v = localStorage.getItem(key);
    return v == null ? defaultOpen : v === "1";
  });
  useEffect(() => { localStorage.setItem(key, open ? "1" : "0"); }, [key, open]);
  return (
    <div className={"ide-side-sec" + (open ? " open" : "")}>
      <button className="ide-side-head" onClick={() => setOpen((v) => !v)} aria-expanded={open}>
        <span className="ide-side-caret">{open ? "▾" : "▸"}</span>
        <span className="section-label">{title}</span>
        {count != null && count > 0 && <span className="ide-side-count">{count}</span>}
      </button>
      {open && <div className="ide-side-body">{children}</div>}
    </div>
  );
}

function ProblemsDock({ diags, compileError, onJump, onClose }) {
  const errCount = diags.filter((d) => d.sev === "error").length + (compileError ? 1 : 0);
  const warnCount = diags.filter((d) => d.sev === "warning").length;
  const rows = [...diags].sort((a, b) => (a.sev === b.sev ? (a.line || 0) - (b.line || 0) : a.sev === "error" ? -1 : 1));
  const ceLine = compileError ? Number((/(?:^|\s)line (\d+)/.exec(compileError) || [])[1] || 0) : 0;
  return (
    <div className="diagnostics">
      <div className="diag-head">
        <span className="diag-title">Problems</span>
        {errCount > 0 && <span className="diag-count err">{errCount} error{errCount > 1 ? "s" : ""}</span>}
        {warnCount > 0 && <span className="diag-count warn">{warnCount} warning{warnCount > 1 ? "s" : ""}</span>}
        {diags.length === 0 && !compileError && <span className="diag-count ok">no problems</span>}
        <span className="grow" />
        <button className="btn-ghost sm" onClick={onClose}>✕</button>
      </div>
      <div className="diag-list">
        {compileError && (
          <button className="diag-row sev-error" onClick={() => ceLine && onJump(ceLine)}
                  title={ceLine ? "Перейти к строке " + ceLine : ""}>
            <span className="diag-sev">✗</span>
            {ceLine > 0 && <span className="diag-line">{ceLine}</span>}
            <span className="diag-msg">{String(compileError).split("\n")[0]}</span>
          </button>
        )}
        {diags.length === 0 && !compileError && <div className="diag-clean">Nothing to fix — the chapter compiles clean.</div>}
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
