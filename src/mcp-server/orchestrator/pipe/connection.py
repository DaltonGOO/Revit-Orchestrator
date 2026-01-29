"""Connection state management for named pipe clients."""

from __future__ import annotations

import asyncio
import logging
import uuid
from typing import Any

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

    Handles reading/writing framed messages and tracking pending requests.
    """

    def __init__(
        self,
        reader: asyncio.StreamReader,
        writer: asyncio.StreamWriter,
        timeout: float = 30.0,
    ) -> None:
        self._reader = reader
        self._writer = writer
        self._timeout = timeout
        self._pending: dict[str, asyncio.Future[dict[str, Any]]] = {}
        self._connected = True
        self._read_task: asyncio.Task[None] | None = None

    @property
    def connected(self) -> bool:
        return self._connected

    async def start(self) -> None:
        """Start the background read loop."""
        self._read_task = asyncio.create_task(self._read_loop())

    async def close(self) -> None:
        """Close the connection."""
        self._connected = False
        if self._read_task:
            self._read_task.cancel()
        self._writer.close()
        # Cancel all pending futures
        for fut in self._pending.values():
            if not fut.done():
                fut.set_exception(ConnectionError("Pipe connection closed"))
        self._pending.clear()

    async def send(self, message: dict[str, Any]) -> None:
        """Send a framed message over the pipe."""
        if not self._connected:
            raise ConnectionError("Pipe is not connected")
        data = encode_message(message)
        self._writer.write(data)
        await self._writer.drain()

    async def send_and_wait(
        self, message: dict[str, Any], timeout: float | None = None
    ) -> dict[str, Any]:
        """Send a message and wait for the response with matching call_id.

        The response must be a tool_result with call_id matching the message id.
        """
        msg_id = message["id"]
        future: asyncio.Future[dict[str, Any]] = asyncio.get_event_loop().create_future()
        self._pending[msg_id] = future

        await self.send(message)
        try:
            return await asyncio.wait_for(future, timeout=timeout or self._timeout)
        finally:
            self._pending.pop(msg_id, None)

    async def _read_loop(self) -> None:
        """Continuously read messages from the pipe."""
        try:
            while self._connected:
                # Read header
                header_bytes = await self._reader.readexactly(HEADER_SIZE)
                length = decode_header(header_bytes)

                # Read payload
                payload_bytes = await self._reader.readexactly(length)
                message = decode_payload(payload_bytes)

                await self._handle_message(message)
        except asyncio.IncompleteReadError:
            logger.info("Pipe connection closed by remote end")
        except asyncio.CancelledError:
            pass
        except Exception:
            logger.exception("Error in pipe read loop")
        finally:
            self._connected = False

    async def _handle_message(self, message: dict[str, Any]) -> None:
        """Handle an incoming message."""
        msg_type = message.get("type")

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

        logger.warning("Unhandled message type: %s", msg_type)
