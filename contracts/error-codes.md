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
| `PRECONDITION_FAILED`       | Tool preconditions not met (e.g., no document open) |
| `USER_DENIED`               | User denied the tool execution via approval dialog |
| `BINDING_ERROR`             | Workflow step binding resolution failed    |
| `STEP_TIMEOUT`              | Workflow step exceeded its timeout         |
| `STEP_ERROR`                | Workflow step raised an exception          |
| `WORKFLOW_STEP_FAILED`      | A step in a workflow failed                |
| `WORKFLOW_FAILED`           | Workflow execution failed                  |
| `WORKFLOW_ERROR`            | Declarative workflow engine error          |
| `NO_EVENTS_FOUND`           | No audit events found for replay           |
