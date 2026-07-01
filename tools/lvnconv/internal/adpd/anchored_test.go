package adpd

import (
	"fmt"
	"os"
	"testing"
)

// TestAnchored measures the graph AFTER linearizeAnchored on a fresh flow.
//
//	ADPD_DIAG=<dir> go test ./internal/adpd -run TestAnchored -v
func TestAnchored(t *testing.T) {
	path := os.Getenv("ADPD_DIAG")
	if path == "" {
		t.Skip("set ADPD_DIAG")
	}
	fl, _, err := loadFlow(path)
	if err != nil {
		t.Fatal(err)
	}
	kids := completeChildren(fl)
	root, ok := hierarchyRoot(fl, kids)
	if !ok {
		t.Fatal("no root")
	}
	entries, ok := linearizeAnchored(fl, root, nil)
	if !ok {
		t.Fatal("anchored failed")
	}
	pg := fl.pg
	var emit []uint32
	for n := range pg.class {
		if pg.isEmittable(n) {
			emit = append(emit, n)
		}
	}
	// forward reach from entry (over fl.succ)
	_, rr := bfs(fl, entries, 1<<30)
	// can-END over fl.succ
	preds := map[uint32][]uint32{}
	var terms []uint32
	for _, n := range emit {
		if len(fl.succ[n]) == 0 {
			terms = append(terms, n)
		}
		for _, e := range fl.succ[n] {
			preds[e.dst] = append(preds[e.dst], n)
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
	rc, ce := 0, 0
	for _, n := range emit {
		if rr[n] {
			rc++
		}
		if canEnd[n] {
			ce++
		}
	}
	fmt.Printf("ANCHORED: entry=%v emittable=%d terminals=%d fwd-reach=%.1f%% can-END=%.1f%%\n",
		entries, len(emit), len(terms), 100*float64(rc)/float64(len(emit)), 100*float64(ce)/float64(len(emit)))
}
