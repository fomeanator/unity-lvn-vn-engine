// Package adpd is the articy:draft binary-project front-end. It reads an
// `ADPD8` Flow partition directly (no JSON export needed), reconstructs the full
// articy object model — including instructions and conditions — and emits it in
// the same JSON shape the `articy` package consumes, so a raw `.adpd` project
// converts through the very same back-end as a JSON export.
//
// Format (see ../../docs/articy-adpd-format.md): the body is a sequence of
// length-prefixed objects. Each object header is
//
//	<bodyLen:uint32> <u16> <u8> <typecode:u8> 00 00 00
//
// where bodyLen counts the bytes after the uint32 up to the next object, and the
// (C, typecode) pair identifies the object kind. An object body is a flat run of
// typed property entries <seq:u16><propid:u16><tag:u8><value>. Objects are flat
// siblings (a fragment's text/speaker/pins/connections are separate objects whose
// parent propid 0x0c points back at it). Connections carry propid 0x02 =
// [src, dst, srcPin, dstPin] — the directed edges.
package adpd

import (
	"encoding/binary"
	"encoding/json"
	"fmt"
	"html"
	"math"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"unicode"
)

// property ids (field names are not stored in the binary)
const (
	pConn    = 0x02  // connection: 4 refs [src, dst, srcPin, dstPin]
	pInstr   = 0x03  // pin leave-instruction (X = N;)
	pParent  = 0x0c  // parent/owner ordinal
	pSelf    = 0x39  // self ordinal (edge id of a flow node)
	pCond    = 0x79  // pin enter-condition ({ns}.{var} == ...;)
	pID      = 0x3a  // object GUID id (stable localization key)
	pCaption = 0x100 // speaker caption (string) / colour (non-string)
	pText    = 0x200 // line text (HTML)
)

// object kinds, by the header's (C, typecode) pair (legacy heuristic; the Global
// Variables / Entities partitions are still classified this way).
type kind struct{ c, t byte }

var (
	kFragment   = kind{1, 4}  // DialogueFragment content (has Text)
	kSpeaker    = kind{2, 6}  // speaker reference (has caption)
	kLogic      = kind{1, 17} // Instruction / Condition node
	kConnection = kind{3, 11} // a flow edge (has 0x02)
)

// Flow-partition class ids — the authoritative type discriminator (uint16 at
// model offset+4), recovered from the editor's [ClassId(N)] attributes via
// decompilation. The model header is <int32 size><uint16 classId><byte version>
// <int32 numProps>; see lvn-adpd-format-truth. (Earlier code keyed on
// (version, numProps&0xFF), which only clustered by accident.)
const (
	cidMLText      uint16 = 24  // ArticyMultiLanguageText — a line's text
	cidModelDep    uint16 = 9   // ModelDependency — a reference (speaker)
	cidConnection  uint16 = 4   // Connection — a flow edge [src,dst,srcPin,dstPin]
	cidPin         uint16 = 10  // input/output Pin
	cidDialog      uint16 = 74  // Dialog — a scene container
	cidFlowFrag    uint16 = 76  // FlowFragment — a chapter container
	cidCondition   uint16 = 162 // Condition — an if split
	cidOutcome     uint16 = 163 // Outcome — a pin script (set/inc)
	cidDialogFrag  uint16 = 75  // DialogFragment — a dialogue node
	cidStoryFolder uint16 = 80  // the project root
	cidHub         uint16 = 77  // Hub (absent in the test projects)
	cidJump        uint16 = 78  // Jump (absent in the test projects)
)

type prop struct {
	tag byte
	s   string
	u   uint32
}

type entry struct {
	propid uint16
	prop
}

// header returns (bodyLen, classId, C, typecode) at o, or ok=false. The model
// frame is <int32 size><uint16 classId><byte version><int32 numProps>; C/typecode
// are the legacy (version, numProps&0xFF) heuristic kept for the other partitions.
func header(d []byte, o, idx int) (uint32, uint16, byte, byte, bool) {
	if o+11 > idx || d[o+8] != 0 || d[o+9] != 0 || d[o+10] != 0 {
		return 0, 0, 0, 0, false
	}
	bl := binary.LittleEndian.Uint32(d[o:])
	if bl <= 4 || int(bl) >= idx || o+4+int(bl) > idx {
		return 0, 0, 0, 0, false
	}
	return bl, binary.LittleEndian.Uint16(d[o+4:]), d[o+6], d[o+7], true
}

// entries parses [a,b) as a flat run of property entries.
func entries(d []byte, a, b int) []entry {
	var out []entry
	o := a
	for o < b && o+5 <= b {
		seq := binary.LittleEndian.Uint16(d[o:])
		pid := binary.LittleEndian.Uint16(d[o+2:])
		tag := d[o+4]
		v := o + 5
		ok := false
		switch {
		case tag == 0x12 && pid < 0x400 && seq < 0x600 && v+4 <= b:
			ln := int(binary.LittleEndian.Uint32(d[v:]))
			if ln >= 0 && ln < 200000 && v+4+ln <= b {
				out = append(out, entry{pid, prop{tag: tag, s: string(d[v+4 : v+4+ln])}})
				o = v + 4 + ln
				ok = true
			}
		case (tag == 0xf6 || tag == 0xf7) && pid < 0x400 && seq < 0x600 && v+8 <= b:
			out = append(out, entry{pid, prop{tag: tag}})
			o = v + 8
			ok = true
		case (tag == 0xfa || tag == 0xfb || tag == 0xfc || tag == 0xfd || tag == 0xfe || tag == 0xee || tag == 0xef) &&
			pid < 0x400 && seq < 0x600 && v+4 <= b:
			out = append(out, entry{pid, prop{tag: tag, u: binary.LittleEndian.Uint32(d[v:])}})
			o = v + 4
			ok = true
		}
		if !ok {
			o++
		}
	}
	return out
}

// findStart returns the offset whose length-prefixed object chain runs longest
// (the real object stream begins after a short partition preamble).
func findStart(d []byte, idx int) int {
	best, bestLen := -1, 0
	for o := 24; o < 3000; o++ {
		if _, _, _, _, ok := header(d, o, idx); !ok {
			continue
		}
		p, n := o, 0
		for p < idx {
			bl, _, _, _, ok := header(d, p, idx)
			if !ok {
				break
			}
			p += 4 + int(bl)
			n++
		}
		if n > bestLen {
			best, bestLen = o, n
		}
	}
	return best
}

