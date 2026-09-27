#!/usr/bin/env python3
"""Read-only live tool-selection eval using the production Gmail + Calendar tool catalog."""

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
EXISTING_NOTE = "Preciso levar os exames."
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
    ("Confere se chegou algum convite do GitHub por email hoje.", "email", "email_search"),
    ("Vê se chegou.", "clarify", None),
    ("Confere aquela mensagem para mim.", "clarify", None),
    ("Pode resolver isso?", "clarify", None),
    ("confirmo", "pending", "email_confirm_pending_action"),
    ("O que tenho amanhã?", "calendar_read", "calendar_list_events"),
    ("Tenho alguma coisa sexta à tarde?", "calendar_read", "calendar_list_events"),
    ("Quando é minha próxima coisa marcada?", "calendar_read", "calendar_list_events"),
    ("Procura a prova de Grafos na minha agenda.", "calendar_read", "calendar_list_events"),
    ("Marca dentista amanhã às 14h por uma hora.", "calendar_create", "calendar_create_event"),
    ("Quais agendas eu tenho?", "calendar_calendars", "calendar_list_calendars"),
    ("Marca dentista amanhã às 14h por uma hora no Diario.", "calendar_secondary", "calendar_create_event"),
    ("Cria o evento Futebol amanhã das 10h às 11h no Family.", "calendar_secondary", "calendar_create_event"),
    ("Marca uma revisão amanhã das 10h às 11h no calendário da faculdade.", "calendar_destination_clarify", "calendar_list_calendars"),
    ("Marca dentista amanhã às 14h.", "calendar_clarify", None),
    ("Muda aquele compromisso para 16h.", "calendar_update", "calendar_update_event"),
    ("Cancela essa reunião.", "calendar_delete", "calendar_delete_event"),
    ("sim", "calendar_pending", "calendar_confirm_pending_action"),
    ("Tenho algum compromisso no dia 10 de outubro?", "calendar_holiday_read", "calendar_list_events"),
    ("Marca estudo dia 10 de outubro às 14h por uma hora.", "calendar_holiday_create", "calendar_create_event"),
    ("Move aquela reunião para 10 de outubro das 16h às 17h.", "calendar_holiday_update", "calendar_update_event"),
    ("Leia e resuma o último email sobre a apresentação no Gmail e me ajude a organizar esse compromisso.", "cross", "email_read"),
]

# These variations exercise model intent, never backend text matching.
for message in ["sim", "pode", "manda bala", "vai", "faz", "beleza", "confirmo", "pode criar", "é isso aí"]:
    CASES.append((message, "calendar_pending", "calendar_confirm_pending_action"))
    # Gmail currently marks messages; it does not create messages/events.
    CASES.append(("pode marcar" if message == "pode criar" else message, "email_pending", "email_confirm_pending_action"))
# A bare "pode criar" can be a colloquial acceptance of the visible proposal.
# This control explicitly requests a different operation, so confirming Gmail is wrong.
CASES.append(("Cria um evento na agenda em vez de marcar o email como lido.", "email_confirmation_clarify", None))
for message in ["não", "deixa", "deixa quieto", "cancela", "melhor não", "esquece", "não faz isso"]:
    CASES.append((message, "calendar_cancel_pending", "calendar_cancel_pending_action"))
    CASES.append((message, "email_cancel_pending", "email_cancel_pending_action"))
for message in ["não, coloca amanhã", "troca pra sexta", "faz às 16h em vez disso", "coloca dia 27", "muda o nome pra X"]:
    CASES.append((message, "calendar_replace", "calendar_amend_pending_action"))
CASES.append(("Cancele a marcação do dia 28 de outubro e faça uma pro dia 27.", "calendar_replace", "calendar_amend_pending_action"))
for message in ["tanto faz", "qualquer horário", "você escolhe"]:
    CASES.append((message, "calendar_delegate_time", "calendar_create_event"))
for message in ["só coloca alguma coisa", "não sei, decide aí", "faz como achar melhor"]:
    CASES.append((message, "calendar_delegate_end", "calendar_create_event"))
