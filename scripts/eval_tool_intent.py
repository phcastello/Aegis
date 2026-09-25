#!/usr/bin/env python3
"""Manual Luna intent eval. Requires OPENAI_API_KEY; never executes Gmail actions."""

import json
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
MODEL = os.getenv("AEGIS_CHAT_MODEL", "gpt-5.6-luna")
IDENTITY = (ROOT / "backend/src/Aegis.Api/Prompts/aegis_identity.md").read_text()
CASES = [
    ("Meu professor falou que a prova vai ser difícil.", "no_tool"),
    ("Preciso responder uma pergunta da faculdade.", "no_tool"),
    ("O GitHub é uma bagunça às vezes.", "no_tool"),
    ("Tenho um prazo amanhã.", "no_tool"),
    ("O que você acha dessa mensagem que eu escrevi?", "no_tool"),
    ("Veja se meu professor mandou algum email sobre a prova.", "tool"),
    ("Tem algum email não lido importante?", "tool"),
    ("Procura emails do GitHub sobre segurança.", "tool"),
    ("Leia o último email da Unicentro.", "tool"),
    ("Confere se chegou o convite por email.", "tool"),
    ("Vê se chegou.", "clarify"),
    ("Confere aquela mensagem para mim.", "clarify"),
    ("Pode resolver isso?", "clarify"),
]

TOOLS = [
    {
        "type": "function", "name": "email_get_status",
        "description": "Check the current Gmail connection state. Use before Gmail operations when status is unknown.",
        "parameters": {"type": "object", "properties": {}, "additionalProperties": False},
    },
    {
        "type": "function", "name": "email_search",
        "description": "Search the connected Gmail account for messages matching a Gmail query. Requires a clear request to access email.",
        "parameters": {
            "type": "object", "properties": {"query": {"type": "string"}},
            "required": ["query"], "additionalProperties": False,
        },
    },
]


def run_case(api_key: str, message: str) -> tuple[str, str]:
    payload = {
        "model": MODEL,
        "input": [
            {"role": "developer", "content": [{"type": "input_text", "text": IDENTITY,
                "prompt_cache_breakpoint": {"mode": "explicit"}}]},
            {"role": "user", "content": message},
        ],
        "prompt_cache_options": {"mode": "explicit"},
        "tools": TOOLS,
        "tool_choice": "auto",
        "store": False,
    }
    request = urllib.request.Request(
        "https://api.openai.com/v1/responses",
        data=json.dumps(payload).encode(),
        headers={"Authorization": f"Bearer {api_key}", "Content-Type": "application/json"},
    )
    with urllib.request.urlopen(request, timeout=90) as response:
        result = json.load(response)
    calls = [item for item in result.get("output", []) if item.get("type") == "function_call"]
    if calls:
        return "tool", ",".join(item.get("name", "?") for item in calls)
    text = result.get("output_text") or " ".join(
        part.get("text", "") for item in result.get("output", [])
        for part in item.get("content", []) if part.get("type") == "output_text"
    )
    # Clarification is reviewed by a human; a question mark is only a report hint.
    return "no_tool", text.strip().replace("\n", " ")[:120]


def main() -> int:
    key = os.getenv("OPENAI_API_KEY")
    if not key:
        print("OPENAI_API_KEY unavailable; live intent eval was not run.")
        return 2
    failures = 0
    for message, expected in CASES:
        try:
            actual, detail = run_case(key, message)
        except (urllib.error.URLError, TimeoutError) as exc:
            print(f"API error for {message!r}: {exc}", file=sys.stderr)
            return 1
        if expected == "clarify":
            passed = actual == "no_tool" and "?" in detail
            label = "clarify" if passed else actual
        else:
            passed = actual == expected
            label = actual
        failures += not passed
        print(f"{'PASS' if passed else 'FAIL'} [{expected} -> {label}] {message} | {detail}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
