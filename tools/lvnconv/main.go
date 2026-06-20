// lvnconv is the narrative transcoder — "ffmpeg for visual novels".
//
// It takes a script in any supported authoring format and compiles it down to
// .lvn, the universal container the runtime plays. New source formats plug in
// as front-ends; the runtime never changes.
//
//	lvnconv convert -i chapter.ink   -o chapter.lvn
//	lvnconv convert -i export.json   -o chapter.lvn   -dialogue Ch1
//	lvnconv validate chapter.lvn
//	lvnconv probe   chapter.lvn
//
// Format is inferred from the input extension (.ink → ink, .json → articy,
// .lvn → already a container) and can be forced with -f.
package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/fomeanator/unity-lvn-vn-engine/tools/lvnconv/internal/articy"
	"github.com/fomeanator/unity-lvn-vn-engine/tools/lvnconv/internal/ink"
	"github.com/fomeanator/unity-lvn-vn-engine/tools/lvnconv/internal/lvns"
	"github.com/fomeanator/unity-lvn-vn-engine/tools/lvnconv/lvn"
)

// newFlagSet builds a subcommand flag set that prints usage to stderr.
func newFlagSet(name string) *flag.FlagSet {
	fs := flag.NewFlagSet(name, flag.ExitOnError)
	fs.SetOutput(os.Stderr)
	return fs
}

func main() {
	if len(os.Args) < 2 {
		usage()
		os.Exit(2)
	}
	switch os.Args[1] {
	case "convert":
		cmdConvert(os.Args[2:])
	case "validate":
		cmdValidate(os.Args[2:])
	case "probe":
		cmdProbe(os.Args[2:])
	case "-h", "--help", "help":
		usage()
	default:
		fmt.Fprintf(os.Stderr, "unknown command %q\n\n", os.Args[1])
		usage()
		os.Exit(2)
	}
}

func usage() {
	fmt.Fprint(os.Stderr, `lvnconv — narrative transcoder (ffmpeg for visual novels)

usage:
  lvnconv convert  -i <in> [-o <out.lvn>] [-f ink|articy] [-dialogue <name>]
  lvnconv validate <in.lvn> [-strict]
  lvnconv probe    <in.lvn>

convert  compile a source script to a .lvn container (stdout if -o omitted)
validate run structural checks on a .lvn (unknown op, dangling jumps, dup labels)
         -strict treats lint warnings (unused labels) as failures
probe    print a one-line summary of a .lvn (counts of ops, labels, choices)
`)
}

// detectFormat infers the front-end from the file extension.
func detectFormat(path string) string {
	switch strings.ToLower(filepath.Ext(path)) {
	case ".ink":
		return "ink"
	case ".lvns":
		return "lvns"
	case ".json", ".articy":
		return "articy"
	case ".lvn":
		return "lvn"
	}
	return ""
}

func cmdConvert(args []string) {
	fs := newFlagSet("convert")
	in := fs.String("i", "", "input file")
	out := fs.String("o", "", "output .lvn (default: stdout)")
	format := fs.String("f", "", "force input format: ink | articy")
	dialogue := fs.String("dialogue", "", "articy: Dialogue to convert (default: the only one)")
	_ = fs.Parse(args)
	if *in == "" {
		// allow positional input: lvnconv convert chapter.ink
		if fs.NArg() == 1 {
			*in = fs.Arg(0)
		} else {
			die("convert: -i <input> is required")
		}
	}

	src, err := os.ReadFile(*in)
	if err != nil {
		die(err.Error())
	}

	f := *format
	if f == "" {
		f = detectFormat(*in)
	}

	var data []byte
	switch f {
	case "ink":
		doc, err := ink.Convert(string(src))
		if err != nil {
			die("ink: " + err.Error())
		}
		data = mustJSON(doc)
	case "lvns":
		doc, err := lvns.Convert(string(src))
		if err != nil {
			die("lvns: " + err.Error())
		}
		data = mustJSON(doc)
	case "articy":
		doc, err := articy.Convert(src, *dialogue)
		if err != nil {
			die("articy: " + err.Error())
		}
		data = mustJSON(doc)
	case "lvn":
		die("convert: input is already a .lvn — nothing to do (use validate/probe)")
	default:
		die(fmt.Sprintf("convert: cannot infer format from %q — pass -f ink|articy", *in))
	}

	if *out == "" {
		os.Stdout.Write(data)
		return
	}
	if err := os.WriteFile(*out, data, 0o644); err != nil {
		die(err.Error())
	}
}

func cmdValidate(args []string) {
	fs := newFlagSet("validate")
	strict := fs.Bool("strict", false, "treat lint warnings as failures")
	_ = fs.Parse(args)
	if fs.NArg() != 1 {
		die("validate: expected one <in.lvn>")
	}
	doc := loadLvn(fs.Arg(0))

	issues := lvn.Validate(doc)
	var errs, warns int
	for _, is := range issues {
		// Document-level label findings are lint warnings; everything else fails.
		warn := is.Index < 0
		if warn {
			warns++
			fmt.Fprintln(os.Stderr, "warning: "+is.String())
		} else {
			errs++
			fmt.Fprintln(os.Stderr, "error: "+is.String())
		}
	}
	if errs > 0 || (*strict && warns > 0) {
		fmt.Fprintf(os.Stderr, "FAIL: %d error(s), %d warning(s)\n", errs, warns)
		os.Exit(1)
	}
	fmt.Fprintf(os.Stderr, "OK: %d command(s), %d warning(s)\n", len(doc.Script), warns)
}

func cmdProbe(args []string) {
	fs := newFlagSet("probe")
	_ = fs.Parse(args)
	if fs.NArg() != 1 {
		die("probe: expected one <in.lvn>")
	}
	doc := loadLvn(fs.Arg(0))

	counts := map[string]int{}
	for _, c := range doc.Script {
		counts[c.Op()]++
	}
	scene := doc.Scene
	if scene == "" {
		scene = "(none)"
	}
	fmt.Printf("scene=%s commands=%d say=%d choice=%d label=%d goto=%d if=%d bg=%d actor=%d\n",
		scene, len(doc.Script), counts["say"], counts["choice"], counts["label"],
		counts["goto"], counts["if"], counts["bg"], counts["actor"])
}

func loadLvn(path string) *lvn.Doc {
	data, err := os.ReadFile(path)
	if err != nil {
		die(err.Error())
	}
	doc, err := lvn.Parse(data)
	if err != nil {
		die(err.Error())
	}
	return doc
}

func mustJSON(v any) []byte {
	data, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		die(err.Error())
	}
	return append(data, '\n')
}

func die(msg string) {
	fmt.Fprintln(os.Stderr, "lvnconv: "+msg)
	os.Exit(1)
}
