#!/usr/bin/env python3
"""Format the opt-in Memory diagnostics API; retrieval may embed the query server-side."""

import argparse
import json
import os
import sys
import urllib.error
import urllib.request


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default=os.getenv("AEGIS_MEMORY_DIAGNOSTICS_BASE_URL",
        os.getenv("AEGIS_API_BASE_URL", "http://localhost:8090")))
    commands = parser.add_subparsers(dest="command", required=True)
    message = commands.add_parser("message", help="Inspect a user or assistant message ID")
    message.add_argument("id")
    retrieval = commands.add_parser("retrieval", help="Trace a memory query")
    retrieval.add_argument("query")
    retrieval.add_argument("--as-of")
    retrieval.add_argument("--limit", type=int, default=10)
    retrieval.add_argument("--automatic", action="store_true")
    args = parser.parse_args()
    base = args.base_url.rstrip("/") + "/api/memory/diagnostics"
    if args.command == "message":
        request = urllib.request.Request(base + "/messages/" + args.id)
    else:
        payload = {"query": args.query, "asOf": args.as_of, "limit": args.limit,
                   "automatic": args.automatic}
        request = urllib.request.Request(base + "/retrieval", data=json.dumps(payload).encode(),
                                         headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            print(json.dumps(json.load(response), indent=2, ensure_ascii=False))
        return 0
    except urllib.error.HTTPError as error:
        print(f"Diagnostics request failed: HTTP {error.code}", file=sys.stderr)
        if error.code == 404:
            print("Check AEGIS_MEMORY_DIAGNOSTICS_ENABLED and the message ID.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