type object struct {
	classId uint16
	c, t    byte
	es      []entry
}

func (o object) u32(pid uint16) (uint32, bool) {
	for _, e := range o.es {
		if e.propid == pid && (e.tag == 0xfe || e.tag == 0xfa || e.tag == 0xfb || e.tag == 0xfc || e.tag == 0xfd) {
			return e.u, true
		}
	}
	return 0, false
}

func (o object) str(pid uint16) string {
	for _, e := range o.es {
		if e.propid == pid && e.tag == 0x12 {
			return e.s
		}
	}
	return ""
}

func (o object) refs(pid uint16) []uint32 {
	var r []uint32
	for _, e := range o.es {
		if e.propid == pid && e.tag == 0xfe {
			r = append(r, e.u)
		}
	}
	return r
}

// walkObjects returns every length-prefixed object in the body.
func walkObjects(d []byte, idx int) []object {
	start := findStart(d, idx)
	if start < 0 {
		return nil
	}
	var objs []object
	for o := start; o < idx; {
		bl, classId, c, t, ok := header(d, o, idx)
		if !ok {
			break
		}
		objs = append(objs, object{classId: classId, c: c, t: t, es: entries(d, o+11, o+4+int(bl))})
		o += 4 + int(bl)
	}
	return objs
}

var (
	namedRefRe = regexp.MustCompile(`\{(\d+):([^}]+)\}`) // {guid:Name}
	bareRefRe  = regexp.MustCompile(`\{(\d+)\}`)         // {guid} (no name)
)

// varMap collects every variable GUID → name seen in any logic expression, so a
// name-less {guid} reference can be resolved to its full name.
func varMap(objs []object) map[string]string {
	m := map[string]string{}
	for _, o := range objs {
		if o.classId != cidCondition && o.classId != cidOutcome {
			continue
		}
		for _, mm := range namedRefRe.FindAllStringSubmatch(o.str(pCond), -1) {
			m[mm[1]] = mm[2]
		}
		for _, mm := range namedRefRe.FindAllStringSubmatch(o.str(pInstr), -1) {
			m[mm[1]] = mm[2]
		}
	}
	return m
}

// resolveExpr turns articy's GUID-encoded expression into a plain one:
// {guid:Name} → Name, {guid} → the mapped name (full names survive even when the
// human-readable copy was truncated with "…").
func resolveExpr(s string, m map[string]string) string {
	s = namedRefRe.ReplaceAllString(s, "$2")
	s = bareRefRe.ReplaceAllStringFunc(s, func(x string) string {
		if n, ok := m[bareRefRe.FindStringSubmatch(x)[1]]; ok {
			return n
		}
		return x
	})
	return strings.TrimSpace(strings.TrimSuffix(strings.TrimSpace(s), ";"))
}

var condOpRe = regexp.MustCompile(`[=!<>]=|[<>]`)

func isCondition(expr string) bool {
	return strings.Contains(expr, "==") || condOpRe.MatchString(expr)
}

// parseableInstr reports whether every `;`-separated statement is in the subset
// the articy back-end accepts (`x = value` / `x = expr` / `x += N`). Expressions
// outside it (rare multi-ref forms) route as a Hub instead, so a single odd
// instruction never fails the whole conversion.
var (
	reIncOK = regexp.MustCompile(`^[\w.]+\s*[+-]=\s*[0-9]+$`)
	reSetOK = regexp.MustCompile(`^[\w.]+\s*=\s*[^=].*$`)
)

func parseableInstr(expr string) bool {
	any := false
	for _, s := range strings.Split(expr, ";") {
		s = strings.TrimSpace(s)
		if s == "" {
			continue
		}
		if !reIncOK.MatchString(s) && !reSetOK.MatchString(s) {
			return false
		}
		any = true
	}
	return any
}

// ── HTML stripping ───────────────────────────────────────────────────────────

var (
	reBodyOpen  = regexp.MustCompile(`(?is).*<body[^>]*>`)
	reBodyClose = regexp.MustCompile(`(?is)</body>.*`)
	reTags      = regexp.MustCompile(`(?s)<[^>]+>`)
	reSpace     = regexp.MustCompile(`\s+`)
)

func stripHTML(t string) string {
	if !strings.Contains(t, "<") {
		return strings.TrimSpace(t)
	}
	b := reBodyOpen.ReplaceAllString(t, "")
	b = reBodyClose.ReplaceAllString(b, "")
	b = reTags.ReplaceAllString(b, " ")
	b = html.UnescapeString(b) // named + numeric entities (&#171; → «)
	return strings.TrimSpace(reSpace.ReplaceAllString(b, " "))
}

// ── reconstructed flow model ─────────────────────────────────────────────────

type logicNode struct {
	cond bool // true → Condition (if), false → Instruction (set)
	expr string
}

type edge struct{ src, dst, srcPin uint32 }

type flow struct {
	text  map[uint32]string    // node ordinal → line
	guid  map[uint32]string    // node ordinal → fragment GUID (stable i18n key)
	sp    map[uint32]string    // node ordinal → speaker caption
	logic map[uint32]logicNode // node ordinal → instruction/condition
	succ  map[uint32][]edge    // node ordinal → outgoing edges
	nodes map[uint32]bool      // every node that appears in an edge

	// Container hierarchy: articy nests content in FlowFragment/Dialogue
	// containers, which list their children (in authoring order) as 0x0 refs.
	// The 0x02 connection graph alone is shattered into hundreds of islands —
	// the cross-scene/chapter flow lives in this nesting. childrenOf maps a
	// container's self ordinal → its ordered children; contSet marks containers.
	childrenOf map[uint32][]uint32
	contSet    map[uint32]bool

	// pg is articy's own pin/connection graph: for a DialogFragment it resolves the
	// stops reachable next (descending containers via pins) — the player's branches.
	pg *pinGraph
}

// container object kinds (FlowFragment / Dialogue), by (C, typecode).
var (
	kSceneCont   = kind{5, 22} // a scene / dialogue container
	kChapterCont = kind{6, 20} // a chapter / top-level container
)

const maxChoiceOptions = 8 // above this a fan-out is structural, not a player menu

const pChild = 0x00 // a container's child ordinal (repeats, in authoring order)

