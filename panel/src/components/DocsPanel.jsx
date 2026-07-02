// Slide-in reference: the engine ops authors can use, grouped, plus a copyable
// prompt for drafting chapters with an LLM. Both are DERIVED from the grammar
// contract (tools/lvn-lang grammar.json → grammar.js) — no hand-kept op lists
// here, so a new op shows up everywhere by editing one file.
import ResizeHandle from "./ResizeHandle.jsx";
import { GROUPS, OP_DOCS } from "lvn-lang/grammar.js";

const SYNTAX = [
  [":label", "a jump target"],
  ["Mara [smile]: Hi.", "speech + emotion"],
  ["- Stay -> inside", "a choice"],
  ['bg sprite_url="…"', "an engine op"],
];

// Op signatures for the LLM prompt come straight from the grammar's hover docs.
const opSignatures = GROUPS
  .flatMap((g) => g.rows.map(([op]) => OP_DOCS[op]?.[0]))
  .filter(Boolean)
  .map((sig) => `  \`${sig}\``)
  .join("\n");

const AI_PROMPT = `# LVNScript (.lvns) — generation rules
Write narrative scripts in LVNScript. Grammar:
- \`scene name\` and \`actor_map Display=asset_id\` at the top.
- \`:label\` is a jump target; \`goto label\`, \`call\`/\`return\`.
- Plain line = narration; \`Name: text\` = speech; \`Name [emo]: text\` = speech + emotion.
- Choices: consecutive \`- Text -> target\` lines (+ optional \`cost=\`, \`min=\`, \`requires_stat=\`).
- Ops:
${opSignatures}
- End paths with \`goto __end\`.`;

export default function DocsPanel({ onClose }) {
  return (
    <aside className="docs enter">
      <ResizeHandle storageKey="ide-w-docs" />
      <div className="docs-head">
        <h2>Reference</h2>
        <button className="btn-ghost sm" onClick={onClose}>✕</button>
      </div>

      <p className="docs-lede">
        Write in <strong>LVNScript</strong> on the left; it compiles to the
        engine's <code>.lvn</code> live. <em>Save to app</em> pushes it to the
        running game in ~2s.
      </p>

      <div className="syntax-mini">
        {SYNTAX.map(([c, d]) => (
          <div key={c}><code>{c}</code><span>{d}</span></div>
        ))}
      </div>

      {GROUPS.map((g) => (
        <div key={g.title} className="ref-group">
          <div className="ref-cat">{g.title}</div>
          {g.rows.map(([op, fields]) => (
            <div key={op} className="ref-row">
              <code>{op}</code><span>{fields}</span>
            </div>
          ))}
        </div>
      ))}

      <div className="ref-group">
        <div className="ref-cat ref-cat-row">
          AI author prompt
          <button className="btn-ghost sm" onClick={() => navigator.clipboard.writeText(AI_PROMPT)}>Copy</button>
        </div>
        <pre className="ai-prompt">{AI_PROMPT}</pre>
      </div>
    </aside>
  );
}
