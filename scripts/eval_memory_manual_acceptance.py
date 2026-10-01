#!/usr/bin/env python3
"""Assertive replay of the literal v0.6.0 manual acceptance messages.

Requires an already running disposable API, database, Qdrant collection and Neo4j instance.
The API must expose opt-in diagnostics and identify itself as isolated. No production API
or default Qdrant collection is accepted. The seed is written through MemoryService.
"""

import json
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

from eval_memory_declarative import JUDGE_SCHEMA, response as model_response
from eval_memory_extraction import setting

ROOT = Path(__file__).resolve().parents[1]
FIXTURE = json.loads((ROOT / "scripts/fixtures/memory_v060_manual_acceptance.json").read_text())
BASE = os.getenv("AEGIS_MEMORY_EVAL_BASE_URL", "").rstrip("/")
SEMANTIC_SCHEMA = {"type": "object", "properties": {
    "satisfiesExpectation": {"type": "boolean"}, "reason": {"type": "string"}},
    "required": ["satisfiesExpectation", "reason"], "additionalProperties": False}
EXPECTED = {
    ("001", 1): "FaZe", ("002", 1): "FaZe",
    ("003", 1): "seed Forgotten; job Suppressed",
    ("004", 1): "sem FaZe persistida", ("004", 2): "FaZe Active; sem papagaio",
    ("005", 1): "FaZe", ("006", 1): "FaZe",
    ("007", 1): "Pedro desenvolve Aegis; Active",
    ("007", 2): "Aegis usa PostgreSQL; Active",
    ("008", 1): "PostgreSQL; vínculo Pedro–Aegis",
    ("009", 1): "16 GB Active; sem papagaio",
    ("009", 2): "transition; 16 GB Active fechado; 32 GB Active",
    ("010", 1): "32 GB atual; sem fatos alheios",
    ("011", 1): "32 GB atual; 16 GB passado",
    ("012", 1): "4K/144 inicial Active",
    ("012", 2): "correct; 4K Superseded; QHD/180 Active",
    ("013", 1): "QHD/180; sem 4K/144 verdadeiro",
    ("014", 1): "nega história 4K/144 falsa",
    ("014", 2): "QHD/180; não afirma 4K/144 verdadeiro",
    ("015", 1): "DarkZero segundo favorito Active; FaZe permanece Active",
    ("016", 1): "DarkZero", ("016", 2): "DarkZero Forgotten; job Suppressed",
    ("017", 1): "sem DarkZero", ("018", 1): "sem DarkZero",
    ("019", 1): "sem DarkZero, monitor, Aegis, RAM ou Vecna",
    ("020", 1): "DarkZero Active novamente",
    ("021", 1): "Vecna alias único de Pedro; Active",
    ("022", 1): "Vecna",
}
CANONICAL_CASES = {("003", 1), ("004", 2), ("007", 1), ("007", 2),
    ("009", 1), ("009", 2), ("012", 1), ("012", 2), ("015", 1),
    ("016", 2), ("020", 1), ("021", 1)}


def api(method, path, payload=None):
    request = urllib.request.Request(BASE + path,
        data=None if payload is None else json.dumps(payload).encode(),
        headers={"Content-Type": "application/json"}, method=method)
    with urllib.request.urlopen(request, timeout=120) as response:
        return json.load(response)


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def inspect(message_id, query=None):
    detail = {}
    try:
        detail["message"] = api("GET", "/api/memory/diagnostics/messages/" + message_id)
    except Exception as error:
        detail["messageError"] = type(error).__name__
    if query:
        try:
            detail["retrieval"] = api("POST", "/api/memory/diagnostics/retrieval",
                                      {"query": query, "limit": 10})
        except Exception as error:
            detail["retrievalError"] = type(error).__name__
    return detail


def poll_terminal(message_id, timeout=100):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        detail = inspect(message_id)["message"]
        job = detail.get("extractionJob")
        if job and job["status"] in ("Completed", "Suppressed", "Failed"):
            require(job["status"] != "Failed", "extraction terminal failure: " + str(job.get("lastError")))
            return detail
        time.sleep(0.4)
    raise AssertionError("extraction job did not reach a terminal state")


