"""Connection state management for named pipe clients."""

from __future__ import annotations

import asyncio
import concurrent.futures
import logging
import queue as thread_queue
import threading
from typing import Any, Callable, Awaitable

import win32api
import win32file
import win32event
import winerror
import pywintypes

from .protocol import (
    HEADER_SIZE,
    decode_header,
    decode_payload,
    encode_message,
    make_pong,
)

logger = logging.getLogger(__name__)


class PipeConnection:
    """Manages a single named pipe connection.

    Supports both asyncio streams and raw win32 pipe handles.
    Chat message processing runs on a dedicated thread with its own event loop
    to avoid contention with the pipe read loop.
    """

    def __init__(
        self,
        reader: Any = None,
        writer: Any = None,
        timeout: float = 30.0,
        win32_handle: Any = None,
        on_chat_message: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
        on_tool_list_request: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
        on_tool_add_request: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
        on_tool_delete_request: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
        on_workflow_save_request: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
        on_workflow_load_request: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
        on_workflow_review_request: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
        on_workflow_test_request: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
        on_workflow_run_request: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
        on_tool_run_request: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
        on_tool_load_request: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
        on_tool_update_request: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
        on_settings_request: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
    ) -> None:
        self._reader = reader
        self._writer = writer
        self._timeout = timeout
        self._win32_handle = win32_handle
        self._on_chat_message = on_chat_message
        self._on_tool_list_request = on_tool_list_request
        self._on_tool_add_request = on_tool_add_request
        self._on_tool_delete_request = on_tool_delete_request
        self._on_workflow_save_request = on_workflow_save_request
        self._on_workflow_load_request = on_workflow_load_request
        self._on_workflow_review_request = on_workflow_review_request
        self._on_workflow_test_request = on_workflow_test_request
        self._on_workflow_run_request = on_workflow_run_request
        self._on_tool_run_request = on_tool_run_request
        self._on_tool_load_request = on_tool_load_request
        self._on_tool_update_request = on_tool_update_request
        self._on_settings_request = on_settings_request
        self._pending: dict[str, concurrent.futures.Future[dict[str, Any]]] = {}
        self._connected = True
        self._read_task: asyncio.Task[None] | None = None
        self._write_lock = threading.Lock()
        self._chat_msg_queue: thread_queue.Queue[dict[str, Any]] = thread_queue.Queue()
        self._chat_thread: threading.Thread | None = None

    @classmethod
    def from_win32_handle(
        cls,
        handle: Any,
        timeout: float = 30.0,
        on_chat_message: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
        on_tool_list_request: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
        on_tool_add_request: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
        on_tool_delete_request: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
        on_workflow_save_request: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
        on_workflow_load_request: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
        on_workflow_review_request: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
        on_workflow_test_request: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
        on_workflow_run_request: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
        on_tool_run_request: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
        on_tool_load_request: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
        on_tool_update_request: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
        on_settings_request: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]] | None = None,
    ) -> PipeConnection:
        """Create a connection backed by a win32 pipe handle."""
        return cls(
            win32_handle=handle,
            timeout=timeout,
            on_chat_message=on_chat_message,
            on_tool_list_request=on_tool_list_request,
            on_tool_add_request=on_tool_add_request,
            on_tool_delete_request=on_tool_delete_request,
            on_workflow_save_request=on_workflow_save_request,
            on_workflow_load_request=on_workflow_load_request,
            on_workflow_review_request=on_workflow_review_request,
            on_workflow_test_request=on_workflow_test_request,
            on_workflow_run_request=on_workflow_run_request,
            on_tool_run_request=on_tool_run_request,
            on_tool_load_request=on_tool_load_request,
            on_tool_update_request=on_tool_update_request,
            on_settings_request=on_settings_request,
        )

    @property
    def connected(self) -> bool:
        return self._connected

    def set_on_chat_message(
        self,
        callback: Callable[[PipeConnection, dict[str, Any]], Awaitable[None]],
    ) -> None:
        """Set the chat message callback after construction."""
        self._on_chat_message = callback

    async def start(self) -> None:
        """Start the background read loop and chat worker thread."""
        self._read_task = asyncio.create_task(self._read_loop())
        self._chat_thread = threading.Thread(
            target=self._chat_worker, daemon=True, name="chat-worker"
        )
        self._chat_thread.start()

    async def close(self) -> None:
        """Close the connection."""
        self._connected = False
        if self._read_task:
            self._read_task.cancel()
        if self._writer is not None:
            self._writer.close()
        if self._win32_handle is not None:
            try:
                win32file.CloseHandle(self._win32_handle)
            except Exception:
                pass
            self._win32_handle = None
        for fut in self._pending.values():
            if not fut.done():
                fut.set_exception(ConnectionError("Pipe connection closed"))
        self._pending.clear()

    async def send(self, message: dict[str, Any]) -> None:
        """Send a framed message over the pipe."""
        if not self._connected:
            raise ConnectionError("Pipe is not connected")
        data = encode_message(message)
        if self._win32_handle is not None:
            # Write directly — overlapped I/O allows concurrent read/write
            # on the same handle, so this won't block even if a read is pending.
            self._win32_write(data)
        else:
            self._writer.write(data)
            await self._writer.drain()

    def _win32_write(self, data: bytes) -> None:
        """Write data to the win32 pipe handle using overlapped I/O.

        Uses an OVERLAPPED structure so the write doesn't contend with
        any concurrent blocking read on the same handle.  Thread-safe
        via ``_write_lock``.
        """
        with self._write_lock:
            ol = pywintypes.OVERLAPPED()
            ol.hEvent = win32event.CreateEvent(None, True, False, None)
            try:
                rc, _nbytes = win32file.WriteFile(
                    self._win32_handle, data, ol
                )
                if rc == winerror.ERROR_IO_PENDING:
                    # Wait for the overlapped write to finish
                    win32event.WaitForSingleObject(
                        ol.hEvent, win32event.INFINITE
                    )
                    win32file.GetOverlappedResult(
                        self._win32_handle, ol, True
                    )
            except pywintypes.error as e:
                self._connected = False
                raise ConnectionError(f"Pipe write error: {e}") from e
            finally:
                try:
                    win32api.CloseHandle(ol.hEvent)
                except Exception:
                    pass

    async def send_and_wait(
        self, message: dict[str, Any], timeout: float | None = None
    ) -> dict[str, Any]:
        """Send a message and wait for the response with matching call_id.

        Uses concurrent.futures.Future so it works from any thread's event loop.
        """
        msg_id = message["id"]
        future: concurrent.futures.Future[dict[str, Any]] = concurrent.futures.Future()
        self._pending[msg_id] = future

        await self.send(message)
        try:
            loop = asyncio.get_running_loop()
            return await asyncio.wait_for(
                asyncio.wrap_future(future, loop=loop),
                timeout=timeout or self._timeout,
            )
        finally:
            self._pending.pop(msg_id, None)

    def _chat_worker(self) -> None:
        """Dedicated thread for processing chat messages.

        Runs its own event loop so async chat handlers (LLM calls, tool
        dispatch) don't contend with the pipe read loop.
        """
        loop = asyncio.new_event_loop()
        asyncio.set_event_loop(loop)
        logger.info("Chat worker thread started")
        try:
            while self._connected:
                try:
                    message = self._chat_msg_queue.get(timeout=1)
                except thread_queue.Empty:
                    continue
                msg_type = message.get("type")
                logger.debug("Chat worker dequeued message type=%s", msg_type)

                handler = None
                if msg_type == "tool_list_request" and self._on_tool_list_request:
                    handler = self._on_tool_list_request
                elif msg_type == "tool_add_request" and self._on_tool_add_request:
                    handler = self._on_tool_add_request
                elif msg_type == "tool_delete_request" and self._on_tool_delete_request:
                    handler = self._on_tool_delete_request
                elif msg_type == "workflow_save_request" and self._on_workflow_save_request:
                    handler = self._on_workflow_save_request
                elif msg_type == "workflow_load_request" and self._on_workflow_load_request:
                    handler = self._on_workflow_load_request
                elif msg_type == "workflow_review_request" and self._on_workflow_review_request:
                    handler = self._on_workflow_review_request
                elif msg_type == "workflow_test_request" and self._on_workflow_test_request:
                    handler = self._on_workflow_test_request
                elif msg_type == "workflow_run_request" and self._on_workflow_run_request:
                    handler = self._on_workflow_run_request
                elif msg_type == "tool_run_request" and self._on_tool_run_request:
                    handler = self._on_tool_run_request
                elif msg_type == "tool_load_request" and self._on_tool_load_request:
                    handler = self._on_tool_load_request
                elif msg_type == "tool_update_request" and self._on_tool_update_request:
                    handler = self._on_tool_update_request
                elif msg_type == "settings_request" and self._on_settings_request:
                    handler = self._on_settings_request
                elif msg_type == "chat_message" and self._on_chat_message:
                    handler = self._on_chat_message

                if handler:
                    try:
                        loop.run_until_complete(handler(self, message))
                    except Exception:
                        logger.exception("Error handling %s message", msg_type)
                else:
                    logger.error("Message type=%s received but no handler is set!", msg_type)
        finally:
            loop.close()
            logger.info("Chat worker thread stopped")

    async def _read_loop(self) -> None:
        """Continuously read messages from the pipe."""
        try:
            while self._connected:
                if self._win32_handle is not None:
                    message = await self._win32_read_message()
                else:
                    header_bytes = await self._reader.readexactly(HEADER_SIZE)
                    length = decode_header(header_bytes)
                    payload_bytes = await self._reader.readexactly(length)
                    message = decode_payload(payload_bytes)

                await self._handle_message(message)
        except (asyncio.IncompleteReadError, ConnectionError):
            logger.info("Pipe connection closed by remote end")
        except asyncio.CancelledError:
            pass
        except Exception:
            logger.exception("Error in pipe read loop")
        finally:
            self._connected = False

    async def _win32_read_message(self) -> dict[str, Any]:
        """Read a single length-prefixed message from a win32 pipe handle."""
        loop = asyncio.get_running_loop()
        header_bytes = await loop.run_in_executor(
            None, self._win32_read_exact, HEADER_SIZE
        )
        length = decode_header(header_bytes)
        payload_bytes = await loop.run_in_executor(
            None, self._win32_read_exact, length
        )
        return decode_payload(payload_bytes)

    def _win32_read_exact(self, size: int) -> bytes:
        """Read exactly ``size`` bytes from the win32 pipe handle.

        Uses overlapped I/O so the read doesn't lock the handle and
        block concurrent writes.
        """
        data = b""
        while len(data) < size:
            ol = pywintypes.OVERLAPPED()
            ol.hEvent = win32event.CreateEvent(None, True, False, None)
            try:
                buf = win32file.AllocateReadBuffer(size - len(data))
                rc, _buf = win32file.ReadFile(
                    self._win32_handle, buf, ol
                )
                if rc == winerror.ERROR_IO_PENDING:
                    # Wait for data (blocks this executor thread, not the event loop)
                    win32event.WaitForSingleObject(
                        ol.hEvent, win32event.INFINITE
                    )
                nbytes = win32file.GetOverlappedResult(
                    self._win32_handle, ol, True
                )
                if nbytes == 0:
                    raise ConnectionError("Pipe closed")
                data += bytes(buf[:nbytes])
            except pywintypes.error as e:
                self._connected = False
                raise ConnectionError(f"Pipe read error: {e}") from e
            finally:
                try:
                    win32api.CloseHandle(ol.hEvent)
                except Exception:
                    pass
        return data

    async def _handle_message(self, message: dict[str, Any]) -> None:
        """Handle an incoming message."""
        msg_type = message.get("type")
        logger.debug("Received message type=%s", msg_type)

        if msg_type == "ping":
            await self.send(make_pong())
            return

        if msg_type == "pong":
            return

        if msg_type == "tool_result":
            call_id = message.get("payload", {}).get("call_id")
            if call_id and call_id in self._pending:
                self._pending[call_id].set_result(message)
                return

        if msg_type == "error":
            call_id = message.get("payload", {}).get("call_id")
            if call_id and call_id in self._pending:
                self._pending[call_id].set_result(message)
                return

        # Resolve screenshot / context / element_identity responses by call_id
        if msg_type in (
            "screenshot_response",
            "context_response",
            "element_identity_response",
            "status_response",
        ):
            call_id = message.get("payload", {}).get("call_id")
            # For status_response the call_id is the original message id
            if not call_id:
                call_id = message.get("id")
            if call_id and call_id in self._pending:
                self._pending[call_id].set_result(message)
                return

        if msg_type == "chat_message":
            # Put on thread-safe queue — picked up by _chat_worker thread
            self._chat_msg_queue.put(message)
            return

        if msg_type == "tool_list_request":
            self._chat_msg_queue.put(message)
            return

        if msg_type == "tool_add_request":
            self._chat_msg_queue.put(message)
            return

        if msg_type == "tool_delete_request":
            self._chat_msg_queue.put(message)
            return

        if msg_type == "workflow_save_request":
            self._chat_msg_queue.put(message)
            return

        if msg_type == "workflow_load_request":
            self._chat_msg_queue.put(message)
            return

        if msg_type == "workflow_review_request":
            self._chat_msg_queue.put(message)
            return

        if msg_type == "workflow_test_request":
            self._chat_msg_queue.put(message)
            return

        if msg_type == "workflow_run_request":
            self._chat_msg_queue.put(message)
            return

        if msg_type == "tool_run_request":
            self._chat_msg_queue.put(message)
            return

        if msg_type == "tool_load_request":
            self._chat_msg_queue.put(message)
            return

        if msg_type == "tool_update_request":
            self._chat_msg_queue.put(message)
            return

        if msg_type == "settings_request":
            self._chat_msg_queue.put(message)
            return

        logger.warning("Unhandled message type: %s", msg_type)
