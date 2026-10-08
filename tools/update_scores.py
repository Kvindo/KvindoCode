#!/usr/bin/env python3
"""Regenerates src/PvCode.App/Assets/scores.json: public benchmark scores per gateway model, for the model picker.

Sources: per-benchmark leaderboards published by benchlm.ai (which mirrors Vals AI, Artificial Analysis, Scale Labs, Gray Swan, SWE-bench ...);
each score keeps the link back to the primary leaderboard. Gateway model ids come from https://plusvibeapi.ru/api/catalog.
Usage: python3 tools/update_scores.py [--out path] [--report]   (the app runs it with --out ~/.pvcode/scores.json)
"""
import json, re, sys, time, ssl, urllib.request, datetime, os

UA = {"User-Agent": "Mozilla/5.0 (pvcode score updater)"}
CTX = ssl.create_default_context()

# topic -> [(benchlm key, label, higher_is_better)]
TOPICS = {
    "coding": [
        ("sweVerified", "SWE-bench Verified", True), ("swePro", "SWE-bench Pro", True), ("liveCodeBenchV6", "LiveCodeBench v6", True),
        ("terminalBench21", "Terminal-Bench 2.1", True), ("sweRebench", "SWE-rebench", True), ("aaLiveCodeBench", "AA LiveCodeBench", True),
        ("multiSweBench", "Multi-SWE-bench", True), ("bigCodeBench", "BigCodeBench", True),
    ],
    "legal": [
        ("valsLegalBench", "Vals LegalBench", True), ("valsCaseLawV2", "Vals CaseLaw v2", True), ("legalResearchBench", "Vals Legal Research", True),
        ("scale-prbench-legal", "Scale PRBench Legal", True), ("hlab", "Harvey Legal Agent Bench (Vals)", True), ("aaHarveyLab", "AA Harvey LAB", True),
    ],
    "security": [
        ("cyberGym", "CyberGym", True), ("cybench", "Cybench", True), ("cweBench", "CWE-Bench", True), ("cweBenchV1", "CWE-bench v1 (patching)", True),
        ("cyber", "Vals CyberBench", True), ("secBenchPro", "SEC-Bench Pro", True), ("cveBenchZeroDayBlackBox", "CVE-Bench zero-day", True),
        ("frontierCyber", "FrontierCyber", True),
    ],
    "safeguards": [
        ("graySwanIpi15", "Gray Swan indirect prompt injection (attack success @15)", False),
        ("bullshitBenchV2", "BullshitBench v2 (pushes back on nonsense prompts)", True),
        ("kindBench", "KindBench psychological safety", True),
    ],
}


def get(url, tries=3):
    # curl rather than urllib: it uses the system CA store, which Python's ssl module cannot find on some machines
    import subprocess
    for i in range(tries):
        r = subprocess.run(["curl", "-sSL", "-m", "60", "-A", UA["User-Agent"], url], capture_output=True)
        if r.returncode == 0 and r.stdout:
            return r.stdout.decode("utf-8", "ignore")
        if i == tries - 1:
            raise RuntimeError(f"curl failed for {url}: {r.stderr.decode()[:200]}")
        time.sleep(2)


def next_data(html):
    m = re.search(r'<script id="__NEXT_DATA__" type="application/json">(.*?)</script>', html, re.S)
    return json.loads(m.group(1))["props"]["pageProps"]


def norm(s):
    s = (s or "").lower()
    s = re.sub(r"^(anthropic|openai|google|deepseek|qwen|moonshotai|z-ai|x-ai|minimax|xiaomi|perplexity)/", "", s)
    s = re.sub(r"\[.*?\]", "", s)
    s = re.sub(r"[-_ ](preview|latest|instruct|thinking|reasoning|high|medium|low|xhigh|max|adaptive|nothink|exp)\b", "", s)
    s = re.sub(r"-\d{4}-\d{2}-\d{2}$", "", s)
    s = re.sub(r"-(\d{4})$", "", s)          # -0731 style date tags
    s = s.replace(".", "-").replace("_", "-").replace(" ", "-")
    return re.sub(r"-+", "-", s).strip("-")


def main():
    out = "src/PvCode.App/Assets/scores.json"
    if "--out" in sys.argv:
        out = sys.argv[sys.argv.index("--out") + 1]
    cat = json.loads(get("https://plusvibeapi.ru/api/catalog"))
    gateway = {}
    for m in cat:
        if m.get("variants") and (m.get("caps") or {}).get("contextWindow"):
            gateway[m.get("apiModel") or m["model"]] = m.get("displayLabel") or m["model"]
    alias = {}
    for gid, label in gateway.items():
        alias[norm(gid)] = gid
        alias[norm(label)] = gid
    scores = {gid: {} for gid in gateway}
    bench_meta = {}
    unmatched = {}
    for topic, items in TOPICS.items():
        for key, label, hib in items:
            slug = key.lower()
            try:
                pp = next_data(get(f"https://benchlm.ai/benchmarks/{slug}"))
            except Exception as e:
                print("  !! cannot load", key, e)
                continue
            lb = pp.get("leaderboard") or []
            b = pp.get("benchmark", {})
            url = b.get("paperUrl") or f"https://benchlm.ai/benchmarks/{slug}"
            best = {}
            for r in lb:
                names = [r.get("sourceModelId"), r.get("sourceLabel"), r.get("model"), r.get("slug")]
                gid = next((alias[n] for n in (norm(x) for x in names if x) if n in alias), None)
                if gid is None:
                    unmatched.setdefault(key, set()).add(r.get("model"))
                    continue
                v = r.get("score")
                if v is None:
                    continue
                if gid not in best or (v > best[gid] if hib else v < best[gid]):
                    best[gid] = v        # several harness/effort variants per model: keep the best result
            vals = sorted((r.get("score") for r in lb if r.get("score") is not None), reverse=hib)
            bench_meta[key] = {"topic": topic, "label": label, "higherIsBetter": hib, "url": url, "page": f"https://benchlm.ai/benchmarks/{slug}",
                               "format": b.get("format"), "models": len(vals), "updated": pp.get("lastUpdated")}
            for gid, v in best.items():
                rank = 1 + sum(1 for x in vals if (x > v if hib else x < v))
                scores[gid][key] = {"v": round(v, 2), "rank": rank, "of": len(vals)}
            print(f"{topic:10} {key:30} rows={len(lb):4} matched={len(best):3}")
    data = {"generated": datetime.date.today().isoformat(),
            "source": "benchlm.ai leaderboards (mirroring Vals AI, Artificial Analysis, Scale Labs, Gray Swan, SWE-bench, ...)",
            "benchmarks": bench_meta, "scores": {g: s for g, s in scores.items() if s}}
    os.makedirs(os.path.dirname(out), exist_ok=True)
    json.dump(data, open(out, "w"), ensure_ascii=False, indent=1)
    print(f"\nwrote {out}: {len(data['scores'])}/{len(gateway)} gateway models have at least one score")
    if "--report" in sys.argv:
        for g in gateway:
            per = {t: [k for k in scores[g] if bench_meta.get(k, {}).get("topic") == t] for t in TOPICS}
            print(f"{g:34}", {t: len(v) for t, v in per.items()})
        print("\nunmatched leaderboard names (sample):")
        for k, v in unmatched.items():
            print(" ", k, sorted(x for x in v if x)[:8])


main()
