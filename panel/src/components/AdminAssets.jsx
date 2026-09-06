import { useCallback, useEffect, useRef, useState } from "react";
import { adminFiles, adminDeleteAsset, putAsset, readSource } from "../lib/api.js";
import { useAsync, useLatest, Status, authMsg, fmt } from "./adminShared.jsx";

const isImg = (n) => /\.(png|jpe?g|webp|gif)$/i.test(n);
const join = (dir, name) => (dir ? dir + "/" : "") + name;

// «1 страница / 2 страницы / 5 страниц» — счётная строка на русском читается
// глазом как факт, а «5 страница» читается как опечатка панели.
export const pagesPhrase = (n) => {
  const t = n % 10, h = n % 100;
  return `${n} ` + (t === 1 && h !== 11 ? "страница" : t >= 2 && t <= 4 && (h < 12 || h > 14) ? "страницы" : "страниц");
};

// ── Комплект Spine ──────────────────────────────────────────────────────────
// Скелет — не файл, а КОМПЛЕКТ: `имя.json` (или двоичный `имя.skel`), рядом
// `имя.atlas.txt` и страницы, которые атлас называет сам. Сцене хватает одного
// адреса — остальное движок выводит из него (LvnSpineRef.FromUrl), и ровно
// поэтому недостача не видна нигде: строка сценария написана правильно,
// компилятор доволен, а на устройстве вместо героя пусто.
//
// Браузер контента — единственное место, где комплект виден целиком ДО сборки.
// Поэтому здесь он показан одной карточкой с приговором: что есть, чего не
// хватает — по именам файлов, а не «что-то не так».

const ATLAS_EXT = [".atlas.txt", ".atlas"];
// ".skel.bytes" длиннее ".skel" и примеряется первым: иначе от имени осталась
// бы основа с хвостом ".bytes", и атлас к ней уже не подобрался бы.
const BINARY_EXT = [".skel.bytes", ".skel"];
// Скелет читается целиком только ради имён анимаций. Выше этого предела не
// читаем: качать мегабайты в браузер, чтобы подсказать одно слово, — плохой
// обмен, и лучше сказать об этом вслух, чем молча подвесить панель.
const SKELETON_READ_LIMIT = 8 * 1024 * 1024;

// Отрезает известное расширение; null — «файл не из этой семьи».
export function stripExt(name, exts) {
  const low = String(name || "").toLowerCase();
  for (const e of exts) if (low.length > e.length && low.endsWith(e)) return name.slice(0, name.length - e.length);
  return null;
}

// Собирает файлы папки в комплекты по общей основе имени.
//
// Атлас и двоичный скелет узнаются по расширению — этого довольно. А `.json`
// сам по себе не значит ничего (манифест, каталог, что угодно), и читать
// КАЖДЫЙ json папки ради заглядывания внутрь — дорого. Поэтому json считается
// скелетом в трёх случаях: рядом лежит атлас с той же основой; папка — та
// самая `spine/…`, куда кладёт выгрузки сам сервер (/v1/admin/spine); либо
// вызывающий уже знает, что это скелет (`known` — так приходит только что
// залитый файл, у которого мы подсмотрели заголовок). Всё прочее честнее
// оставить в покое, чем угадывать.
export function groupSpineKits(files, dir, known) {
  const spineDir = /(^|\/)spine(\/|$)/i.test(String(dir || ""));
  const knownBases = new Set(known || []);
  const kits = new Map();
  const kit = (base) => {
    if (!kits.has(base)) kits.set(base, { base, skeleton: "", atlas: "", binary: false, size: 0 });
    return kits.get(base);
  };
  const rows = (files || []).filter((f) => f && !f.dir);
  const sizeOf = new Map(rows.map((f) => [f.name, Number(f.size) || 0]));
  for (const f of rows) {
    const b = stripExt(f.name, ATLAS_EXT);
    if (b) kit(b).atlas = f.name;
  }
  for (const f of rows) {
    const b = stripExt(f.name, BINARY_EXT);
    if (!b) continue;
    const k = kit(b);
    k.skeleton = f.name;
    k.binary = true;
    k.size = sizeOf.get(f.name) || 0;
  }
  for (const f of rows) {
    const b = stripExt(f.name, [".json"]);
    if (!b) continue;
    if (!kits.has(b) && !spineDir && !knownBases.has(b)) continue;
    // Лежат оба написания — берём json: по нему видны имена анимаций, а по
    // двоичному .skel нет.
    const k = kit(b);
    k.skeleton = f.name;
    k.binary = false;
    k.size = sizeOf.get(f.name) || 0;
  }
  // Основа, про которую известно, что это скелет, но файла её в папке нет,
  // комплектом не считается — сообщать не о чем.
  for (const [base, k] of kits) if (!k.skeleton && !k.atlas) kits.delete(base);
  return [...kits.values()].sort((a, b) => a.base.localeCompare(b.base));
}

