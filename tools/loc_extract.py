# Moves UI text out of launcher source into assets\lang\en.json (Loc.cs), one area at a time.
#   python tools\loc_extract.py <area> src\Home.cs src\Screens.cs ...      rewrite and add the strings
#   python tools\loc_extract.py --dry <area> src\...                         only list what would change
#   python tools\loc_extract.py --loose [--dry] <area> src\...                also returns, ternaries, .Text = ...
#   src\X.cs:120-400 (or :120-) only takes the literals on those lines (key names, font names and the like stay)
#   python tools\loc_extract.py --left src\*.cs                           list English-looking literals still in code
# Only literals in known UI calls are moved (CALLS below): row labels and values, card titles, notes, toasts,
# messages. Interpolated strings become T("id", args) with {0}, {1}; ones with quotes inside a hole, verbatim
# and raw strings are reported for a hand edit. Ids are <area>.<first words>; the same English text reuses
# its id. Each file keeps its encoding (BOM or not) and line endings.
import glob, json, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EN = os.path.join(ROOT, "assets", "lang", "en.json")

# call name -> argument indexes whose literals are UI text. "Fonts.Text" style names need that prefix.
CALLS = {
    "AddRow": (0, 1), "AddRowFor": (0, 1), "AddSlider": (0,), "Aside": (1,), "Section": (0,), "BeginBox": (0,),
    "Toasts.Show": (0, 1), "Fonts.Text": (0,), "base": (1,), "MessageCard": (1, 2), "TextCard": (1, 2),
    "Note": (0,), "Error": (0,), "No": (0,), "Say": (0,), "Choice": (0,), "Fact": (0,), "Stat": (0,),
    "Caption": (0,), "Hint": (0,), "step": (0,), "Tile": (0,), "Line": (0,),
}
COMMON = {"Automatic", "Done", "Back", "Cancel", "OK", "On", "Off", "Close", "Save", "Later", "Yes", "No", "None", "Add", "Remove", "Rename"}
SKIP_PREV = {"Log.Write", "Online.Note"}   # never UI text
KEEP = {"Ionea", "Steam", "Discord", "View + Start", "SteamGridDB", "GitHub"}   # names, not words


def tokens(s):
    """C# tokens with spans: ('str', start, end, kind) for literals, ('id', ...), ('p', ...) for punctuation."""
    i, n = 0, len(s)
    while i < n:
        c = s[i]
        if s.startswith("//", i):
            j = s.find("\n", i); i = n if j < 0 else j; continue
        if s.startswith("/*", i):
            j = s.find("*/", i + 2); i = n if j < 0 else j + 2; continue
        if c.isspace(): i += 1; continue
        m = re.match(r'(\$@|@\$|\$|@)?("""|")', s[i:])
        if m:
            pre, q = m.group(1) or "", m.group(2)
            j = i + m.end()
            if q == '"""':
                k = s.find('"""', j); k = n if k < 0 else k + 3
                yield ("str", i, k, "raw"); i = k; continue
            verb, interp, depth = "@" in pre, "$" in pre, 0
            while j < n:
                ch = s[j]
                if interp and ch == "{":
                    if s.startswith("{{", j): j += 2; continue
                    depth += 1
                elif interp and ch == "}" and depth > 0: depth -= 1
                elif depth > 0 and ch == '"':   # a string inside a hole
                    k = j + 1
                    while k < n and s[k] != '"':
                        k += 2 if s[k] == "\\" else 1
                    j = k
                elif ch == "\\" and not verb and depth == 0: j += 1
                elif ch == '"' and depth == 0:
                    if verb and s.startswith('""', j): j += 2; continue
                    break
                j += 1
            yield ("str", i, j + 1, ("$" if interp else "") + ("@" if verb else "")); i = j + 1; continue
        if c == "'":
            j = i + 1
            while j < n and s[j] != "'": j += 2 if s[j] == "\\" else 1
            yield ("p", i, j + 1, "char"); i = j + 1; continue
        m = re.match(r"[A-Za-z_][\w]*(\s*\.\s*[A-Za-z_]\w*)*", s[i:])
        if m: yield ("id", i, i + m.end(), re.sub(r"\s+", "", m.group(0))); i += m.end(); continue
        yield ("p", i, i + 1, c); i += 1


def unescape(body):
    return re.sub(r'\\(u[0-9a-fA-F]{4}|.)', lambda m: chr(int(m.group(1)[1:], 16)) if m.group(1)[0] == "u" else
                  {"n": "\n", "t": "\t", "r": "\r", "0": "\0"}.get(m.group(1), m.group(1)), body)


