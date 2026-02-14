"""Tests for the declarative workflow engine."""

import pytest
from unittest.mock import AsyncMock

from orchestrator.dispatcher.result import ToolResult
from orchestrator.workflow.engine import WorkflowEngine


@pytest.fixture
def mock_dispatcher():
    dispatcher = AsyncMock()
    dispatcher.dispatch.return_value = ToolResult.ok({"element_id": 123})
    return dispatcher


class TestWorkflowEngine:
    async def test_two_step_workflow(self, mock_dispatcher):
        workflow_def = {
            "steps": [
                {"id": "step1", "tool": "revit.create_wall", "args": {"height": 10}},
                {"id": "step2", "tool": "revit.get_element_info", "args": {"element_id": 123}},
            ]
        }
        engine = WorkflowEngine(mock_dispatcher)
        result = await engine.execute(workflow_def)

        assert result.success is True
        assert result.steps_completed == 2
        assert result.steps_total == 2
        assert mock_dispatcher.dispatch.call_count == 2

    async def test_binding_between_steps(self, mock_dispatcher):
        mock_dispatcher.dispatch.side_effect = [
            ToolResult.ok({"element_id": 42}),
            ToolResult.ok({"category": "Walls"}),
        ]

        workflow_def = {
            "steps": [
                {"id": "step1", "tool": "revit.create_wall", "args": {"height": 10}},
                {
                    "id": "step2",
                    "tool": "revit.get_element_info",
                    "args": {},
                    "bindings": {"element_id": "$steps.step1.data.element_id"},
                },
            ]
        }

        engine = WorkflowEngine(mock_dispatcher)
        result = await engine.execute(workflow_def)

        assert result.success is True
        # Check that step2 was called with the bound element_id
        call_args = mock_dispatcher.dispatch.call_args_list[1]
        assert call_args[0][1]["element_id"] == 42

    async def test_on_failure_stop(self, mock_dispatcher):
        mock_dispatcher.dispatch.side_effect = [
            ToolResult.fail("ERR", "Failed"),
            ToolResult.ok({"x": 1}),  # Should not be called
        ]

        workflow_def = {
            "steps": [
                {"id": "step1", "tool": "tool_a", "on_failure": "stop"},
                {"id": "step2", "tool": "tool_b"},
            ]
        }

        engine = WorkflowEngine(mock_dispatcher)
        result = await engine.execute(workflow_def)

        assert result.success is False
        assert result.steps_completed == 1
        assert mock_dispatcher.dispatch.call_count == 1

    async def test_on_failure_skip(self, mock_dispatcher):
        mock_dispatcher.dispatch.side_effect = [
            ToolResult.fail("ERR", "Failed"),
            ToolResult.ok({"x": 1}),
        ]

        workflow_def = {
            "steps": [
                {"id": "step1", "tool": "tool_a", "on_failure": "skip"},
                {"id": "step2", "tool": "tool_b"},
            ]
        }

        engine = WorkflowEngine(mock_dispatcher)
        result = await engine.execute(workflow_def)

        assert result.success is True
        assert "step1" in result.skipped_steps
        assert mock_dispatcher.dispatch.call_count == 2

    async def test_guard_skips_step(self, mock_dispatcher):
        mock_dispatcher.dispatch.side_effect = [
            ToolResult.ok({"status": "done"}),
        ]

        workflow_def = {
            "steps": [
                {"id": "step1", "tool": "tool_a"},
                {"id": "step2", "tool": "tool_b", "guard": "$steps.step1.data.missing_field"},
            ]
        }

        engine = WorkflowEngine(mock_dispatcher)
        result = await engine.execute(workflow_def)

        assert result.success is True
        assert "step2" in result.skipped_steps
        assert mock_dispatcher.dispatch.call_count == 1

    async def test_retry_on_failure(self, mock_dispatcher):
        mock_dispatcher.dispatch.side_effect = [
            ToolResult.fail("TEMP", "Temporary failure"),
            ToolResult.ok({"result": "success"}),
        ]

        workflow_def = {
            "steps": [
                {"id": "step1", "tool": "tool_a", "on_failure": "retry", "max_retries": 1},
            ]
        }

        engine = WorkflowEngine(mock_dispatcher)
        result = await engine.execute(workflow_def)

        assert result.success is True
        assert mock_dispatcher.dispatch.call_count == 2

    async def test_auto_step_ids(self, mock_dispatcher):
        """Steps without explicit IDs get auto-generated IDs."""
        workflow_def = {
            "steps": [
                {"tool": "tool_a"},
                {"tool": "tool_b"},
            ]
        }

        engine = WorkflowEngine(mock_dispatcher)
        result = await engine.execute(workflow_def)

        assert result.success is True
        assert "step1" in result.step_results
        assert "step2" in result.step_results

    async def test_input_args_passed_to_context(self, mock_dispatcher):
        mock_dispatcher.dispatch.return_value = ToolResult.ok({})

        workflow_def = {
            "steps": [
                {
                    "id": "step1",
                    "tool": "tool_a",
                    "args": {},
                    "bindings": {"height": "$input.wall_height"},
                },
            ]
        }

        engine = WorkflowEngine(mock_dispatcher)
        result = await engine.execute(workflow_def, input_args={"wall_height": 15})

        assert result.success is True
        call_args = mock_dispatcher.dispatch.call_args_list[0]
        assert call_args[0][1]["height"] == 15

    async def test_governance_metadata_in_audit_log(self, mock_dispatcher):
        """Workflow with governance metadata should include it in the audit event."""
        from unittest.mock import MagicMock

        audit_log = MagicMock()

        workflow_def = {
            "steps": [
                {"id": "step1", "tool": "tool_a", "args": {}},
            ],
            "governance": {
                "author": "test_user",
                "version": "2.0.0",
                "permission_mode": "write",
                "approval_required": True,
                "tags": ["test"],
                "side_effects": ["creates_elements"],
            },
        }

        engine = WorkflowEngine(mock_dispatcher, audit_log=audit_log)
        result = await engine.execute(workflow_def)

        assert result.success is True
        audit_log.log_event.assert_called_once()
        event = audit_log.log_event.call_args[0][0]
        assert event["event_type"] == "workflow_completed"
        assert "governance" in event
        assert event["governance"]["author"] == "test_user"
        assert event["governance"]["version"] == "2.0.0"
        assert event["governance"]["approval_required"] is True
        assert event["governance"]["tags"] == ["test"]
        assert event["governance"]["side_effects"] == ["creates_elements"]

    async def test_no_governance_no_governance_in_audit(self, mock_dispatcher):
        """Workflow without governance metadata should not include it in audit."""
        from unittest.mock import MagicMock

        audit_log = MagicMock()

        workflow_def = {
            "steps": [
                {"id": "step1", "tool": "tool_a", "args": {}},
            ],
        }

        engine = WorkflowEngine(mock_dispatcher, audit_log=audit_log)
        result = await engine.execute(workflow_def)

        assert result.success is True
        audit_log.log_event.assert_called_once()
        event = audit_log.log_event.call_args[0][0]
        assert "governance" not in event

    async def test_duct_workflow_dispatches_create_element(self, mock_dispatcher):
        """A 2-step duct workflow should dispatch revit.create_element for each step."""
        mock_dispatcher.dispatch.side_effect = [
            ToolResult.ok({"element_id": 501}),
            ToolResult.ok({"element_id": 502}),
        ]

        workflow_def = {
            "steps": [
                {
                    "id": "step1",
                    "tool": "revit.create_element",
                    "args": {
                        "start_point": [0, 0, 10],
                        "end_point": [20, 0, 10],
                        "type_name": "Default",
                        "level_name": "Level 1",
                    },
                },
                {
                    "id": "step2",
                    "tool": "revit.create_element",
                    "args": {
                        "start_point": [20, 0, 10],
                        "end_point": [40, 0, 10],
                        "type_name": "Default",
                        "level_name": "Level 1",
                    },
                },
            ]
        }

        engine = WorkflowEngine(mock_dispatcher)
        result = await engine.execute(workflow_def)

        assert result.success is True
        assert result.steps_completed == 2
        assert result.steps_total == 2
        # Verify both dispatched to revit.create_element
        assert mock_dispatcher.dispatch.call_args_list[0][0][0] == "revit.create_element"
        assert mock_dispatcher.dispatch.call_args_list[1][0][0] == "revit.create_element"

    async def test_input_bindings_fallback_to_static_defaults(self, mock_dispatcher):
        """When $input.xxx bindings resolve to None (no input provided),
        static args should be preserved as defaults, not overwritten with None."""
        mock_dispatcher.dispatch.return_value = ToolResult.ok({"element_id": 42})

        workflow_def = {
            "steps": [
                {
                    "id": "step1",
                    "tool": "revit.create_element",
                    "args": {
                        "level_name": "Level 1",
                        "type_name": "Round Duct",
                        "diameter": 300,
                    },
                    "bindings": {
                        "level_name": "$input.level_name",
                        "type_name": "$input.type_name",
                        "diameter": "$input.diameter",
                    },
                },
            ]
        }

        engine = WorkflowEngine(mock_dispatcher)

        # Run with NO input — defaults should be used
        result = await engine.execute(workflow_def, input_args={})

        assert result.success is True
        call_args = mock_dispatcher.dispatch.call_args_list[0][0][1]
        assert call_args["level_name"] == "Level 1"
        assert call_args["type_name"] == "Round Duct"
        assert call_args["diameter"] == 300

    async def test_input_bindings_override_when_provided(self, mock_dispatcher):
        """When $input.xxx bindings have actual values, they override static args."""
        mock_dispatcher.dispatch.return_value = ToolResult.ok({"element_id": 42})

        workflow_def = {
            "steps": [
                {
                    "id": "step1",
                    "tool": "revit.create_element",
                    "args": {
                        "level_name": "Level 1",
                        "type_name": "Round Duct",
                        "diameter": 300,
                    },
                    "bindings": {
                        "level_name": "$input.level_name",
                        "type_name": "$input.type_name",
                        "diameter": "$input.diameter",
                    },
                },
            ]
        }

        engine = WorkflowEngine(mock_dispatcher)

        # Run with overrides — provided values should be used
        result = await engine.execute(workflow_def, input_args={
            "level_name": "Level 2",
            "diameter": 500,
        })

        assert result.success is True
        call_args = mock_dispatcher.dispatch.call_args_list[0][0][1]
        assert call_args["level_name"] == "Level 2"
        assert call_args["type_name"] == "Round Duct"  # Not provided → static default
        assert call_args["diameter"] == 500

    async def test_mixed_wall_and_element_workflow(self, mock_dispatcher):
        """A mixed workflow with wall + element steps should dispatch to different tools."""
        mock_dispatcher.dispatch.side_effect = [
            ToolResult.ok({"element_id": 100}),
            ToolResult.ok({"element_id": 200}),
        ]

        workflow_def = {
            "steps": [
                {
                    "id": "step1",
                    "tool": "revit.create_wall",
                    "args": {"start_point": [0, 0, 0], "end_point": [10, 0, 0], "height": 10},
                },
                {
                    "id": "step2",
                    "tool": "revit.create_element",
                    "args": {
                        "start_point": [0, 0, 10],
                        "end_point": [10, 0, 10],
                        "type_name": "Round Duct",
                    },
                },
            ]
        }

        engine = WorkflowEngine(mock_dispatcher)
        result = await engine.execute(workflow_def)

        assert result.success is True
        assert result.steps_completed == 2
        assert mock_dispatcher.dispatch.call_args_list[0][0][0] == "revit.create_wall"
        assert mock_dispatcher.dispatch.call_args_list[1][0][0] == "revit.create_element"
