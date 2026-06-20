package lvns

import (
	"fmt"
	"regexp"
	"strconv"
	"strings"
)

// Cmd is one .lvn command object.
type Cmd map[string]any

// Doc is the .lvn document shape ({scene?, script}).
type Doc struct {
	Scene  string `json:"scene,omitempty"`
	Script []Cmd  `json:"script"`
}

var KnownOps = map[string]bool{
	"say": true, "choice": true, "bg": true, "actor": true, "obj": true,
	"fade": true, "dim": true, "camera": true, "particles": true,
	"audio": true, "wait": true, "preload": true,
	"label": true, "goto": true, "if": true,
	"set": true, "inc": true, "hint": true,
	"call": true, "return": true,
}

var reDialogue = regexp.MustCompile(`^([^:=]+?)(?:\s*\[([^\]]+)\])?\s*:\s*(.*)$`)

// Convert parses lvns source and returns the .lvn document.
func Convert(src string) (*Doc, error) {
	doc := &Doc{Script: []Cmd{}}
	actorMaps := make(map[string]string)

	// Pre-process and clean lines
	var lines []string
	rawLines := strings.Split(src, "\n")
	const urlGuard = "\x00PROTO\x00"

	for _, raw := range rawLines {
		// Strip inline // comments, protecting URL "://".
		line := strings.ReplaceAll(raw, "://", urlGuard)
		if idx := strings.Index(line, "//"); idx >= 0 {
			line = line[:idx]
		}
		line = strings.TrimSpace(strings.ReplaceAll(line, urlGuard, "://"))

		// Skip comments and empty lines
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		lines = append(lines, line)
	}

	for i := 0; i < len(lines); {
		line := lines[i]

		// 1. Directives: scene
		if strings.HasPrefix(line, "scene ") {
			doc.Scene = strings.TrimSpace(line[6:])
			i++
			continue
		}
		if strings.HasPrefix(line, "scene:") {
			doc.Scene = strings.TrimSpace(line[6:])
			i++
			continue
		}

		// 2. Directives: actor_map
		if strings.HasPrefix(line, "actor_map ") {
			mapping := strings.TrimSpace(line[10:])
			parts := strings.SplitN(mapping, "=", 2)
			if len(parts) == 2 {
				actorMaps[strings.TrimSpace(parts[0])] = strings.TrimSpace(parts[1])
			}
			i++
			continue
		}

		// 3. Label: :label_name
		if strings.HasPrefix(line, ":") {
			labelID := strings.TrimPrefix(line, ":")
			labelID = strings.TrimSpace(labelID)
			if labelID == "" {
				return nil, fmt.Errorf("line %d: label cannot be empty", i+1)
			}
			doc.Script = append(doc.Script, Cmd{"op": "label", "id": labelID})
			i++
			continue
		}

		// 4. Choice: consecutive lines starting with -
		if strings.HasPrefix(line, "-") {
			var options []any
			j := i
			for j < len(lines) {
				curr := lines[j]
				if strings.HasPrefix(curr, "-") {
					opt, err := parseChoiceOption(curr)
					if err != nil {
						return nil, fmt.Errorf("line %d: %w", j+1, err)
					}
					options = append(options, opt)
					j++
				} else {
					break
				}
			}
			doc.Script = append(doc.Script, Cmd{"op": "choice", "options": options})
			i = j
			continue
		}

		// 5. Commands and Dialogue
		words := strings.Fields(line)
		firstWord := ""
		if len(words) > 0 {
			firstWord = words[0]
		}

		isCommand := false
		var cmd Cmd

		if KnownOps[firstWord] {
			if firstWord == "return" && len(words) == 1 {
				isCommand = true
				cmd = Cmd{"op": "return"}
			} else if (firstWord == "goto" || firstWord == "call") && len(words) == 2 {
				isCommand = true
				cmd = Cmd{"op": firstWord, "label": words[1]}
			} else if firstWord != "return" && firstWord != "goto" && firstWord != "call" {
				rest := strings.TrimSpace(line[len(firstWord):])
				if rest == "" {
					isCommand = true
					cmd = Cmd{"op": firstWord}
				} else {
					if params, err := parseKeyValue(rest); err == nil {
						isCommand = true
						cmd = Cmd{"op": firstWord}
						for k, v := range params {
							cmd[k] = v
						}
					}
				}
			}
		}

		if isCommand {
			doc.Script = append(doc.Script, cmd)
			i++
			continue
		}

		// Dialogue: Name [emotion]: Text or Narration
		if m := reDialogue.FindStringSubmatch(line); m != nil {
			speaker := strings.TrimSpace(m[1])
			emotion := strings.TrimSpace(m[2])
			text := strings.TrimSpace(m[3])

			text = stripQuotes(text)

			if emotion != "" {
				actorID, ok := actorMaps[speaker]
				if !ok {
					actorID = strings.ToLower(strings.ReplaceAll(speaker, " ", "_"))
				}
				doc.Script = append(doc.Script, Cmd{"op": "actor", "id": actorID, "emotion": emotion})
			}

			doc.Script = append(doc.Script, Cmd{"op": "say", "who": speaker, "text": text})
		} else {
			// Narration
			text := stripQuotes(line)
			doc.Script = append(doc.Script, Cmd{"op": "say", "text": text})
		}

		i++
	}

	return doc, nil
}

