"""Tests for workflow input parameterization (promoted parameters)."""

import json
import pathlib
import pytest

from orchestrator.server import _infer_json_schema_type, _handle_workflow_save, _handle_workflow_load
from orchestrator.pipe.protocol import make_workflow_run_request, make_workflow_run_response

from conftest import FakePipeConnection


# ---------------------------------------------------------------------------
# Unit: _infer_json_schema_type
# ---------------------------------------------------------------------------

class TestInferType:
    def test_string(self):
        assert _infer_json_schema_type("hello") == "string"

    def test_integer(self):
        assert _infer_json_schema_type(42) == "integer"

    def test_float(self):
        assert _infer_json_schema_type(3.14) == "number"

    def test_boolean(self):
        assert _infer_json_schema_type(True) == "boolean"
        assert _infer_json_schema_type(False) == "boolean"

    def test_list(self):
        assert _infer_json_schema_type([1, 2, 3]) == "array"

    def test_dict(self):
        assert _infer_json_schema_type({"a": 1}) == "object"

    def test_none(self):
        assert _infer_json_schema_type(None) == "string"


# ---------------------------------------------------------------------------
# Integration: workflow save with promoted parameters
# ---------------------------------------------------------------------------

@pytest.fixture
def tools_dir(tmp_path):
    """Provide a temporary tools directory and patch config."""
    import orchestrator.server as srv
    original = srv.config.tools_dir
    srv.config.tools_dir = tmp_path
    yield tmp_path
    srv.config.tools_dir = original


@pytest.mark.asyncio
async def test_promoted_params_populated_in_save(tools_dir):
    """Promoted parameters should appear in the saved JSON's parameters.properties."""
    conn = FakePipeConnection()
    message = {
        "type": "workflow_save_request",
        "payload": {
            "name": "test.param_workflow",
            "description": "A test workflow",
            "steps": [
                {"tool_name": "revit.create_wall", "args": {"level_name": "L1", "height": 3000}},
            ],
            "promoted_parameters": {
                "level_name": {"type": "string", "description": "Target level", "default": "L1"},
                "height": {"type": "number", "description": "Wall height", "default": 3000},
            },
        },
    }

    await _handle_workflow_save(conn, message)

    # Verify response success
    assert len(conn.messages) == 1
    assert conn.messages[0]["payload"]["success"] is True

    # Read saved JSON
    saved_path = tools_dir / "test.param_workflow.json"
    assert saved_path.exists()
    saved = json.loads(saved_path.read_text())

    props = saved["parameters"]["properties"]
    assert "level_name" in props
    assert props["level_name"]["type"] == "string"
    assert props["level_name"]["default"] == "L1"
    assert "height" in props
    assert props["height"]["type"] == "number"
    assert props["height"]["default"] == 3000

    # required stays empty
    assert saved["parameters"]["required"] == []


@pytest.mark.asyncio
async def test_promoted_params_inject_bindings(tools_dir):
    """Promoted parameters should inject $input.xxx bindings into steps."""
    conn = FakePipeConnection()
    message = {
        "type": "workflow_save_request",
        "payload": {
            "name": "test.binding_workflow",
            "description": "Test bindings",
            "steps": [
                {"tool_name": "revit.create_wall", "args": {"level_name": "L1", "type_name": "Generic"}},
                {"tool_name": "revit.create_wall", "args": {"level_name": "L1", "height": 5000}},
            ],
            "promoted_parameters": {
                "level_name": {"default": "L1"},
            },
        },
    }

    await _handle_workflow_save(conn, message)

    saved_path = tools_dir / "test.binding_workflow.json"
    saved = json.loads(saved_path.read_text())

    steps = saved["workflow"]["steps"]
    # Both steps have level_name → both should get $input.level_name binding
    assert steps[0]["bindings"]["level_name"] == "$input.level_name"
    assert steps[1]["bindings"]["level_name"] == "$input.level_name"
    # height is not promoted, so step 2 should NOT have a binding for it
    assert "height" not in steps[1].get("bindings", {})


@pytest.mark.asyncio
async def test_no_promoted_stays_empty(tools_dir):
    """When no promoted_parameters are sent, parameters.properties stays empty."""
    conn = FakePipeConnection()
    message = {
        "type": "workflow_save_request",
        "payload": {
            "name": "test.empty_params",
            "description": "No promoted params",
            "steps": [
                {"tool_name": "revit.create_wall", "args": {"level_name": "L1"}},
            ],
        },
    }

    await _handle_workflow_save(conn, message)

    saved_path = tools_dir / "test.empty_params.json"
    saved = json.loads(saved_path.read_text())
    assert saved["parameters"]["properties"] == {}
    assert saved["parameters"]["required"] == []


@pytest.mark.asyncio
async def test_workflow_load_returns_parameters(tools_dir):
    """Loading a workflow should return its parameters in the response."""
    import orchestrator.server as srv

    # First save a workflow with parameters
    tool_def = {
        "name": "test.loadable",
        "description": "Loadable workflow",
        "adapter": "workflow",
        "parameters": {
            "type": "object",
            "properties": {
                "level_name": {"type": "string", "default": "L1", "description": "Level"}
            },
            "required": [],
        },
        "workflow": {"steps": [{"id": "step1", "tool": "revit.create_wall", "args": {"level_name": "L1"}}]},
    }
    srv.registry.register(tool_def)

    conn = FakePipeConnection()
    message = {
        "type": "workflow_load_request",
        "payload": {"workflow_name": "test.loadable"},
    }

    await _handle_workflow_load(conn, message)

    assert len(conn.messages) == 1
    resp_payload = conn.messages[0]["payload"]
    assert resp_payload["success"] is True
    wf = resp_payload["workflow"]
    assert "parameters" in wf
    assert "level_name" in wf["parameters"]["properties"]


# ---------------------------------------------------------------------------
# Protocol: workflow_run_request / workflow_run_response
# ---------------------------------------------------------------------------

class TestProtocolMessages:
    def test_workflow_run_request(self):
        msg = make_workflow_run_request("test.wf", {"level_name": "L2"})
        assert msg["type"] == "workflow_run_request"
        assert msg["payload"]["workflow_name"] == "test.wf"
        assert msg["payload"]["input_args"]["level_name"] == "L2"

    def test_workflow_run_response_success(self):
        msg = make_workflow_run_response(True, data={"element_id": 123})
        assert msg["type"] == "workflow_run_response"
        assert msg["payload"]["success"] is True
        assert msg["payload"]["data"]["element_id"] == 123

    def test_workflow_run_response_failure(self):
        msg = make_workflow_run_response(False, error="Something went wrong")
        assert msg["type"] == "workflow_run_response"
        assert msg["payload"]["success"] is False
        assert msg["payload"]["error"] == "Something went wrong"
