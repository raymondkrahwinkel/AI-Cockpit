import datetime
import json
import os
import sys


LOG_PATH = os.environ.get("COCKPIT_HOURS_STUB_LOG", "hours-stub.jsonl")
SEED = json.loads(os.environ.get("COCKPIT_HOURS_STUB_SEED", "[]"))
ENTRIES = list(SEED)


def write_message(message):
    sys.stdout.write(json.dumps(message, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def log_call(name, arguments):
    row = {
        "at": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "tool": name,
        "arguments": arguments,
    }
    with open(LOG_PATH, "a", encoding="utf-8") as log:
        log.write(json.dumps(row, ensure_ascii=False) + "\n")


def tool_result(request_id, value):
    return {
        "jsonrpc": "2.0",
        "id": request_id,
        "result": {"content": [{"type": "text", "text": json.dumps(value, ensure_ascii=False)}]},
    }


def handle(message):
    method = message.get("method")
    request_id = message.get("id")
    if method == "initialize":
        return {
            "jsonrpc": "2.0",
            "id": request_id,
            "result": {
                "protocolVersion": "2024-11-05",
                "capabilities": {"tools": {}},
                "serverInfo": {"name": "hours-booking-stub", "version": "1.0.0"},
            },
        }
    if method == "tools/list":
        return {
            "jsonrpc": "2.0",
            "id": request_id,
            "result": {
                "tools": [
                    {
                        "name": "book_entry",
                        "description": "Records one proposed hours entry in the local test log.",
                        "inputSchema": {
                            "type": "object",
                            "properties": {
                                "date": {"type": "string"},
                                "duration_minutes": {"type": "integer", "minimum": 1},
                                "contact_or_project": {"type": "string"},
                                "description": {"type": "string"},
                            },
                            "required": ["date", "duration_minutes", "contact_or_project", "description"],
                            "additionalProperties": False,
                        },
                    },
                    {
                        "name": "list_entries",
                        "description": "Lists seeded and locally booked entries for the requested date range.",
                        "inputSchema": {
                            "type": "object",
                            "properties": {"from": {"type": "string"}, "to": {"type": "string"}},
                            "additionalProperties": False,
                        },
                    },
                ]
            },
        }
    if method == "tools/call":
        params = message.get("params", {})
        name = params.get("name")
        arguments = params.get("arguments", {})
        log_call(name, arguments)
        if name == "book_entry":
            ENTRIES.append(arguments)
            return tool_result(request_id, {"ok": True, "entry": arguments})
        if name == "list_entries":
            return tool_result(request_id, {"entries": ENTRIES})
        return {
            "jsonrpc": "2.0",
            "id": request_id,
            "error": {"code": -32601, "message": f"Unknown tool: {name}"},
        }
    if request_id is None:
        return None
    return {
        "jsonrpc": "2.0",
        "id": request_id,
        "error": {"code": -32601, "message": f"Unknown method: {method}"},
    }


for line in sys.stdin:
    try:
        response = handle(json.loads(line))
        if response is not None:
            write_message(response)
    except Exception as exception:
        write_message({"jsonrpc": "2.0", "id": None, "error": {"code": -32603, "message": str(exception)}})
