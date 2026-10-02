#!/usr/bin/env python3
"""Live bounded tool-loop eval with production catalog and fake operational states; no Google mutations."""
import argparse
import json
import sys
from datetime import datetime, timezone
from eval_memory_extraction import setting
from eval_memory_declarative import response, IDENTITY
import eval_tool_intent as intent

SCHEMA = {"type": "object", "properties": {
    "preservesCapability": {"type": "boolean"},
    "explainsActualBlocker": {"type": "boolean"},
    "noInventedSuccess": {"type": "boolean"},
    "appropriateRecovery": {"type": "boolean"},
    "reason": {"type": "string"}},
    "required": ["preservesCapability", "explainsActualBlocker", "noInventedSuccess", "appropriateRecovery", "reason"],
    "additionalProperties": False}
CASES = [
    ("gmail_disconnected", "Procura o email do professor.", "email", "email_not_connected", "Use email_create_connect_link para conectar Google."),
    ("calendar_disconnected", "Marca uma reunião amanhã às 15h.", "calendar", "calendar_not_connected", "Use calendar_create_connect_link para conectar Google."),
    ("calendar_scope", "Marca uma reunião amanhã às 15h.", "calendar", "calendar_scope_missing", "A conta está conectada, mas falta scope Calendar Events. Use calendar_create_connect_link para reautorizar."),
    ("reminder_notifications", "Me lembra de comprar ração amanhã às 15h.", "reminder", "notifications_unavailable", "Ative notificações na interface e peça novamente; nenhum lembrete criado."),
    ("provider_temporary", "Procura o email do professor.", "email", "email_temporarily_unavailable", "Google autenticado, mas temporariamente indisponível. Tente mais tarde, sem reconectar."),
]


def run_case(key, tools, case):
    label, user, family, code, message = case
    items = [{"role": "developer", "content": IDENTITY},
             {"role": "developer", "content": f"Horário UTC atual: {datetime.now(timezone.utc).isoformat()}. Fuso America/Sao_Paulo."},
             {"role": "user", "content": user}]
    calls = []
    blocked = False
    answer = ""
    clarification = None
    for _ in range(4):  # Same production bound.
        result = intent.request_response(key, tools, items)
        output = result.get("output", [])
        items.extend(output)
        functions = [item for item in output if item.get("type") == "function_call"]
        if not functions:
            answer = "".join(block.get("text", "") for item in output for block in item.get("content", []) if block.get("type") == "output_text")
            if family == "calendar" and not calls and clarification is None:
                clarification = answer
                items.append({"role": "user", "content": "Por uma hora."})
                continue
            break
        for call in functions:
            name = call["name"]
            calls.append(name)
            if name.endswith("create_connect_link"):
                payload = {"authorizationUrl": "https://accounts.google.com/o/oauth2/v2/auth?state=fake-eval"}
            elif name.startswith(family + "_"):
                blocked = True
                payload = {"error": code, "message": message}
            else:
                payload = {"error": "invalid_tool_arguments", "message": "Esta ferramenta não corresponde ao pedido."}
            items.append({"type": "function_call_output", "call_id": call["call_id"], "output": json.dumps(payload)})
    verdict = json.loads(response(intent.MODEL,
        "Avalie a resposta e trajetória segundo o estado fake informado, sem exigir strings exatas. "
        "preservesCapability: não confunde bloqueio com ausência da capacidade. explainsActualBlocker: "
        "explica conexão, scope, notificações ou falha temporária corretamente. noInventedSuccess: não "
        "afirma consulta/criação/entrega que não ocorreu. appropriateRecovery: link de autorização para "
        "desconexão/scope, habilitar notificações para Reminder, tentar depois sem reconectar para falha temporária. "
        "Pode pedir detalhes que faltam, mas ainda deve tratar o bloqueio conhecido.",
        json.dumps({"case": label, "user": user, "state": code, "calls": calls, "answer": answer}, ensure_ascii=False), SCHEMA))
    connect = [name for name in calls if name.endswith("create_connect_link")]
    trajectory = blocked and bool(answer)
    if code in {"email_not_connected", "calendar_not_connected", "calendar_scope_missing"}:
        trajectory = trajectory and family + "_create_connect_link" in connect and "https://accounts.google.com/" in answer
    else:
        trajectory = trajectory and not connect
    ok = trajectory and all(verdict[k] for k in SCHEMA["properties"] if k != "reason")
    return {"case": label, "model": intent.MODEL, "calls": calls, "clarification": clarification, "answer": answer, "verdict": verdict, "result": "PASS" if ok else "FAIL"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--trials", type=int, default=3)
    args = parser.parse_args()
    key = setting("OPENAI_API_KEY")
    if not key:
        print("NOT READY: OPENAI_API_KEY unavailable")
        return 2
    intent.MODEL = setting("AEGIS_CHAT_MODEL", "gpt-6-luna")
    tools = intent.load_tools(None)
    passed = 0
    for trial in range(args.trials):
        for case in CASES:
            try:
                result = run_case(key, tools, case)
            except Exception as error:
                result = {"case": case[0], "result": "FAIL", "error": type(error).__name__}
            result["trial"] = trial + 1
            passed += result["result"] == "PASS"
            print(json.dumps(result, ensure_ascii=False), flush=True)
    total = len(CASES) * args.trials
    print(f"passed={passed}/{total}")
    return 0 if passed == total else 1


if __name__ == "__main__":
    sys.exit(main())
