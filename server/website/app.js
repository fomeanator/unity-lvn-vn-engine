// Client-side compiler matching the Go implementation 1:1

function compileLVNScript(src) {
    const doc = { script: [] };
    const actorMaps = {};
    const lines = [];
    const rawLines = src.split('\n');
    
    const urlGuard = "\x00PROTO\x00";
    for (let raw of rawLines) {
        let line = raw.replaceAll("://", urlGuard);
        let idx = line.indexOf("//");
        if (idx >= 0) {
            line = line.substring(0, idx);
        }
        line = line.replaceAll(urlGuard, "://").trim();
        if (line === "" || line.startsWith("#")) {
            continue;
        }
        lines.push(line);
    }
    
    const KnownOps = {
        "say": true, "choice": true, "bg": true, "actor": true, "obj": true,
        "fade": true, "dim": true, "camera": true, "particles": true,
        "audio": true, "wait": true, "preload": true,
        "label": true, "goto": true, "if": true,
        "set": true, "inc": true, "hint": true,
        "call": true, "return": true
    };
    
    const reDialogue = /^([^:=]+?)(?:\s*\[([^\]]+)\])?\s*:\s*(.*)$/;
    
    let i = 0;
    while (i < lines.length) {
        let line = lines[i];
        
        if (line.startsWith("scene ")) {
            doc.scene = line.substring(6).trim();
            i++;
            continue;
        }
        if (line.startsWith("scene:")) {
            doc.scene = line.substring(6).trim();
            i++;
            continue;
        }
        
        if (line.startsWith("actor_map ")) {
            let mapping = line.substring(10).trim();
            let parts = mapping.split("=");
            if (parts.length === 2) {
                actorMaps[parts[0].trim()] = parts[1].trim();
            }
            i++;
            continue;
        }
        
        if (line.startsWith(":")) {
            let labelID = line.substring(1).trim();
            if (labelID === "") {
                throw new Error(`Line ${i+1}: label cannot be empty`);
            }
            doc.script.push({ op: "label", id: labelID });
            i++;
            continue;
        }
        
        if (line.startsWith("-")) {
            let options = [];
            let j = i;
            while (j < lines.length) {
                let curr = lines[j];
                if (curr.startsWith("-")) {
                    let opt = parseChoiceOption(curr, j + 1);
                    options.push(opt);
                    j++;
                } else {
                    break;
                }
            }
            doc.script.push({ op: "choice", options: options });
            i = j;
            continue;
        }
        
        let words = line.split(/\s+/);
        let firstWord = words[0] || "";
        
        let isCommand = false;
        let cmd = null;
        
        if (KnownOps[firstWord]) {
            if (firstWord === "return" && words.length === 1) {
                isCommand = true;
                cmd = { op: "return" };
            } else if ((firstWord === "goto" || firstWord === "call") && words.length === 2) {
                isCommand = true;
                cmd = { op: firstWord, label: words[1] };
            } else if (firstWord !== "return" && firstWord !== "goto" && firstWord !== "call") {
                let rest = line.substring(firstWord.length).trim();
                if (rest === "") {
                    isCommand = true;
                    cmd = { op: firstWord };
                } else {
                    try {
                        let params = parseKeyValue(rest);
                        isCommand = true;
                        cmd = { op: firstWord, ...params };
                    } catch (e) {
                        // not a command or parse error
                    }
                }
            }
        }
        
        if (isCommand) {
            doc.script.push(cmd);
            i++;
            continue;
        }
        
        let match = line.match(reDialogue);
        if (match) {
            let speaker = match[1].trim();
            let emotion = match[2] ? match[2].trim() : "";
            let text = match[3].trim();
            
            text = stripQuotes(text);
            
            if (emotion !== "") {
                let actorID = actorMaps[speaker] || speaker.toLowerCase().replace(/ /g, "_");
                doc.script.push({ op: "actor", id: actorID, emotion: emotion });
            }
            
            doc.script.push({ op: "say", who: speaker, text: text });
        } else {
            let text = stripQuotes(line);
            doc.script.push({ op: "say", text: text });
        }
        
        i++;
    }
    
    return doc;
}

function parseChoiceOption(line, lineNum) {
    let text = line.substring(1).trim(); // strip '-'
    let arrowIdx = text.indexOf("->");
    if (arrowIdx === -1) {
        throw new Error(`Line ${lineNum}: choice option must have a target label (use '-> label')`);
    }
    let optText = text.substring(0, arrowIdx).trim();
    if (optText === "") {
        throw new Error(`Line ${lineNum}: choice option text cannot be empty`);
    }
    let rest = text.substring(arrowIdx + 2).trim();
    if (rest === "") {
        throw new Error(`Line ${lineNum}: choice option must specify a target label after '->'`);
    }
    
    let spaceIdx = rest.search(/\s/);
    let targetLabel = "";
    let paramsStr = "";
    if (spaceIdx === -1) {
        targetLabel = rest;
    } else {
        targetLabel = rest.substring(0, spaceIdx);
        paramsStr = rest.substring(spaceIdx + 1).trim();
    }
    
    let opt = {
        text: stripQuotes(optText),
        goto: targetLabel
    };
    
    if (paramsStr !== "") {
        let params = parseKeyValue(paramsStr);
        Object.assign(opt, params);
    }
    return opt;
}