func decodeFlow(d []byte) flow {
	idx := int(binary.LittleEndian.Uint64(d[8:]))
	if idx <= 0 || idx > len(d) {
		idx = len(d)
	}
	objs := walkObjects(d, idx)
	vm := varMap(objs)
	fl := flow{
		text: map[uint32]string{}, guid: map[uint32]string{}, sp: map[uint32]string{},
		logic: map[uint32]logicNode{}, succ: map[uint32][]edge{}, nodes: map[uint32]bool{},
		childrenOf: map[uint32][]uint32{}, contSet: map[uint32]bool{},
		pg: buildPinGraph(objs),
	}
	for _, o := range objs {
		switch o.classId {
		case cidConnection:
			r := o.refs(pConn)
			if len(r) >= 4 {
				e := edge{src: r[0], dst: r[1], srcPin: r[2]}
				fl.succ[e.src] = append(fl.succ[e.src], e)
				fl.nodes[e.src] = true
				fl.nodes[e.dst] = true
			}
		case cidMLText: // the line's text, parented to its DialogFragment
			if par, ok := o.u32(pParent); ok {
				if t := o.str(pText); t != "" {
					fl.text[par] = stripHTML(t)
					if g := o.str(pID); g != "" {
						fl.guid[par] = g
					}
				}
			}
		case cidModelDep: // a reference (the speaker), parented to the fragment
			if par, ok := o.u32(pParent); ok {
				if s := o.str(pCaption); s != "" {
					fl.sp[par] = s
				}
			}
		case cidDialog, cidFlowFrag, cidStoryFolder: // container — ordered children
			if self, ok := o.u32(pSelf); ok {
				fl.contSet[self] = true
				if ch := o.refs(pChild); len(ch) > 0 {
					fl.childrenOf[self] = ch // ordered child list (authoring order)
				}
			}
		case cidCondition: // an if split (0x79 holds the GUID-encoded script)
			if self, ok := o.u32(pSelf); ok {
				expr := resolveExpr(o.str(pCond), vm)
				if expr == "" {
					expr = resolveExpr(o.str(pInstr), vm)
				}
				if expr != "" {
					fl.logic[self] = logicNode{cond: true, expr: expr}
				}
			}
		case cidOutcome: // a pin script — set/inc
			if self, ok := o.u32(pSelf); ok {
				// Prefer the full GUID-encoded script (0x79); the readable 0x03 copy
				// is truncated with "…" for long names and must not leak into a set.
				expr := resolveExpr(o.str(pCond), vm)
				if expr == "" {
					expr = resolveExpr(o.str(pInstr), vm)
				}
				if parseableInstr(expr) {
					fl.logic[self] = logicNode{cond: false, expr: expr}
				}
			}
		}
	}
	return fl
}

// ── structural vs. player-choice disambiguation ──────────────────────────────
//
// articy stores the whole flow as a graph: a node with several outgoing edges is
// either a real player choice (the targets are the menu lines) OR structural
// branching (a scene transition, a routing hub, a logic split). The earlier
// decoder turned EVERY fan-out into a `choice`, so scene delimiters ("Сцена N. …")
// and empty routing hubs surfaced as bogus "Дальше"/"Сцена N" buttons — the novel
// read as a branch index, not a story. We classify each target and keep a `choice`
// only where the options are genuine dialogue lines; structural fan-outs collapse
// to a single sequential continuation (the scenes/sub-flows they point at are
// still emitted — buildExportAll surfaces every node — just not as menu options).

var sceneTextRe = regexp.MustCompile(`^\s*Сцена\s+\d+\b`)

// nodeClass labels a node by what it contributes to the flow.
func (fl flow) nodeClass(n uint32) string {
	if ln, ok := fl.logic[n]; ok {
		if ln.cond {
			return "cond"
		}
		return "instr"
	}
	t := strings.TrimSpace(fl.text[n])
	switch {
	case t == "":
		return "empty" // a routing hub — never a player option
	case sceneTextRe.MatchString(t):
		return "scene" // a scene delimiter — a transition, never a player option
	default:
		return "text" // a real dialogue line — a valid menu option
	}
}

// linearizeStructuralFanouts rewrites the graph so only genuine menus stay
// branching. For each fan-out: if ≥2 distinct targets are real dialogue lines it
// is a player choice (keep just those text options); otherwise it is structural
// and collapses to one continuation — preferring a dialogue line, then a scene
// transition, then a logic node, then the first edge — so the story flows on
// instead of presenting a menu of delimiters.
func linearizeStructuralFanouts(fl flow) {
	for n := range fl.nodes {
		es := fl.succ[n]
		if len(es) < 2 {
			continue
		}
		seen := map[uint32]bool{}
		var uniq, textEdges []edge
		for _, e := range es {
			if seen[e.dst] {
				continue
			}
			seen[e.dst] = true
			uniq = append(uniq, e)
			if fl.nodeClass(e.dst) == "text" {
				textEdges = append(textEdges, e)
			}
		}
		if len(uniq) < 2 {
			fl.succ[n] = uniq
			continue
		}
		if len(textEdges) >= 2 {
			fl.succ[n] = textEdges // a real choice between dialogue lines
			continue
		}
		// Structural: keep a single continuation so the flow stays linear here.
		pick := uniq[0]
		for _, want := range []string{"text", "scene", "instr", "cond"} {
			done := false
			for _, e := range uniq {
				if fl.nodeClass(e.dst) == want {
					pick, done = e, true
					break
				}
			}
			if done {
				break
			}
		}
		fl.succ[n] = []edge{pick}
	}
}

// ── hierarchy spine ──────────────────────────────────────────────────────────
//
// The 0x02 connection graph is shattered into hundreds of islands; the connective
// tissue (scene→scene, chapter→chapter) is the container nesting. hierarchyOrder
// walks the container tree depth-first in child-list (authoring) order and returns
// every flow node in that order — the story's spine. On the test novels this
// recovers ~98% of content as one ordered sequence (vs ~144 disconnected 0x02
// components).

