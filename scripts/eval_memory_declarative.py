#!/usr/bin/env python3
"""Opt-in PASS/FAIL evaluation of non-tautological replies to exact manual statements."""

import argparse
import json
import os
import sys
import urllib.request
from pathlib import Path

from eval_memory_extraction import setting

ROOT = Path(__file__).resolve().parents[1]
IDENTITY = (ROOT / "backend/src/Aegis.Api/Prompts/aegis_identity.md").read_text()
STATEMENTS = [
    "Meu PC tinha 16 GB de RAM.",
    "A Aegis usa PostgreSQL como fonte canônica do sistema de memória.",
    "Troquei a memória do PC e agora ele tem 32 GB de RAM.",
    "Meu monitor é 4K 144 Hz.",
    "Depois da FaZe, meu time de R6 favorito é a DarkZero.",
    "Na faculdade me chamam de Vecna.",
]
JUDGE_SCHEMA = {
    "type": "object", "properties": {
        "tautologicalRestatement": {"type": "boolean"},
        "addsUsefulComment": {"type": "boolean"},
        "reason": {"type": "string"}},
    "required": ["tautologicalRestatement", "addsUsefulComment", "reason"],
    "additionalProperties": False,
}


def response(model, prompt, statement, schema=None):
    payload = {"model": model, "input": [
        {"role": "developer", "content": prompt}, {"role": "user", "content": statement}],
        "store": False, "max_output_tokens": 400}
    if schema:
        payload["text"] = {"format": {"type": "json_schema", "name": "reply_quality",
                                      "strict": True, "schema": schema}}
    request = urllib.request.Request(setting("AEGIS_OPENAI_BASE_URL", "https://api.openai.com").rstrip("/") + "/v1/responses",
        data=json.dumps(payload).encode(), headers={"Authorization": "Bearer " + setting("OPENAI_API_KEY"),
            "Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=60) as result:
        body = json.load(result)
    text = body.get("output_text") or next((block.get("text") for item in body.get("output", [])
        for block in item.get("content", []) if block.get("type") == "output_text"), None)
    if not text:
        raise ValueError("missing model response")
    return text


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--trials", type=int, default=1)
    args = parser.parse_args()
    if os.getenv("AEGIS_MEMORY_DECLARATIVE_LIVE") != "YES":
        print("Skipped: set AEGIS_MEMORY_DECLARATIVE_LIVE=YES.")
        return 0
    if not setting("OPENAI_API_KEY"):
        print("OPENAI_API_KEY unavailable", file=sys.stderr)
        return 2
    model = setting("AEGIS_CHAT_MODEL", "gpt-6-luna")
    judged = 0
    total = len(STATEMENTS) * args.trials
    for trial in range(args.trials):
        for statement in STATEMENTS:
            try:
                reply = response(model, IDENTITY, statement)
                if len(reply.strip().split()) <= 2 and not any(x in reply.lower() for x in ("16", "32", "4k", "144", "qhd")):
                    verdict = {"tautologicalRestatement": False, "addsUsefulComment": False,
                               "reason": "minimal acknowledgement"}
                else:
                    instruction = ("Avalie se a resposta da assistente apenas repete ou parafraseia a declaração "
                        "factual do usuário, sem acrescentar comentário pertinente e não óbvio. "
                        "Sarcasmo ou observação natural com informação nova é aceitável. "
                        "Marque tautologicalRestatement=true se a resposta só reformula o fato, mesmo com 'Entendi'.")
                    verdict = json.loads(response(model, instruction,
                        json.dumps({"user": statement, "assistant": reply}, ensure_ascii=False), JUDGE_SCHEMA))
                ok = not verdict["tautologicalRestatement"]
                judged += ok
                print(json.dumps({"trial": trial + 1, "statement": statement, "reply": reply,
                    "verdict": verdict, "result": "PASS" if ok else "FAIL"}, ensure_ascii=False))
            except Exception as error:
                print(json.dumps({"trial": trial + 1, "statement": statement,
                    "error": type(error).__name__, "result": "FAIL"}, ensure_ascii=False))
    print(f"passed={judged}/{total}")
    return 0 if judged == total else 1


if __name__ == "__main__":
    sys.exit(main())