def split_holes(body):
    """$"..." body -> (format text, [expressions]) or None when a hole holds a string."""
    out, args, i = [], [], 0
    while i < len(body):
        if body.startswith("{{", i) or body.startswith("}}", i): out.append(body[i:i + 2]); i += 2; continue
        if body[i] == "{":
            depth, j = 1, i + 1
            while j < len(body) and depth:
                depth += {"{": 1, "}": -1}.get(body[j], 0); j += 1
            hole = body[i + 1:j - 1]
            if '"' in hole or "'" in hole: return None
            # top-level ',' alignment / ':' format (not inside parens / brackets)
            d, cut = 0, len(hole)
            for k, ch in enumerate(hole):
                if ch in "([": d += 1
                elif ch in ")]": d -= 1
                elif d == 0 and ch in ",:" and not hole.startswith("::", k): cut = k; break
            expr, spec = hole[:cut].strip(), hole[cut:]
            out.append("{%d%s}" % (len(args), spec)); args.append(expr); i = j; continue
        out.append(body[i]); i += 1
    return "".join(out), args


def looks_like_text(t):
    plain = re.sub(r"\{\d+[^}]*\}", "", t)
    return re.search(r"[A-Za-z]{2}", plain) is not None and not re.fullmatch(r"[a-z0-9_.\-/\\:]+", plain.strip())


def slug(t):
    words = re.findall(r"[a-z0-9]+", re.sub(r"\{\d+[^}]*\}", " ", t.lower()))
    return "_".join(words[:6]) or "text"


def candidates(s):
    """Literal tokens inside the UI arguments of known calls."""
    toks = list(tokens(s))
    found = []
    for x, tk in enumerate(toks):
        if tk[0] != "id" or x + 1 >= len(toks) or toks[x + 1][3] != "(": continue
        name = tk[3]
        key = name if name in CALLS else next((k for k in CALLS if "." in k and name.endswith("." + k) or name == k), None)
        if key is None or name in SKIP_PREV: continue
        want = CALLS[key]
        # walk the call's arguments
        depth, arg, y = 0, 0, x + 1
        calls = []   # stack: is this paren a call?
        while y < len(toks):
            t = toks[y]
            if t[0] == "p" and t[3] in "([{":
                if t[3] == "(" and y != x + 1:
                    prev = toks[y - 1]
                    calls.append(prev[0] == "id" and prev[3] not in ("if", "while", "for", "switch", "return", "new") or prev[3] in (">", "]"))
                elif y != x + 1: calls.append(False)
                depth += 1
            elif t[0] == "p" and t[3] in ")]}":
                depth -= 1
                if depth == 0: break
                calls.pop() if calls else None
            elif t[0] == "p" and t[3] == "," and depth == 1: arg += 1
            elif t[0] == "str" and depth >= 1 and arg in want and not any(calls):
                found.append((t[1], t[2], t[3], key))
            y += 1
    return found


# --loose: also English-looking literals outside those calls (returns, ternaries, .Text = ...), but never
# inside these calls, after case / == / [, or before == / ]. Check the --dry list before using it.
DENY = {"Log.Write", "Online.Note", "Row", "StartsWith", "EndsWith", "Contains", "Equals", "IndexOf", "Replace", "Split",
        "IsMatch", "Match", "Matches", "Combine", "GetCultureInfo", "ToString", "Play", "T", "N", "nameof", "TrimEnd",
        "TrimStart", "GetEnvironmentVariable", "Parse", "TryParse", "Exists", "Format", "Write", "WriteLine", "Fail", "Ok", "Check",
        "Spot", "RailElement", "TutorialSpot", "GetValue", "TryGetValue", "Get", "Has", "Is", "Field", "Prop", "Key", "Press"}


def looks_like_sentence(t):
    return re.match(r"^\W{0,3}[A-Z][a-z']+([ ,.!?…:]|$)", t) is not None and looks_like_text(t)


def candidates_loose(s):
    toks = list(tokens(s))
    stack, found = [], []   # stack of enclosing call names (None for grouping parens)
    for x, t in enumerate(toks):
        if t[0] == "p" and t[3] in "([{":
            prev = toks[x - 1] if x else None
            stack.append(prev[3] if t[3] == "(" and prev and prev[0] == "id" else None); continue
        if t[0] == "p" and t[3] in ")]}":
            if stack: stack.pop()
            continue
        if t[0] != "str": continue
        prev = toks[x - 1][3] if x else ""
        nxt = toks[x + 1][3] if x + 1 < len(toks) else ""
        nxt2 = toks[x + 2][3] if x + 2 < len(toks) else ""
        if prev in ("case", "[") or nxt in ("]",) or (prev in ("=", "!") and toks[x - 2][3] in ("=", "!")) or (nxt == "=" and nxt2 == "=") or (nxt == "!" and nxt2 == "="):
            continue
        line = s[s.rfind("\n", 0, t[1]) + 1:s.find("\n", t[1])]
        if "Log.Write" in line or "[Json" in line: continue
        if any(c and c.split(".")[-1] in DENY | {d.split(".")[-1] for d in DENY} for c in stack): continue
        if any(c and c.endswith("Exception") for c in stack): continue
        found.append((t[1], t[2], t[3], "loose"))
    return found


