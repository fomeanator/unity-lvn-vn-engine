import { forwardRef, useImperativeHandle, useRef, useEffect } from "react";
import Editor, { loader } from "@monaco-editor/react";
import * as monaco from "monaco-editor";
import editorWorker from "monaco-editor/esm/vs/editor/editor.worker?worker";
import { OP_DOCS } from "lvn-lang/grammar.js";
import {
  completionAt, hoverAt, definitionAt, documentSymbols, predictGhost, labelsIn, labelInfo, varInfo,
  labelOccurrences,
} from "lvn-lang/analyze.js";

// Bundle Monaco from npm (no CDN) and give it a local worker — works offline.
self.MonacoEnvironment = { getWorker: () => new editorWorker() };
loader.config({ monaco });

// actor_map aliases parsed straight from the document, for character completion.
function actorMapOf(text) {
  const m = {};
  text.split("\n").forEach((l) => { const mm = l.match(/^\s*actor_map\s+(\S+)\s*=\s*(\S+)/); if (mm) m[mm[1]] = mm[2]; });
  return m;
}

const KIND = (mo, k) => ({
  op: mo.languages.CompletionItemKind.Keyword,
  directive: mo.languages.CompletionItemKind.Keyword,
  snippet: mo.languages.CompletionItemKind.Snippet,
  attr: mo.languages.CompletionItemKind.Field,
  value: mo.languages.CompletionItemKind.Value,
  entity: mo.languages.CompletionItemKind.Reference,
  emotion: mo.languages.CompletionItemKind.EnumMember,
  label: mo.languages.CompletionItemKind.Reference,
  speaker: mo.languages.CompletionItemKind.Variable,
}[k] ?? mo.languages.CompletionItemKind.Text);

