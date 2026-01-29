# Error Codes

All error codes used in pipe protocol `error` messages and `tool_result` payloads.

| Code                        | Meaning                                    |
|-----------------------------|--------------------------------------------|
| `SCHEMA_VALIDATION_FAILED`  | Args failed JSON Schema check              |
| `TOOL_NOT_FOUND`            | No registered tool with that name          |
| `ADAPTER_NOT_AVAILABLE`     | Target adapter not connected               |
| `REVIT_TRANSACTION_FAILED`  | Transaction.Commit() failed                |
| `REVIT_API_ERROR`           | General Revit API exception                |
| `PIPE_TIMEOUT`              | Named pipe call timed out                  |
| `PIPE_DISCONNECTED`         | Named pipe connection lost                 |
| `PIPE_MESSAGE_TOO_LARGE`    | Message exceeds 16 MiB limit               |
| `HANDLER_ERROR`             | Python handler raised exception            |
| `PYREVIT_SCRIPT_ERROR`      | pyRevit script returned non-zero exit code |
| `DYNAMO_EXECUTION_ERROR`    | Dynamo graph failed                        |