def poll_projection(message_id, timeout=100):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        detail = inspect(message_id)["message"]
        jobs = detail.get("projectionJobs", [])
        if jobs and all(x["status"] == "Completed" for x in jobs):
            return detail
        if any(x["status"] == "Failed" for x in jobs):
            raise AssertionError("projection failed: " + json.dumps(jobs, ensure_ascii=False))
        time.sleep(0.4)
    raise AssertionError("projection jobs did not converge")


def contents(detail):
    return " ".join(x["content"] for x in detail.get("memoriesFromThisMessage", [])).lower()


def final_contents(trace):
    return " ".join(x["content"] for x in trace["final"]["memories"]).lower()


def activity_contents(activity):
    return " ".join(item for section in (activity or {}).get("sections", [])
                    for item in section.get("items", [])).lower()


def response_check(number, turn, response):
    answer = response.lower()
    positive = {
        ("001", 1): ["faze"], ("002", 1): ["faze"], ("005", 1): ["faze"],
        ("006", 1): ["faze"], ("008", 1): ["postgresql"],
        ("010", 1): ["32"], ("011", 1): ["32", "16"],
        ("013", 1): ["qhd", "180"], ("014", 2): ["qhd", "180"],
        ("016", 1): ["darkzero"], ("022", 1): ["vecna"],
    }.get((number, turn), [])
    require(all(x in answer for x in positive), "response missing: " + ", ".join(positive))
    negative = {
        ("004", 1): ["faze"],
        ("017", 1): ["darkzero"],
        ("018", 1): ["darkzero"], ("019", 1): ["darkzero"],
    }.get((number, turn), [])
    require(not any(x in answer for x in negative), "response asserted forbidden fact")
    if (number, turn) == ("014", 1):
        require(bool(re.search(r"\b(não|nunca|incorreto|errado)\b", answer)),
                "response did not deny false monitor history")
    if (number, turn) in (("003", 1), ("016", 2)):
        require(bool(re.search(r"apag|esquec|remov|exclu", answer)),
                "response did not indicate deletion")
    nuanced = {
        ("011", 1): "A resposta afirma 32 GB como RAM atual e 16 GB como RAM passada, sem inverter as épocas.",
        ("014", 1): "A resposta nega semanticamente que o monitor tenha sido 4K 144 Hz; não apresenta essa especificação como história verdadeira.",
        ("014", 2): "A resposta afirma QHD 180 Hz como especificação atual e não afirma que 4K 144 Hz tenha sido uma especificação verdadeira. Uma menção negada a 4K 144 Hz é permitida.",
        ("019", 1): "A resposta não afirma que DarkZero ou outro segundo time seja um fato conhecido ou recuperado.",
    }.get((number, turn))
    if (number, turn) == ("013", 1) and re.search(r"\b4k\b|\b144\s*hz\b", answer, re.I):
        nuanced = ("A resposta afirma QHD e 180 Hz como especificações do monitor e não afirma "
                   "4K ou 144 Hz como especificações verdadeiras. Menções negadas são permitidas.")
    if nuanced:
        instruction = ("Verifique objetivamente se a resposta satisfaz a expectativa. "
            "Seja rigoroso com negação, tempo verbal e atribuição de fato conhecido. "
            "Retorne apenas o JSON estruturado.")
        verdict = json.loads(model_response(setting("AEGIS_CHAT_MODEL", "gpt-6-luna"), instruction,
            json.dumps({"expectation": nuanced, "assistant": response}, ensure_ascii=False), SEMANTIC_SCHEMA))
        require(verdict["satisfiesExpectation"], "response semantics failed: " + verdict["reason"])


def declarative_check(number, turn, message, reply):
    cases = {("004", 2), ("007", 1), ("007", 2), ("009", 1), ("009", 2),
             ("012", 1), ("015", 1), ("020", 1), ("021", 1)}
    if (number, turn) not in cases:
        return
    if len(reply.strip().split()) <= 2 and not re.search(r"\b(16|32|4k|144|180)\b", reply.lower()):
        return
    instruction = ("Avalie se a resposta da assistente apenas repete ou parafraseia a declaração "
        "factual do usuário sem acrescentar comentário pertinente e não óbvio. "
        "Sarcasmo ou observação natural com informação nova é aceitável. "
        "Marque tautologicalRestatement=true se a resposta só reformula o fato, mesmo com 'Entendi'.")
    verdict = json.loads(model_response(setting("AEGIS_CHAT_MODEL", "gpt-6-luna"), instruction,
        json.dumps({"user": message, "assistant": reply}, ensure_ascii=False), JUDGE_SCHEMA))
    require(not verdict["tautologicalRestatement"], "tautological restatement: " + verdict["reason"])