func (fl flow) hierarchyOrder() []uint32 {
	isChild := map[uint32]bool{}
	for _, ch := range fl.childrenOf {
		for _, c := range ch {
			isChild[c] = true
		}
	}
	var tops []uint32
	for c := range fl.contSet {
		if !isChild[c] {
			tops = append(tops, c)
		}
	}
	sort.Slice(tops, func(i, j int) bool { return tops[i] < tops[j] })

	visited := map[uint32]bool{}
	var order []uint32
	// A scene/chapter name lives on its container (FlowFragment/Dialog DisplayName,
	// e.g. "Сцена 8. Двор общаги"). Emit it as a narration beat on entry so
	// AutoStage turns it into a background — the scene transitions the player sees.
	synth := uint32(0xF0000000)
	var dfs func(n uint32, depth int)
	dfs = func(n uint32, depth int) {
		if visited[n] || depth > 1<<16 {
			return
		}
		visited[n] = true
		if fl.contSet[n] {
			if name := strings.TrimSpace(fl.text[n]); name != "" {
				fl.text[synth] = name // a synthetic narration node (no speaker)
				order = append(order, synth)
				synth++
			}
			for _, c := range fl.childrenOf[n] {
				dfs(c, depth+1)
			}
			return
		}
		order = append(order, n) // a content / logic node
	}
	for _, t := range tops {
		dfs(t, 0)
	}
	return order
}

// linearizeByComponents reconstructs the flow with FAITHFUL reconvergence: within
// a scene the 0x02 pin graph (articy's own TraverseFlow, via pinGraph.nextStops)
// drives the successors, so a choice's branches rejoin at their shared next stop
// (the merge points the spine flattens away). Scenes are not 0x02-connected, so any
// node the entry's flow doesn't reach is chained — in authoring order — onto a
// reached dead-end leaf (a single goto, never a bogus choice). Self-validates 100%
// coverage; returns ok=false to fall back to linearizeByHierarchy if anything would
// be stranded.
// linearizeFaithful is the port of articy's TraverseFlow: it builds the flow graph
// over ONLY emittable nodes (DialogFragment / Condition / Outcome), connected by
// forward pin-flow (reachFromPin descends/surfaces containers, passes through
// hubs). A DF with ≥2 forward targets is a player choice whose branches reconverge
// forward at their shared next node — no backward "revisit the menu" loop unless
// the author genuinely wired one. Conditions stay as if/then/else (kept in
// fl.logic), Outcomes as set. Returns the root entries + ok; ok=false (stranded
// content) falls back to the older heuristics.
func linearizeFaithful(fl flow) ([]uint32, bool) {
	if fl.pg == nil {
		return nil, false
	}
	pg := fl.pg
	childOf := map[uint32]bool{}
	for _, ch := range fl.childrenOf {
		for _, c := range ch {
			childOf[c] = true
		}
	}
	entries := pg.rootEntry(childOf)
	if len(entries) == 0 {
		return nil, false
	}

	var emit []uint32
	for n, c := range pg.class {
		if !pg.isEmittable(n) {
			continue
		}
		emit = append(emit, n)
		fl.nodes[n] = true
		if c == cidCondition {
			// [in, out-true, out-false] → two branches, tagged by source pin so
			// emitModels' conditionPins can split them into the true/false outputs.
			ops := pg.outPins(n)
			var es []edge
			for _, op := range ops {
				for _, t := range pg.reachFromPin(op) {
					es = append(es, edge{src: n, dst: t, srcPin: op})
				}
			}
			fl.succ[n] = es
			continue
		}
		// DialogFragment (say / choice) or Outcome (set): single output pin, its
		// forward targets. ≥2 targets ⇒ a choice.
		var targets []uint32
		seen := map[uint32]bool{}
		for _, p := range pg.outPins(n) {
			for _, t := range pg.reachFromPin(p) {
				if !seen[t] {
					seen[t] = true
					targets = append(targets, t)
				}
			}
		}
		var es []edge
		for _, t := range targets {
			es = append(es, edge{src: n, dst: t})
		}
		fl.succ[n] = es
	}

	// Coverage: reach from the root entries; chain any stranded emittable island
	// (pockets entered only by Jump, etc.) onto a reached dead-end leaf so nothing
	// is lost — a single forward goto, never a bogus choice.
	_, R := bfs(fl, entries, 1<<30)
	var leaves []uint32
	for _, n := range emit {
		if R[n] && len(fl.succ[n]) == 0 {
			leaves = append(leaves, n)
		}
	}
	sort.Slice(emit, func(i, j int) bool { return emit[i] < emit[j] })
	for _, x := range emit {
		if R[x] {
			continue
		}
		if len(leaves) == 0 {
			return nil, false
		}
		l := leaves[len(leaves)-1]
		leaves = leaves[:len(leaves)-1]
		fl.succ[l] = []edge{{src: l, dst: x}}
		_, sub := bfs(fl, []uint32{x}, 1<<30)
		for k := range sub {
			if !R[k] {
				R[k] = true
				if pg.isEmittable(k) && len(fl.succ[k]) == 0 {
					leaves = append(leaves, k)
				}
			}
		}
	}
	for _, n := range emit {
		if !R[n] {
			return nil, false
		}
	}
	return entries, true
}

func linearizeByComponents(fl flow) (uint32, bool) {
	if fl.pg == nil {
		return 0, false
	}
	order := fl.hierarchyOrder()
	if len(order) == 0 {
		return 0, false
	}
	pg := fl.pg
	isOption := func(t uint32) bool {
		txt := strings.TrimSpace(fl.text[t])
		return txt != "" && !sceneTextRe.MatchString(txt)
	}

	var stops []uint32
	for i, n := range order {
		if ln, ok := fl.logic[n]; ok && ln.cond {
			delete(fl.logic, n)
		}
		fl.nodes[n] = true
		hasNext := i+1 < len(order)
		if !pg.isStop(n) {
			// scene-name beats / logic glue: chain along the authoring spine so they
			// are emitted and lead into their scene's first stop.
			if hasNext {
				fl.succ[n] = []edge{{src: n, dst: order[i+1]}}
			} else {
				fl.succ[n] = nil
			}
			continue
		}
		stops = append(stops, n)
		raw := pg.nextStops(n)
		var opts []uint32
		seen := map[uint32]bool{}
		for _, t := range raw {
			if !seen[t] && isOption(t) {
				seen[t] = true
				opts = append(opts, t)
			}
		}
		switch {
		case len(opts) >= 2 && len(opts) <= maxChoiceOptions:
			var es []edge
			for _, t := range opts {
				es = append(es, edge{src: n, dst: t}) // branches reconverge via their own succ
			}
			fl.succ[n] = es
		case len(raw) >= 1:
			fl.succ[n] = []edge{{src: n, dst: raw[0]}} // in-scene linear flow
		default:
			fl.succ[n] = nil // leaf: a scene/branch end
		}
	}

	entry := order[0]
	_, R := bfs(fl, []uint32{entry}, 1<<30)
	var leaves []uint32
	for _, n := range stops {
		if R[n] && len(fl.succ[n]) == 0 {
			leaves = append(leaves, n)
		}
	}
	for _, x := range order {
		if R[x] {
			continue
		}
		if len(leaves) == 0 {
			return 0, false // nothing safe to chain from → fall back
		}
		l := leaves[len(leaves)-1]
		leaves = leaves[:len(leaves)-1]
		fl.succ[l] = []edge{{src: l, dst: x}}
		_, sub := bfs(fl, []uint32{x}, 1<<30)
		for k := range sub {
			if !R[k] {
				R[k] = true
				if pg.isStop(k) && len(fl.succ[k]) == 0 {
					leaves = append(leaves, k)
				}
			}
		}
	}
	for _, n := range stops {
		if !R[n] {
			return 0, false // would strand content → fall back
		}
	}
	return entry, true
}

