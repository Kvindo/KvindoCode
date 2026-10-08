#!/usr/bin/env python3
"""Regenerate src/PvCode.Core/Secrets/gitleaks-rules.json from the upstream gitleaks default config (MIT).

    python3 tools/update-gitleaks-rules.py [path-to-gitleaks.toml]      # default: download from GitHub master

Only what the C# loader honours is kept: id, regex, keywords, entropy, secretGroup, allowlists (regexes, stopwords,
regexTarget, condition) and the global allowlist (regexes, stopwords). Path allowlists are dropped - PvCode scans
tool output and messages, not repository files. Go-RE2 syntax is converted to .NET where they differ.
"""
import json, re, sys, tomllib, urllib.request, pathlib

URL = "https://raw.githubusercontent.com/gitleaks/gitleaks/master/config/gitleaks.toml"
OUT = pathlib.Path(__file__).resolve().parent.parent / "src/PvCode.Core/Secrets/gitleaks-rules.json"

POSIX = {"alnum": "a-zA-Z0-9", "alpha": "a-zA-Z", "digit": "0-9", "lower": "a-z", "upper": "A-Z",
         "space": r"\s", "xdigit": "0-9a-fA-F", "punct": r"!-/:-@\[-`{-~", "word": r"\w"}

def to_dotnet(rx: str) -> str:
    rx = rx.replace("(?P<", "(?<")
    rx = re.sub(r"\[:(\w+):\]", lambda m: POSIX.get(m.group(1), m.group(0)), rx)
    return rx

def allow(a: dict) -> dict:
    o = {}
    if a.get("regexes"): o["regexes"] = [to_dotnet(x) for x in a["regexes"]]
    if a.get("stopwords"): o["stopwords"] = [x.lower() for x in a["stopwords"]]
    if a.get("regexTarget"): o["target"] = a["regexTarget"]
    if a.get("condition"): o["condition"] = a["condition"]
    return o

def main():
    raw = pathlib.Path(sys.argv[1]).read_bytes() if len(sys.argv) > 1 else urllib.request.urlopen(URL, timeout=60).read()
    d = tomllib.loads(raw.decode())
    rules = []
    for r in d["rules"]:
        if "regex" not in r:                      # path-only rules (pkcs12-file) do not apply to text
            continue
        o = {"id": r["id"], "regex": to_dotnet(r["regex"]), "keywords": [k.lower() for k in r.get("keywords", [])]}
        if "entropy" in r: o["entropy"] = r["entropy"]
        if "secretGroup" in r: o["group"] = r["secretGroup"]
        al = [x for x in (allow(a) for a in r.get("allowlists", [])) if x]
        if al: o["allow"] = al
        rules.append(o)
    g = allow(d.get("allowlist", {}))
    OUT.write_text(json.dumps({"source": "gitleaks (MIT) " + d.get("title", ""), "global": g, "rules": rules},
                              ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    print(f"wrote {OUT} : {len(rules)} rules, global regexes={len(g.get('regexes', []))} stopwords={len(g.get('stopwords', []))}")

main()
