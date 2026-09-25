#!/usr/bin/env python3
"""Read-only live tool-selection eval using the production Gmail tool catalog."""

import argparse
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timedelta, timezone
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
IDENTITY = (ROOT / "backend/src/Aegis.Api/Prompts/aegis_identity.md").read_text()
MODEL = "gpt-5.6-luna"
CASES = [
    ("Meu professor falou que a prova vai ser difícil.", "none", None),
    ("Preciso responder uma pergunta da faculdade.", "none", None),
    ("O GitHub é uma bagunça às vezes.", "none", None),
    ("Tenho um prazo amanhã.", "none", None),
    ("O que você acha dessa mensagem que eu escrevi?", "none", None),
    ("Isso é importante para a apresentação.", "none", None),
    ("Marcar pontos no texto ajuda a estudar?", "none", None),
    ("Responda essa pergunta de matemática: quanto é 2 + 2?", "none", None),
    ("Confirma que 2 + 2 = 4?", "none", None),
    ("Veja se meu professor mandou algum email sobre a prova.", "email", "email_search"),
    ("Tem algum email não lido importante?", "email", "email_search"),
    ("Procura emails do GitHub sobre segurança.", "email", "email_search"),
    ("Leia o último email da Unicentro.", "email", "email_read"),
    ("Confere se chegou o convite por email.", "email", "email_search"),
    ("Vê se chegou.", "clarify", None),
    ("Confere aquela mensagem para mim.", "clarify", None),
    ("Pode resolver isso?", "clarify", None),
    ("confirmo", "pending", "email_confirm_pending_action"),
]


def load_key() -> str:
    key = os.getenv("OPENAI_API_KEY", "").strip()
    if key:
        return key
    env_file = ROOT / ".env"
    if env_file.exists():
        for line in env_file.read_text().splitlines():
            if line.startswith("OPENAI_API_KEY="):
                return line.partition("=")[2].strip().strip("\"'")
    return ""


def load_tools(path: Path | None) -> list[dict]:
    if path:
        tools = json.loads(path.read_text())
    else:
        project = ROOT / "scripts/Aegis.ToolCatalogExport/Aegis.ToolCatalogExport.csproj"
        tools = json.loads(subprocess.check_output(
            ["dotnet", "run", "--project", str(project), "--configuration", "Release"],
            cwd=ROOT, text=True,
        ))
    names = [tool["name"] for tool in tools]
    if len(names) != 13 or names != sorted(names) or len(set(names)) != len(names):
        raise ValueError(f"Expected 13 sorted production tools, got {names!r}")
    return tools


def request_response(key: str, tools: list[dict], input_items: list[dict]) -> dict:
    payload = {
        "model": MODEL,
        "reasoning": {"effort": "medium"},
        "input": input_items,
        "prompt_cache_options": {"mode": "implicit"},
        "tools": tools,
        "tool_choice": "auto",
        "parallel_tool_calls": False,
        "max_output_tokens": 4000,
        "store": False,
    }
    request = urllib.request.Request(
        "https://api.openai.com/v1/responses",
        data=json.dumps(payload, ensure_ascii=False).encode(),
        headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"},
    )
    for attempt in range(3):
        try:
            with urllib.request.urlopen(request, timeout=120) as response:
                return json.load(response)
        except (urllib.error.URLError, TimeoutError) as exc:
            if isinstance(exc, urllib.error.HTTPError) and exc.code not in {429, 500, 502, 503, 504}:
                raise
            if attempt == 2:
                raise
            time.sleep(2 ** attempt)
    raise RuntimeError("unreachable")