def canonical_check(number, turn, detail, saved, seed_id):
    facts = detail.get("memoriesFromThisMessage", [])
    text = contents(detail)
    expected = {
        ("004", 2): ["faze"], ("007", 1): ["aegis"],
        ("007", 2): ["postgresql"], ("009", 1): ["16", "ram"],
        ("009", 2): ["32", "ram"], ("012", 1): ["4k", "144"],
        ("012", 2): ["qhd", "180"], ("015", 1): ["darkzero"],
        ("020", 1): ["darkzero"], ("021", 1): ["vecna"],
    }.get((number, turn))
    if expected:
        require(all(x in text for x in expected), "canonical fact missing: " + ", ".join(expected))
        require(any(x["status"] == "Active" for x in facts), "canonical fact is not Active")
        saved[(number, turn)] = detail["source"]["userMessageId"]
    if (number, turn) == ("003", 1):
        seed = inspect(seed_id)["message"]["memoriesFromThisMessage"]
        require(any(x["status"] == "Forgotten" for x in seed), "seed was not forgotten")
        require(detail["extractionJob"]["status"] == "Suppressed", "forget extraction not suppressed")
    if (number, turn) == ("009", 2):
        old = inspect(saved[("009", 1)])["message"]["memoriesFromThisMessage"]
        prior = next((x for x in old if "16" in x["content"] and x["status"] == "Active" and
                      x["validUntil"]), None)
        current = next((x for x in facts if "32" in x["content"] and
                        x["status"] == "Active" and x["validFrom"]), None)
        require(prior is not None, "16 GB was not closed as Active history")
        require(current is not None, "32 GB has no transition start")
        require(prior["validUntil"] == current["validFrom"],
                "RAM transition has a gap or overlap between canonical intervals")
        require(detail["extractionJob"]["transitioned"] > 0, "extractor did not transition")
    if (number, turn) == ("012", 2):
        old = inspect(saved[("012", 1)])["message"]["memoriesFromThisMessage"]
        correction = next((x for x in old if x["status"] == "Superseded"), None)
        replacement = next((x for x in facts if x["status"] == "Active" and
                            "qhd" in x["content"].lower()), None)
        require(correction is not None, "4K was not Superseded")
        require(replacement is not None and correction["supersededById"] == replacement["id"],
                "4K correction does not point to the active QHD replacement")
        require(not re.search(r"\b4k\b|\b144\s*hz\b", replacement["content"], re.I),
                "active corrected fact repeats the false 4K/144 specification")
        require(detail["extractionJob"]["corrected"] > 0, "extractor did not correct")
    if (number, turn) == ("015", 1):
        first = inspect(saved[("004", 2)])["message"]["memoriesFromThisMessage"]
        require(any("faze" in x["content"].lower() and x["status"] == "Active" and
                    x["validUntil"] is None for x in first),
                "second favorite incorrectly closed the first favorite")
        require(detail["extractionJob"]["created"] > 0 and
                detail["extractionJob"]["transitioned"] == 0,
                "second favorite was incorrectly extracted as a transition")
    if (number, turn) == ("016", 2):
        old = inspect(saved[("015", 1)])["message"]["memoriesFromThisMessage"]
        require(any(x["status"] == "Forgotten" for x in old), "DarkZero was not forgotten")
        require(detail["extractionJob"]["status"] == "Suppressed", "forget extraction not suppressed")
        relations = inspect(saved[("015", 1)])["message"]["relationsFromTheseMemories"]
        require(not any(x["status"] == "Active" for x in relations),
                "relation exclusively supported by forgotten DarkZero remains Active")
        historical = api("POST", "/api/memory/diagnostics/retrieval",
                         {"query": "Depois da FaZe, qual time de R6 eu mais gosto?",
                          "asOf": inspect(saved[("015", 1)])["message"]["source"]["createdAt"], "limit": 10})
        require("darkzero" not in final_contents(historical), "forgotten DarkZero survived historical retrieval")
    if (number, turn) == ("021", 1):
        aliases = api("GET", "/api/memory/diagnostics/entities/aliases/Vecna")
        require(len(aliases) == 1 and aliases[0]["canonicalName"] == "Pedro",
                "Vecna is not uniquely an alias of Pedro")


