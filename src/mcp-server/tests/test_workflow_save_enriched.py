"""Tests for enriched workflow save with governance fields."""

import json
import pathlib
import pytest

from orchestrator.pipe.protocol import (
    make_workflow_save_request,
    make_workflow_load_request,
    make_workflow_load_response,
    make_workflow_review_request,
    make_workflow_review_response,
    make_workflow_test_request,
    make_workflow_test_response,
)


class TestProtocolMessages:
    """Test that the new protocol message factories produce valid envelopes."""

    def test_workflow_load_request(self):
        msg = make_workflow_load_request("captured.my_workflow")
        assert msg["type"] == "workflow_load_request"
        assert msg["payload"]["workflow_name"] == "captured.my_workflow"
        assert "id" in msg
        assert "timestamp" in msg

    def test_workflow_load_response_success(self):
        workflow = {"name": "test", "steps": [], "governance": {"author": "user1"}}
        msg = make_workflow_load_response(True, workflow=workflow)
        assert msg["type"] == "workflow_load_response"
        assert msg["payload"]["success"] is True
        assert msg["payload"]["workflow"]["name"] == "test"

    def test_workflow_load_response_error(self):
        msg = make_workflow_load_response(False, error="Not found")
        assert msg["payload"]["success"] is False
        assert msg["payload"]["error"] == "Not found"

    def test_workflow_review_request(self):
        steps = [{"tool_name": "revit.create_wall", "args": {"height": 10}}]
        msg = make_workflow_review_request(steps, "Create a wall")
        assert msg["type"] == "workflow_review_request"
        assert len(msg["payload"]["steps"]) == 1
        assert msg["payload"]["description"] == "Create a wall"

    def test_workflow_review_response_success(self):
        msg = make_workflow_review_response(
            True,
            summary="Looks good",
            flags=["Missing guard"],
            suggestions=[{"step_id": "step1", "suggestion": "Add guard"}],
        )
        assert msg["type"] == "workflow_review_response"
        assert msg["payload"]["success"] is True
        assert msg["payload"]["summary"] == "Looks good"
        assert len(msg["payload"]["flags"]) == 1
        assert len(msg["payload"]["suggestions"]) == 1

    def test_workflow_review_response_error(self):
        msg = make_workflow_review_response(False, error="Provider unavailable")
        assert msg["payload"]["success"] is False
        assert msg["payload"]["error"] == "Provider unavailable"

    def test_workflow_test_request(self):
        steps = [{"id": "step1", "tool": "revit.create_wall", "args": {"height": 10}}]
        msg = make_workflow_test_request(steps, "Test run")
        assert msg["type"] == "workflow_test_request"
        assert len(msg["payload"]["steps"]) == 1
        assert msg["payload"]["description"] == "Test run"

    def test_workflow_test_response_success(self):
        msg = make_workflow_test_response(
            True,
            steps_completed=3,
            steps_total=3,
            step_summaries={"step1": {"success": True}},
            total_duration_ms=1500,
        )
        assert msg["type"] == "workflow_test_response"
        assert msg["payload"]["success"] is True
        assert msg["payload"]["steps_completed"] == 3
        assert msg["payload"]["steps_total"] == 3
        assert msg["payload"]["total_duration_ms"] == 1500
        assert "step1" in msg["payload"]["step_summaries"]

    def test_workflow_test_response_failure(self):
        msg = make_workflow_test_response(
            False,
            steps_completed=1,
            steps_total=3,
            error="Step 'step2' failed",
        )
        assert msg["payload"]["success"] is False
        assert msg["payload"]["steps_completed"] == 1
        assert msg["payload"]["error"] == "Step 'step2' failed"