// linearizeByHierarchy reconstructs the playable flow the way articy's own
// ArticyFlowPlayer traverses it (decompiled TraverseFlow): a scene's local 0x02
// connection graph carries the in-scene flow — including real player choices (a
// fragment whose output fans out to several dialogue lines) — and the container
// hierarchy chains scenes and chapters. Concretely:
//
//   - 0x02 connections drive flow WITHIN a Dialog; a ≥2-dialogue-line fan-out
//     stays a choice (linearizeStructuralFanouts already dropped scene/empty
//     pseudo-choices);
//   - entering a container routes to its first child (descent);
//   - a node with no outgoing connection (a scene/branch exit) continues to the
//     next sibling — and a container's last child climbs to the container's own
//     next sibling — so scenes and chapters reconverge into one connected novel.
//
// Returns the entry (the first top-level container) and ok=false when there is no
// decodable hierarchy (e.g. a synthetic test graph), so the caller falls back to
// the 0x02 component export.
func linearizeByHierarchy(fl flow) (uint32, bool) {
	order := fl.hierarchyOrder()
	if len(order) == 0 {
		return 0, false
	}

	// Real player choices come from articy's own pin traversal when available
	// (nextStops resolves the stops reachable from a node, descending containers),
	// else the direct 0x02 dialogue-line fan-out. Kept only when ≥2 options are
	// genuine dialogue lines (scene-marker / empty stops are transitions, not
	// options). nextStops is capped to direct successors as a safety net so a
	// mis-resolved deep traversal can't strand a node in convert.
	isOption := func(t uint32) bool {
		txt := strings.TrimSpace(fl.text[t])
		return txt != "" && !sceneTextRe.MatchString(txt)
	}
	choiceOpts := map[uint32][]uint32{}
	for _, n := range order {
		// Only a DialogFragment (a stop) presents a player choice — never an
		// instruction/condition (that would emit a node with multiple continuations
		// convert.go rejects). Options come from articy's pin traversal when
		// available (descends containers), else the direct 0x02 fan-out.
		if fl.pg != nil && !fl.pg.isStop(n) {
			continue
		}
		var dsts []uint32
		if fl.pg != nil {
			dsts = fl.pg.nextStops(n)
		} else {
			for _, e := range fl.succ[n] {
				dsts = append(dsts, e.dst)
			}
		}
		var opts []uint32
		seen := map[uint32]bool{}
		for _, t := range dsts {
			if !seen[t] && isOption(t) {
				seen[t] = true
				opts = append(opts, t)
			}
		}
		// A real player menu is a small local fan-out. A large one is structural —
		// a node whose pin traversal descended into many scenes/chapters — and must
		// not become a giant bogus choice; let it linearize through the spine.
		if len(opts) >= 2 && len(opts) <= maxChoiceOptions {
			choiceOpts[n] = opts
		}
	}

	// The authoring-order spine guarantees full coverage and reconvergence; a choice
	// node offers its pin-resolved options plus a fall-through to the next authored
	// node so the backbone (hence coverage) is never broken. The fall-through is the
	// honest cost of not having articy's runtime reconvergence: a choice's branches
	// live in disjoint 0x02 islands, so without it ~99% of content is stranded.
	for i, n := range order {
		if ln, ok := fl.logic[n]; ok && ln.cond {
			delete(fl.logic, n)
		}
		next := uint32(0)
		hasNext := i+1 < len(order)
		if hasNext {
			next = order[i+1]
		}
		if opts, ok := choiceOpts[n]; ok {
			seen := map[uint32]bool{}
			var es []edge
			for _, t := range opts {
				if !seen[t] {
					seen[t] = true
					es = append(es, edge{src: n, dst: t})
				}
			}
			if hasNext && !seen[next] {
				es = append(es, edge{src: n, dst: next})
			}
			fl.succ[n] = es
		} else if hasNext {
			fl.succ[n] = []edge{{src: n, dst: next}}
		} else {
			fl.succ[n] = nil
		}
		fl.nodes[n] = true
	}
	return order[0], true
}

// ── model emission (articy JSON-export shape) ────────────────────────────────

const dlgID = "dialogue-root-0000-0000-000000000000"

func nodeID(o uint32) string {
	return fmt.Sprintf("node-%08d-0000-0000-000000000000", o)
}

// bfs returns the nodes reachable from starts (capped at maxN), in visit order.
func bfs(fl flow, starts []uint32, maxN int) ([]uint32, map[uint32]bool) {
	seen := map[uint32]bool{}
	var reach []uint32
	queue := append([]uint32{}, starts...)
	for len(queue) > 0 && len(reach) < maxN {
		x := queue[0]
		queue = queue[1:]
		if seen[x] {
			continue
		}
		seen[x] = true
		reach = append(reach, x)
		for _, e := range fl.succ[x] {
			queue = append(queue, e.dst)
		}
	}
	return reach, seen
}

func buildExport(fl flow, start uint32, maxN int, gvars []nsVars) export {
	reach, seen := bfs(fl, []uint32{start}, maxN)
	return emitModels(fl, reach, seen, []uint32{start}, gvars)
}

