// Drift guard: grammar.js is GENERATED from grammar.json (npm run gen). If
// someone edits either side by hand and forgets to regenerate, this fails —
// the whole point of the single-source contract.
import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { OPS, OP_FIELDS, ATTR_VALUES, OP_DOCS, SNIPPETS, DIRECTIVES, GROUPS } from "./src/grammar.js";

const json = JSON.parse(readFileSync(new URL("./src/grammar.json", import.meta.url), "utf8"));

test("grammar.js matches grammar.json (regenerate with `npm run gen`)", () => {
  assert.deepEqual(OPS, json.ops);
  assert.deepEqual(OP_FIELDS, json.op_fields);
  assert.deepEqual(ATTR_VALUES, json.attr_values);
  assert.deepEqual(OP_DOCS, json.op_docs);
  assert.deepEqual(SNIPPETS, json.snippets);
  assert.deepEqual(DIRECTIVES, json.directives);
});

test("every grouped op exists and has docs", () => {
  for (const g of GROUPS)
    for (const [op] of g.rows) {
      assert.ok(json.ops.includes(op) || json.structural_ops.includes(op), `unknown op in groups: ${op}`);
      assert.ok(json.op_docs[op], `grouped op has no docs: ${op}`);
    }
});

test("closed-field ops and enums reference real ops/fields", () => {
  for (const op of json.closed_field_ops)
    assert.ok(json.op_fields[op] !== undefined, `closed op missing from op_fields: ${op}`);
  for (const [op, fields] of Object.entries(json.enums)) {
    assert.ok(json.op_fields[op], `enum op missing from op_fields: ${op}`);
    for (const f of Object.keys(fields))
      assert.ok(json.op_fields[op].includes(f), `enum field not in op_fields: ${op}.${f}`);
  }
});

test("labelOccurrences finds the definition and every reference", async () => {
  const { labelOccurrences } = await import("./src/analyze.js");
  const src = [
    ":tavern",            // def (line 1)
    "goto tavern",        // ref (line 2)
    "- Drink -> tavern",  // ref (line 3)
    'if expr="x" then="tavern" else="street"', // ref (line 4)
    "goto tavern_back",   // NOT a match (longer name)
    "call tavern",        // ref (line 6)
  ].join("\n");
  const occ = labelOccurrences(src, "tavern");
  assert.deepEqual(occ.map((o) => o.line), [1, 2, 3, 4, 6]);
  for (const o of occ)
    assert.equal(src.split("\n")[o.line - 1].slice(o.col - 1, o.col - 1 + o.len), "tavern");
});