CASES.extend([
    ("Festa de Halloween dia 31 de outubro, começa às 19h. Não sei quando acaba, coloca qualquer coisa.", "calendar_delegate_direct", "calendar_create_event"),
    ("Cria um evento de teste dia 29 de outubro. Tanto faz o horário.", "calendar_delegate_direct", "calendar_create_event"),
    ("Marca dentista amanhã às 14h, tanto faz a duração.", "calendar_delegate_direct", "calendar_create_event"),
    ("Ainda não sei.", "calendar_missing_end", None),
    ("Manda bala.", "clarify", None),
])
CASES.extend([
    ("Marca amanhã às 14h.", "calendar_reminder_create_default", "calendar_create_event"),
    ("Marca amanhã às 14h mas me lembra só uma hora antes.", "calendar_reminder_create_custom", "calendar_create_event"),
    ("Marca amanhã às 14h sem lembrete.", "calendar_reminder_create_none", "calendar_create_event"),
    ("Marca amanhã às 14h usando o padrão de lembretes da agenda.", "calendar_reminder_create_calendar", "calendar_create_event"),
    ("Cria como dia inteiro.", "calendar_reminder_create_all_day", "calendar_create_event"),
    ("Coloca os lembretes normais da Aegis nesse evento.", "calendar_reminder_aegis", "calendar_update_event"),
    ("Coloca os lembretes normais.", "calendar_reminder_aegis", "calendar_update_event"),
    ("Usa o padrão da agenda para os lembretes desse evento.", "calendar_reminder_calendar", "calendar_update_event"),
    ("Tira os lembretes desse evento.", "calendar_reminder_none", "calendar_update_event"),
    ("Me lembra desse evento 3 dias antes e na hora.", "calendar_reminder_custom", "calendar_update_event"),
    ("Me lembra desse evento só 3 dias antes e na hora.", "calendar_reminder_custom_only", "calendar_update_event"),
    ("Me lembra uma semana antes também.", "calendar_reminder_add", "calendar_update_event"),
    ("Muda esse evento pra 16h.", "calendar_reminder_move", "calendar_update_event"),
    ("Quais são os lembretes desse evento?", "calendar_reminder_read", "calendar_get_event"),
    ("Quais são os lembretes padrão do calendário Diario?", "calendar_reminder_defaults_read", "calendar_list_calendars"),
])
CASES.extend([
    ("Marca X amanhã às 14h.", "calendar_note_create_empty", "calendar_create_event"),
    ("Marca X amanhã às 14h e anota que preciso levar os exames.", "calendar_note_create_explicit", "calendar_create_event"),
    ("Anota que preciso chegar 15 minutos antes naquele evento.", "calendar_note_set", "calendar_update_event"),
    ("Adiciona na anotação que tenho que levar documento.", "calendar_note_append", "calendar_update_event"),
    ("Troca a anotação para 'levar RG e comprovante'.", "calendar_note_replace", "calendar_update_event"),
    ("Remove a anotação desse evento.", "calendar_note_clear", "calendar_update_event"),
    ("Muda o evento para 16h.", "calendar_note_move", "calendar_update_event"),
    ("Qual é a anotação dessa reunião?", "calendar_note_read", "calendar_get_event"),
    ("Troca a anotação para 'levar RG e comprovante'.", "calendar_note_pending", "calendar_amend_pending_action"),
])


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
    if len(names) != 24 or names != sorted(names) or len(set(names)) != len(names):
        raise ValueError(f"Expected 24 sorted production tools, got {names!r}")
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


def valid_calendar_values(values: dict) -> bool:
    try:
        if not isinstance(values.get("summary"), str) or not values["summary"].strip() or not isinstance(values.get("allDay"), bool):
            return False
        start = datetime.fromisoformat(values["start"].replace("Z", "+00:00"))
        end = datetime.fromisoformat(values["end"].replace("Z", "+00:00"))
        if values["allDay"]:
            return len(values["start"]) == len(values["end"]) == 10 and end >= start and values.get("timeZone") is None
        return start.tzinfo is not None and end.tzinfo is not None and end > start
    except (KeyError, TypeError, ValueError):
        return False


def reminder_result(values: dict, creating: bool) -> dict | None:
    """Stub the backend policy; production defaults are covered by backend tests."""
    mode = values.get("reminderMode", "aegis_default" if creating else "keep")
    custom = values.get("reminders")
    if mode not in {"keep", "aegis_default", "calendar_default", "custom", "none"} or creating and mode == "keep":
        raise ValueError("Invalid reminderMode")
    if mode != "custom" and custom is not None:
        raise ValueError("Only custom accepts overrides")
    if mode == "keep":
        return None
    if mode == "calendar_default":
        return {"useDefault": True}
    if mode == "none":
        return {"useDefault": False, "overrides": []}
    if mode == "aegis_default":
        minutes = [3480, 600, 240] if values.get("allDay") else [4320, 1440, 240, 60, 0]
        return {"useDefault": False, "overrides": [{"method": "popup", "minutes": minute} for minute in minutes]}
    if not isinstance(custom, list) or len(custom) > 5:
        raise ValueError("Custom requires up to five overrides")
    for item in custom:
        if not isinstance(item, dict) or set(item) != {"method", "minutes"} or item["method"] not in {"popup", "email"} or type(item["minutes"]) is not int or not 0 <= item["minutes"] <= 40320:
            raise ValueError("Invalid override")
    if len({(item["method"], item["minutes"]) for item in custom}) != len(custom):
        raise ValueError("Duplicate override")
    return {"useDefault": False, "overrides": custom}