let registered = false;
// Language providers are registered once (module-level) but every chapter switch
// remounts MonacoEditor (keyed by selId). So the live context must live at module
// scope too — a per-instance ref would freeze the providers on the FIRST chapter's
// catalog/actorMap. Each render writes the current values here.
const sharedCtx = { catalog: {}, actorMap: {} };
// Register the `lvns` language once: highlighting + all providers wired to the
// shared lvn-lang core. getCtx() returns the live { catalog, actorMap }.
function registerLvns(mo, getCtx) {
  if (registered) return;
  registered = true;

  mo.languages.register({ id: "lvns" });
  mo.languages.setLanguageConfiguration("lvns", {
    comments: { lineComment: "//" },
    brackets: [["[", "]"], ["{", "}"], ["(", ")"]],
    autoClosingPairs: [
      { open: '"', close: '"' }, { open: "[", close: "]" }, { open: "{", close: "}" }, { open: "(", close: ")" },
    ],
  });

  // Monarch highlighter — labels, ops, strings, interpolation, choices, comments.
  mo.languages.setMonarchTokensProvider("lvns", {
    ops: ["scene", "actor_map", "bg", "actor", "obj", "fade", "dim", "flash", "tint", "blur",
      "camera", "particles", "audio", "wait", "preload", "text_pace", "goto", "if", "set", "inc",
      "hint", "call", "return", "choice"],
    tokenizer: {
      root: [
        [/^\s*\/\/.*$/, "comment"],
        [/^\s*:[A-Za-z0-9_]+/, "type"],            // :label definition
        [/^\s*-\s/, "keyword"],                     // choice dash
        [/->/, "operator"],
        [/\{[^}]*\}/, "variable"],                  // {interpolation}
        [/\b__end\b/, "constant"],
        [/^\s*([a-z_]+)\b/, { cases: { "@ops": "keyword", "@default": "" } }],
        [/\b[a-z_]+(?==)/, "attribute.name"],       // key=
        [/"[^"]*"/, "string"],
        [/\b\d+(\.\d+)?\b/, "number"],
        [/\[[^\]]*\]/, "tag"],                      // [emotion]
      ],
    },
  });

  mo.editor.defineTheme("lvn-dark", {
    base: "vs-dark", inherit: true,
    rules: [
      { token: "comment", foreground: "6b6457", fontStyle: "italic" },
      { token: "type", foreground: "cf90b6" },          // labels — rose
      { token: "keyword", foreground: "c8a050" },        // ops — gold
      { token: "string", foreground: "9db17e" },
      { token: "number", foreground: "d6a36b" },
      { token: "variable", foreground: "7fb0c8" },
      { token: "attribute.name", foreground: "b59a5e" },
      { token: "operator", foreground: "cf90b6" },
      { token: "constant", foreground: "cf90b6" },
      { token: "tag", foreground: "c8a0c0" },
    ],
    colors: { "editor.background": "#131210", "editorLineNumber.foreground": "#4b463c", "editorCursor.foreground": "#cf90b6" },
  });

  mo.languages.registerCompletionItemProvider("lvns", {
    triggerCharacters: [" ", '"', "[", "=", ">", "-"],
    provideCompletionItems(model, position) {
      const { catalog, actorMap } = getCtx();
      const lineToCaret = model.getValueInRange({ startLineNumber: position.lineNumber, startColumn: 1, endLineNumber: position.lineNumber, endColumn: position.column });
      const r = completionAt(lineToCaret, labelsIn(model.getValue()), catalog, actorMap);
      if (!r) return { suggestions: [] };
      const startCol = position.column - (r.token ? r.token.length : 0);
      const range = new mo.Range(position.lineNumber, startCol, position.lineNumber, position.column);
      const triggerSuggest = { id: "editor.action.triggerSuggest", title: "" };
      const suggestions = r.items.map((it) => {
        const isSnippet = it.kind === "snippet";
        let insert = it.text;
        let command;
        if (it.kind === "op" || it.kind === "directive") insert = it.text + " ";
        else if (it.kind === "attr") insert = it.text + "=";
        else if (it.kind === "emotion") insert = it.text + "]: ";       // close bracket + colon
        else if (it.kind === "speaker") {
          if (it.emote) { insert = it.text + " ["; command = triggerSuggest; } // open emotions
          else insert = it.text + ": ";
        }
        if (isSnippet) insert = it.body;
        return {
          label: it.label || it.text,
          kind: KIND(mo, it.kind),
          insertText: insert,
          insertTextRules: isSnippet ? mo.languages.CompletionItemInsertTextRule.InsertAsSnippet : undefined,
          detail: OP_DOCS[it.text] ? OP_DOCS[it.text][0] : it.kind,
          documentation: OP_DOCS[it.text] ? OP_DOCS[it.text][1] : undefined,
          command,
          range,
        };
      });
      return { suggestions };
    },
  });

  mo.languages.registerHoverProvider("lvns", {
    provideHover(model, position) {
      const { catalog, actorMap } = getCtx();
      const h = hoverAt(model.getValue(), position.lineNumber, position.column - 1, { catalog, actorMap });
      if (!h) return null;
      let md = "";
      if (h.kind === "op") md = "`" + h.sig + "`\n\n" + h.desc;
      else if (h.kind === "entity") { const e = catalog[h.id]; md = "**" + ((e && e.name) || h.id) + "**" + (e && e.axes ? "\n\n" + Object.keys(e.axes).map((a) => a + ": " + (e.axes[a] || []).join("/")).join(" · ") : " · object"); }
      else if (h.kind === "emotion") md = (h.ok ? "✓ " : "⚠ ") + "`" + h.emo + "` — " + (h.ok ? "emotion of " + h.charId : "not an emotion of " + h.charId + (h.emos && h.emos.length ? " (try: " + h.emos.join(", ") + ")" : ""));
      else if (h.kind === "label") md = "`:" + h.name + "`" + (h.defined ? " — defined at line " + h.defLine + " · " + h.refs + " jump(s)" : " — ⚠ undefined label");
      else if (h.kind === "var") md = "`" + h.name + (h.lastVal ? " " + h.lastVal : "") + "` — variable · set " + h.sets + "× · read " + h.uses + "×";
      return { contents: [{ value: md }] };
    },
  });

  mo.languages.registerDefinitionProvider("lvns", {
    provideDefinition(model, position) {
      const d = definitionAt(model.getValue(), position.lineNumber, position.column - 1);
      if (!d) return null;
      return { uri: model.uri, range: new mo.Range(d.line, 1, d.line, (d.length || 0) + 1) };
    },
  });

  // F2 rename on a label: rewrites the :definition and every goto/call/->/
  // then/else reference in one undoable edit.
  mo.languages.registerRenameProvider("lvns", {
    resolveRenameLocation(model, position) {
      const w = model.getWordAtPosition(position);
      if (!w) return { range: new mo.Range(1, 1, 1, 1), text: "", rejectReason: "Nothing to rename here" };
      const labels = labelsIn(model.getValue());
      if (!labels.includes(w.word))
        return { range: new mo.Range(1, 1, 1, 1), text: "", rejectReason: "Only labels can be renamed" };
      return { range: new mo.Range(position.lineNumber, w.startColumn, position.lineNumber, w.endColumn), text: w.word };
    },
    provideRenameEdits(model, position, newName) {
      const w = model.getWordAtPosition(position);
      if (!w) return { edits: [] };
      if (!/^[A-Za-z0-9_]+$/.test(newName))
        return { edits: [], rejectReason: "Labels are letters, digits and _" };
      const edits = labelOccurrences(model.getValue(), w.word).map((o) => ({
        resource: model.uri,
        textEdit: { range: new mo.Range(o.line, o.col, o.line, o.col + o.len), text: newName },
        versionId: undefined,
      }));
      return { edits };
    },
  });

  mo.languages.registerDocumentSymbolProvider("lvns", {
    provideDocumentSymbols(model) {
      return documentSymbols(model.getValue()).map((s) => ({
        name: s.name,
        kind: s.kind === "scene" ? mo.languages.SymbolKind.Namespace : mo.languages.SymbolKind.Key,
        range: new mo.Range(s.line, 1, s.line, 1),
        selectionRange: new mo.Range(s.line, 1, s.line, 1),
        tags: [],
      }));
    },
  });

  // Copilot-style ghost (rule-based) via native inline completions.
  mo.languages.registerInlineCompletionsProvider("lvns", {
    provideInlineCompletions(model, position) {
      if (position.column !== model.getLineMaxColumn(position.lineNumber)) return { items: [] };
      const { catalog, actorMap } = getCtx();
      const lineToCaret = model.getValueInRange({ startLineNumber: position.lineNumber, startColumn: 1, endLineNumber: position.lineNumber, endColumn: position.column });
      const g = predictGhost(lineToCaret, { catalog, actorMap });
      if (!g) return { items: [] };
      return { items: [{ insertText: g, range: new mo.Range(position.lineNumber, position.column, position.lineNumber, position.column) }] };
    },
    freeInlineCompletions() {},
  });
}

