"""Tests for ToolResult dataclass."""

from orchestrator.dispatcher.result import ToolResult


class TestToolResult:
    def test_ok_result(self):
        result = ToolResult.ok({"element_id": 123})
        assert result.success is True
        assert result.data == {"element_id": 123}
        assert result.error_code is None

    def test_fail_result(self):
        result = ToolResult.fail("TEST_ERROR", "Something went wrong")
        assert result.success is False
        assert result.error_code == "TEST_ERROR"
        assert result.error_message == "Something went wrong"

    def test_to_dict_success(self):
        result = ToolResult.ok({"x": 1}, duration_ms=42)
        d = result.to_dict()
        assert d["success"] is True
        assert d["data"] == {"x": 1}
        assert d["error"] is None
        assert d["duration_ms"] == 42

    def test_to_dict_failure(self):
        result = ToolResult.fail("ERR", "msg", duration_ms=10)
        d = result.to_dict()
        assert d["success"] is False
        assert d["error"]["code"] == "ERR"
        assert d["error"]["message"] == "msg"

    def test_ok_with_duration(self):
        result = ToolResult.ok({}, duration_ms=100)
        assert result.duration_ms == 100