def run(area, files, dry, loose=False):
    en = json.load(open(EN, encoding="utf-8")) if os.path.exists(EN) else {"_name": "English"}
    by_text = {v: k for k, v in en.items() if isinstance(v, str) and not k.startswith("_")}
    added, manual = 0, []
    for f in files:
        lo, hi = 1, 10**9   # src\X.cs:120-400 only takes literals on those lines
        m = re.match(r"(.*\.cs):(\d+)-(\d*)$", f)
        if m: f, lo, hi = m.group(1), int(m.group(2)), int(m.group(3) or 10**9)
        raw = open(f, "rb").read()
        bom = raw.startswith(b"\xef\xbb\xbf")
        s = raw.decode("utf-8-sig")
        edits = []
        cands = candidates(s)
        if loose:
            have = {(a, b) for a, b, _, _ in cands}
            cands += [c for c in candidates_loose(s) if (c[0], c[1]) not in have and looks_like_sentence(
                unescape(c[2] == "$" and (split_holes(s[c[0] + 2:c[1] - 1]) or ("",))[0] or s[c[0] + 1:c[1] - 1]))]
        for a, b, kind, call in sorted(cands):
            if not lo <= s.count("\n", 0, a) + 1 <= hi: continue
            lit = s[a:b]
            if kind in ("raw", "@", "$@"):
                if looks_like_text(lit): manual.append((f, s.count("\n", 0, a) + 1, lit[:70]))
                continue
            body = lit[2:-1] if kind == "$" else lit[1:-1]
            args = []
            if kind == "$":
                sp = split_holes(body)
                if sp is None:
                    if looks_like_text(body): manual.append((f, s.count("\n", 0, a) + 1, lit[:70]))
                    continue
                body, args = sp
            text = unescape(body)
            if not looks_like_text(text) or text in KEEP: continue
            if text in by_text: id_ = by_text[text]
            else:
                base = ("common." if text in COMMON else area + ".") + slug(text)
                id_, k = base, 2
                while id_ in en: id_ = f"{base}_{k}"; k += 1
                en[id_] = text; by_text[text] = id_; added += 1
            edits.append((a, b, 'T("%s"%s)' % (id_, "".join(", " + e for e in args))))
            if dry: print(f"{os.path.basename(f)}:{s.count(chr(10), 0, a) + 1}: {id_} = {text!r}")
        if edits and not dry:
            for a, b, rep in sorted(edits, reverse=True): s = s[:a] + rep + s[b:]
            if "using static Ionea.Loc;" not in s:
                last = list(re.finditer(r"^using [^\n]*;[^\n]*\n", s, re.M))
                nl = "\r\n" if "\r\n" in s else "\n"
                at = last[-1].end() if last else 0
                s = s[:at] + "using static Ionea.Loc;" + nl + s[at:]
            open(f, "wb").write((b"\xef\xbb\xbf" if bom else b"") + s.encode("utf-8"))
        print(f"{f}: {len(edits)} strings")
    if not dry:
        os.makedirs(os.path.dirname(EN), exist_ok=True)
        with open(EN, "w", encoding="utf-8", newline="\n") as o: json.dump(en, o, ensure_ascii=False, indent=2); o.write("\n")
    print(f"{added} new ids; {sum(1 for k in en if not k.startswith('_'))} in en.json")
    for f, line, lit in manual: print(f"by hand: {os.path.basename(f)}:{line}: {lit}")


def left(files):
    for f in files:
        s = open(f, encoding="utf-8-sig").read()
        n = 0
        for t in tokens(s):
            if t[0] != "str": continue
            lit = s[t[1]:t[2]]
            line_start = s.rfind("\n", 0, t[1]) + 1
            line = s[line_start:s.find("\n", t[1])]
            if "Log.Write" in line or "Online.Note" in line: continue
            if re.match(r'\$?@?"[A-Z][a-z]+[ ,.!?…\']', lit): n += 1
        print(f"{n:5}  {f}")


if __name__ == "__main__":
    a = sys.argv[1:]
    files = lambda xs: [g for x in xs for g in (sorted(glob.glob(x)) or [x])]
    if a and a[0] == "--left": left(files(a[1:]))
    else:
        dry, loose = "--dry" in a, "--loose" in a
        a = [x for x in a if x not in ("--dry", "--loose")]
        if len(a) >= 2: run(a[0], files(a[1:]), dry, loose)
        else: print(open(__file__).read().split("\n\n")[0])