const MonacoEditor = forwardRef(function MonacoEditor({ src, onChange, diags = [], jump, catalog = {}, onCaret, readOnly = false }, ref) {
  const edRef = useRef(null);
  const moRef = useRef(null);
  // keep the shared (module-level) context current for the registered providers.
  // actorMapOf is a full-document scan — rebuilding it on EVERY keystroke's
  // render is waste; throttle to ~3/s (completions tolerate a 300ms-stale map).
  const amCache = useRef({ src: null, map: {}, at: 0 });
  sharedCtx.catalog = catalog;
  if (amCache.current.src !== src) {
    const now = performance.now();
    if (now - amCache.current.at > 300) {
      amCache.current = { src, map: actorMapOf(src), at: now };
    }
  }
  sharedCtx.actorMap = amCache.current.map;

  useImperativeHandle(ref, () => ({
    applyText(text) {
      const ed = edRef.current; if (!ed) return;
      ed.pushUndoStop();
      ed.executeEdits("applyText", [{ range: ed.getModel().getFullModelRange(), text }]);
      ed.pushUndoStop();
      ed.setPosition({ lineNumber: 1, column: 1 });
      ed.revealLine(1);
    },
  }));

  function onMount(ed, mo) {
    edRef.current = ed;
    moRef.current = mo;
    registerLvns(mo, () => sharedCtx);
    mo.editor.setTheme("lvn-dark");
    ed.onDidChangeCursorPosition((e) => onCaret && onCaret({ line: e.position.lineNumber, col: e.position.column }));
  }

  // diagnostics → markers (rich: message on hover, squiggle, in the Problems list)
  useEffect(() => {
    const ed = edRef.current, mo = moRef.current;
    if (!ed || !mo) return;
    const model = ed.getModel(); if (!model) return;
    mo.editor.setModelMarkers(model, "lvns", diags.filter((d) => d.line > 0).map((d) => ({
      startLineNumber: d.line, startColumn: 1,
      endLineNumber: d.line, endColumn: model.getLineMaxColumn(d.line),
      message: (d.op ? d.op + ": " : "") + (d.msg || ""),
      severity: d.sev === "error" ? mo.MarkerSeverity.Error : mo.MarkerSeverity.Warning,
    })));
  }, [diags, src]);

  // external jump (Outline / Problems click) → reveal + place caret
  useEffect(() => {
    const ed = edRef.current; if (!ed || !jump || !jump.line) return;
    ed.revealLineInCenter(jump.line);
    ed.setPosition({ lineNumber: jump.line, column: 1 });
    ed.focus();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [jump?.n]);

  return (
    <Editor
      height="100%"
      language="lvns"
      theme="lvn-dark"
      value={src}
      onChange={(v) => onChange(v ?? "")}
      onMount={onMount}
      options={{
        readOnly,
        fontSize: 13,
        fontFamily: "var(--font-mono), ui-monospace, monospace",
        minimap: { enabled: true },
        lineNumbers: "on",
        renderWhitespace: "none",
        tabSize: 2,
        insertSpaces: true,
        wordWrap: "off",
        inlineSuggest: { enabled: true },
        quickSuggestions: { other: true, comments: false, strings: true },
        suggestOnTriggerCharacters: true,
        scrollBeyondLastLine: false,
        smoothScrolling: true,
        cursorBlinking: "smooth",
        automaticLayout: true,
        padding: { top: 12 },
      }}
    />
  );
});

export default MonacoEditor;