// Страницы называет сам атлас: имя картинки стоит отдельной строкой перед
// своим списком регионов. Правило то же, что у `lvnconv optimize`
// (atlasPageNames): два разных чтения одного формата разошлись бы рано или
// поздно, и хуже было бы то, которое молчит.
export function atlasPages(text) {
  const out = [];
  for (const raw of String(text || "").split("\n")) {
    const line = raw.trim();
    if (/\.(png|jpe?g)$/i.test(line) && !out.includes(line)) out.push(line);
  }
  return out;
}

// Чего комплекту не хватает — по именам файлов, которые автор должен долить.
//
// Регистр сверяется отдельно: Мак и Windows найдут `Hero.png` по запросу
// `hero.png`, а Linux на проде — нет, и страница пропадёт только там. Такую
// находку называем именно так, а не «файла нет»: файл-то есть.
export function kitMissing(kit, names) {
  const present = new Set(names || []);
  const byLower = new Map((names || []).map((n) => [String(n).toLowerCase(), n]));
  const missing = [];
  if (!kit.skeleton) missing.push(kit.base + ".json");
  if (!kit.atlas) missing.push(kit.base + ".atlas.txt");
  for (const page of kit.pages || []) {
    if (present.has(page)) continue;
    // Страница из соседней папки здешним списком не проверяется — молчим о
    // ней вовсе, вместо того чтобы объявить пропажей то, что не смотрели.
    if (page.includes("/")) continue;
    const other = byLower.get(page.toLowerCase());
    missing.push(other ? `${page} (на диске ${other} — разный регистр, Linux этого не простит)` : page);
  }
  return missing;
}

// Имена анимаций живут в объекте `animations` скелета. Не разобрали — пустой
// список, а не догадка: подставленное наугад `play=` тише всего ломает сцену.
export function animationNames(text) {
  try {
    const doc = JSON.parse(String(text));
    const a = doc && doc.animations;
    return a && typeof a === "object" && !Array.isArray(a) ? Object.keys(a) : [];
  } catch {
    return [];
  }
}

