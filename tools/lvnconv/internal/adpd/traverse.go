package adpd

import "sort"

// This is articy's own flow model, reconstructed from the .adpd object graph and
// traversed the way ArticyFlowPlayer.TraverseFlow does (decompiled): flow runs
// pin→connection→pin; a DialogFragment is a "stop" (a beat the player sees);
// entering a container (Dialog/FlowFragment) descends through the pin the
// connection landed on; the set of stops reachable from a node = its branches (a
// player choice when ≥2). Connections carry [src, dst, srcPin, dstPin], and Pin
// objects own a node — together they are the real graph, no hierarchy guesswork.

type pinEdge struct{ dst, dstPin uint32 }

type pinGraph struct {
	class  map[uint32]uint16    // node ordinal → classId
	pinOf  map[uint32]uint32    // pin id → owner node
	pins   map[uint32][]uint32  // node → its pin ids (in file order)
	outPin map[uint32][]pinEdge // pin id → connections leaving it
	hasOut map[uint32]bool      // pin has any outgoing connection
}

func buildPinGraph(objs []object) *pinGraph {
	g := &pinGraph{
		class: map[uint32]uint16{}, pinOf: map[uint32]uint32{},
		pins: map[uint32][]uint32{}, outPin: map[uint32][]pinEdge{}, hasOut: map[uint32]bool{},
	}
	for _, o := range objs {
		self, hasSelf := o.u32(pSelf)
		switch o.classId {
		case cidDialogFrag, cidDialog, cidFlowFrag, cidStoryFolder, cidCondition, cidOutcome, cidHub, cidJump:
			if hasSelf {
				g.class[self] = o.classId
			}
		case cidPin:
			if par, ok := o.u32(pParent); ok && hasSelf {
				g.pinOf[self] = par
				g.pins[par] = append(g.pins[par], self)
			}
		case cidConnection:
			r := o.refs(pConn)
			if len(r) >= 4 {
				g.outPin[r[2]] = append(g.outPin[r[2]], pinEdge{dst: r[1], dstPin: r[3]})
				g.hasOut[r[2]] = true
			}
		}
	}
	return g
}

func (g *pinGraph) isStop(n uint32) bool { return g.class[n] == cidDialogFrag }
func (g *pinGraph) isContainer(n uint32) bool {
	c := g.class[n]
	return c == cidDialog || c == cidFlowFrag || c == cidStoryFolder
}

// isEmittable marks nodes that become commands in the .lvn — a DialogFragment
// (say/choice), a Condition (if), or an Outcome/Instruction (set). The faithful
// flow graph is built over ONLY these; containers and hubs are transparent
// routing. Articy's TraverseFlow "pause" set (DialogueFragments) plus the flow
// logic it evaluates in passing.
func (g *pinGraph) isEmittable(n uint32) bool {
	switch g.class[n] {
	case cidDialogFrag, cidCondition, cidOutcome:
		return true
	}
	return false
}

// outPins returns a node's output pins. Articy file order is [input, output…];
// verified across DF (2), Condition (3: in,true,false), Outcome (2), container (2+).
func (g *pinGraph) outPins(n uint32) []uint32 {
	ps := g.pins[n]
	if len(ps) <= 1 {
		return nil
	}
	return ps[1:]
}

// reachFromPin follows one output pin's connections to the next EMITTABLE nodes,
// passing transparently through containers (descend via an input pin / surface via
// an output pin — whichever the connection entered) and hubs. This is TraverseFlow
// reduced to "where does flow leaving this pin next pause / branch" — forward, by
// construction, so a choice's options and a scene's linear next both fall out.
func (g *pinGraph) reachFromPin(pin uint32) []uint32 {
	var out []uint32
	seenStep := map[uint64]bool{}
	seenOut := map[uint32]bool{}
	var visit func(node, via uint32, depth int)
	visit = func(node, via uint32, depth int) {
		if depth > 100000 {
			return
		}
		key := uint64(node)<<32 | uint64(via)
		if seenStep[key] {
			return
		}
		seenStep[key] = true
		if g.isEmittable(node) {
			if !seenOut[node] {
				seenOut[node] = true
				out = append(out, node)
			}
			return // an emittable node is where this branch pauses — don't cross it
		}
		if g.isContainer(node) {
			for _, e := range g.outPin[via] {
				visit(e.dst, e.dstPin, depth+1)
			}
			return
		}
		for _, p := range g.outPins(node) { // hub / other: transparent
			for _, e := range g.outPin[p] {
				visit(e.dst, e.dstPin, depth+1)
			}
		}
	}
	for _, e := range g.outPin[pin] {
		visit(e.dst, e.dstPin, 0)
	}
	return out
}