// buildExportAll emits the WHOLE novel: every flow node, reachable through a
// synthetic chapter hub that fans out to all in-degree-0 roots plus one entry per
// otherwise-unreachable pocket (sub-flows entered by Jump). Nothing is dropped.
func buildExportAll(fl flow, gvars []nsVars) export {
	indeg := map[uint32]int{}
	for _, es := range fl.succ {
		for _, e := range es {
			indeg[e.dst]++
		}
	}
	var roots []uint32
	for n := range fl.nodes {
		if indeg[n] == 0 {
			roots = append(roots, n)
		}
	}
	sort.Slice(roots, func(i, j int) bool { return roots[i] < roots[j] })

	entries := append([]uint32{}, roots...)
	_, seen := bfs(fl, roots, math.MaxInt32)
	// surface every remaining pocket by adding its lowest node as an entry.
	for {
		min := uint32(math.MaxUint32)
		for n := range fl.nodes {
			if !seen[n] && n < min {
				min = n
			}
		}
		if min == math.MaxUint32 {
			break
		}
		entries = append(entries, min)
		_, more := bfs(fl, []uint32{min}, math.MaxInt32)
		for n := range more {
			seen[n] = true
		}
	}
	reach := make([]uint32, 0, len(seen))
	for n := range seen {
		reach = append(reach, n)
	}
	sort.Slice(reach, func(i, j int) bool { return reach[i] < reach[j] })
	return emitModels(fl, reach, seen, entries, gvars)
}

// emitModels builds the articy-export model. Every dialogue fragment carries a
// StableId (its articy GUID) so the back-end can stamp the say/option with a key
// that survives reimport — the importer's localization pass keys its catalog off
// it, and saves/analytics stay valid across content edits.
func emitModels(fl flow, reach []uint32, seen map[uint32]bool, entries []uint32, gvars []nsVars) export {
	outsOf := func(o uint32) []edge {
		var es []edge
		for _, e := range fl.succ[o] {
			if seen[e.dst] {
				es = append(es, e)
			}
		}
		return es
	}
	conns := func(es []edge) []any {
		var c []any
		for _, e := range es {
			c = append(c, map[string]any{"Target": nodeID(e.dst)})
		}
		if len(c) == 0 {
			c = []any{map[string]any{"Target": dlgID}}
		}
		return c
	}
	onePin := func(es []edge) []any {
		return []any{map[string]any{"Text": "", "Connections": conns(es)}}
	}
	emptyIn := []any{map[string]any{"Text": "", "Connections": []any{}}}

	speakers := map[string]bool{}
	var models []model
	for _, o := range reach {
		es := outsOf(o)
		switch {
		case fl.logic[o].expr != "" && fl.logic[o].cond:
			// Condition → two output pins (true/false), split by source pin.
			models = append(models, model{Type: "Condition", Properties: map[string]any{
				"Id": nodeID(o), "Expression": fl.logic[o].expr,
				"InputPins": emptyIn, "OutputPins": conditionPins(es),
			}})
		case fl.logic[o].expr != "":
			models = append(models, model{Type: "Instruction", Properties: map[string]any{
				"Id": nodeID(o), "Expression": fl.logic[o].expr,
				"InputPins": emptyIn, "OutputPins": onePin(es),
			}})
		case fl.text[o] != "":
			sp := fl.sp[o]
			if sp != "" {
				speakers[sp] = true
			}
			text, menu := fl.text[o], fl.text[o]
			if len(menu) > 80 {
				menu = menu[:80]
			}
			// StableId is the fragment's articy GUID — a key that survives reimport,
			// so saves, analytics and localization catalogs stay valid across content
			// edits. The back-end carries it onto the say/option for the importer's
			// localization pass (see importer.Localize).
			key := fl.guid[o]
			if key == "" {
				key = nodeID(o)
			}
			models = append(models, model{Type: "DialogueFragment", Properties: map[string]any{
				"Id": nodeID(o), "Text": text, "MenuText": menu, "Speaker": sp,
				"StableId": key, "InputPins": emptyIn, "OutputPins": onePin(es),
			}})
		default:
			models = append(models, model{Type: "Hub", Properties: map[string]any{
				"Id": nodeID(o), "DisplayName": "", "InputPins": emptyIn, "OutputPins": onePin(es),
			}})
		}
	}

	var spNames []string
	for s := range speakers {
		spNames = append(spNames, s)
	}
	sort.Strings(spNames)
	for _, s := range spNames {
		models = append(models, model{Type: "Entity", Properties: map[string]any{"Id": s, "DisplayName": s}})
	}
	// The dialogue's entry: a single start, or a synthetic "chapters" hub that
	// fans out to every chapter root / pocket entry so nothing is unreachable.
	const hubID = "chapter-hub-0000-0000-000000000000"
	entry := dlgID
	if len(entries) == 1 {
		entry = nodeID(entries[0])
	} else {
		var hubConns []any
		for i, e := range entries {
			hubConns = append(hubConns, map[string]any{"Target": nodeID(e)})
			_ = i
		}
		models = append(models, model{Type: "Hub", Properties: map[string]any{
			"Id": hubID, "DisplayName": "Главы",
			"InputPins":  []any{map[string]any{"Text": "", "Connections": []any{}}},
			"OutputPins": []any{map[string]any{"Text": "", "Connections": hubConns}},
		}})
		entry = hubID
	}
	models = append(models, model{Type: "Dialogue", Properties: map[string]any{
		"Id": dlgID, "TechnicalName": "chapter", "DisplayName": "chapter",
		"InputPins":  []any{map[string]any{"Text": "", "Connections": []any{map[string]any{"Target": entry}}}},
		"OutputPins": []any{map[string]any{"Text": "", "Connections": []any{}}},
	}})
	return export{GlobalVariables: gvars, Packages: []pkg{{Models: models}}}
}

// conditionPins splits a condition's outgoing edges into two pins (true/false)
// by source pin id, padding to the two pins convert.go expects.
func conditionPins(es []edge) []any {
	bySrc := map[uint32][]edge{}
	var order []uint32
	for _, e := range es {
		if _, ok := bySrc[e.srcPin]; !ok {
			order = append(order, e.srcPin)
		}
		bySrc[e.srcPin] = append(bySrc[e.srcPin], e)
	}
	sort.Slice(order, func(i, j int) bool { return order[i] < order[j] })
	var pins []any
	for _, sp := range order {
		grp := bySrc[sp]
		pins = append(pins, map[string]any{"Text": "", "Connections": []any{
			map[string]any{"Target": nodeID(grp[0].dst)},
		}})
	}
	for len(pins) < 2 {
		pins = append(pins, map[string]any{"Text": "", "Connections": []any{map[string]any{"Target": dlgID}}})
	}
	return pins[:2]
}

// ── export JSON shapes (mirror internal/articy's expected input) ─────────────

