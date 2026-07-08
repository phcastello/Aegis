#!/usr/bin/env python3
import json
import subprocess
import sys


PSQL_BASE = [
    "docker",
    "compose",
    "exec",
    "-T",
    "postgres",
    "psql",
    "-U",
    "aegis",
    "-d",
    "aegis",
    "-t",
    "-A",
]


def run_sql(sql: str):
    result = subprocess.run(
        [*PSQL_BASE, "-c", sql],
        check=True,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )

    output = result.stdout.strip()
    if not output:
        return None

    return json.loads(output)


def format_block(label: str, content: str):
    print(f"{label}: {content.strip() if content else ''}")


def try_json(value):
    if value is None:
        return None
    if isinstance(value, (dict, list)):
        return value
    if not isinstance(value, str):
        return None
    try:
        return json.loads(value)
    except json.JSONDecodeError:
        return None


def compact(value, limit=1200):
    if value is None:
        return ""
    if not isinstance(value, str):
        value = json.dumps(value, ensure_ascii=False, indent=2)
    value = value.strip()
    return value if len(value) <= limit else value[: limit - 3].rstrip() + "..."


def print_tool_loop(audit):
    payload = try_json(audit.get("requestPayloadJson"))
    if not isinstance(payload, dict) or payload.get("type") != "tool_loop":
        print("Tools: nenhuma toolcall registrada.")
        if not audit.get("success"):
            print(f"Erro: {audit.get('failureReason') or audit.get('errorType') or 'erro não especificado'}")
        return

    executions = payload.get("toolExecutions") or []
    if not executions:
        print("Tools: tool loop registrado, mas sem execuções.")
    else:
        print("Tools:")
        for index, item in enumerate(executions, start=1):
            print(f"  {index}. {item.get('tool')} success={item.get('success')} iteration={item.get('iteration')}")
            arguments = item.get("arguments")
            if arguments not in (None, {}, []):
                print(f"     args: {compact(arguments, 900)}")
            if item.get("errorCode"):
                print(f"     errorCode: {item.get('errorCode')}")
            content_json = try_json(item.get("content"))
            print(f"     result: {compact(content_json if content_json is not None else item.get('content'), 1400)}")
            if item.get("auditMetadataJson"):
                print(f"     audit: {compact(item.get('auditMetadataJson'), 900)}")

    if not audit.get("success"):
        print(f"Erro: {audit.get('failureReason') or audit.get('errorType') or 'erro não especificado'}")


def print_audits(audits):
    if not audits:
        print("Tools: nenhuma auditoria encontrada para esta resposta.")
        return

    for audit_index, audit in enumerate(audits, start=1):
        if len(audits) > 1:
            print(f"Auditoria #{audit_index}:")
        print(
            "Modelo: "
            f"{audit.get('provider')}/{audit.get('model')} "
            f"success={audit.get('success')} "
            f"durationMs={audit.get('durationMilliseconds')}"
        )
        print_tool_loop(audit)


def main():
    data = run_sql(
        """
        WITH latest_conversation AS (
            SELECT "Id"
            FROM conversations
            WHERE "DeletedAt" IS NULL
            ORDER BY "UpdatedAt" DESC, "Id" DESC
            LIMIT 1
        ),
        messages_json AS (
            SELECT jsonb_agg(
                jsonb_build_object(
                    'id', m."Id",
                    'role', m."Role",
                    'content', m."Content",
                    'createdAt', m."CreatedAt",
                    'model', m."Model",
                    'audits', COALESCE((
                        SELECT jsonb_agg(
                            jsonb_build_object(
                                'provider', a."Provider",
                                'model', a."Model",
                                'success', a."Success",
                                'durationMilliseconds', a."DurationMilliseconds",
                                'httpStatusCode', a."HttpStatusCode",
                                'failureReason', a."FailureReason",
                                'errorType', a."ErrorType",
                                'requestPayloadJson', a."RequestPayloadJson",
                                'responseBody', a."ResponseBody",
                                'createdAt', a."CreatedAt"
                            )
                            ORDER BY a."CreatedAt"
                        )
                        FROM llm_request_audits a
                        WHERE a."AssistantMessageId" = m."Id"
                    ), '[]'::jsonb)
                )
                ORDER BY m."CreatedAt", m."Id"
            ) AS messages
            FROM chat_messages m
            WHERE m."ConversationId" = (SELECT "Id" FROM latest_conversation)
        )
        SELECT jsonb_build_object(
            'conversationId', c."Id",
            'title', c."Title",
            'updatedAt', c."UpdatedAt",
            'messages', COALESCE(m.messages, '[]'::jsonb)
        )
        FROM conversations c
        CROSS JOIN messages_json m
        WHERE c."Id" = (SELECT "Id" FROM latest_conversation);
        """
    )

    if not data:
        print("Nenhuma conversa encontrada.")
        return 0

    print(f"Conversation: {data.get('title')} ({data.get('conversationId')})")
    print(f"UpdatedAt: {data.get('updatedAt')}")
    print()

    for message in data.get("messages", []):
        role = message.get("role")
        if role == "user":
            format_block("User", message.get("content") or "")
            print()
        elif role == "assistant":
            format_block("Aegis", message.get("content") or "")
            print()
            print_audits(message.get("audits") or [])
            print()
            print("-" * 80)
            print()

    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except subprocess.CalledProcessError as error:
        sys.stderr.write(error.stderr or str(error))
        raise SystemExit(error.returncode)
