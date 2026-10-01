#!/usr/bin/env python3
"""Provision, run and remove disposable live v0.6.0 Memory acceptance stacks.

Requires Docker Compose, reachable real models and AEGIS_MEMORY_ACCEPTANCE_LIVE=YES.
Set AEGIS_MEMORY_ACCEPTANCE_FOCUSED_ONLY=YES for only three clean critical trials, or
AEGIS_MEMORY_ACCEPTANCE_REPLAY_ONLY=YES for only the full literal replay and relevance benchmark.
No production Compose project, database, Qdrant collection or Neo4j volume is used.
"""

import json
import os
import secrets
import socket
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

from eval_memory_extraction import setting

ROOT = Path(__file__).resolve().parents[1]
COMPOSE = ROOT / "scripts/compose.memory-acceptance.yml"


def port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def compose(project, environment, *args):
    return subprocess.run(["docker", "compose", "--project-directory", str(ROOT),
        "-f", str(COMPOSE), "-p", project, *args], cwd=ROOT, env=environment,
        check=True)


def stack():
    run_id = secrets.token_hex(5)
    project = "aegis_memory_v060_" + run_id
    environment = os.environ.copy()
    environment.update({
        "AEGIS_MEMORY_EVAL_RUN_ID": run_id,
        "AEGIS_MEMORY_EVAL_DB_PASSWORD": secrets.token_hex(20),
        "AEGIS_MEMORY_EVAL_NEO4J_PASSWORD": secrets.token_hex(20),
        "AEGIS_MEMORY_EVAL_API_PORT": str(port()),
        "OPENAI_API_KEY": setting("OPENAI_API_KEY"),
    })
    if not environment["OPENAI_API_KEY"]:
        raise RuntimeError("OPENAI_API_KEY is unavailable")
    base = "http://127.0.0.1:" + environment["AEGIS_MEMORY_EVAL_API_PORT"]
    return project, environment, base


def wait_ready(base, expected_db, timeout=240):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(base + "/api/memory/diagnostics/environment", timeout=3) as response:
                state = json.load(response)
            if state.get("isolated") and state.get("database") == expected_db and \
                    state.get("qdrantCollection") == expected_db:
                return state
        except Exception:
            pass
        time.sleep(1)  # poll a real API readiness state, not extraction completion
    raise RuntimeError("isolated API did not become ready")


def run_script(name, environment, *args):
    result = subprocess.run([sys.executable, str(ROOT / "scripts" / name), *args],
        cwd=ROOT, env=environment, check=False)
    return result.returncode


def main():
    if os.getenv("AEGIS_MEMORY_ACCEPTANCE_LIVE") != "YES":
        print("Skipped: set AEGIS_MEMORY_ACCEPTANCE_LIVE=YES for paid isolated live acceptance.")
        return 0
    focused_only = os.getenv("AEGIS_MEMORY_ACCEPTANCE_FOCUSED_ONLY") == "YES"
    replay_only = os.getenv("AEGIS_MEMORY_ACCEPTANCE_REPLAY_ONLY") == "YES"
    if focused_only and replay_only:
        print("Choose only one limited acceptance mode.", file=sys.stderr)
        return 2
    active = []
    results = {}
    try:
        if not focused_only:
            project, environment, base = stack()
            active.append((project, environment))
            compose(project, environment, "up", "-d", "--build", "postgres", "qdrant", "neo4j", "api")
            wait_ready(base, project)
            replay_environment = environment.copy()
            replay_environment.update({
                "AEGIS_MEMORY_EVAL_BASE_URL": base,
                "AEGIS_MEMORY_EVAL_ISOLATED": "YES",
                "AEGIS_MEMORY_EVAL_DISPOSABLE_NEO4J": "YES",
                "AEGIS_MEMORY_EVAL_COMPOSE_PROJECT": project,
                "AEGIS_MEMORY_ACCEPTANCE_DATABASE":
                    "Host=postgres;Port=5432;Database=" + project + ";Username=aegis_eval;Password=" +
                    environment["AEGIS_MEMORY_EVAL_DB_PASSWORD"],
            })
            results["manual_001_022"] = run_script("eval_memory_manual_acceptance.py", replay_environment)
            benchmark_environment = replay_environment.copy()
            benchmark_environment["AEGIS_MEMORY_RETRIEVAL_LIVE"] = "YES"
            results["retrieval_relevance"] = run_script("eval_memory_retrieval.py", benchmark_environment)
            compose(project, environment, "down", "-v", "--remove-orphans")
            active.pop()
            if replay_only:
                print(json.dumps({"results": results, "passed": all(code == 0 for code in results.values())}))
                return 0 if all(code == 0 for code in results.values()) else 1

        trial_results = []
        trial_databases = set()
        for index in range(1, 4):
            trial_project, trial_environment, trial_base = stack()
            if trial_project in trial_databases:
                raise RuntimeError("focused trial project was reused")
            trial_databases.add(trial_project)
            active.append((trial_project, trial_environment))
            compose(trial_project, trial_environment, "up", "-d", "--build",
                    "postgres", "qdrant", "neo4j", "api")
            wait_ready(trial_base, trial_project)
            focused_environment = trial_environment.copy()
            focused_environment.update({"AEGIS_MEMORY_EVAL_BASE_URL": trial_base,
                "AEGIS_MEMORY_FOCUSED_TRIAL_INDEX": str(index),
                "AEGIS_MEMORY_EVAL_DISPOSABLE_NEO4J": "YES"})
            trial_results.append(run_script("eval_memory_focused_trials.py", focused_environment))
            compose(trial_project, trial_environment, "down", "-v", "--remove-orphans")
            active.pop()
        results["critical_3_trials"] = 0 if all(code == 0 for code in trial_results) else 1
        print(json.dumps({"focusedTrialExitCodes": trial_results}))
        if focused_only:
            print(json.dumps({"results": results, "passed": results["critical_3_trials"] == 0}))
            return results["critical_3_trials"]
        extraction_environment = environment.copy()
        extraction_environment["AEGIS_MEMORY_TEST_OPENAI"] = "YES"
        results["focused_extraction"] = run_script("eval_memory_extraction.py", extraction_environment)
        declarative_environment = environment.copy()
        declarative_environment["AEGIS_MEMORY_DECLARATIVE_LIVE"] = "YES"
        results["declarative_3_trials"] = run_script("eval_memory_declarative.py",
            declarative_environment, "--trials", "3")
        memory_kinds = ("memory_bulk", "memory_casual", "memory_forget_lookup",
            "memory_forget_second", "memory_remember", "memory_search", "memory_technical",
            "memory_update_observed", "memory_update_unobserved")
        results["focused_intent"] = run_script("eval_tool_intent.py", environment,
            *(part for kind in memory_kinds for part in ("--kind", kind)))
        print(json.dumps({"results": results, "passed": all(code == 0 for code in results.values())}))
        return 0 if all(code == 0 for code in results.values()) else 1
    except Exception as error:
        print("disposable acceptance failed: " + type(error).__name__ + ": " + str(error), file=sys.stderr)
        return 2
    finally:
        for project, environment in reversed(active):
            try:
                compose(project, environment, "down", "-v", "--remove-orphans")
            except Exception as error:
                print("cleanup failed for " + project + ": " + type(error).__name__, file=sys.stderr)


if __name__ == "__main__":
    sys.exit(main())
