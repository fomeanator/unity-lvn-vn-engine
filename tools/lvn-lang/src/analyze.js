// LVNScript analysis core — pure, framework-free language intelligence shared
// by the authoring panel and the language server. No editor, DOM or Node deps.
// Functions take plain text + a position + a context {catalog, actorMap}; the
// catalog is the manifest's `sprites` map (id → {name, axes, defaults}).

import { OPS, OP_FIELDS, ATTR_VALUES, OP_DOCS, SNIPPETS, DIRECTIVES } from "./grammar.js";

// ── label / variable facts ────────────────────────────────────────────────
export function labelsIn(src) {
  const out = new Set();
  src.split("\n").forEach((l) => { const m = l.match(/^\s*:(\S+)/); if (m) out.add(m[1]); });
  return [...out];
}

export function labelInfo(src, name) {
  const lines = src.split("\n");
  let defLine = 0, target = "";
  for (let i = 0; i < lines.length; i++) {
    const m = lines[i].match(/^\s*:(\S+)/);
    if (m && m[1] === name) { defLine = i + 1; target = (lines[i + 1] || "").trim(); break; }
  }
  let refs = 0;
  const re = new RegExp("(?:goto|call|->|then\\s*=\\s*\"?|else\\s*=\\s*\"?)\\s*" + name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&") + "\\b");
  lines.forEach((l) => { if (re.test(l)) refs++; });
  return { defLine, target, refs, defined: defLine > 0 || name === "__end" };
}

export function varInfo(src, name) {
  const lines = src.split("\n");
  let sets = 0, uses = 0, lastVal = null;
  const setRe = new RegExp('(?:set|inc)\\s+key="' + name + '"', "i");
  lines.forEach((l) => {
    if (setRe.test(l)) {
      sets++;
      const v = l.match(/value=("[^"]*"|\S+)/); const ex = l.match(/expr="([^"]*)"/); const by = l.match(/by=(\S+)/);
      if (ex) lastVal = "= " + ex[1]; else if (v) lastVal = "= " + v[1]; else if (by) lastVal = "+= " + by[1];
    }
    const interp = new RegExp("\\{[^}]*\\b" + name + "\\b[^}]*\\}", "i");
    const inExpr = new RegExp('expr="[^"]*\\b' + name + '\\b', "i");
    if (interp.test(l) || inExpr.test(l)) uses++;
  });
  return { sets, uses, lastVal, known: sets > 0 || uses > 0 };
}

function labelComp(tok, labels) {
  const items = [...labels, "__end"].filter((l) => l.startsWith(tok)).map((t) => ({ text: t, kind: "label" }));
  return items.length ? { token: tok, items } : null;
}

// The identifier under (line, col). `line` is 1-based, `col` a 0-based column.
export function wordAt(src, line, col) {
  const l = src.split("\n")[line - 1];
  if (l == null || col < 0 || col > l.length) return null;
  const isW = (c) => c && /[A-Za-z_]/.test(c);
  let s = col, e = col;
  while (s > 0 && isW(l[s - 1])) s--;
  while (e < l.length && isW(l[e])) e++;
  return s === e ? null : l.slice(s, e);
}

// Resolve a speaker name to its sprite-catalog entity id (via actor_map, else
// the lowercased name).
export function speakerEntity(name, catalog, actorMap) {
  const id = (actorMap && actorMap[name]) || (name || "").toLowerCase().replace(/[^a-z0-9]+/g, "_");
  return { id, ent: catalog && catalog[id] };
}

// Display names for the cast — actor_map aliases plus each catalog entry's
// `name` (or its capitalized id).
export function castNames(catalog, actorMap) {
  const out = new Set();
  if (actorMap) Object.keys(actorMap).forEach((k) => out.add(k));
  if (catalog) Object.keys(catalog).forEach((id) => {
    const ent = catalog[id];
    out.add(ent && ent.name ? ent.name : id.charAt(0).toUpperCase() + id.slice(1));
  });
  return [...out];
}

// ── completion ──────────────────────────────────────────────────────────────
// Decide what to suggest, given the line text up to the caret + the catalog.
// Returns { token, items:[{text, kind, label?, body?, quoted?, entity?}] } | null.
export function completionAt(line, labels, catalog, actorMap) {
  let m = line.match(/^([a-zA-Z_]*)$/); // first word → op / directive / snippet / speaker
  if (m) {
    const tok = m[1];
    if (!tok) return null;
    const lower = tok.toLowerCase();
    // cast names first — typing a speaker is the most common line start. For a
    // character with emotions we ALSO offer a "Name [emotion]" variant so the
    // bracket syntax is discoverable without knowing to type `[`.
    const names = [];
    castNames(catalog, actorMap)
      .filter((n) => n.toLowerCase().startsWith(lower))
      .forEach((n) => {
        names.push({ text: n, kind: "speaker" });
        const { ent } = speakerEntity(n, catalog, actorMap);
        if (ent && ent.axes && ent.axes.emotion && ent.axes.emotion.length) {
          names.push({ text: n, kind: "speaker", emote: true, label: n + " [emotion]" });
        }
      });
    const kw = [
      ...SNIPPETS.map((s) => ({ text: s.trigger, label: s.label, body: s.body, kind: "snippet" })),
      ...OPS.map((t) => ({ text: t, kind: "op" })),
      ...DIRECTIVES.map((t) => ({ text: t, kind: "directive" })),
    ].filter((it) => it.text.startsWith(tok));
    const items = [...names, ...kw];
    return items.length ? { token: tok, items } : null;
  }

  if ((m = line.match(/^([^:[\]]+?)\s*\[([A-Za-z0-9_]*)$/))) {
    const tok = m[2];
    const { ent } = speakerEntity(m[1].trim(), catalog, actorMap);
    const emos = ent && ent.axes && ent.axes.emotion ? ent.axes.emotion : ["neutral", "happy", "sad", "angry", "smile"];
    const items = emos.filter((e) => e.startsWith(tok)).map((e) => ({ text: e, kind: "emotion" }));
    return items.length ? { token: tok, items } : null;
  }
  if ((m = line.match(/(?:goto|call)\s+(\S*)$/))) return labelComp(m[1], labels);
  if ((m = line.match(/->\s*(\S*)$/))) return labelComp(m[1], labels);
  if ((m = line.match(/(?:then|else)\s*=\s*"?([A-Za-z0-9_]*)$/))) return labelComp(m[1], labels);

  if ((m = line.match(/([a-z_]+)\s*=\s*("?)([A-Za-z0-9_-]*)$/))) {
    const key = m[1], quoted = m[2] === '"', tok = m[3];
    const wrap = (arr, kind, extra) => arr.filter((v) => v.startsWith(tok)).map((v) => ({ text: v, kind, quoted, ...(extra ? extra(v) : null) }));
    if (key === "id" && catalog) {
      const items = wrap(Object.keys(catalog), "entity", (v) => ({ entity: v }));
      if (items.length) return { token: tok, items };
    }
    const idm = line.match(/\bid\s*=\s*"?([A-Za-z0-9_-]+)/);
    const ent = idm && catalog && catalog[idm[1]];
    if (ent && ent.axes && ent.axes[key]) {
      const items = wrap(ent.axes[key], "value");
      if (items.length) return { token: tok, items };
    }
    // play="…" → the character's named animations
    if (key === "play" && ent && ent.anim) {
      const items = wrap(Object.keys(ent.anim), "value");
      if (items.length) return { token: tok, items };
    }
    if (key === "action") {
      const op0 = (line.match(/^([a-z_]+)/) || [])[1];
      const set = op0 === "camera" ? ["shake", "zoom", "pan", "reset"] : op0 === "audio" ? ["play", "stop"] : null;
      if (set) { const items = wrap(set, "value"); if (items.length) return { token: tok, items }; }
    }
    if (ATTR_VALUES[key]) {
      const items = wrap(ATTR_VALUES[key], "value");
      if (items.length) return { token: tok, items };
    }
  }

  const fw = line.match(/^([a-z_]+)\s+/); // attribute key for a known op
  if (fw && OP_FIELDS[fw[1]] && !/=\s*\S*$/.test(line)) {
    const tail = line.match(/(?:^|\s)([a-zA-Z_]*)$/);
    if (tail) {
      const tok = tail[1];
      if (!tok) return null; // empty token right after the op → ghost takes over
      const items = OP_FIELDS[fw[1]].map((t) => ({ text: t, kind: "attr" })).filter((it) => it.text.startsWith(tok));
      return items.length ? { token: tok, items } : null;
    }
  }
  return null;
}

// ── inline ghost prediction (rule-based, no AI) ──────────────────────────────
const OP_GHOST = {
  bg: 'id=""', actor: 'id="" show=true position="center"',
  obj: 'id="" sprite_url="" x=0.5 y=0.6 width=0.2 on_click=""',
  fade: 'to="black" duration=0.8', dim: "alpha=0.4 duration=0.5",
  flash: 'color="white" duration=0.2', tint: 'color="warm" alpha=0.3 duration=0.6',
  blur: "alpha=0.5 duration=0.5", camera: 'action="shake" duration=0.5',
  particles: 'type="rain" on=true', audio: 'channel="music" url="" action="play"',
  wait: "ms=1000", text_pace: "cps=30", hint: 'text="" show=true',
  set: 'key="" value=', inc: 'key="" by=1', if: 'expr="" then="" else=""',
  goto: "__end", call: "__end",
};
const ATTR_GHOST = {
  to: '"black"', position: '"center"', show: "true", on: "true",
  color: '"warm"', type: '"rain"', channel: '"music"', duration: "0.5", alpha: "0.4", by: "1", ms: "1000", cps: "30",
};

export function predictGhost(line, ctx) {
  const { catalog, actorMap } = ctx || {};
  let m;
  if ((m = line.match(/^([a-z_]+)\s$/)) && OP_GHOST[m[1]]) return OP_GHOST[m[1]];
  if ((m = line.match(/^actor\s+id="([A-Za-z0-9_-]+)"\s.*\s$/)) || (m = line.match(/^actor\s+id="([A-Za-z0-9_-]+)"\s$/))) {
    const ent = catalog && catalog[m[1]];
    if (ent && ent.axes) {
      const parts = [];
      if (ent.axes.pose && !/\bpose=/.test(line)) parts.push(`pose="${(ent.defaults && ent.defaults.pose) || ent.axes.pose[0]}"`);
      if (ent.axes.emotion && !/\bemotion=/.test(line)) parts.push(`emotion="${(ent.defaults && ent.defaults.emotion) || ent.axes.emotion[0]}"`);
      if (parts.length) return parts.join(" ");
    }
  }
  if ((m = line.match(/(?:^|\s)([a-z_]+)=$/))) {
    if (m[1] === "action") return /^audio\b/.test(line) ? '"play"' : '"shake"';
    if (ATTR_GHOST[m[1]]) return ATTR_GHOST[m[1]];
  }
  if ((m = line.match(/^([A-Za-z][A-Za-z0-9_ ]*?)$/))) {
    const typed = m[1];
    const { ent } = speakerEntity(typed.trim(), catalog, actorMap);
    if (ent) return ": ";
    const names = castNames(catalog, actorMap);
    const lp = typed.toLowerCase();
    const hits = names.filter((n) => n.toLowerCase().startsWith(lp) && n.toLowerCase() !== lp);
    if (hits.length === 1) return hits[0].slice(typed.length) + ": ";
  }
  if (line === "- ") return "Option -> next";
  return null;
}

// ── signature help (the line the caret is on) ───────────────────────────────
export function describeLine(lineText, col, ctx) {
  const { catalog, actorMap, src } = ctx || {};
  const trimmed = lineText.trim();
  if (!trimmed) return null;
  let m;

  if ((m = lineText.match(/^\s*([a-z_]+)\b/)) && OP_DOCS[m[1]] && m[1] !== "say") {
    const op = m[1];
    const [sig, desc] = OP_DOCS[op];
    const upto = lineText.slice(0, col);
    const km = [...upto.matchAll(/\b([a-z_]+)=/g)];
    const active = km.length ? km[km.length - 1][1] : null;
    return { kind: "op", op, sig, desc, active };
  }
  if ((m = lineText.match(/^([^:[\]]+?)(?:\s*\[([^\]]*)\])?\s*:/))) {
    const name = m[1].trim(), emo = (m[2] || "").trim();
    const { id, ent } = speakerEntity(name, catalog, actorMap);
    const emos = ent && ent.axes && ent.axes.emotion ? ent.axes.emotion : null;
    const valid = !emo || !emos || emos.includes(emo);
    return { kind: "speaker", name, emo, id: ent ? id : null, emos, valid };
  }
  if (/^-\s/.test(trimmed)) {
    const tm = trimmed.match(/->\s*(\S+)/);
    const target = tm ? tm[1] : null;
    const info = target && target !== "__end" && src ? labelInfo(src, target) : null;
    return { kind: "choice", target, defined: target === "__end" || (info && info.defined), info };
  }
  if ((m = trimmed.match(/^:(\S+)/))) {
    const info = src ? labelInfo(src, m[1]) : { refs: 0 };
    return { kind: "labeldef", name: m[1], refs: info.refs };
  }
  return { kind: "prose" };
}

// ── hover ────────────────────────────────────────────────────────────────────
// What's under (line, col)? Returns { kind, ... } | null. kinds: op, entity,
// emotion, label, var. (1-based line, 0-based col.)
export function hoverAt(src, line, col, ctx) {
  const { catalog = {}, actorMap = {} } = ctx || {};
  const w = wordAt(src, line, col);
  if (!w) return null;
  const lineText = src.split("\n")[line - 1] || "";
  const labels = labelsIn(src);

  if (OP_DOCS[w]) return { kind: "op", word: w, sig: OP_DOCS[w][0], desc: OP_DOCS[w][1] };

  const sm = lineText.match(/^([^:[\]]+?)(?:\s*\[([^\]]*)\])?\s*:/);
  if (sm) {
    const name = sm[1].trim(), emo = (sm[2] || "").trim();
    const { id, ent } = speakerEntity(name, catalog, actorMap);
    if (emo && (w === emo || emo.split(/\s+/).includes(w))) {
      const ok = !!(ent && ent.axes && ent.axes.emotion && ent.axes.emotion.includes(w));
      const emos = ent && ent.axes && ent.axes.emotion ? ent.axes.emotion : [];
      return { kind: "emotion", emo: w, charId: id, ok, emos };
    }
    if (name.split(/\s+/).includes(w) && ent) return { kind: "entity", id };
  }

  const lw = w.toLowerCase();
  const direct = catalog[w] ? w : (catalog[lw] ? lw : null);
  if (direct) return { kind: "entity", id: direct };

  const isLabelCtx = /^\s*:/.test(lineText) || /(?:goto|call|->|then\s*=|else\s*=)/.test(lineText);
  if (isLabelCtx && /^[A-Za-z0-9_]+$/.test(w) && (labels.includes(w) || w === "__end" || /(?:goto|call|->)\s*"?$/.test(lineText.slice(0, lineText.indexOf(w))))) {
    return { kind: "label", name: w, ...labelInfo(src, w) };
  }

  const vi = varInfo(src, w);
  const inInterp = new RegExp("\\{[^}]*\\b" + w + "\\b[^}]*\\}", "i").test(lineText);
  const inSetKey = new RegExp('(?:set|inc)\\s+key="' + w + '"', "i").test(lineText);
  const inExpr = new RegExp('(?:expr|value)="[^"]*\\b' + w + '\\b', "i").test(lineText);
  if (vi.known && (inInterp || inSetKey || inExpr)) return { kind: "var", name: w, ...vi };

  return null;
}

// ── go to definition ─────────────────────────────────────────────────────────
// Ctrl+click a goto/call/->/then/else target → its `:definition` line (1-based).
export function definitionAt(src, line, col) {
  const w = wordAt(src, line, col);
  if (!w) return null;
  const lineText = src.split("\n")[line - 1] || "";
  const isRef = /(?:goto|call|->|then\s*=|else\s*=)/.test(lineText) && /^[A-Za-z0-9_]+$/.test(w);
  if (!isRef || w === "__end") return null;
  const lines = src.split("\n");
  const defLine = lines.findIndex((l) => new RegExp("^\\s*:" + w.replace(/[.*+?^${}()|[\]\\]/g, "\\$&") + "\\b").test(l));
  if (defLine < 0) return null;
  return { line: defLine + 1, name: w, length: lines[defLine].length };
}

// ── document symbols (outline): scenes + labels ──────────────────────────────
export function documentSymbols(src) {
  const items = [];
  src.split("\n").forEach((l, i) => {
    let m;
    if ((m = l.match(/^\s*scene\s+(\S+)/))) items.push({ kind: "scene", name: m[1], line: i + 1 });
    else if ((m = l.match(/^\s*:(\S+)/))) items.push({ kind: "label", name: m[1], line: i + 1 });
  });
  return items;
}

// ── position helpers (LSP is 0-based {line, character}) ──────────────────────
export function offsetToPos(src, offset) {
  const before = src.slice(0, offset);
  const line = before.split("\n").length - 1;
  const character = offset - (before.lastIndexOf("\n") + 1);
  return { line, character };
}
export function posToOffset(src, pos) {
  const lines = src.split("\n");
  let off = 0;
  for (let i = 0; i < pos.line && i < lines.length; i++) off += lines[i].length + 1;
  return off + pos.character;
}

// Every textual occurrence of a label — its :definition and each reference
// (goto/call/-> targets, if then=/else=) — as {line, col, len} (1-based),
// pointing at the NAME itself. Powers rename-symbol in the IDE: renaming a
// label rewrites every jump to it in one undoable edit.
export function labelOccurrences(src, name) {
  const out = [];
  if (!name) return out;
  const esc = name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const res = [
    new RegExp("^\\s*:(" + esc + ")(?![\\w])", "g"),                 // :def
    new RegExp("->\\s*(" + esc + ")(?![\\w])", "g"),                 // choice target
    new RegExp("\\b(?:goto|call)\\s+(" + esc + ")(?![\\w])", "g"),   // jumps
    new RegExp("\\b(?:then|else)\\s*=\\s*\"?(" + esc + ")(?![\\w])", "g"), // if branches
  ];
  const lines = src.split("\n");
  for (let i = 0; i < lines.length; i++) {
    const seen = new Set(); // one line can match several patterns at one spot
    for (const re of res) {
      re.lastIndex = 0;
      let m;
      while ((m = re.exec(lines[i]))) {
        const col = m.index + m[0].lastIndexOf(m[1]) + 1;
        if (seen.has(col)) continue;
        seen.add(col);
        out.push({ line: i + 1, col, len: name.length });
      }
    }
  }
  out.sort((a, b) => a.line - b.line || a.col - b.col);
  return out;
}
