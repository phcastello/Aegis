#!/usr/bin/env python3
"""Live behavioral evaluation of concise, non-tautological conversational presence."""

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
RICH_STATEMENT = "Agora tenho dois monitores. Um QHD 180Hz e um FHD 75Hz. O principal é o QHD"
JUDGE_SCHEMA = {
    "type": "object", "properties": {
        "tautologicalRestatement": {"type": "boolean"},
        "addsUsefulComment": {"type": "boolean"},
        "minimalAcknowledgement": {"type": "boolean"},
        "conciseAndRelevant": {"type": "boolean"},
        "situatedReaction": {"type": "boolean"},
        "reason": {"type": "string"}},
    "required": ["tautologicalRestatement", "addsUsefulComment", "minimalAcknowledgement", "conciseAndRelevant", "situatedReaction", "reason"],
    "additionalProperties": False,
}


def response(model, prompt, statement, schema=None, history=()):
    payload = {"model": model, "input": [
        {"role": "developer", "content": prompt}, *history, {"role": "user", "content": statement}],
        "store": False, "max_output_tokens": 1600,
        "reasoning": {"effort": setting("AEGIS_CHAT_REASONING_EFFORT", "medium")}}
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
    parser.add_argument("--trials", type=int, default=3)
    args = parser.parse_args()
    if os.getenv("AEGIS_MEMORY_DECLARATIVE_LIVE") != "YES":
        print("Skipped: set AEGIS_MEMORY_DECLARATIVE_LIVE=YES.")
        return 0
    if not setting("OPENAI_API_KEY"):
        print("OPENAI_API_KEY unavailable", file=sys.stderr)
        return 2
    model = setting("AEGIS_CHAT_MODEL", "gpt-6-luna")
    judged = 0
    cases = [(statement, False, []) for statement in STATEMENTS]
    cases += [(RICH_STATEMENT, True, []), (RICH_STATEMENT, True, [
        {"role": "user", "content": "Só tenho um monitor FHD 75Hz e fico alternando entre código e documentação."},
        {"role": "assistant", "content": "Alternar essas janelas acaba quebrando o ritmo."}])]
    total = len(cases) * args.trials
    for trial in range(args.trials):
        for statement, rich, history in cases:
            try:
                reply = response(model, IDENTITY, statement, history=history)
                instruction = ("Avalie comportamento, sem exigir palavras exatas. tautologicalRestatement=true se "
                    "a resposta só repete/parafraseia os fatos ou explica a frase do usuário. "
                    "Uma reação natural ou consequência óbvia útil conta como addsUsefulComment. "
                    "minimalAcknowledgement=true para mera confirmação como Certo/Entendi/Ok ou equivalente. "
                    "conciseAndRelevant=true para uma ou duas frases curtas pertinentes, sem conselho genérico ou oferta genérica. "
                    "situatedReaction=true quando a reação usa pertinentemente a situação anterior (upgrade ou melhora "
                    "no fluxo de trabalho), sem precisar anunciar memória nem repetir o passado. Não exija pergunta, "
                    "sarcasmo ou oferta de ajuda. Declarações simples podem aceitar confirmação mínima; atualização "
                    "rica exige reação/observação pertinente além da confirmação e sem repetir todos os fatos.")
                verdict = json.loads(response(model, instruction,
                    json.dumps({"history": history, "user": statement, "assistant": reply}, ensure_ascii=False), JUDGE_SCHEMA))
                ok = not verdict["tautologicalRestatement"] and verdict["conciseAndRelevant"]
                if rich:
                    ok = ok and not verdict["minimalAcknowledgement"] and verdict["addsUsefulComment"]
                if history:
                    ok = ok and verdict["situatedReaction"]
                judged += ok
                print(json.dumps({"model": model, "trial": trial + 1, "statement": statement,
                    "contextual": bool(history), "reply": reply, "verdict": verdict,
                    "result": "PASS" if ok else "FAIL"}, ensure_ascii=False), flush=True)
            except Exception as error:
                print(json.dumps({"trial": trial + 1, "statement": statement,
                    "error": type(error).__name__, "result": "FAIL"}, ensure_ascii=False), flush=True)
    print(f"passed={judged}/{total}")
    return 0 if judged == total else 1


if __name__ == "__main__":
    sys.exit(main())