// rootEntry returns the first emittable node of the novel: descend from the
// top-level container(s) (containers that own children but are never a child).
func (g *pinGraph) rootEntry(childOf map[uint32]bool) []uint32 {
	var tops []uint32
	for n, c := range g.class {
		if (c == cidStoryFolder || c == cidFlowFrag || c == cidDialog) && !childOf[n] {
			tops = append(tops, n)
		}
	}
	sort.Slice(tops, func(i, j int) bool { return tops[i] < tops[j] })
	var out []uint32
	seen := map[uint32]bool{}
	for _, t := range tops {
		if ps := g.pins[t]; len(ps) > 0 { // descend from the container's input pin
			for _, r := range g.reachFromPin(ps[0]) {
				if !seen[r] {
					seen[r] = true
					out = append(out, r)
				}
			}
		}
	}
	return out
}

// nextStops returns the stop nodes reachable when leaving node n — exactly the
// set ArticyFlowPlayer would pause on next (so ≥2 ⇒ a player choice). It follows
// n's output pins' connections, descending through containers (via the pin the
// connection entered) and passing through conditions/instructions, until it lands
// on DialogFragments.
func (g *pinGraph) nextStops(n uint32) []uint32 {
	var out []uint32
	seenStop := map[uint32]bool{}
	seenStep := map[uint64]bool{}
	var visit func(node, viaPin uint32, depth int)
	resolveEdges := func(edges []pinEdge, depth int) {
		for _, e := range edges {
			visit(e.dst, e.dstPin, depth)
		}
	}
	visit = func(node, viaPin uint32, depth int) {
		if depth > 100000 {
			return
		}
		key := uint64(node)<<32 | uint64(viaPin)
		if seenStep[key] {
			return
		}
		seenStep[key] = true
		switch {
		case g.isStop(node):
			if !seenStop[node] {
				seenStop[node] = true
				out = append(out, node)
			}
		case g.isContainer(node):
			resolveEdges(g.outPin[viaPin], depth+1) // descend through the entered pin
		default: // condition / instruction / hub / jump → pass through every output pin
			for _, p := range g.pins[node] {
				resolveEdges(g.outPin[p], depth+1)
			}
		}
	}
	for _, p := range g.pins[n] {
		resolveEdges(g.outPin[p], 0)
	}
	sort.Slice(out, func(i, j int) bool { return out[i] < out[j] })
	return out
}

// entryStops returns the first stops of the whole novel: descend from the root
// container(s) (those owning children but never owned).
func (g *pinGraph) entryStops() []uint32 {
	owned := map[uint32]bool{}
	for _, p := range g.pinOf { // a node owning pins isn't a "child pin"
		_ = p
	}
	// roots = containers that are not the dst of any connection
	isDst := map[uint32]bool{}
	for _, edges := range g.outPin {
		for _, e := range edges {
			isDst[e.dst] = true
		}
	}
	var roots []uint32
	for n, c := range g.class {
		if (c == cidStoryFolder || c == cidFlowFrag || c == cidDialog) && !isDst[n] {
			roots = append(roots, n)
		}
	}
	sort.Slice(roots, func(i, j int) bool { return roots[i] < roots[j] })
	var out []uint32
	seen := map[uint32]bool{}
	for _, r := range roots {
		for _, s := range g.nextStops(r) {
			if !seen[s] {
				seen[s] = true
				out = append(out, s)
			}
		}
	}
	_ = owned
	return out
}
