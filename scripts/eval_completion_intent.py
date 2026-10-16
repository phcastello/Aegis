#!/usr/bin/env python3
"""Bounded live intent eval: production catalog/prompt, synthetic tool results only.

Does not execute application tools, send notifications, or contact Google. Uses
the existing inference settings and key loader; never prints credentials.
Function-calling contract: https://developers.openai.com/api/docs/guides/function-calling
"""
import argparse
import json
import re
import unicodedata
import uuid
from datetime import datetime
from pathlib import Path

from eval_tool_intent import IDENTITY, MODEL, load_key, request_response

SERIES = "11111111-1111-4111-8111-111111111111"
OCCURRENCE = "22222222-2222-4222-8222-222222222222"
HOME_SP = "33333333-3333-4333-8333-333333333333"
HOME_GUA = "44444444-4444-4444-8444-444444444444"
CAMPUS = "55555555-5555-4555-8555-555555555555"
RULE = {"frequency": "daily", "interval": 1, "localTime": "08:00", "startDate": "2026-10-15"}
SERIES_VIEW = {"seriesId": SERIES, "text": "X", "timeZoneId": "America/Sao_Paulo", "rule": RULE, "status": "active", "revision": 1, "deliveryPolicy": "allEligible"}
PLACES = [{"id": HOME_SP, "name": "Casa São Paulo", "type": "property", "aliases": ["casa"], "hasArea": True},
          {"id": HOME_GUA, "name": "Casa Guarapuava", "type": "property", "aliases": ["casa"], "hasArea": True},
          {"id": CAMPUS, "name": "Faculdade", "type": "property", "aliases": [], "hasArea": True}]
CASES = [
    ("daily", "Todo dia às oito me lembre de X."),
    ("interval", "A cada três dias às 19h me lembre de X."),
    ("weekly", "Segunda, quarta e sexta às 18h me lembre de X."),
    ("monthly", "Todo dia 5 às 9h me lembre de X."),
    ("last_day", "No último dia de cada mês às 18h me lembre de X, por três ocorrências."),
    ("snooze", "Adia só o de hoje."),
    ("skip", "Pula o próximo."),
    ("pause", "Pausa esse lembrete diário."),
    ("update", "Muda a série para nove da manhã."),
    ("home_exit", "Quando eu sair de casa, me lembra de passar na padaria."),
    ("home_enter", "Quando eu chegar em casa, me lembra de colocar a roupa para lavar."),
    ("campus_exit", "Quando eu sair da faculdade, me lembra de ir ao mercado."),
    ("campus_tomorrow", "Quando eu chegar na faculdade amanhã, me lembra de falar com o professor."),
    ("ambiguous_home", "Quando eu sair de casa, me lembra de passar na padaria."),
]


def normalized(value):
    return "".join(c for c in unicodedata.normalize("NFD", value.lower()) if unicodedata.category(c) != "Mn")


def validate_shape(schema, value):
    assert isinstance(value, dict), "Tool arguments must be an object"
    assert set(schema.get("required", [])) <= value.keys(), "Missing required argument"
    assert value.keys() <= schema.get("properties", {}).keys(), "Unknown argument"