type export struct {
	GlobalVariables []nsVars `json:"GlobalVariables"`
	Packages        []pkg    `json:"Packages"`
}
type pkg struct {
	Models []model `json:"Models"`
}
type model struct {
	Type       string         `json:"Type"`
	Properties map[string]any `json:"Properties"`
}
type nsVars struct {
	Namespace string    `json:"Namespace"`
	Variables []varDecl `json:"Variables"`
}
type varDecl struct {
	Variable string `json:"Variable"`
	Type     string `json:"Type"`
	Value    string `json:"Value"`
}

// global-variable object kinds in the Global_Variables partition
var (
	kNamespace = kind{2, 7} // a namespace (name 0x03, self 0x39)
	kVariable  = kind{1, 7} // a variable (name 0x03, parent-ns 0x0c, string default 0x74)
)

const pStrDefault = 0x74 // a string variable's default value

func (o object) has(pid uint16) bool {
	for _, e := range o.es {
		if e.propid == pid {
			return true
		}
	}
	return false
}

// intVars marks variables that the flow uses arithmetically (X += N, X = <number>,
// X </>/comparison) — those are Integers; the rest default to Boolean.
var (
	reArith  = regexp.MustCompile(`([A-Za-z_][\w.]*)\s*[+-]=`)
	reNumSet = regexp.MustCompile(`([A-Za-z_][\w.]*)\s*=\s*-?\d+\b`)
	reNumCmp = regexp.MustCompile(`([A-Za-z_][\w.]*)\s*(?:<|>|<=|>=)`)
	reNumRHS = regexp.MustCompile(`=\s*([A-Za-z_][\w.]*)\s*[+\-]`)
)

func intVars(exprs []string) map[string]bool {
	m := map[string]bool{}
	add := func(re *regexp.Regexp, s string) {
		for _, mm := range re.FindAllStringSubmatch(s, -1) {
			m[mm[1]] = true
		}
	}
	for _, e := range exprs {
		add(reArith, e)
		add(reNumSet, e)
		add(reNumCmp, e)
		add(reNumRHS, e)
	}
	return m
}

// globalVars reconstructs the project's global variables with their real
// namespaces, types and defaults from the Global_Variables partition (object
// framing): String vars keep their default (0x74), arithmetic vars are Integer
// (default 0), the rest Boolean (default false). exprs are the flow's
// instruction/condition strings, used to tell Integer from Boolean.
func globalVars(projectDir string, exprs []string) []nsVars {
	p := findPartition(projectDir, "Global_Variables")
	if p == "" {
		return nil
	}
	d, err := os.ReadFile(p)
	if err != nil || len(d) < 24 {
		return nil
	}
	idx := int(binary.LittleEndian.Uint64(d[8:]))
	if idx <= 0 || idx > len(d) {
		idx = len(d)
	}
	objs := walkObjects(d, idx)

	nsName := map[uint32]string{}
	for _, o := range objs {
		if o.c == kNamespace.c && o.t == kNamespace.t {
			if self, ok := o.u32(pSelf); ok {
				nsName[self] = o.str(0x03)
			}
		}
	}
	isInt := intVars(exprs)

	byNs := map[string][]varDecl{}
	var order []string
	for _, o := range objs {
		if o.c != kVariable.c || o.t != kVariable.t {
			continue
		}
		name := o.str(0x03)
		par, _ := o.u32(pParent)
		ns := nsName[par]
		if name == "" || ns == "" {
			continue
		}
		vd := varDecl{Variable: name}
		switch {
		case o.has(pStrDefault):
			vd.Type, vd.Value = "String", o.str(pStrDefault)
		case isInt[ns+"."+name]:
			vd.Type, vd.Value = "Integer", "0"
		default:
			vd.Type, vd.Value = "Boolean", "false"
		}
		if _, seen := byNs[ns]; !seen {
			order = append(order, ns)
		}
		byNs[ns] = append(byNs[ns], vd)
	}
	var out []nsVars
	for _, ns := range order {
		out = append(out, nsVars{Namespace: ns, Variables: byNs[ns]})
	}
	return out
}

// ── public entry ─────────────────────────────────────────────────────────────

func findPartition(dir, kind string) string {
	var found string
	filepath.Walk(dir, func(p string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() || found != "" {
			return nil
		}
		name := filepath.Base(p)
		if strings.Contains(name, kind) && strings.HasSuffix(name, ".adpd") {
			found = p
		}
		return nil
	})
	return found
}

func flowPath(path string) (string, string, error) {
	info, err := os.Stat(path)
	if err != nil {
		return "", "", err
	}
	if info.IsDir() {
		fp := findPartition(path, "Flow")
		if fp == "" {
			return "", "", fmt.Errorf("no Flow partition found under %s", path)
		}
		return fp, path, nil
	}
	return path, filepath.Dir(filepath.Dir(path)), nil
}

func loadFlow(path string) (flow, string, error) {
	fp, proj, err := flowPath(path)
	if err != nil {
		return flow{}, "", err
	}
	d, err := os.ReadFile(fp)
	if err != nil {
		return flow{}, "", err
	}
	if len(d) < 24 || string(d[:5]) != "ADPD8" {
		return flow{}, "", fmt.Errorf("%s: not an ADPD8 partition", fp)
	}
	fl := decodeFlow(d)
	if len(fl.nodes) == 0 {
		return flow{}, "", fmt.Errorf("%s: no flow connections decoded", fp)
	}
	return fl, proj, nil
}

func buildModel(fl flow, proj string, start, maxN int) export {
	// With an explicit -start, emit a single chapter from that node over the raw
	// 0x02 flow (after collapsing structural fan-outs). Without it, emit the WHOLE
	// novel as one connected, ordered chapter built from the container hierarchy —
	// the only structure that links the otherwise-shattered 0x02 islands.
	if start >= 0 {
		linearizeStructuralFanouts(fl)
		gvars := globalVars(proj, flowExprs(fl))
		if maxN <= 0 {
			maxN = math.MaxInt32
		}
		return buildExport(fl, uint32(start), maxN, gvars)
	}

	// Faithful port of articy's TraverseFlow (forward pin-flow over emittable nodes)
	// — no spurious backward menu loops. Tried first; falls back only if it would
	// strand content.
	if entries, ok := linearizeFaithful(fl); ok {
		gvars := globalVars(proj, flowExprs(fl))
		reach, seen := bfs(fl, entries, math.MaxInt32)
		return emitModels(fl, reach, seen, entries, gvars)
	}
	for k := range fl.succ { // faithful mutated succ — reset before the heuristics
		delete(fl.succ, k)
	}

	if entry, ok := linearizeByComponents(fl); ok {
		gvars := globalVars(proj, flowExprs(fl))
		return buildExport(fl, entry, math.MaxInt32, gvars)
	}
	// Faithful flow would strand content — clear its partial edges and fall back to
	// the authoring-order spine (full coverage, no reconvergence).
	for k := range fl.succ {
		delete(fl.succ, k)
	}
	if entry, ok := linearizeByHierarchy(fl); ok {
		gvars := globalVars(proj, flowExprs(fl))
		return buildExport(fl, entry, math.MaxInt32, gvars)
	}
	// No container hierarchy (e.g. a synthetic test graph) — fall back to surfacing
	// every 0x02 component through a chapter hub.
	linearizeStructuralFanouts(fl)
	return buildExportAll(fl, globalVars(proj, flowExprs(fl)))
}