def fake_tool_result(name: str, message: str, kind: str, arguments: dict, state: dict) -> str:
    holiday_calendar = "pt.brazilian#holiday@group.v.calendar.google.com"
    holiday = {"eventId": "evalholiday1", "calendarId": holiday_calendar, "calendarName": "Feriados",
               "type": "holiday", "summary": "Dia da Comunidade", "start": "2026-10-10", "end": "2026-10-10",
               "allDay": True, "timeZone": "America/Sao_Paulo", "status": "confirmed"}
    if name == "email_get_status":
        return json.dumps({"status": {"isConnected": True, "provider": "gmail",
                                      "emailAddress": "eval@example.test"}})
    if name == "email_search":
        if "Leia o último email" not in message and "apresentação" not in message:
            return json.dumps({"emails": [], "totalMatchingCount": 0, "returnedCount": 0})
        return json.dumps({"emails": [{"id": "eval-message-1", "threadId": "eval-thread-1",
                                       "from": "university@example.test", "subject": "Informações",
                                       "snippet": "Resumo de teste", "isUnread": True,
                                       "isStarred": False, "isImportant": True}],
                           "totalMatchingCount": 1, "returnedCount": 1})
    if name == "email_read" and "apresentação" in message:
        return json.dumps({"email": {"id": "eval-message-1", "subject": "Apresentação",
                                      "bodyText": "A apresentação foi marcada para sexta-feira das 14h às 15h, no campus."}})
    if name == "calendar_get_status":
        return json.dumps({"status": {"isConnected": True, "emailAddress": "eval@example.test", "calendarAuthorized": True, "calendarEventsAuthorized": True, "calendarListAuthorized": True}})
    if name == "calendar_list_calendars":
        calendars = [{"calendarId": "eval@example.test", "name": "Principal", "primary": True, "accessRole": "owner"},
                     {"calendarId": "eval-diario", "name": "Diario", "primary": False, "accessRole": "writer"},
                     {"calendarId": "eval-family", "name": "Family", "primary": False, "accessRole": "writer"}]
        if "faculdade" in message.lower():
            calendars.extend([{"calendarId": "eval-faculty-1", "name": "Faculdade", "primary": False, "accessRole": "writer"},
                              {"calendarId": "eval-faculty-2", "name": "Faculdade", "primary": False, "accessRole": "writer"}])
        for calendar in calendars:
            calendar["calendarType"] = "calendar"
            calendar["defaultReminders"] = [{"method": "popup", "minutes": 30}, {"method": "email", "minutes": 90}]
        if kind.startswith("calendar_holiday_"):
            calendars.append({"calendarId": holiday_calendar, "name": "Feriados", "primary": False,
                              "accessRole": "reader", "calendarType": "holiday"})
        return json.dumps({"calendars": calendars})
    if name in {"calendar_list_events", "calendar_get_event"}:
        event = {"eventId": "evalcalendar1", "calendarId": "eval-diario", "calendarName": "Diario", "summary": "Reunião", "start": "2026-10-02T14:00:00-03:00",
                 "end": "2026-10-02T15:00:00-03:00", "allDay": False, "timeZone": "America/Sao_Paulo", "status": "confirmed", "type": "event",
                 "reminders": {"useDefault": False, "overrides": [{"method": "popup", "minutes": 60}, {"method": "email", "minutes": 15}]}}
        if name == "calendar_get_event":
            event["description"] = EXISTING_NOTE
        if kind == "calendar_holiday_read":
            return json.dumps({"events": [], "holidays": [holiday], "hasMore": False, "hasMoreEvents": False, "hasMoreHolidays": False}
                              if name == "calendar_list_events" else {"calendarEvent": holiday})
        return json.dumps({"events": [event], "holidays": [], "hasMore": False, "hasMoreEvents": False, "hasMoreHolidays": False}
                          if name == "calendar_list_events" else {"calendarEvent": event})
    if name in {"calendar_create_event", "calendar_update_event", "calendar_delete_event", "calendar_amend_pending_action"}:
        if name == "calendar_create_event":
            values = arguments.copy()
            values.setdefault("reminderMode", "aegis_default")
        elif name == "calendar_amend_pending_action":
            if not state.get("calendarPending"):
                return json.dumps({"error": "no_pending_calendar_action", "message": "Não há proposta para emendar."})
            values = {**state.get("calendarPending", {}), **arguments}
        else:
            values = {"summary": "Reunião", "start": "2026-10-02T14:00:00-03:00", "end": "2026-10-02T15:00:00-03:00", "allDay": False,
                      "description": EXISTING_NOTE, **arguments}
        if "description" in arguments and (not isinstance(arguments["description"], str) or len(arguments["description"]) > 8000):
            return json.dumps({"error": "invalid_calendar_tool_arguments", "message": "description deve ser string de até 8000 caracteres: omita para preservar, string vazia limpa."})
        if name != "calendar_delete_event" and not valid_calendar_values(values):
            return json.dumps({"error": "invalid_calendar_tool_arguments", "message": "Informe título, início, fim e allDay tecnicamente válidos. Campos omitidos na emenda preservam os anteriores."})
        try:
            reminders = reminder_result(values, name == "calendar_create_event") if name != "calendar_delete_event" else None
        except ValueError as error:
            return json.dumps({"error": "invalid_calendar_tool_arguments", "message": str(error)})
        values["resolvedReminders"] = reminders if reminders is not None else (state.get("calendarPending") or {}).get("resolvedReminders",
            {"useDefault": False, "overrides": [{"method": "popup", "minutes": 60}, {"method": "email", "minutes": 15}]})
        state["calendarPending"] = values
        state["preparedThisTurn"] = True
        warnings = [{"name": holiday["summary"], "date": holiday["start"], "calendarId": holiday_calendar, "calendarName": "Feriados"}]
        human_summary = f"Proposta: {values.get('summary')} de {values.get('start')} até {values.get('end')}"
        if "description" in arguments:
            human_summary += "; anotação: " + (arguments["description"] or "removida")
        return json.dumps({"pendingActionId": "eval-pending", "humanSummary": human_summary,
                           "reminderMode": values.get("reminderMode", "aegis_default" if name == "calendar_create_event" else "keep"), "reminders": reminders,
                           "supersededActionIds": ["eval-old-pending"] if kind == "calendar_replace" else [],
                           "holidayWarnings": warnings if kind in {"calendar_holiday_create", "calendar_holiday_update"} else [],
                           "holidayWarningsHasMore": False,
                           "userMessage": "Ação preparada; exige confirmação em nova mensagem. Nenhuma alteração executada."})
    if name in {"calendar_confirm_pending_action", "email_confirm_pending_action"}:
        integration = "calendarPending" if name.startswith("calendar_") else "emailPending"
        if not state.get(integration):
            return json.dumps({"error": "no_pending_action"})
        if state.get("preparedThisTurn"):
            return json.dumps({"error": "confirmation_required", "message": "Nova proposta exige confirmação em turno posterior."})
        state[integration] = None
        return json.dumps({"verified": True, "userMessage": "Operação simulada confirmada."})
    if name in {"calendar_cancel_pending_action", "email_cancel_pending_action"}:
        integration = "calendarPending" if name.startswith("calendar_") else "emailPending"
        if not state.get(integration):
            return json.dumps({"error": "no_pending_action"})
        state[integration] = None
        return json.dumps({"pendingActionId": "eval-pending", "possibleExternalEffects": False, "userMessage": "Proposta descartada sem execução."})
    if name == "email_read":
        return json.dumps({"email": {"id": "eval-message-1", "threadId": "eval-thread-1",
                                      "subject": "Informações", "bodyText": "Conteúdo de teste."}})
    return json.dumps({"error": "eval_stub_only", "message": "No Google action was executed."})