function parseKeyValue(s) {
    let res = {};
    s = s.trim();
    while (s.length > 0) {
        let eqIdx = s.indexOf("=");
        if (eqIdx === -1) {
            throw new Error(`expected '=' in key-value pair at "${s}"`);
        }
        let key = s.substring(0, eqIdx).trim();
        if (!isValidKey(key)) {
            throw new Error(`invalid key name "${key}"`);
        }
        s = s.substring(eqIdx + 1).trim();
        if (s.length === 0) {
            throw new Error(`missing value for key "${key}"`);
        }
        
        let val = "";
        if (s[0] === '"' || s[0] === "'") {
            let quote = s[0];
            let end = -1;
            for (let i = 1; i < s.length; i++) {
                if (s[i] === quote && s[i - 1] !== '\\') {
                    end = i;
                    break;
                }
            }
            if (end === -1) {
                throw new Error(`unclosed quote for key "${key}"`);
            }
            val = s.substring(1, end);
            val = val.replaceAll('\\"', '"').replaceAll("\\'", "'");
            s = s.substring(end + 1);
        } else {
            let spaceIdx = s.search(/\s/);
            if (spaceIdx === -1) {
                val = s;
                s = "";
            } else {
                val = s.substring(0, spaceIdx);
                s = s.substring(spaceIdx + 1);
            }
        }
        
        if (val === "true") {
            res[key] = true;
        } else if (val === "false") {
            res[key] = false;
        } else if (val === "null") {
            res[key] = null;
        } else {
            let num = Number(val);
            if (!isNaN(num) && val !== "") {
                res[key] = num;
            } else {
                res[key] = val;
            }
        }
        s = s.trim();
    }
    return res;
}

function isValidKey(k) {
    return /^[a-zA-Z0-9_\.]+$/.test(k);
}

function stripQuotes(s) {
    s = s.trim();
    if (s.length >= 2) {
        if ((s[0] === '"' && s[s.length - 1] === '"') || (s[0] === "'" && s[s.length - 1] === "'")) {
            return s.substring(1, s.length - 1);
        }
    }
    return s;
}

// UI wiring
document.addEventListener("DOMContentLoaded", () => {
    const editor = document.getElementById("editor");
    const output = document.getElementById("output");
    const statusBadge = document.getElementById("status-badge");
    const copyBtn = document.getElementById("copy-btn");
    
    function compile() {
        const src = editor.value;
        try {
            const compiled = compileLVNScript(src);
            output.textContent = JSON.stringify(compiled, null, 2);
            output.classList.remove("error-text");
            
            statusBadge.textContent = "✓ Compiled Successfully";
            statusBadge.className = "badge success";
        } catch (err) {
            output.textContent = err.message;
            output.classList.add("error-text");
            
            statusBadge.textContent = "✗ Compilation Error";
            statusBadge.className = "badge error";
        }
    }
    
    editor.addEventListener("input", compile);
    
    // Copy output JSON
    copyBtn.addEventListener("click", () => {
        navigator.clipboard.writeText(output.textContent).then(() => {
            const originalText = copyBtn.textContent;
            copyBtn.textContent = "Copied!";
            setTimeout(() => {
                copyBtn.textContent = originalText;
            }, 1500);
        });
    });

    // Copy AI System Prompt
    const copyAiBtn = document.getElementById("copy-ai-btn");
    copyAiBtn.addEventListener("click", () => {
        const aiPromptText = document.getElementById("ai-prompt-text").textContent;
        navigator.clipboard.writeText(aiPromptText).then(() => {
            const originalText = copyAiBtn.textContent;
            copyAiBtn.textContent = "Copied Prompt!";
            setTimeout(() => {
                copyAiBtn.textContent = originalText;
            }, 1500);
        });
    });
    
    // Wire lesson try-buttons
    document.querySelectorAll(".try-btn").forEach(btn => {
        btn.addEventListener("click", () => {
            const codeId = btn.getAttribute("data-code-id");
            const codeEl = document.getElementById(codeId);
            if (codeEl) {
                editor.value = codeEl.textContent.trim();
                compile();
                // Smooth scroll to editor
                document.getElementById("playground").scrollIntoView({ behavior: "smooth" });
            }
        });
    });
    
    // Initial compile
    compile();
});
