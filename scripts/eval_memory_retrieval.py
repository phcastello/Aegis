#!/usr/bin/env python3
"""Opt-in embedding relevance benchmark against an isolated diagnostics API.

Seed the manual acceptance fixture first. This script calls only retrieval, never chat.
"""

import json
import os
import sys
import urllib.request

# The 019 query has a state-dependent answer: after 020, DarkZero is known again.
# The literal 019 state is asserted during the ordered manual replay, before 020.
CASES = [
    ("Qual é meu time favorito de R6?", ["FaZe"], ["monitor", "Aegis", "RAM", "Vecna"]),
    ("Qual é a resolução e a frequência do meu monitor?", ["QHD", "180"],
     ["FaZe", "Aegis", "RAM", "Vecna", "4K", "144"]),
    ("Quanto de RAM meu PC tem hoje?", ["32", "RAM"], ["monitor", "FaZe", "Aegis", "Vecna", "16"]),
    ("Qual banco é usado como fonte canônica de memória pelo projeto que eu desenvolvo?",
     ["PostgreSQL", "Aegis"], ["monitor", "RAM", "FaZe", "Vecna"]),
]


def main():
    if os.getenv("AEGIS_MEMORY_RETRIEVAL_LIVE") != "YES":
        print("Skipped: set AEGIS_MEMORY_RETRIEVAL_LIVE=YES for live embedding retrieval.")
        return 0
    if os.getenv("AEGIS_MEMORY_EVAL_ISOLATED") != "YES":
        print("Refusing to query a non-isolated memory deployment.", file=sys.stderr)
        return 2
    base = os.getenv("AEGIS_MEMORY_EVAL_BASE_URL", "").rstrip("/")
    if not base:
        print("AEGIS_MEMORY_EVAL_BASE_URL is required.", file=sys.stderr)
        return 2
    try:
        with urllib.request.urlopen(base + "/api/memory/diagnostics/environment", timeout=15) as response:
            environment = json.load(response)
    except Exception as error:
        print("Isolated diagnostics unavailable: " + type(error).__name__, file=sys.stderr)
        return 2
    if not (environment.get("isolated") and
            environment.get("database", "").startswith("aegis_memory_v060_") and
            environment.get("qdrantCollection", "").startswith("aegis_memory_v060_")):
        print("Refusing to query a deployment without disposable Memory resources.", file=sys.stderr)
        return 2
    passed = 0
    for query, positive, negative in CASES:
        request = urllib.request.Request(base + "/api/memory/diagnostics/retrieval",
            data=json.dumps({"query": query, "limit": 10}).encode(),
            headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=45) as response:
                result = json.load(response)
        except Exception as error:
            print(json.dumps({"query": query, "error": type(error).__name__}, ensure_ascii=False))
            continue
        final = [x["content"] for x in result["final"]["memories"]]
        text = " ".join(final).lower()
        ok = all(x.lower() in text for x in positive) and not any(x.lower() in text for x in negative)
        passed += ok
        print(json.dumps({"query": query, "expectedRelevant": positive, "expectedIrrelevant": negative,
            "scores": [{"content": x.get("content"), "score": x["score"], "kept": x["kept"],
                        "dropReason": x.get("dropReason")} for x in result["semantic"]["candidates"]],
            "finalResults": final, "result": "PASS" if ok else "FAIL"}, ensure_ascii=False))
    print(f"passed={passed}/{len(CASES)}")
    return 0 if passed == len(CASES) else 1


if __name__ == "__main__":
    sys.exit(main())
