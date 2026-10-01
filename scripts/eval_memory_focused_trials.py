#!/usr/bin/env python3
"""Run critical v0.6.0 conversations once in each of three clean isolated APIs.

Provide three disposable API URLs in AEGIS_MEMORY_FOCUSED_BASE_URLS, separated by commas.
The disposable runner may instead invoke one trial at a time with BASE_URL and TRIAL_INDEX.
Each trial uses an empty PostgreSQL database, Qdrant collection and disposable Neo4j.
This script never retries a failed trial to improve its score.
"""

import json
import os
import sys

import eval_memory_manual_acceptance as replay

DECLARATION_CASES = {("007", 2), ("009", 1), ("009", 2), ("012", 1),
                     ("015", 1), ("021", 1)}
declarations_passed = set()


def check(condition, message):
    if not condition:
        raise AssertionError(message)


def send(message, conversation=None, case=None, saved=None):
    response = ""
    user_id = None
    try:
        result = replay.api("POST", "/api/chat/messages",
                            {"conversationId": conversation, "content": message})
        conversation = result["conversationId"]
        response = result["message"]["content"]
        assistant_id = result["message"]["id"]
        history = replay.api("GET", "/api/chat/conversations/" + conversation)["messages"]
        user_id = [item["id"] for item in history if item["role"] == "user"][-1]
        detail = replay.poll_terminal(user_id)
        if detail["memoriesFromThisMessage"]:
            detail = replay.poll_projection(user_id)
        activity = replay.api("GET", "/api/chat/messages/" + assistant_id + "/memory-activity")
        if case:
            number, turn = case
            replay.response_check(number, turn, response)
            replay.declarative_check(number, turn, message, response)
            if case in DECLARATION_CASES:
                declarations_passed.add(case)
            if saved is not None:
                replay.canonical_check(number, turn, detail, saved, None)
            if message.endswith("?"):
                trace = replay.api("POST", "/api/memory/diagnostics/retrieval",
                                   {"query": message, "limit": 10})
                replay.retrieval_check(number, turn, trace, activity)
        return conversation, user_id
    except Exception:
        print(json.dumps({"exactUserMessage": message, "assistantResponse": response,
            "diagnostics": replay.inspect(user_id, message if message.endswith("?") else None)
                if user_id else None}, ensure_ascii=False), file=sys.stderr)
        raise


def run_trial(index):
    declarations_passed.clear()
    saved = {}
    outcomes = {}
    try:
        conversation, _ = send("Eu desenvolvo você, a Aegis.", case=("007", 1), saved=saved)
        send("A Aegis usa PostgreSQL como fonte canônica do sistema de memória.",
             conversation, ("007", 2), saved)
        send("Qual banco é usado como fonte canônica de memória pelo projeto que eu desenvolvo?",
             case=("008", 1))
        outcomes["A_postgresql"] = "PASS"
    except Exception as error:
        outcomes["A_postgresql"] = "FAIL: " + str(error)
    try:
        conversation, first = send("Meu PC tinha 16 GB de RAM.", case=("009", 1), saved=saved)
        send("Troquei a memória do PC e agora ele tem 32 GB de RAM.", conversation,
             ("009", 2), saved)
        replay.poll_projection(first)
        send("Quanto de RAM meu PC tem hoje?", case=("010", 1))
        send("Quanto de RAM meu PC tem hoje? e quanto tinha no passado?", case=("011", 1))
        outcomes["B_ram"] = "PASS"
    except Exception as error:
        outcomes["B_ram"] = "FAIL: " + str(error)
    try:
        conversation, first = send("Meu monitor é 4K 144 Hz.", case=("012", 1), saved=saved)
        send("Não, eu falei errado. Meu monitor é QHD 180 Hz. Ele nunca foi 4K 144 Hz.",
             conversation, ("012", 2), saved)
        replay.poll_projection(first)
        send("Qual é a resolução e a frequência do meu monitor?", case=("013", 1))
        conversation, _ = send("Meu monitor já foi 4K 144 Hz?", case=("014", 1))
        send("O que você sabe sobre meu monitor?", conversation, ("014", 2))
        outcomes["C_monitor"] = "PASS"
    except Exception as error:
        outcomes["C_monitor"] = "FAIL: " + str(error)
    try:
        send("Meu time favorito de R6 é a FaZe Clan.", case=("004", 2), saved=saved)
        send("Depois da FaZe, meu time de R6 favorito é a DarkZero.", case=("015", 1), saved=saved)
        conversation, _ = send("Depois da FaZe, qual time de R6 eu mais gosto?", case=("016", 1))
        send("Esquece essa informação sobre a DarkZero.", conversation, ("016", 2), saved)
        replay.poll_projection(saved[("015", 1)])
        send("Na faculdade me chamam de Vecna.", case=("021", 1), saved=saved)
        send("Historicamente, qual time eu dizia gostar mais depois da FaZe?", case=("019", 1))
        outcomes["D_retrieval_019"] = "PASS"
    except Exception as error:
        outcomes["D_retrieval_019"] = "FAIL: " + str(error)
    outcomes["E_no_paraphrase"] = "PASS" if declarations_passed == DECLARATION_CASES else \
        "FAIL: missing or tautological declaration responses"
    print(json.dumps({"trial": index, "outcomes": outcomes}, ensure_ascii=False))
    return all(value == "PASS" for value in outcomes.values())


def main():
    if os.getenv("AEGIS_MEMORY_ACCEPTANCE_LIVE") != "YES":
        print("Skipped: set AEGIS_MEMORY_ACCEPTANCE_LIVE=YES.")
        return 0
    urls = [x.strip().rstrip("/") for x in os.getenv("AEGIS_MEMORY_FOCUSED_BASE_URLS", "").split(",") if x.strip()]
    single = os.getenv("AEGIS_MEMORY_EVAL_BASE_URL", "").rstrip("/")
    index = os.getenv("AEGIS_MEMORY_FOCUSED_TRIAL_INDEX", "")
    if single and index in ("1", "2", "3") and not urls:
        urls = [single]
    else:
        check(len(urls) == 3 and len(set(urls)) == 3,
              "three distinct isolated API URLs are required")
    check(os.getenv("AEGIS_MEMORY_EVAL_DISPOSABLE_NEO4J") == "YES",
          "disposable Neo4j instances must be confirmed")
    environments = []
    for url in urls:
        replay.BASE = url
        environment = replay.api("GET", "/api/memory/diagnostics/environment")
        check(environment["isolated"] and environment["database"].startswith("aegis_memory_v060_")
              and environment["qdrantCollection"].startswith("aegis_memory_v060_")
              and environment["memoryCount"] == 0 and environment["extractionJobCount"] == 0,
              "each focused trial requires a clean disposable environment")
        environments.append(environment)
    if len(urls) == 3:
        check(len({x["database"] for x in environments}) == 3 and
              len({x["qdrantCollection"] for x in environments}) == 3,
              "focused trials must use distinct databases and collections")
    passed = 0
    for trial_number, url in enumerate(urls, int(index) if len(urls) == 1 else 1):
        replay.BASE = url
        passed += run_trial(trial_number)
    print(f"critical focused trials: {passed}/{len(urls)}")
    return 0 if passed == len(urls) else 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print("focused trial setup failed: " + str(error), file=sys.stderr)
        sys.exit(2)