def run_case(key, tools, kind, message, trace):
    catalog = {tool["name"]: tool for tool in tools}
    seen_series = seen_occurrence = False
    calls = []
    trace["calls"] = calls
    writes = []
    answer = ""
    history = [{"role": "developer", "content": IDENTITY},
               {"role": "developer", "content": "Contexto temporal: agora 2026-10-15T12:00:00-03:00, timezone America/Sao_Paulo. Dados deste ensaio são sintéticos; nenhum dado pessoal real está disponível."}]
    if kind in {"snooze", "skip", "pause", "update"}:
        history += [{"role": "user", "content": "Meu lembrete diário de X está salvo?"},
                    {"role": "assistant", "content": "Sim. Há uma série diária de X às 8h; o aviso de hoje já foi disparado."}]
    history.append({"role": "user", "content": message})
    for _ in range(5):
        response = request_response(key, tools, history)
        output = response.get("output", [])
        history.extend(output)
        tool_calls = [item for item in output if item.get("type") == "function_call"]
        if not tool_calls:
            answer = "".join(part.get("text", "") for item in output if item.get("type") == "message" for part in item.get("content", []))
            # "Às oito" can mean 08:00 or 20:00. Exercise the real clarification
            # round trip instead of requiring the model to guess a local hour.
            if kind == "daily" and not writes and "?" in answer and re.search(r"8|oito|20|vinte", normalized(answer)) and "clarification" not in trace:
                trace["clarification"] = answer
                history.append({"role": "user", "content": "Às oito da manhã."})
                continue
            break
        for call in tool_calls:
            name = call["name"]
            values = json.loads(call["arguments"])
            assert name in catalog, "Unknown production tool"
            validate_shape(catalog[name]["parameters"], values)
            calls.append({"name": name, "arguments": values})
            if name == "reminder_series_list":
                seen_series = True
                result = {"series": [SERIES_VIEW]}
            elif name == "reminder_list":
                seen_occurrence = True
                result = {"reminders": [{"reminderId": OCCURRENCE, "seriesId": SERIES, "text": "X", "dueAt": "2026-10-15T08:00:00-03:00", "status": "Triggered", "triggeredAt": "2026-10-15T08:00:02-03:00", "revision": 1, "generation": 1}], "hasMore": False}
            elif name == "reminder_series_create":
                validate_shape(catalog[name]["parameters"]["properties"]["rule"], values["rule"])
                result = {**SERIES_VIEW, **values}
                writes.append({"name": name, **values})
            elif name == "reminder_series_change":
                assert seen_series and values["seriesId"] == SERIES and values["expectedRevision"] == 1, "Series reference was not observed"
                result = {**SERIES_VIEW, **values, "revision": 2}
                writes.append({"name": name, **values})
            elif name == "reminder_action":
                assert seen_occurrence and values["reminderId"] == OCCURRENCE, "Occurrence reference was not observed"
                assert values["expectedRevision"] == values["generation"] == 1
                uuid.UUID(values["actionId"])
                result = {"applied": True, "replayed": False, "conflict": False, "reminder": {"reminderId": OCCURRENCE, "revision": 2, "generation": 2, "status": "Scheduled", "dueAt": "2026-10-15T12:10:00-03:00"}}
                writes.append({"name": name, **values})
            elif name in {"place_resolve", "geographic_reminder_create"}:
                reference = normalized(values["reference"])
                place = PLACES[2] if "faculdade" in reference else PLACES[1] if "guarapuava" in reference else PLACES[0]
                assert "padaria" not in reference and "mercado" not in reference, "Task destination became a trigger"
                if kind == "ambiguous_home":
                    result = {"status": "clarification_required", "error": "place_clarification_required", "created": False, "reason": "location_stale", "candidates": PLACES[:2]}
                elif name == "place_resolve":
                    result = {"status": "resolved", "placeId": place["id"], "reason": "recent_personal_phone_city_context", "locationAgeSeconds": 60, "candidates": [place]}
                else:
                    result = {"created": True, "rule": {"id": str(uuid.uuid4()), "revision": 1, "status": "active", "syncStatus": "pending_sync", "placeIds": [place["id"]], **values}, "places": [place]}
                    writes.append({"name": name, **values})
            elif name == "place_list":
                result = {"places": PLACES}
            elif name == "location_status":
                result = {"sourceSelected": True, "available": False, "ageSeconds": 86400, "place": None}
            else:
                raise AssertionError("Unexpected integration/tool: " + name)
            history.append({"type": "function_call_output", "call_id": call["call_id"], "output": json.dumps(result, ensure_ascii=False)})
    trace["answer"] = answer
    assert answer, "No final answer within five model turns"
    if kind in {"daily", "interval", "weekly", "monthly", "last_day"}:
        assert len(writes) == 1 and writes[0]["name"] == "reminder_series_create"
        args = writes[0]
        assert args["timeZoneId"] == "America/Sao_Paulo" and args.get("deliveryPolicy", "allEligible") == "allEligible"
        rule = args["rule"]
        frequency, hour = {"daily": ("daily", "08:00"), "interval": ("daily", "19:00"), "weekly": ("weekly", "18:00"), "monthly": ("monthly", "09:00"), "last_day": ("monthly", "18:00")}[kind]
        assert rule["frequency"] == frequency and rule["localTime"] == hour
        assert rule["interval"] == (3 if kind == "interval" else 1)
        if kind == "weekly":
            assert sorted(rule["weekdays"]) == [1, 3, 5]
        if kind == "monthly":
            assert rule["monthDay"] == 5 and not rule.get("lastDay", False)
        if kind == "last_day":
            assert rule["lastDay"] and rule["count"] == 3 and rule.get("monthDay") is None
    elif kind in {"skip", "pause", "update"}:
        assert len(writes) == 1 and writes[0]["name"] == "reminder_series_change" and writes[0]["operation"] == kind
        if kind == "update":
            assert writes[0]["rule"] == {**RULE, "localTime": "09:00"}, "Changing the hour must preserve the structured rule"
    elif kind == "snooze":
        assert len(writes) == 1 and writes[0]["name"] == "reminder_action" and writes[0]["action"] == "snooze" and writes[0].get("snoozeMinutes", 10) == 10
    elif kind == "ambiguous_home":
        assert not writes and "?" in answer, "Ambiguous residence needs clarification without saving"
    else:
        assert len(writes) == 1 and writes[0]["name"] == "geographic_reminder_create"
        assert writes[0]["transition"] == ("enter" if kind in {"home_enter", "campus_tomorrow"} else "exit")
        assert not writes[0].get("repeat", False) and not writes[0].get("allCandidates", False)
        assert re.search(r"pend|sincron", normalized(answer)), "Saved rule must not be presented as installed"
        if kind.startswith("home_"):
            assert "casa sao paulo" in normalized(answer), "Resolved residence must be visible"
        if kind == "campus_tomorrow":
            assert datetime.fromisoformat(writes[0]["startsAt"]).date().isoformat() == "2026-10-16"
    return {"case": kind, "message": message, "passed": True, **trace, "calls": calls, "answer": answer}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tools-json", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--case", action="append", default=[])
    args = parser.parse_args()
    tools = json.loads(args.tools_json.read_text())
    names = [tool["name"] for tool in tools]
    assert names == sorted(set(names)), "Production catalog must be sorted and unique"
    assert {"reminder_series_create", "reminder_series_change", "reminder_action", "geographic_reminder_create", "place_resolve"} <= set(names)
    key = load_key()
    if not key:
        raise SystemExit("Existing inference credential unavailable; no live eval executed.")
    results = []
    print(f"model={MODEL} production_tools={len(tools)} tools_simulated=true", flush=True)
    for kind, message in CASES:
        if args.case and kind not in args.case:
            continue
        trace = {}
        try:
            result = run_case(key, tools, kind, message, trace)
        except Exception as error:
            result = {"case": kind, "message": message, "passed": False, **trace, "errorType": type(error).__name__, "reason": str(error) if isinstance(error, AssertionError) else "API/transport failed; credential and response omitted"}
        results.append(result)
        print(f"{'PASS' if result['passed'] else 'FAIL'} {kind}", flush=True)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps({"model": MODEL, "productionTools": len(tools), "scope": "Live inference, production prompt/catalog, synthetic tool results only; no application mutation or native delivery.", "results": results}, ensure_ascii=False, indent=2) + "\n")
    assert results and all(result["passed"] for result in results), "Intent eval did not pass completely; inspect bounded synthetic report"


if __name__ == "__main__":
    main()
