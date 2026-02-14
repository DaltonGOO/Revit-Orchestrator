"""Tests for workflow context and expression evaluation."""

import pytest

from orchestrator.dispatcher.result import ToolResult
from orchestrator.workflow.context import WorkflowContext


class TestWorkflowContext:
    def test_resolve_input(self):
        ctx = WorkflowContext(input_args={"name": "test", "count": 5})
        assert ctx.evaluate("$input.name") == "test"
        assert ctx.evaluate("$input.count") == 5

    def test_resolve_step_success(self):
        ctx = WorkflowContext()
        ctx.record_result("step1", ToolResult.ok({"element_id": 123}))
        assert ctx.evaluate("$steps.step1.success") is True

    def test_resolve_step_data_field(self):
        ctx = WorkflowContext()
        ctx.record_result("step1", ToolResult.ok({"element_id": 123}))
        assert ctx.evaluate("$steps.step1.data.element_id") == 123

    def test_resolve_nested_data(self):
        ctx = WorkflowContext()
        ctx.record_result("step1", ToolResult.ok({"info": {"name": "Wall"}}))
        assert ctx.evaluate("$steps.step1.data.info.name") == "Wall"

    def test_missing_step_raises_error(self):
        ctx = WorkflowContext()
        with pytest.raises(KeyError, match="Step 'step99' not found"):
            ctx.evaluate("$steps.step99.success")

    def test_literal_value(self):
        ctx = WorkflowContext()
        assert ctx.evaluate("hello") == "hello"

    def test_resolve_bindings(self):
        ctx = WorkflowContext(input_args={"height": 10})
        ctx.record_result("step1", ToolResult.ok({"element_id": 42}))
        bindings = {
            "element_id": "$steps.step1.data.element_id",
            "height": "$input.height",
        }
        resolved = ctx.resolve_bindings(bindings)
        assert resolved == {"element_id": 42, "height": 10}

    def test_model_changes_aggregation(self):
        ctx = WorkflowContext()
        ctx.record_result(
            "s1",
            ToolResult.ok({
                "model_changes": {
                    "created": [{"element_id": 1}],
                    "modified": [],
                    "deleted": [],
                }
            }),
        )
        ctx.record_result(
            "s2",
            ToolResult.ok({
                "model_changes": {
                    "created": [{"element_id": 2}],
                    "modified": [{"element_id": 3}],
                    "deleted": [4],
                }
            }),
        )
        changes = ctx.get_aggregated_changes()
        assert len(changes["created"]) == 2
        assert len(changes["modified"]) == 1
        assert len(changes["deleted"]) == 1

    def test_missing_input_returns_none(self):
        ctx = WorkflowContext()
        assert ctx.evaluate("$input.nonexistent") is None