def retrieval_check(number, turn, trace, activity):
    text = final_contents(trace)
    positive = {
        ("001", 1): ["faze"], ("002", 1): ["faze"],
        ("005", 1): ["faze"], ("006", 1): ["faze"],
        ("008", 1): ["postgresql", "aegis"],
        ("010", 1): ["32", "ram"], ("011", 1): ["32", "16", "ram"],
        ("013", 1): ["qhd", "180"], ("014", 2): ["qhd", "180"],
        ("016", 1): ["darkzero"], ("022", 1): ["vecna"],
    }.get((number, turn), [])
    require(all(x in text for x in positive), "retrieval missing: " + ", ".join(positive))
    if (number, turn) == ("008", 1):
        develops_memory = any("aegis" in x["content"].lower() and
            ("pedro" in x["content"].lower() or "desenvolv" in x["content"].lower())
            for x in trace["final"]["memories"])
        develops_relation = any(x["predicate"] == "DEVELOPS" and
            {x["subject"].lower(), x["object"].lower()} == {"pedro", "aegis"}
            for x in trace["graph"]["anchorRelations"])
        require(develops_memory or develops_relation,
                "retrieval lacks Pedro develops Aegis knowledge")
    negative = {
        ("010", 1): ["monitor", "faze", "aegis", "vecna"],
        ("017", 1): ["darkzero"], ("018", 1): ["darkzero"],
        ("019", 1): ["darkzero", "monitor", "aegis", "postgresql", "ram", "vecna"],
    }.get((number, turn), [])
    require(not any(x in text for x in negative), "retrieval included forbidden fact")
    if (number, turn) == ("013", 1):
        require(not re.search(r"\b4k\b|\b144\s*hz\b", text, re.I),
                "retrieval included the false 4K/144 Hz monitor specification")
    if (number, turn) == ("019", 1):
        activity_text = activity_contents(activity)
        require(not any(x in activity_text for x in negative), "Memory Activity included unrelated fact")
    if (number, turn) in (("008", 1), ("010", 1), ("013", 1)):
        activity_text = activity_contents(activity)
        forbidden = {"008": ("monitor", "ram", "faze", "vecna"),
                     "010": ("monitor", "faze", "aegis", "postgresql", "vecna"),
                     "013": ("faze", "aegis", "postgresql", "ram", "vecna")}[number]
        require(not any(x in activity_text for x in forbidden),
                "Memory Activity included unrelated fact")


def seed():
    connection = os.getenv("AEGIS_MEMORY_ACCEPTANCE_DATABASE", "")
    require("Database=aegis_memory_v060_" in connection or "database=aegis_memory_v060_" in connection,
            "disposable AEGIS_MEMORY_ACCEPTANCE_DATABASE is required")
    project = os.getenv("AEGIS_MEMORY_EVAL_COMPOSE_PROJECT", "")
    if project:
        require(project.startswith("aegis_memory_v060_"), "invalid disposable Compose project")
        command = ["docker", "compose", "--project-directory", str(ROOT), "-f",
            str(ROOT / "scripts/compose.memory-acceptance.yml"), "-p", project,
            "--profile", "seed", "run", "--rm", "seed"]
    else:
        command = ["docker", "run", "--rm", "--network", "host", "-e",
            "AEGIS_MEMORY_ACCEPTANCE_DATABASE",
            "-v", str(ROOT) + ":/src", "-w", "/src", "mcr.microsoft.com/dotnet/sdk:8.0",
            "dotnet", "run", "--project", "scripts/Aegis.MemoryAcceptanceSeed", "--configuration", "Release"]
    environment = os.environ.copy()
    environment["AEGIS_MEMORY_ACCEPTANCE_DATABASE"] = connection
    completed = subprocess.run(command, capture_output=True, text=True, check=False, env=environment)
    if completed.returncode != 0:
        detail = (completed.stderr + "\n" + completed.stdout).replace(connection, "[REDACTED]")
        raise RuntimeError("disposable seed command failed (exit " + str(completed.returncode) +
            "): " + " | ".join(detail.splitlines()[-8:]))
    output = completed.stdout
    return json.loads(output.strip().splitlines()[-1])