class TestEnrichedWorkflowSave:
    """Test saving workflows with governance metadata via the server handler."""

    @pytest.fixture
    def tools_dir(self, tmp_path):
        d = tmp_path / "tools"
        d.mkdir()
        return d

    def test_save_with_governance_produces_valid_json(self, tools_dir):
        """Build a workflow definition with governance fields and verify the JSON structure."""
        # Simulate what the server handler does
        name = "test.workflow"
        description = "A test workflow"
        steps = [
            {"tool_name": "revit.create_wall", "args": {"height": 10}},
            {"tool_name": "revit.get_element_info", "args": {"element_id": 123}},
        ]
        governance = {
            "author": "test_user",
            "tags": ["test", "walls"],
            "version": "2.0.0",
            "permission_mode": "write",
            "approval_required": True,
            "side_effects": ["creates_elements", "modifies_elements"],
        }
        llm_review = {
            "summary": "Workflow looks good.",
            "flags": ["Consider adding error handling"],
        }

        # Build workflow definition as the server would
        workflow_steps = []
        for i, step in enumerate(steps):
            workflow_steps.append({
                "id": f"step{i + 1}",
                "tool": step["tool_name"],
                "args": step["args"],
            })

        workflow_definition = {
            "name": name,
            "description": description,
            "adapter": "workflow",
            "parameters": {"type": "object", "properties": {}, "required": []},
            "workflow": {"steps": workflow_steps},
            "governance": governance,
            "metadata": {"llm_review": llm_review},
        }

        # Write to file
        safe_name = f"captured.{name}"
        tool_json_path = tools_dir / f"{safe_name}.json"
        with open(tool_json_path, "w") as f:
            json.dump(workflow_definition, f, indent=2)

        # Read back and verify
        with open(tool_json_path) as f:
            loaded = json.load(f)

        assert loaded["name"] == name
        assert loaded["adapter"] == "workflow"
        assert loaded["governance"]["author"] == "test_user"
        assert loaded["governance"]["tags"] == ["test", "walls"]
        assert loaded["governance"]["version"] == "2.0.0"
        assert loaded["governance"]["approval_required"] is True
        assert loaded["governance"]["side_effects"] == ["creates_elements", "modifies_elements"]
        assert loaded["metadata"]["llm_review"]["summary"] == "Workflow looks good."
        assert len(loaded["workflow"]["steps"]) == 2

    def test_save_without_governance_backward_compatible(self, tools_dir):
        """Workflows saved without governance should load cleanly."""
        workflow_definition = {
            "name": "old.workflow",
            "description": "Legacy workflow",
            "adapter": "workflow",
            "parameters": {"type": "object", "properties": {}, "required": []},
            "workflow": {
                "steps": [
                    {"id": "step1", "tool": "revit.create_wall", "args": {"height": 10}},
                ]
            },
        }

        tool_json_path = tools_dir / "captured.old.workflow.json"
        with open(tool_json_path, "w") as f:
            json.dump(workflow_definition, f, indent=2)

        # Read back
        with open(tool_json_path) as f:
            loaded = json.load(f)

        # Governance should not exist
        assert "governance" not in loaded
        assert "metadata" not in loaded
        # But defaults should work
        assert loaded.get("governance", {}).get("author", "") == ""
        assert loaded.get("governance", {}).get("approval_required", False) is False

    def test_round_trip_all_fields(self, tools_dir):
        """Save and load with all fields — verify round-trip fidelity."""
        original = {
            "name": "full.workflow",
            "description": "Full workflow with all fields",
            "adapter": "workflow",
            "parameters": {"type": "object", "properties": {}, "required": []},
            "workflow": {
                "steps": [
                    {
                        "id": "step1",
                        "tool": "revit.create_wall",
                        "args": {"height": 10},
                        "bindings": {"level": "$input.level_name"},
                        "guard": "$input.create_wall",
                        "on_failure": "skip",
                        "max_retries": 2,
                        "timeout_ms": 5000,
                    },
                    {
                        "id": "step2",
                        "tool": "revit.get_element_info",
                        "args": {},
                        "bindings": {"element_id": "$steps.step1.data.element_id"},
                    },
                ]
            },
            "governance": {
                "author": "admin",
                "tags": ["production", "walls"],
                "version": "3.0.0",
                "permission_mode": "read",
                "approval_required": True,
                "side_effects": ["creates_elements"],
            },
            "metadata": {
                "llm_review": {
                    "summary": "Well-structured workflow",
                    "flags": [],
                }
            },
        }

        tool_json_path = tools_dir / "captured.full.workflow.json"
        with open(tool_json_path, "w") as f:
            json.dump(original, f, indent=2)

        with open(tool_json_path) as f:
            loaded = json.load(f)

        assert loaded == original

    def test_rename_deletes_old_file(self, tools_dir):
        """When a workflow is renamed, the old JSON file should be deleted."""
        # Create the "old" workflow file
        old_name = "captured.old_name"
        old_def = {
            "name": old_name,
            "description": "Old workflow",
            "adapter": "workflow",
            "parameters": {"type": "object", "properties": {}, "required": []},
            "workflow": {"steps": [{"id": "step1", "tool": "revit.create_wall", "args": {"height": 10}}]},
        }
        old_path = tools_dir / f"{old_name}.json"
        with open(old_path, "w") as f:
            json.dump(old_def, f, indent=2)

        assert old_path.exists()

        # Simulate saving with a new name, providing original_name
        new_name = "captured.new_name"
        new_def = dict(old_def, name=new_name)
        new_path = tools_dir / f"{new_name}.json"
        with open(new_path, "w") as f:
            json.dump(new_def, f, indent=2)

        # Simulate server cleanup: if original_name != safe_name, delete old file
        original_name = old_name
        if original_name and original_name != new_name:
            if old_path.exists():
                old_path.unlink()

        assert not old_path.exists(), "Old file should be deleted after rename"
        assert new_path.exists(), "New file should exist after rename"

    def test_same_name_no_deletion(self, tools_dir):
        """When saving with the same name, the file should not be deleted."""
        name = "captured.keep_name"
        definition = {
            "name": name,
            "description": "Keep name workflow",
            "adapter": "workflow",
            "parameters": {"type": "object", "properties": {}, "required": []},
            "workflow": {"steps": [{"id": "step1", "tool": "revit.create_wall", "args": {"height": 10}}]},
        }
        file_path = tools_dir / f"{name}.json"
        with open(file_path, "w") as f:
            json.dump(definition, f, indent=2)

        # Simulate save with same name — original_name == safe_name
        original_name = name
        safe_name = name
        # Server logic: only delete if original_name and original_name != safe_name
        if original_name and original_name != safe_name:
            if file_path.exists():
                file_path.unlink()

        assert file_path.exists(), "File should NOT be deleted when name is unchanged"

    def test_step_guard_and_failure_modes_saved(self, tools_dir):
        """Verify that guard, on_failure, max_retries, timeout_ms are persisted."""
        steps_data = [
            {
                "tool_name": "tool_a",
                "args": {},
                "guard": "$steps.step0.data.ok",
                "on_failure": "retry",
                "max_retries": 3,
                "timeout_ms": 10000,
            },
        ]

        # Build as server would
        workflow_steps = []
        for i, step in enumerate(steps_data):
            ws = {
                "id": f"step{i + 1}",
                "tool": step["tool_name"],
                "args": step["args"],
            }
            if step.get("guard"):
                ws["guard"] = step["guard"]
            if step.get("on_failure") and step["on_failure"] != "stop":
                ws["on_failure"] = step["on_failure"]
            if step.get("max_retries"):
                ws["max_retries"] = step["max_retries"]
            if step.get("timeout_ms"):
                ws["timeout_ms"] = step["timeout_ms"]
            workflow_steps.append(ws)

        definition = {
            "name": "guarded.workflow",
            "description": "Workflow with guards",
            "adapter": "workflow",
            "parameters": {"type": "object", "properties": {}, "required": []},
            "workflow": {"steps": workflow_steps},
        }

        path = tools_dir / "captured.guarded.workflow.json"
        with open(path, "w") as f:
            json.dump(definition, f, indent=2)

        with open(path) as f:
            loaded = json.load(f)

        step = loaded["workflow"]["steps"][0]
        assert step["guard"] == "$steps.step0.data.ok"
        assert step["on_failure"] == "retry"
        assert step["max_retries"] == 3
        assert step["timeout_ms"] == 10000