// Признак скелета в первых килобайтах файла: Spine всегда пишет заголовок
// `"skeleton": {…}` и список костей. Нужен только на заливке — отличить свежий
// скелет от любого другого json, чтобы предупредить про забытый атлас.
export function looksLikeSkeleton(head) {
  const s = String(head || "");
  return /"skeleton"\s*:\s*\{/.test(s) && /"bones"\s*:\s*\[/.test(s);
}

// Готовая строка сцены — ровно того вида, каким её знает движок:
// `actor id=hero spine="…/hero.json" play="idle"`. Адрес и анимация всегда в
// кавычках (так написано во всех примерах, и так строку не жалко перенести),
// а имя актёра — только если без кавычек оно распалось бы на два слова:
// пробел в имени папки дело обычное, а разбор .lvns допускает кавычки у
// любого значения.
export function actorLine(id, url, anim) {
  const esc = (v) => String(v).replace(/(["\\])/g, "\\$1");
  const name = /[\s"']/.test(id) ? '"' + esc(id) + '"' : id;
  return `actor id=${name} spine="${esc(url)}"` + (anim ? ` play="${esc(anim)}"` : "");
}

// Как автор зовёт комплект. Выгрузка Spine часто называет скелет по-своему
// (`skeleton.json`) и складывает комплект в папку с именем героя — тогда имя
// папки и есть имя актёра. Но если в папке несколько комплектов, она их не
// различает: там имя даёт файл.
export function kitActorId(kit, dir, kitCount) {
  const folder = String(dir || "").split("/").filter(Boolean).pop() || "";
  if (kitCount === 1 && folder && !/^spine$/i.test(folder)) return folder;
  return kit.base;
}

// The content-directory browser: breadcrumb navigation, image previews, upload
// into the current directory, click-to-copy content urls, and delete. Scripts
// are versioned server-side on delete; art is gone for good — the confirm says so.
export default function AdminAssets({ token, notify }) {
  const [dir, setDir] = useState("");
  const { loading, error, data, reload } = useAsync(() => adminFiles(dir, token), [dir, token]);
  const fileInput = useRef(null);
  const [uploading, setUploading] = useState(false);
  const [kits, setKits] = useState([]);
  const [kitWarn, setKitWarn] = useState(null);
  // Скелеты, узнанные при заливке по заголовку файла. Список нужен и после
  // перечитывания папки: иначе `hero.json` без атласа, лежащий не в `spine/`,
  // исчез бы из карточек ровно тогда, когда о нём и надо говорить.
  const [known, setKnown] = useState([]);

  const files = (data && data.files) || [];
  const crumbs = ["content", ...dir.split("/").filter(Boolean)];
  const rel = (name) => join(dir, name);

  // Список файлов говорит, ЧТО лежит в папке, но не говорит, полон ли комплект:
  // страницы называет атлас, и его надо прочитать. Читаем ровно необходимое —
  // атлас (килобайты) и, если скелет текстовый и не огромный, сам скелет ради
  // имён анимаций. Чего не прочли, то так и помечаем: «не проверено» — это
  // другое сообщение, чем «всё на месте», и подменять одно другим нельзя.
  const scan = useCallback(async (rows, extra) => {
    const found = groupSpineKits(rows, dir, [...known, ...(extra || [])]);
    const names = (rows || []).filter((f) => f && !f.dir).map((f) => f.name);
    return Promise.all(found.map(async (k) => {
      const out = { ...k, pages: [], atlasRead: false, anims: [], note: "" };
      if (k.atlas) {
        const { text } = await readSource(join(dir, k.atlas));
        if (text == null) out.note = "атлас не прочитан — страницы не проверены";
        else { out.atlasRead = true; out.pages = atlasPages(text); }
      }
      out.missing = kitMissing(out, names);
      if (!k.skeleton) return out;
      if (k.binary) out.animNote = "двоичный " + k.skeleton + " — имена анимаций из него не прочесть";
      else if (k.size > SKELETON_READ_LIMIT) out.animNote = "скелет весит " + fmt(Math.round(k.size / 1048576)) + " МБ — не читаем ради списка анимаций";
      else {
        const { text } = await readSource(join(dir, k.skeleton));
        if (text == null) out.animNote = "скелет не прочитан — имена анимаций неизвестны";
        else {
          out.anims = animationNames(text);
          if (!out.anims.length) out.animNote = "в скелете не объявлено ни одной анимации";
        }
      }
      return out;
    }));
  }, [dir, known]);

  // Предупреждение и «узнанные» скелеты принадлежат своей папке — гасим их на
  // переходе, а не на каждом перечитывании списка: перечитывание идёт и сразу
  // после заливки, и оно погасило бы предупреждение раньше, чем автор его
  // прочтёт.
  useEffect(() => { setKitWarn(null); setKnown([]); }, [dir]);

  const startScan = useLatest();
  useEffect(() => {
    if (loading || error || !data) { setKits([]); return; }
    const run = startScan();
    scan(data.files || []).then((list) => { if (run.fresh()) setKits(list); })
      .catch(() => { if (run.fresh()) setKits([]); });
    return run.drop;
  }, [data, loading, error, scan, startScan]);

  // ЧЕСТНЫЙ ОТКАЗ НА ЗАЛИВКЕ. Комплект льют по файлу за раз, и забытый атлас
  // (или страница, которую атлас называет) сейчас не заметен никак: загрузка
  // прошла, список пополнился — а узнаёт об этом автор на устройстве, часом
  // позже и без имени файла. Поэтому сразу после заливки скелета перечитываем
  // папку и говорим, чего не хватает, ПО ИМЕНАМ.
  async function checkKitAfterUpload(list) {
    const known = [];
    for (const f of list) {
      const bin = stripExt(f.name, BINARY_EXT);
      if (bin) { known.push(bin); continue; }
      const asJson = stripExt(f.name, [".json"]);
      // Заголовок читаем у себя: файл в руках, сервер для этого не нужен.
      if (asJson && looksLikeSkeleton(await readHead(f))) known.push(asJson);
    }
    if (!known.length) return;
    const fresh = ((await adminFiles(dir, token)) || {}).files || [];
    const scanned = await scan(fresh, known);
    setKits(scanned);
    const mine = scanned.filter((k) => known.includes(k.base));
    const bad = mine.filter((k) => k.missing.length || !k.atlasRead);
    if (!bad.length) {
      for (const k of mine) notify(`Комплект ${k.base} полный: скелет + атлас + ${pagesPhrase(k.pages.length)}`, "ok");
      return;
    }
    setKitWarn(bad);
    notify("✗ Комплект Spine неполный — см. предупреждение над списком", "err");
  }

  async function upload(list) {
    if (!list || !list.length) return;
    setUploading(true);
    try {
      for (const f of list) {
        await putAsset(rel(f.name), f, token, f.type || "application/octet-stream");
        notify("Загружен " + f.name, "ok");
      }
      reload();
      // Проверка комплекта идёт СВОИМ путём отказа: сорвалась она — файлы всё
      // равно залиты, и говорить «загрузка не удалась» было бы неправдой.
      try {
        await checkKitAfterUpload(list);
      } catch (e) {
        notify("Файлы залиты, но комплект Spine проверить не удалось: " + authMsg(e), "err");
      }
    } catch (e) { notify("✗ " + authMsg(e), "err"); }
    finally { setUploading(false); if (fileInput.current) fileInput.current.value = ""; }
  }

  async function remove(path) {
    if (!window.confirm("Удалить " + path + "? (скрипты уходят в историю, арт — безвозвратно)")) return;
    try {
      await adminDeleteAsset(path, token);
      notify("Удалено", "ok");
      reload();
    } catch (e) { notify("✗ " + authMsg(e), "err"); }
  }

  function copyUrl(path) {
    const url = "/content/" + path;
    navigator.clipboard.writeText(url);
    notify("Скопировано: " + url, "ok");
  }

  // Строку кладём в буфер целиком собранной — с адресом скелета и первой
  // анимацией. Анимации не прочитаны — копируем без `play=` и говорим почему:
  // строка без имени анимации рабочая, а выдуманное имя — нет.
  function copyActor(kit) {
    const anim = kit.anims[0] || "";
    const line = actorLine(kitActorId(kit, dir, kits.length), "/content/" + rel(kit.skeleton), anim);
    navigator.clipboard.writeText(line);
    notify(anim ? "Скопировано: " + line : "Скопировано без play= — " + (kit.animNote || "имена анимаций недоступны"),
           anim ? "ok" : "err");
  }

  return (
    <div className="admin-card">
      <div className="admin-cardhead">
        <h2 className="admin-crumbs">
          {crumbs.map((c, i) => (
            <span key={i}>
              {i > 0 && <span className="crumb-sep">/</span>}
              <button className="as-link crumb-link" onClick={() => setDir(crumbs.slice(1, i + 1).join("/"))}>{c}</button>
            </span>
          ))}
        </h2>
        <div className="admin-rowbtns">
          <input ref={fileInput} type="file" multiple style={{ display: "none" }}
                 onChange={(e) => upload(Array.from(e.target.files || []))} />
          <button className="btn btn-primary" disabled={uploading}
                  onClick={() => fileInput.current && fileInput.current.click()}>
            {uploading ? "Загрузка…" : "Загрузить файлы сюда"}
          </button>
        </div>
      </div>
      <p className="admin-hint">Клик по имени — копирует content-url для манифеста/скрипта. Картинки с превью.</p>
      {kitWarn && (
        <div className="admin-kitwarn">
          <button className="as-link admin-kitwarn-x" title="скрыть" onClick={() => setKitWarn(null)}>×</button>
          <b>Скелет залит, комплект неполный.</b> Движок соберёт его по одному адресу — и не найдёт остального:
          {kitWarn.map((k) => (
            <div key={k.base} className="admin-kitwarn-row">
              <code>{k.base}</code>
              {k.missing.length > 0 && <> — долейте сюда: <b>{k.missing.join(", ")}</b></>}
              {!k.atlasRead && <> {k.missing.length ? "; " : "— "}{k.note || "атлас не прочитан — страницы не проверены"}</>}
            </div>
          ))}
        </div>
      )}
      <Status loading={loading} error={error} />
      {!loading && !error && kits.length > 0 && (
        <div className="admin-kits">
          {kits.map((k) => <SpineKit key={k.base} kit={k} onCopy={() => copyActor(k)} />)}
        </div>
      )}
      {!loading && !error && (
        <div className="admin-filegrid">
          {files.map((f) => f.dir ? (
            <button key={f.name} className="admin-file admin-dir" onClick={() => setDir(rel(f.name))}>
              <div className="admin-fileicon">📁</div>
              <div className="admin-filename">{f.name}</div>
            </button>
          ) : (
            <div key={f.name} className="admin-file">
              {isImg(f.name)
                ? <img src={"/content/" + rel(f.name)} loading="lazy" alt={f.name} className="admin-filepreview" />
                : <div className="admin-fileicon">📄</div>}
              <button className="as-link admin-filename" title={"скопировать /content/" + rel(f.name)}
                      onClick={() => copyUrl(rel(f.name))}>{f.name}</button>
              <div className="muted admin-filesize">{fmt(f.size)} b</div>
              <button className="btn-ghost sm danger" onClick={() => remove(rel(f.name))}>удалить</button>
            </div>
          ))}
          {!files.length && <p className="admin-empty">Пусто.</p>}
        </div>
      )}
    </div>
  );
}

// Одна карточка на комплект — рядом с файлами, из которых он собран, но выше
// их: состояние комплекта важнее любого отдельного файла в этой папке.
function SpineKit({ kit, onCopy }) {
  const full = !kit.missing.length && kit.atlasRead;
  return (
    <div className={"admin-kit" + (full ? " ok" : "")}>
      <div className="admin-kit-name">🦴 {kit.base}</div>
      <div className="admin-kit-state">
        {full
          ? `скелет + атлас + ${pagesPhrase(kit.pages.length)} — полный`
          : kit.missing.length
            ? <>не хватает: <b>{kit.missing.join(", ")}</b></>
            : (kit.note || "проверить не удалось")}
      </div>
      {full && (
        <>
          <button className="btn-ghost sm" onClick={onCopy}>Строка для сцены</button>
          <div className="muted admin-kit-anims">
            {kit.anims.length
              ? "анимации: " + kit.anims.slice(0, 6).join(", ") + (kit.anims.length > 6 ? ", …" : "")
              : kit.animNote || "имена анимаций неизвестны"}
          </div>
        </>
      )}
    </div>
  );
}

// Первые килобайты залитого файла — их хватает на заголовок скелета. Файл
// читается из руки автора, а не с сервера: сервер о нём ещё ничего не знает.
async function readHead(file, bytes = 8192) {
  try {
    if (!file || typeof file.slice !== "function") return "";
    return await file.slice(0, bytes).text();
  } catch {
    return "";
  }
}