func flowExprs(fl flow) []string {
	var exprs []string
	for _, ln := range fl.logic {
		exprs = append(exprs, ln.expr)
	}
	return exprs
}

// BuildExportJSON reads the .adpd project at path (a project directory or a Flow
// partition file), reconstructs the articy model (text, speakers, choices,
// instructions and conditions), and returns it as JSON in the articy-export
// shape. start < 0 picks the story opening; maxN caps the chapter (0 = no cap).
func BuildExportJSON(path string, start, maxN int) ([]byte, error) {
	fl, proj, err := loadFlow(path)
	if err != nil {
		return nil, err
	}
	return json.Marshal(buildModel(fl, proj, start, maxN))
}

// Lang returns the project's primary language code (from the Settings partition,
// e.g. "ru"), or "und" when it can't be determined. Localization is done by the
// importer (importer.Localize) keyed off each fragment's StableId; this names the
// catalog sidecar (<script>.<lang>.json) the runtime loads per locale.
func Lang(path string) (string, error) {
	_, proj, err := flowPath(path)
	if err != nil {
		return "und", err
	}
	return langOf(proj), nil
}

var langCodeRe = regexp.MustCompile(`\b([a-z]{2})-[A-Z]{2}\b`)

// langOf reads the project's primary language code from the Settings partition
// ("ru-RU" → "ru"); defaults to "und" (undetermined) when absent.
func langOf(projectDir string) string {
	p := findPartition(projectDir, "Settings")
	if p == "" {
		return "und"
	}
	d, err := os.ReadFile(p)
	if err != nil {
		return "und"
	}
	if m := langCodeRe.FindSubmatch(d); m != nil {
		return string(m[1])
	}
	return "und"
}

func marshalExport(ex export) ([]byte, error) { return json.Marshal(ex) }

// ── cast catalog (for auto-staging) ──────────────────────────────────────────

var (
	spritePathRe = regexp.MustCompile(`(?i)([^/\\]+\.(?:png|jpg|jpeg))$`)
	castSkip     = map[string]bool{
		"PreviewImageAsset": true, "OriginalSource": true, "Entity": true,
		"DefaultMainCharacterTemplate": true, "DisplayName": true,
		"BackgroundColor": true, "DisplayNameMultiLanguageText": true, "Text": true,
		"Attachments": true, "Articy": true,
	}
	castNameRe = regexp.MustCompile(`^(?:[A-Za-z_][\w]*|.*[А-Яа-я].*)$`)
	guidRe2    = regexp.MustCompile(`^[0-9a-f-]{36}$`)
)

// translitMap is a common Cyrillic→Latin romanization (handles digraphs like
// ю→yu, я→ya, ж→zh) so a Russian character name also matches a Latin speaker
// caption: "Тимур" → "Timur", "Люба" → "Lyuba", "Андрей" → "Andrey".
var translitMap = map[rune]string{
	'а': "a", 'б': "b", 'в': "v", 'г': "g", 'д': "d", 'е': "e", 'ё': "e",
	'ж': "zh", 'з': "z", 'и': "i", 'й': "y", 'к': "k", 'л': "l", 'м': "m",
	'н': "n", 'о': "o", 'п': "p", 'р': "r", 'с': "s", 'т': "t", 'у': "u",
	'ф': "f", 'х': "kh", 'ц': "ts", 'ч': "ch", 'ш': "sh", 'щ': "sch",
	'ъ': "", 'ы': "y", 'ь': "", 'э': "e", 'ю': "yu", 'я': "ya",
}

func transliterate(s string) string {
	hasCyr := false
	var b strings.Builder
	for _, r := range s {
		lo := unicode.ToLower(r)
		if t, ok := translitMap[lo]; ok {
			hasCyr = true
			if r != lo && t != "" { // preserve leading capital
				t = strings.ToUpper(t[:1]) + t[1:]
			}
			b.WriteString(t)
		} else {
			b.WriteRune(r)
		}
	}
	if !hasCyr {
		return ""
	}
	return b.String()
}

// Cast reads the project's Entities partition and returns character name → sprite
// filename. Each entity is keyed by every plausible name it carries (its Russian
// display name AND its Latin technical name) plus a transliteration of the Russian
// name, so a speaker caption resolves to its art regardless of language.
func Cast(path string) (map[string]string, error) {
	_, proj, err := flowPath(path)
	if err != nil {
		return nil, err
	}
	p := findPartition(proj, "Entities")
	if p == "" {
		return nil, fmt.Errorf("no Entities partition under %s", proj)
	}
	d, err := os.ReadFile(p)
	if err != nil {
		return nil, err
	}
	idx := int(binary.LittleEndian.Uint64(d[8:]))
	if idx <= 0 || idx > len(d) {
		idx = len(d)
	}
	cast := map[string]string{}
	for _, o := range walkObjects(d, idx) {
		var sprite string
		var names []string
		for _, e := range o.es {
			if e.tag != 0x12 || e.s == "" {
				continue
			}
			if strings.HasPrefix(e.s, "file:///") {
				if m := spritePathRe.FindStringSubmatch(e.s); m != nil {
					sprite = m[1]
				}
			} else if len(e.s) < 28 && !castSkip[e.s] && !guidRe2.MatchString(e.s) && castNameRe.MatchString(e.s) {
				names = append(names, e.s)
			}
		}
		if sprite != "" {
			for _, n := range names {
				if _, ok := cast[n]; !ok {
					cast[n] = sprite
				}
				if tr := transliterate(n); tr != "" && tr != n {
					if _, ok := cast[tr]; !ok {
						cast[tr] = sprite
					}
				}
			}
		}
	}
	return cast, nil
}
