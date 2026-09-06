package main

import (
	"fmt"
	"os"
	"os/exec"
	"strings"

	"github.com/fomeanator/elvin/tools/lvnconv/internal/optimize"
)

// cmdOptimize is the built-in image compressor: shrinks oversized
// backgrounds/sprites to a cap and recompresses them, without ever resizing a
// Spine atlas page (see internal/optimize for why that specific op is unsafe).
//
//	lvnconv optimize -i server/content                      # dry run, just report
//	lvnconv optimize -i server/content -apply                # write the smaller files
//	lvnconv optimize -i server/content -apply -rewrite-refs   # + fix png→jpg references
//	lvnconv optimize -i server/content -apply -lossless       # recompress only: same pixels, size, format
func cmdOptimize(args []string) {
	fs := newFlagSet("optimize")
	in := fs.String("i", "server/content", "content root to scan (recursively)")
	lossless := fs.Bool("lossless", false, "recompress only — same pixels, same size, same format (no resize, no png→jpg); for artist sources saved without compression")
	maxSize := fs.Int("max", 2560, "longest-side cap in px for non-atlas images")
	quality := fs.Int("quality", 85, "JPEG quality for alpha-free images converted from PNG")
	apply := fs.Bool("apply", false, "write the results (default: dry run, report only)")
	rewriteRefs := fs.Bool("rewrite-refs", false, "patch manifest.json/.lvns for any png→jpg renames, then recompile touched scripts (requires -apply)")
	_ = fs.Parse(args)

	if *rewriteRefs && !*apply {
		die("optimize: -rewrite-refs requires -apply")
	}

	results, err := optimize.Run(*in, optimize.Options{
		MaxSize: *maxSize, JPEGQuality: *quality, Apply: *apply, Lossless: *lossless,
	})
	if err != nil {
		die("optimize: " + err.Error())
	}

	var oldTotal, newTotal int64
	var changed int
	renames := map[string]string{}
	for _, r := range results {
		if r.Err != nil {
			fmt.Fprintf(os.Stderr, "  ERROR %s: %v\n", r.Path, r.Err)
			continue
		}
		if r.Action == optimize.ActionSkip {
			continue
		}
		changed++
		oldTotal += r.OldBytes
		newTotal += r.NewBytes
		pct := 100 * (1 - float64(r.NewBytes)/float64(r.OldBytes))
		dims := ""
		if r.NewW != r.OldW || r.NewH != r.OldH {
			dims = fmt.Sprintf(" %dx%d→%dx%d", r.OldW, r.OldH, r.NewW, r.NewH)
		}
		fmt.Printf("  [%s] %-7s %s%s  %s→%s (%.0f%% smaller)\n",
			r.Kind, r.Action, r.Path, dims, humanBytes(r.OldBytes), humanBytes(r.NewBytes), pct)
		if oldURL, newURL, ok := r.Rename(); ok {
			if u1, ok1 := optimize.URLFor(*in, oldURL); ok1 {
				if u2, ok2 := optimize.URLFor(*in, newURL); ok2 {
					renames[u1] = u2
				}
			}
		}
	}

	mode := "dry run — nothing written (pass -apply to write)"
	if *apply {
		mode = "applied"
	}
	fmt.Fprintf(os.Stderr, "\noptimize: %d/%d file(s) changed, %s → %s (%.0f%% smaller) — %s\n",
		changed, len(results), humanBytes(oldTotal), humanBytes(newTotal),
		100*(1-safeDiv(float64(newTotal), float64(oldTotal))), mode)

	if !*rewriteRefs || len(renames) == 0 {
		if len(renames) > 0 {
			fmt.Fprintf(os.Stderr, "%d file(s) changed extension (png→jpg) — re-run with -rewrite-refs to fix manifest.json/.lvns references\n", len(renames))
		}
		return
	}

	touched, err := optimize.RewriteRefs(*in, renames)
	if err != nil {
		die("optimize: rewriting references: " + err.Error())
	}
	fmt.Fprintf(os.Stderr, "rewrite-refs: patched %d reference(s) in %d file(s)\n", len(renames), len(touched))
	var failed []string
	for _, path := range touched {
		if !isLvns(path) {
			continue // manifest.json needs no recompile step
		}
		out := lvnPathFor(path)
		cmd := exec.Command(os.Args[0], "convert", "-i", path, "-o", out)
		cmd.Stdout, cmd.Stderr = os.Stdout, os.Stderr
		if err := cmd.Run(); err != nil {
			fmt.Fprintf(os.Stderr, "  recompile FAILED %s: %v\n", path, err)
			failed = append(failed, path)
			continue
		}
		fmt.Fprintf(os.Stderr, "  recompiled %s → %s\n", path, out)
	}
	if len(failed) > 0 {
		// The images are renamed and the .lvns already reference the new names,
		// but these compiled .lvn still point at files that no longer exist —
		// shipping now means broken art at runtime. Refuse to exit clean.
		die(fmt.Sprintf("optimize: %d script(s) failed to recompile after rewrite-refs (their .lvn still reference the OLD image names): %s — fix the syntax errors above and run `lvnconv convert` on each",
			len(failed), strings.Join(failed, ", ")))
	}
}

func isLvns(path string) bool {
	return len(path) > 5 && path[len(path)-5:] == ".lvns"
}

func lvnPathFor(lvnsPath string) string {
	return lvnsPath[:len(lvnsPath)-len(".lvns")] + ".lvn"
}

func safeDiv(a, b float64) float64 {
	if b == 0 {
		return 0
	}
	return a / b
}

func humanBytes(n int64) string {
	const unit = 1024
	if n < unit {
		return fmt.Sprintf("%dB", n)
	}
	div, exp := int64(unit), 0
	for n1 := n / unit; n1 >= unit; n1 /= unit {
		div *= unit
		exp++
	}
	return fmt.Sprintf("%.1f%ciB", float64(n)/float64(div), "KMGTPE"[exp])
}
