// LVNScript grammar data — the single source of truth for ops, their fields,
// enumerated attribute values, hover docs and snippets. Pure data, no editor or
// framework deps, so both the authoring panel and the language server consume it.

export const DIRECTIVES = ["scene", "actor_map"];

export const OPS = [
  "say", "choice", "bg", "actor", "obj", "fade", "dim", "flash", "tint", "blur",
  "camera", "particles", "audio", "wait", "preload", "text_pace", "text", "anim",
  "goto", "if", "set", "inc", "hint", "call", "return", "save", "load",
];

export const OP_FIELDS = {
  bg: ["sprite_url", "id"],
  actor: ["id", "sprite_url", "show", "position", "emotion", "play"],
  obj: ["id", "sprite_url", "x", "y", "width", "height", "on_click"],
  fade: ["to", "duration"], dim: ["alpha", "duration"], flash: ["color", "duration"],
  tint: ["color", "alpha", "duration"], blur: ["alpha", "duration"],
  camera: ["action", "amplitude", "factor", "x", "y", "duration"], particles: ["type", "on"],
  audio: ["channel", "url", "action"], wait: ["ms"], text_pace: ["cps"],
  text: ["id", "text", "hide", "x", "y", "anchor", "size", "color", "font"],
  anim: ["id", "anim", "stop", "channel", "mode"],
  save: ["slot"], load: ["slot"],
  if: ["expr", "then", "else"], set: ["key", "value", "expr"], inc: ["key", "by"], hint: ["text", "show"],
};

// Enumerated values per attribute key — suggested after `key=`.
export const ATTR_VALUES = {
  to: ["black", "white", "clear"],
  color: ["white", "black", "red", "blue", "green", "yellow", "cyan", "magenta", "cold", "warm", "sepia"],
  type: ["rain", "snow"],
  position: ["left", "center", "right"],
  show: ["true", "false"], on: ["true", "false"],
  channel: ["music", "ambient", "sfx"],
  // `action` is op-specific (camera vs audio) — handled in completion.
};

// Hover docs: a one-line signature + what the op does.
export const OP_DOCS = {
  scene: ["scene <name>", "Chapter id at the top of the file."],
  actor_map: ["actor_map Display=asset_id", "Map a speaker name to a sprite id."],
  say: ["Name: text · Name [emo]: text", "A line of dialogue (narration if no name)."],
  choice: ["- Text -> label  [cost= min= requires_stat=]", "Branching options — one '-' line each; optional cost/min/requires_stat gate an option."],
  bg: ['bg sprite_url="…" | id="…"', "Set the background image."],
  actor: ['actor id="x" show=true position="left" emotion="happy"', "Show, place and emote a character."],
  obj: ['obj id="x" sprite_url="…" x= y= on_click="label"', "A placeable, optionally clickable sprite."],
  fade: ['fade to="black|white|clear" duration=0.8', "Fade the screen to/from a colour."],
  dim: ["dim alpha=0.4 duration=0.5", "Dim the scene under an alpha veil."],
  flash: ['flash color="white" duration=0.2', "A quick colour flash."],
  tint: ['tint color="warm" alpha=0.3 duration=0.6', "Wash the scene with a colour tint."],
  blur: ["blur alpha=0.5 duration=0.5", "Blur the scene (alpha 0 clears it)."],
  camera: ['camera action="shake|zoom|pan|reset" duration=0.5', "Move the world layer (shake/zoom/pan)."],
  particles: ['particles type="rain|snow" on=true', "Weather particles over the scene."],
  audio: ['audio channel="music" url="…" action="play|stop"', "Play or stop a track."],
  wait: ["wait ms=1000", "Pause for a number of milliseconds."],
  preload: ["preload assets=[…]", "Warm assets before they're shown."],
  text_pace: ["text_pace cps=40", "Typewriter speed (characters per second)."],
  goto: ["goto label   ·   goto __end", "Jump to a label; __end ends the chapter."],
  if: ['if expr="score>=10" then="win" else="lose"', "Branch on an expression."],
  set: ['set key="k" value=v · set key="k" expr="a+1"', "Set a variable (literal or computed)."],
  inc: ['inc key="k" by=1', "Increment a numeric variable."],
  hint: ['hint text="…" show=true', "Show or hide a hint."],
  call: ["call label", "Jump to a label, remembering where to return."],
  return: ["return", "Return to the matching call."],
  text: ['text id="hp" text="{hp} HP" x= y= anchor="top-left"', "A reactive on-screen label (HUD/stat); text hide=true removes it."],
  anim: ['anim id="x" anim={…}  ·  anim id="x" stop="all"', "Play or stop a script-driven tween on an entity."],
  save: ['save slot="quick"', "Snapshot the current state to a slot."],
  load: ['load slot="quick"', "Restore state from a slot."],
};

// Multi-field templates. `$1`,`$2`,… are tab-stops; `$0` is the final caret.
export const SNIPPETS = [
  { trigger: "choice", label: "choice — two options", body: "- $1 -> $2\n- $3 -> __end" },
  { trigger: "if", label: "if / then / else", body: 'if expr="$1" then="$2" else="$3"' },
  { trigger: "actor", label: "actor — show", body: 'actor id="$1" show=true position="$2" emotion="$3"' },
  { trigger: "bg", label: "bg — background", body: 'bg id="$1"' },
  { trigger: "fade", label: "fade", body: 'fade to="$1" duration=$2' },
  { trigger: "set", label: "set variable", body: 'set key="$1" value=$2' },
  { trigger: "inc", label: "inc variable", body: 'inc key="$1" by=$2' },
];

// Default emotions when a character has no catalog axes.
export const DEFAULT_EMOTIONS = ["neutral", "happy", "sad", "angry", "smile"];
