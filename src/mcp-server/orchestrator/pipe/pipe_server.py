"""Windows named pipe server using asyncio."""

from __future__ import annotations

import asyncio
import ctypes
import logging
from typing import Any, Callable, Awaitable

from .connection import PipeConnection

logger = logging.getLogger(__name__)

# Windows named pipe constants
PIPE_ACCESS_DUPLEX = 0x00000003
PIPE_TYPE_BYTE = 0x00000000
PIPE_READMODE_BYTE = 0x00000000
PIPE_WAIT = 0x00000000
PIPE_UNLIMITED_INSTANCES = 255
BUFFER_SIZE = 65536
INVALID_HANDLE_VALUE = -1


class PipeServer:
    """Async named pipe server for Windows.

    Listens for connections from the Revit add-in and manages them.
    """

    def __init__(
        self,
        pipe_name: str,
        timeout: float = 30.0,
        on_connect: Callable[[PipeConnection], Awaitable[None]] | None = None,
        on_disconnect: Callable[[PipeConnection], Awaitable[None]] | None = None,
    ) -> None:
        self._pipe_name = pipe_name
        self._timeout = timeout
        self._on_connect = on_connect
        self._on_disconnect = on_disconnect
        self._connections: list[PipeConnection] = []
        self._running = False
        self._server_task: asyncio.Task[None] | None = None

    @property
    def connections(self) -> list[PipeConnection]:
        """Return active connections."""
        return [c for c in self._connections if c.connected]

    async def start(self) -> None:
        """Start listening for pipe connections."""
        self._running = True
        self._server_task = asyncio.create_task(self._accept_loop())
        logger.info("Pipe server started on %s", self._pipe_name)

    async def stop(self) -> None:
        """Stop the server and close all connections."""
        self._running = False
        if self._server_task:
            self._server_task.cancel()
        for conn in self._connections:
            await conn.close()
        self._connections.clear()
        logger.info("Pipe server stopped")

    async def _accept_loop(self) -> None:
        """Accept loop using proactor event loop for Windows named pipes."""
        loop = asyncio.get_event_loop()

        while self._running:
            try:
                # Use asyncio's built-in Windows pipe support
                # The proactor event loop handles named pipes natively
                server = await asyncio.start_server(
                    self._handle_client,
                    path=self._pipe_name,
                )
                async with server:
                    await server.serve_forever()
            except asyncio.CancelledError:
                break
            except Exception:
                logger.exception("Error in pipe accept loop")
                if self._running:
                    await asyncio.sleep(1)

    async def _handle_client(
        self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter
    ) -> None:
        """Handle a new pipe client connection."""
        connection = PipeConnection(reader, writer, timeout=self._timeout)
        self._connections.append(connection)
        logger.info("New pipe client connected")

        if self._on_connect:
            await self._on_connect(connection)

        await connection.start()

        # Wait for connection to close
        while connection.connected:
            await asyncio.sleep(1)

        self._connections.remove(connection)
        logger.info("Pipe client disconnected")

        if self._on_disconnect:
            await self._on_disconnect(connection)