func parseChoiceOption(line string) (map[string]any, error) {
	text := strings.TrimSpace(line[1:]) // strip '-'
	arrowIdx := strings.Index(text, "->")
	if arrowIdx == -1 {
		return nil, fmt.Errorf("choice option must have a target label (use '-> label')")
	}
	optText := strings.TrimSpace(text[:arrowIdx])
	if optText == "" {
		return nil, fmt.Errorf("choice option text cannot be empty")
	}
	rest := strings.TrimSpace(text[arrowIdx+2:])
	if rest == "" {
		return nil, fmt.Errorf("choice option must specify a target label after '->'")
	}

	spaceIdx := strings.IndexAny(rest, " \t")
	var targetLabel string
	var paramsStr string
	if spaceIdx == -1 {
		targetLabel = rest
	} else {
		targetLabel = rest[:spaceIdx]
		paramsStr = strings.TrimSpace(rest[spaceIdx+1:])
	}

	opt := map[string]any{
		"text": stripQuotes(optText),
		"goto": targetLabel,
	}

	if paramsStr != "" {
		params, err := parseKeyValue(paramsStr)
		if err != nil {
			return nil, fmt.Errorf("invalid choice option parameters: %w", err)
		}
		for k, v := range params {
			opt[k] = v
		}
	}
	return opt, nil
}

func parseKeyValue(s string) (map[string]any, error) {
	res := make(map[string]any)
	s = strings.TrimSpace(s)
	for len(s) > 0 {
		eqIdx := strings.Index(s, "=")
		if eqIdx == -1 {
			return nil, fmt.Errorf("expected '=' in key-value pair at %q", s)
		}
		key := strings.TrimSpace(s[:eqIdx])
		if !isValidKey(key) {
			return nil, fmt.Errorf("invalid key name %q", key)
		}
		s = s[eqIdx+1:]
		s = strings.TrimSpace(s)
		if len(s) == 0 {
			return nil, fmt.Errorf("missing value for key %q", key)
		}

		var val string
		if s[0] == '"' || s[0] == '\'' {
			quote := s[0]
			end := -1
			for i := 1; i < len(s); i++ {
				if s[i] == quote && s[i-1] != '\\' {
					end = i
					break
				}
			}
			if end == -1 {
				return nil, fmt.Errorf("unclosed quote for key %q", key)
			}
			val = s[1:end]
			val = strings.ReplaceAll(val, "\\\"", "\"")
			val = strings.ReplaceAll(val, "\\'", "'")
			s = s[end+1:]
		} else {
			spaceIdx := strings.IndexAny(s, " \t")
			if spaceIdx == -1 {
				val = s
				s = ""
			} else {
				val = s[:spaceIdx]
				s = s[spaceIdx+1:]
			}
		}

		if val == "true" {
			res[key] = true
		} else if val == "false" {
			res[key] = false
		} else if val == "null" {
			res[key] = nil
		} else if n, err := strconv.ParseFloat(val, 64); err == nil {
			if !strings.Contains(val, ".") {
				if valInt, err := strconv.ParseInt(val, 10, 64); err == nil {
					res[key] = valInt
				} else {
					res[key] = n
				}
			} else {
				res[key] = n
			}
		} else {
			res[key] = val
		}
		s = strings.TrimSpace(s)
	}
	return res, nil
}

func isValidKey(k string) bool {
	if len(k) == 0 {
		return false
	}
	for _, r := range k {
		if !((r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9') || r == '_' || r == '.') {
			return false
		}
	}
	return true
}

func stripQuotes(s string) string {
	s = strings.TrimSpace(s)
	if len(s) >= 2 {
		if (s[0] == '"' && s[len(s)-1] == '"') || (s[0] == '\'' && s[len(s)-1] == '\'') {
			return s[1 : len(s)-1]
		}
	}
	return s
}
