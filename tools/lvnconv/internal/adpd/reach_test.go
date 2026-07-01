package adpd

import (
	"fmt"
	"os"
	"testing"
)

// TestReach measures the pure forward pin-flow graph (no stitching): forward reach
// from the biggest region head + can-reach-a-leaf (END) — to separate pin-flow
// cyclicity from stitching artifacts.
//
//	ADPD_DIAG=<dir> go test ./internal/adpd -run TestReach -v
func TestReach(t *testing.T) {
	path := os.Getenv("ADPD_DIAG")
	if path == "" {
		t.Skip("set ADPD_DIAG")
	}
	fl, _, err := loadFlow(path)
	if err != nil {
		t.Fatal(err)
	}
	pg := fl.pg
	succ := map[uint32][]uint32{}
	var emit []uint32
	for n := range pg.class {
		if !pg.isEmittable(n) {
			continue
		}
		emit = append(emit, n)
		var outs []uint32
		seen := map[uint32]bool{}
		for _, p := range pg.outPins(n) {
			for _, tt := range pg.reachFromPin(p, nil) {
				if !seen[tt] {
					seen[tt] = true
					outs = append(outs, tt)
				}
			}
		}
		succ[n] = outs
	}
	// terminals = no succ; can-END via reverse BFS
	preds := map[uint32][]uint32{}
	var terms []uint32
	for _, n := range emit {
		if len(succ[n]) == 0 {
			terms = append(terms, n)
		}
		for _, d := range succ[n] {
			preds[d] = append(preds[d], n)
		}
	}
	canEnd := map[uint32]bool{}
	q := append([]uint32{}, terms...)
	for _, t := range terms {
		canEnd[t] = true
	}
	for len(q) > 0 {
		x := q[0]
		q = q[1:]
		for _, p := range preds[x] {
			if !canEnd[p] {
				canEnd[p] = true
				q = append(q, p)
			}
		}
	}
	ce := 0
	for _, n := range emit {
		if canEnd[n] {
			ce++
		}
	}
	fmt.Printf("PIN-FLOW (no stitch): emittable=%d terminals(leaf)=%d can-END=%.1f%%\n",
		len(emit), len(terms), 100*float64(ce)/float64(len(emit)))

	// how many emittable nodes are in the giant SCC? approximate: nodes NOT able to
	// reach a leaf AND reachable-from many = trapped in cycles.
	trapped := len(emit) - ce
	fmt.Printf("  trapped (cannot reach any leaf): %d\n", trapped)
	// sample a few trapped nodes with text
	shown := 0
	for _, n := range emit {
		if !canEnd[n] && fl.text[n] != "" {
			txt := fl.text[n]
			if len(txt) > 50 {
				txt = txt[:50]
			}
			fmt.Printf("    trapped [%d c=%d] %s: %s\n", n, pg.class[n], fl.sp[n], txt)
			shown++
			if shown >= 6 {
				break
			}
		}
	}
}