def output_text(response: dict) -> str:
    return " ".join(
        part.get("text", "") for item in response.get("output", [])
        for part in item.get("content", []) if part.get("type") == "output_text"
    ).strip()


def run_case(key: str, tools: list[dict], message: str, kind: str) -> tuple[list[str], str, list[dict]]:
    input_items = [{"role": "developer", "content": [{"type": "input_text", "text": IDENTITY,
                    "prompt_cache_breakpoint": {"mode": "explicit"}}]}]
    if kind in {"pending", "email_pending", "email_confirmation_clarify"}:
        input_items.extend([
            {"role": "user", "content": "Marque como lido o email que você acabou de mostrar."},
            {"role": "assistant", "content": "Posso marcar esse email como lido. Quer que eu faça?"},
        ])
    if kind in {"calendar_update", "calendar_delete", "calendar_holiday_update"}:
        input_items.extend([
            {"role": "user", "content": "O que tenho sexta?"},
            {"role": "assistant", "content": "Você tem uma reunião sexta, dia 2 de outubro, das 14h às 15h no campus."},
        ])
    if kind.startswith("calendar_reminder_create_"):
        input_items.extend([
            {"role": "user", "content": "Quero criar Teste por uma hora amanhã na agenda principal."},
            {"role": "assistant", "content": "Qual horário você prefere?"},
        ])
    elif kind.startswith("calendar_reminder_"):
        input_items.extend([
            {"role": "user", "content": "O que tenho sexta?"},
            {"role": "assistant", "content": "Você tem uma reunião dia 2 de outubro das 14h às 15h no Diario."},
        ])
    if kind.startswith("calendar_note_create_"):
        input_items.extend([
            {"role": "user", "content": "Quero criar X por uma hora amanhã na agenda principal."},
            {"role": "assistant", "content": "Qual horário você prefere?"},
        ])
    elif kind == "calendar_note_pending":
        input_items.extend([
            {"role": "user", "content": "Prepara X amanhã das 14h às 15h e anota que preciso levar os exames."},
            {"role": "assistant", "content": "Posso criar X amanhã das 14h às 15h na agenda principal. Anotação: Preciso levar os exames. Confirmo?"},
        ])
    elif kind.startswith("calendar_note_"):
        input_items.extend([
            {"role": "user", "content": "O que tenho sexta?"},
            {"role": "assistant", "content": "Você tem uma reunião dia 2 de outubro das 14h às 15h no Diario."},
        ])
    if kind == "calendar_pending":
        input_items.extend([
            {"role": "user", "content": "Marca dentista amanhã às 14h por uma hora."},
            {"role": "assistant", "content": "Posso criar Dentista amanhã das 14h às 15h. Quer que eu coloque na agenda?"},
        ])
    if kind in {"calendar_cancel_pending", "email_cancel_pending"}:
        # A brief withdrawal is contextual: asking permission to proceed can also make
        # Portuguese "deixa" sound like permission. Use a proposal awaiting a decision.
        proposal = "Dentista amanhã, 27/09/2026, das 14h às 15h" if kind.startswith("calendar_") else "Marcar o email mostrado como lido"
        input_items.extend([
            {"role": "user", "content": "Estou em dúvida se quero fazer isso. Prepare a proposta por enquanto."},
            {"role": "assistant", "content": f"Proposta preparada: {proposal}. Nada foi executado. Fico aguardando sua decisão."},
        ])
    if kind == "calendar_replace":
        input_items.extend([
            {"role": "user", "content": "Prepara Entregar encomenda nos Correios dia 28 de outubro das 14h às 15h."},
            {"role": "assistant", "content": "Posso criar Entregar encomenda nos Correios em 28/10/2026 das 14h às 15h, na agenda principal. Confirmo?"},
        ])
    if kind == "calendar_delegate_time":
        input_items.extend([
            {"role": "user", "content": "Cria um evento de teste em 29 de outubro."},
            {"role": "assistant", "content": "Você quer um horário específico ou um evento de dia inteiro?"},
        ])
    if kind in {"calendar_delegate_end", "calendar_missing_end"}:
        input_items.extend([
            {"role": "user", "content": "Vai ter uma Festa de Halloween dia 31 de outubro, começando às 19h. Coloca na agenda."},
            {"role": "assistant", "content": "Qual é o horário de término?"},
        ])
    input_items.append({"role": "user", "content": message})
    # Fixed runtime makes date-sensitive cases reproducible and mirrors the production runtime context.
    now = datetime(2026, 9, 26, 15, 0, tzinfo=timezone.utc)
    runtime = f"Current UTC timestamp: {now.isoformat()}. Current Brasilia timestamp: 2026-09-26T12:00:00-03:00. Reference timezone: America/Sao_Paulo."
    if kind in {"pending", "email_pending", "email_cancel_pending", "email_confirmation_clarify"}:
        runtime += (f"\nExiste uma ação pendente de Gmail do tipo mark_read, válida até "
                    f"{(now + timedelta(minutes=10)).isoformat()}. "
                    "Aceitação: email_confirm_pending_action; desistência: email_cancel_pending_action; correção: prepare uma nova proposta.")
    if kind in {"calendar_pending", "calendar_cancel_pending"}:
        runtime += ("\nAção pendente Calendar: Criar Dentista em 2026-09-27 das 14h às 15h; "
                    "válida até 2026-09-26T15:10:00Z. Aceitação: calendar_confirm_pending_action; desistência: calendar_cancel_pending_action; correção da proposta: calendar_amend_pending_action.")
    if kind == "calendar_replace":
        runtime += ("\nAção pendente Calendar: Criar Entregar encomenda nos Correios em primary: 2026-10-28T14:00:00-03:00 até 2026-10-28T15:00:00-03:00; "
                    "válida até 2026-09-26T15:10:00Z. Não executada, sem possíveis efeitos externos. "
                    "Aceitação: calendar_confirm_pending_action; desistência: calendar_cancel_pending_action; correção da proposta: calendar_amend_pending_action.")
    if kind == "calendar_note_pending":
        runtime += ("\nAção pendente Calendar: Criar X em primary, amanhã 2026-09-27 das 14h às 15h; anotação: Preciso levar os exames. "
                    "Válida até 2026-09-26T15:10:00Z, não executada, sem possíveis efeitos externos. "
                    "Correção da proposta: calendar_amend_pending_action; aceitação: calendar_confirm_pending_action.")
    input_items.append({"role": "developer", "content": "Contexto operacional (use apenas quando relevante):\n" + runtime})
    calls_seen = []
    call_arguments = []
    state = {"calendarPending": None, "emailPending": kind in {"pending", "email_pending", "email_cancel_pending", "email_confirmation_clarify"}}
    if kind == "calendar_note_pending":
        state["calendarPending"] = {"summary": "X", "start": "2026-09-27T14:00:00-03:00", "end": "2026-09-27T15:00:00-03:00",
                                    "allDay": False, "calendarId": "primary", "description": EXISTING_NOTE, "reminderMode": "aegis_default"}
    if kind == "calendar_replace":
        state["calendarPending"] = {"summary": "Entregar encomenda nos Correios", "start": "2026-10-28T14:00:00-03:00",
                                    "end": "2026-10-28T15:00:00-03:00", "allDay": False, "calendarId": "primary", "reminderMode": "aegis_default"}
    elif kind in {"calendar_pending", "calendar_cancel_pending"}:
        state["calendarPending"] = {"summary": "Dentista", "start": "2026-09-27T14:00:00-03:00", "end": "2026-09-27T15:00:00-03:00", "allDay": False}
    final_text = ""
    for _ in range(6):
        response = request_response(key, tools, input_items)
        calls = [item for item in response.get("output", []) if item.get("type") == "function_call"]
        final_text = output_text(response) or final_text
        if not calls:
            break
        input_items.extend(response.get("output", []))
        for call in calls:
            calls_seen.append(call.get("name", "?"))
            arguments = json.loads(call.get("arguments", "{}"))
            result = fake_tool_result(call.get("name", ""), message, kind, arguments, state)
            call_arguments.append({"name": call.get("name"), "arguments": arguments, "result": json.loads(result),
                                   "effectiveValues": (state.get("calendarPending") or {}).copy()})
            input_items.append({"type": "function_call_output", "call_id": call["call_id"],
                                "output": result})
    return calls_seen, final_text, call_arguments


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tools-json", type=Path, help="catalog exported by Aegis.ToolCatalogExport")
    parser.add_argument("--kind", action="append", help="Run only the selected case kind(s).")
    parser.add_argument("--report-json", type=Path, help="Save full responses, arguments and results for review.")
    args = parser.parse_args()
    key = load_key()
    if not key:
        print("OPENAI_API_KEY unavailable; live eval was not run.", file=sys.stderr)
        return 2
    tools = load_tools(args.tools_json)
    print(f"model={MODEL} effort=medium production_tools={len(tools)}", flush=True)
    failures = 0
    cases = [case for case in CASES if not args.kind or case[1] in args.kind]
    report = []
    for message, kind, required_tool in cases:
        try:
            calls, answer, arguments = run_case(key, tools, message, kind)
        except (urllib.error.URLError, TimeoutError) as exc:
            print(f"API error for {message!r}: {type(exc).__name__}: {exc}", file=sys.stderr)
            return 2
        if kind == "none":
            passed = not calls
        elif kind == "email_confirmation_clarify":
            passed = all(name in {"email_get_status", "calendar_get_status", "calendar_list_calendars", "email_cancel_pending_action"} for name in calls) and bool(answer)
        elif kind == "clarify":
            # An imperative request for details is also clarification; no question mark is required.
            passed = not calls and bool(answer)
        elif kind == "email":
            passed = required_tool in calls and all(name in {"email_get_status", "email_search", "email_read", "email_read_thread"} for name in calls)
        elif kind == "calendar_destination_clarify":
            passed = "calendar_list_calendars" in calls and "?" in answer and not any(name in calls for name in {"calendar_create_event", "calendar_confirm_pending_action"})
        elif kind in {"calendar_clarify", "calendar_missing_end"}:
            passed = "?" in answer and not any(name in calls for name in {"calendar_create_event", "calendar_confirm_pending_action"})
        elif kind == "cross":
            passed = required_tool in calls and ("agenda" in answer.lower() or "calendar" in answer.lower() or "calendário" in answer.lower() or any(name.startswith("calendar_") for name in calls))
        elif kind == "calendar_replace":
            allowed = {"calendar_get_status", "calendar_amend_pending_action", "calendar_create_event"}
            passed = any(name in calls for name in {"calendar_amend_pending_action", "calendar_create_event"}) and all(name in allowed for name in calls)
            changes = [call for call in arguments if call["name"] in {"calendar_amend_pending_action", "calendar_create_event"}]
            passed = passed and any("pendingActionId" in call["result"] for call in changes)
            passed = passed and all(call["arguments"].get("calendarId") in {None, "primary"} for call in changes)
            if "nome" in message:
                passed = passed and any(call["arguments"].get("summary") == "X" for call in changes)
            else:
                passed = passed and any("start" in call["arguments"] and "end" in call["arguments"] for call in changes)
            completed = [call["effectiveValues"] for call in changes if "pendingActionId" in call["result"]]
            for values in completed:
                start = datetime.fromisoformat(values["start"])
                end = datetime.fromisoformat(values["end"])
                passed = passed and end - start == timedelta(hours=1)
                if "amanhã" in message:
                    passed = passed and start.date().isoformat() == "2026-09-27"
                elif "sexta" in message:
                    passed = passed and start.weekday() == 4
                elif "16h" in message:
                    passed = passed and start.hour == 16 and start.date().isoformat() == "2026-10-28"
                elif "27" in message:
                    passed = passed and start.date().isoformat() == "2026-10-27"
        elif kind == "calendar_reminder_read":
            # Listing already returns reminders; an extra GET is optional, not required.
            passed = any(name in calls for name in {"calendar_list_events", "calendar_get_event"}) and all(
                name in {"calendar_get_status", "calendar_list_events", "calendar_get_event"} for name in calls)
        elif kind.startswith("calendar_"):
            allowed = {"calendar_get_status", "calendar_list_calendars", "calendar_list_events", "calendar_get_event", required_tool}
            passed = required_tool in calls and all(name in allowed for name in calls)
        else:
            passed = required_tool in calls and all(name in {"email_get_status", required_tool} for name in calls)
        if kind in {"calendar_create", "calendar_holiday_create"} or kind.startswith("calendar_delegate_"):
            passed = passed and all(call["arguments"].get("calendarId") in {None, "primary"} for call in arguments if call["name"] == "calendar_create_event")
            passed = passed and any(valid_calendar_values(call["arguments"]) and "pendingActionId" in call["result"] for call in arguments if call["name"] == "calendar_create_event")
            if kind.startswith("calendar_delegate_"):
                completed = [call["arguments"] for call in arguments if call["name"] == "calendar_create_event" and "pendingActionId" in call["result"]]
                for values in completed:
                    start = datetime.fromisoformat(values["start"])
                    if kind == "calendar_delegate_time" or "teste" in message:
                        passed = passed and start.date().isoformat() == "2026-10-29"
                    elif kind == "calendar_delegate_end" or "Halloween" in message:
                        passed = passed and not values["allDay"] and start.date().isoformat() == "2026-10-31" and start.hour == 19
                    elif "dentista" in message:
                        passed = passed and not values["allDay"] and start.date().isoformat() == "2026-09-27" and start.hour == 14
        elif kind == "calendar_secondary":
            destination = "eval-diario" if "Diario" in message else "eval-family"
            passed = passed and "calendar_list_calendars" in calls and all(call["arguments"].get("calendarId") == destination for call in arguments if call["name"] == "calendar_create_event")
        elif kind in {"calendar_update", "calendar_delete", "calendar_holiday_update"}:
            passed = passed and all(call["arguments"].get("eventId") == "evalcalendar1" and call["arguments"].get("calendarId") in {None, "eval-diario"}
                                    for call in arguments if call["name"] == required_tool)
        if kind.startswith("calendar_holiday_"):
            passed = passed and "feriado" in answer.lower() and "dia da comunidade" in answer.lower()
            if kind != "calendar_holiday_read":
                # "Confirme para criar" is also a valid request for a later confirmation.
                passed = passed and "calendar_confirm_pending_action" not in calls and any("pendingActionId" in call["result"] for call in arguments)
        if kind.startswith("calendar_reminder_"):
            writes = [call for call in arguments if call["name"] in {"calendar_create_event", "calendar_update_event"}]
            if writes:
                passed = passed and all("pendingActionId" in call["result"] for call in writes)
                # Preparation must not execute, cancel, or ask again for a specified reminder preference.
                passed = passed and "calendar_confirm_pending_action" not in calls and "calendar_cancel_pending_action" not in calls
                for call in writes:
                    tool_args = call["arguments"]
                    mode = tool_args.get("reminderMode", "aegis_default" if call["name"] == "calendar_create_event" else "keep")
                    expected_modes = {"calendar_reminder_create_custom": "custom", "calendar_reminder_custom": "custom", "calendar_reminder_custom_only": "custom", "calendar_reminder_add": "custom",
                                      "calendar_reminder_create_none": "none", "calendar_reminder_none": "none",
                                      "calendar_reminder_create_calendar": "calendar_default", "calendar_reminder_calendar": "calendar_default",
                                      "calendar_reminder_aegis": "aegis_default", "calendar_reminder_move": "keep"}
                    passed = passed and mode == expected_modes.get(kind, "aegis_default")
                    if kind == "calendar_reminder_create_custom":
                        passed = passed and tool_args.get("reminders") == [{"method": "popup", "minutes": 60}]
                    elif kind in {"calendar_reminder_custom", "calendar_reminder_custom_only"}:
                        actual = {(r["method"], r["minutes"]) for r in tool_args.get("reminders", [])}
                        desired = {("popup", 0), ("popup", 4320)}
                        # Without "só", retaining existing reminders also satisfies the request.
                        passed = passed and (actual == desired if kind.endswith("only") else desired <= actual <= desired | {("popup", 60), ("email", 15)})
                    elif kind == "calendar_reminder_add":
                        passed = passed and sorted((r["method"], r["minutes"]) for r in tool_args.get("reminders", [])) == [("email", 15), ("popup", 60), ("popup", 10080)]
                    elif kind == "calendar_reminder_move":
                        passed = passed and "reminders" not in tool_args and datetime.fromisoformat(tool_args.get("start", "1970-01-01")).hour == 16
                    if kind.startswith("calendar_reminder_create_"):
                        passed = passed and valid_calendar_values(tool_args) and tool_args.get("calendarId") in {None, "primary"}
                        if kind.endswith("all_day"):
                            passed = passed and tool_args["allDay"] and tool_args["start"] == tool_args["end"] == "2026-09-27"
                        else:
                            start = datetime.fromisoformat(tool_args["start"])
                            end = datetime.fromisoformat(tool_args["end"])
                            passed = passed and not tool_args["allDay"] and start.hour == 14 and start.date().isoformat() == "2026-09-27" and end - start == timedelta(hours=1)
                    else:
                        passed = passed and tool_args.get("eventId") == "evalcalendar1" and tool_args.get("calendarId") in {None, "eval-diario"} and any(name in calls for name in {"calendar_list_events", "calendar_get_event"})
            elif kind == "calendar_reminder_read":
                passed = passed and any(text in answer.lower() for text in ["1h", "uma hora", "1 hora", "60"]) and "15" in answer
            elif kind == "calendar_reminder_defaults_read":
                passed = passed and ("30" in answer or "meia hora" in answer.lower()) and any(text in answer.lower().replace(" ", "")
                    for text in ["90", "1h30", "1horae30", "1horae**30", "1hora30", "1h**30", "1horaemeia", "umahoraemeia"])
        if kind.startswith("calendar_note_"):
            writes = [call for call in arguments if call["name"] in {"calendar_create_event", "calendar_update_event", "calendar_amend_pending_action"}]
            if kind == "calendar_note_read":
                passed = passed and "exames" in answer.lower() and not writes
            else:
                passed = passed and len(writes) == 1 and "pendingActionId" in writes[0]["result"]
                passed = passed and "calendar_confirm_pending_action" not in calls and "calendar_cancel_pending_action" not in calls
                for call in writes:
                    tool_args = call["arguments"]
                    values = call["effectiveValues"]
                    note = values.get("description", "")
                    passed = passed and isinstance(note, str) and len(note) <= 8000
                    passed = passed and tool_args.get("reminderMode", "keep" if call["name"] == "calendar_update_event" else "aegis_default") in {"keep", "aegis_default"} and "reminders" not in tool_args
                    if kind.startswith("calendar_note_create_"):
                        passed = passed and valid_calendar_values(values) and values["summary"] == "X" and tool_args.get("calendarId") in {None, "primary"}
                        if kind == "calendar_note_create_empty":
                            passed = passed and not note and "description" not in tool_args
                        else:
                            passed = passed and "exames" in note.lower() and "levar" in note.lower() and len(note.split()) <= 10
                    elif kind == "calendar_note_move":
                        passed = passed and "description" not in tool_args and note == EXISTING_NOTE
                        passed = passed and datetime.fromisoformat(values["start"]).hour == 16 and datetime.fromisoformat(values["end"]) - datetime.fromisoformat(values["start"]) == timedelta(hours=1)
                    else:
                        allowed_fields = {"description"} if kind == "calendar_note_pending" else {"eventId", "calendarId", "description"}
                        passed = passed and "description" in tool_args and set(tool_args) <= allowed_fields
                        if kind == "calendar_note_set":
                            passed = passed and "15" in note and "antes" in note.lower()
                        elif kind == "calendar_note_append":
                            passed = passed and all(text in note.lower() for text in ["exames", "documento", "levar"])
                            passed = passed and "calendar_get_event" in calls and calls.index("calendar_get_event") < calls.index("calendar_update_event")
                        elif kind in {"calendar_note_replace", "calendar_note_pending"}:
                            passed = passed and "rg" in note.lower() and "comprovante" in note.lower() and "exames" not in note.lower()
                        elif kind == "calendar_note_clear":
                            passed = passed and tool_args["description"] == ""
                    if call["name"] == "calendar_update_event":
                        passed = passed and tool_args.get("eventId") == "evalcalendar1" and tool_args.get("calendarId") in {None, "eval-diario"}
                        if kind != "calendar_note_move":
                            passed = passed and values["start"] == "2026-10-02T14:00:00-03:00" and values["end"] == "2026-10-02T15:00:00-03:00" and values["summary"] == "Reunião"
        if not kind.startswith("calendar_note_"):
            # No note request exists in these fixtures: preparation must not invent one.
            passed = passed and all("description" not in call["arguments"] for call in arguments
                                    if call["name"] in {"calendar_create_event", "calendar_update_event", "calendar_amend_pending_action"})
        failures += not passed
        report.append({"message": message, "kind": kind, "passed": bool(passed), "calls": calls, "arguments": arguments, "answer": answer})
        details = ",".join(calls) or "none"
        excerpt = answer if kind.startswith(("calendar_holiday_", "calendar_delegate_")) or kind == "calendar_replace" else answer[:100]
        print(f"{'PASS' if passed else 'FAIL'} [{kind}] {message} | tools={details} | answer={excerpt!r}", flush=True)
    if args.report_json:
        args.report_json.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    print(f"RESULT {len(cases) - failures}/{len(cases)} passed", flush=True)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
