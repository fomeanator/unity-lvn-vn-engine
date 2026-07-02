// Regenerates src/grammar.js from src/grammar.json — the single source of
// truth for the LVNScript op contract. Run after editing grammar.json:
//
//   npm run gen
//
// The generated file keeps grammar.js's public exports stable (OPS, OP_FIELDS,
// ATTR_VALUES, OP_DOCS, SNIPPETS, DIRECTIVES, DEFAULT_EMOTIONS) and adds the
// derived reference-panel GROUPS and the AI-prompt op list, so the panel,
// Monaco and the docs all read one contract. The Go validator parity-tests
// against the same JSON (tools/lvnconv/lvn/grammar_sync_test.go).
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const g = JSON.parse(readFileSync(join(root, "src/grammar.json"), "utf8"));

// Field summary for the reference panel: fields, with closed enums inlined.
function fieldsSummary(op) {
  const fields = g.op_fields[op] || [];
  const enums = g.enums[op] || {};
  return fields
    .map((f) => {
      const vals = (enums[f] || []).filter((v) => v !== "");
      return vals.length ? `${f} (${vals.join("/")})` : f;
    })
    .join(", ");
}

const groups = g.groups.map((grp) => ({
  title: grp.title,
  rows: grp.ops.map((op) => [op, fieldsSummary(op)]),
}));

const j = (v) => JSON.stringify(v, null, 2);

const out = `// GENERATED from grammar.json — do not edit by hand (npm run gen).
// grammar.json is the single source of truth for the LVNScript op contract;
// the Go validator parity-tests against the same file, so an edit here (or a
// forgotten regen) fails tests instead of silently drifting.

export const DIRECTIVES = ${j(g.directives)};

export const OPS = ${j(g.ops)};

export const OP_FIELDS = ${j(g.op_fields)};

// Enumerated values per attribute key — suggested after \`key=\`.
export const ATTR_VALUES = ${j(g.attr_values)};

// Hover docs: a one-line signature + what the op does.
export const OP_DOCS = ${j(g.op_docs)};

// Multi-field templates. \`$1\`,\`$2\`,… are tab-stops; \`$0\` is the final caret.
export const SNIPPETS = ${j(g.snippets)};

// Default emotions when a character has no catalog axes.
export const DEFAULT_EMOTIONS = ${j(g.default_emotions)};

// Reference-panel grouping, with field summaries derived from OP_FIELDS+enums.
export const GROUPS = ${j(groups)};

// The op-signature block for the AI authoring prompt, derived from OP_DOCS.
export const AI_OP_LINES = OPS.map((op) => {
  const d = OP_DOCS[op];
  return d ? \`\\\`\${d[0]}\\\`\` : null;
}).filter(Boolean);
`;

writeFileSync(join(root, "src/grammar.js"), out);
console.log("generated src/grammar.js from grammar.json");