def main():
    if os.getenv("AEGIS_MEMORY_ACCEPTANCE_LIVE") != "YES":
        print("Skipped: set AEGIS_MEMORY_ACCEPTANCE_LIVE=YES for isolated live replay.")
        return 0
    require(BASE and not BASE.endswith(":8090"), "set an isolated AEGIS_MEMORY_EVAL_BASE_URL")
    environment = api("GET", "/api/memory/diagnostics/environment")
    require(environment["isolated"] and environment["database"].startswith("aegis_memory_v060_")
            and environment["qdrantCollection"].startswith("aegis_memory_v060_"),
            "API is not using disposable Memory resources")
    require(environment["memoryCount"] == 0 and environment["extractionJobCount"] == 0,
            "manual replay requires a clean disposable canonical store")
    require(os.getenv("AEGIS_MEMORY_EVAL_DISPOSABLE_NEO4J") == "YES",
            "disposable Neo4j confirmation is required")
    seed_info = seed()
    poll_projection(seed_info["userMessageId"])
    rows = []
    saved = {}
    failures = 0
    for conversation in FIXTURE["conversations"]:
        conversation_id = None
        number = conversation["id"]
        for turn, message in enumerate(conversation["messages"], 1):
            response = ""; user_id = None; detail = None; trace = None; activity = None
            canonical = "—"; retrieval = "—"; activity_result = "—"; checks = []
            try:
                result = api("POST", "/api/chat/messages", {"conversationId": conversation_id,
                    "content": message})
                conversation_id = result["conversationId"]
                response = result["message"]["content"]
                assistant_id = result["message"]["id"]
                history = api("GET", "/api/chat/conversations/" + conversation_id)["messages"]
                user_id = [x["id"] for x in history if x["role"] == "user"][-1]
                detail = poll_terminal(user_id)
                require(detail["extractionJob"]["status"] in ("Completed", "Suppressed"),
                        "extraction not terminal")
                if detail["memoriesFromThisMessage"]:
                    detail = poll_projection(user_id)
                activity = api("GET", "/api/chat/messages/" + assistant_id + "/memory-activity")
                response_check(number, turn, response)
                declarative_check(number, turn, message, response)
                checks.append("response")
                canonical_check(number, turn, detail, saved, seed_info["userMessageId"])
                prior_projection = {("009", 2): ("009", 1), ("012", 2): ("012", 1),
                                    ("016", 2): ("015", 1)}.get((number, turn))
                if prior_projection:
                    poll_projection(saved[prior_projection])
                if (number, turn) in CANONICAL_CASES:
                    canonical = "PASS"; checks.append("canonical")
                is_search = message.endswith("?") or number in ("017", "018", "019")
                if is_search:
                    trace = api("POST", "/api/memory/diagnostics/retrieval",
                                {"query": message, "limit": 10})
                    retrieval_check(number, turn, trace, activity)
                    retrieval = "PASS"; checks.append("retrieval")
                activity_result = "PASS"; checks.append("activity")
                status = "PASS"
            except Exception as error:
                failures += 1
                status = "FAIL: " + str(error)
                print(json.dumps({"conversation": number, "exactUserMessage": message,
                    "assistantResponse": response, "checksPassed": checks,
                    "memoryActivity": activity, "diagnostics": inspect(user_id, message if message.endswith("?") else None)
                        if user_id else None}, ensure_ascii=False), file=sys.stderr)
            rows.append((number, message, EXPECTED[(number, turn)], response,
                         canonical, retrieval, activity_result, status))
    print("| Conversation | Message | Expected | Response | Canonical | Retrieval | Activity | Result |")
    print("| --- | --- | --- | --- | --- | --- | --- | --- |")
    for row in rows:
        print("| " + " | ".join(str(x).replace("|", "\\|").replace("\n", " ") for x in row) + " |")
    print(f"manual replay: {len(rows)-failures}/{len(rows)} passed")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print("manual replay setup failed:", str(error), file=sys.stderr)
        sys.exit(2)