def fake_tool_result(name: str, message: str) -> str:
    if name == "email_get_status":
        return json.dumps({"status": {"isConnected": True, "provider": "gmail",
                                      "emailAddress": "eval@example.test"}})
    if name == "email_search":
        if "Leia o último email" not in message:
            return json.dumps({"emails": [], "totalMatchingCount": 0, "returnedCount": 0})
        return json.dumps({"emails": [{"id": "eval-message-1", "threadId": "eval-thread-1",
                                       "from": "university@example.test", "subject": "Informações",
                                       "snippet": "Resumo de teste", "isUnread": True,
                                       "isStarred": False, "isImportant": True}],
                           "totalMatchingCount": 1, "returnedCount": 1})
    if name == "email_read":
        return json.dumps({"email": {"id": "eval-message-1", "threadId": "eval-thread-1",
                                      "subject": "Informações", "bodyText": "Conteúdo de teste."}})
    return json.dumps({"error": "eval_stub_only", "message": "No Gmail action was executed."})


def output_text(response: dict) -> str:
    return " ".join(
        part.get("text", "") for item in response.get("output", [])
        for part in item.get("content", []) if part.get("type") == "output_text"
    ).strip()


def run_case(key: str, tools: list[dict], message: str, kind: str) -> tuple[list[str], str]:
    input_items = [{"role": "developer", "content": [{"type": "input_text", "text": IDENTITY,
                    "prompt_cache_breakpoint": {"mode": "explicit"}}]}]
    if kind == "pending":
        input_items.extend([
            {"role": "user", "content": "Marque como lido o email que você acabou de mostrar."},
            {"role": "assistant", "content": "Posso marcar esse email como lido. Responda 'confirmo' em uma nova mensagem para executar."},
        ])
    input_items.append({"role": "user", "content": message})
    now = datetime.now(timezone.utc)
    runtime = f"Horário atual {now.isoformat()}."
    if kind == "pending":
        runtime += (f"\nExiste uma ação pendente de Gmail do tipo mark_read, válida até "
                    f"{(now + timedelta(minutes=10)).isoformat()}. "
                    "Use email_confirm_pending_action somente se a mensagem atual confirmar essa ação; "
                    "o backend valida a confirmação.")
    input_items.append({"role": "developer", "content": "Contexto operacional (use apenas quando relevante):\n" + runtime})
    calls_seen = []
    final_text = ""
    for _ in range(3):
        response = request_response(key, tools, input_items)
        calls = [item for item in response.get("output", []) if item.get("type") == "function_call"]
        final_text = output_text(response) or final_text
        if not calls:
            break
        input_items.extend(response.get("output", []))
        for call in calls:
            calls_seen.append(call.get("name", "?"))
            input_items.append({"type": "function_call_output", "call_id": call["call_id"],
                                "output": fake_tool_result(call.get("name", ""), message)})
    return calls_seen, final_text


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tools-json", type=Path, help="catalog exported by Aegis.ToolCatalogExport")
    args = parser.parse_args()
    key = load_key()
    if not key:
        print("OPENAI_API_KEY unavailable; live eval was not run.", file=sys.stderr)
        return 2
    tools = load_tools(args.tools_json)
    print(f"model={MODEL} effort=medium production_tools={len(tools)}", flush=True)
    failures = 0
    for message, kind, required_tool in CASES:
        try:
            calls, answer = run_case(key, tools, message, kind)
        except (urllib.error.URLError, TimeoutError) as exc:
            print(f"API error for {message!r}: {type(exc).__name__}: {exc}", file=sys.stderr)
            return 2
        if kind == "none":
            passed = not calls
        elif kind == "clarify":
            passed = not calls and "?" in answer
        elif kind == "email":
            passed = required_tool in calls and all(name in {"email_get_status", "email_search", "email_read", "email_read_thread"} for name in calls)
        else:
            passed = required_tool in calls and all(name in {"email_get_status", required_tool} for name in calls)
        failures += not passed
        details = ",".join(calls) or "none"
        print(f"{'PASS' if passed else 'FAIL'} [{kind}] {message} | tools={details} | answer={answer[:100]!r}", flush=True)
    print(f"RESULT {len(CASES) - failures}/{len(CASES)} passed", flush=True)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
