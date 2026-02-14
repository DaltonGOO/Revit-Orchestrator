"""Windows named pipe server using win32pipe."""

from __future__ import annotations

import asyncio
import logging
import threading
from typing import Any, Callable, Awaitable

import win32api
import win32pipe
import win32file
import win32event
import winerror
import pywintypes

from .connection import PipeConnection

logger = logging.getLogger(__name__)

PIPE_BUFFER_SIZE = 65536


class PipeServer:
    """Named pipe server for Windows using win32pipe.

    Runs the accept loop in a background thread and bridges into
    an asyncio event loop for connection handling.

    Uses FILE_FLAG_OVERLAPPED so that reads and writes can happen
    concurrently on the same pipe handle.
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
        self._thread: threading.Thread | None = None
        self._loop: asyncio.AbstractEventLoop | None = None

    @property
    def connections(self) -> list[PipeConnection]:
        """Return active connections."""
        return [c for c in self._connections if c.connected]

    def start(self) -> None:
        """Start the pipe server on a background thread with its own event loop."""
        self._running = True
        self._thread = threading.Thread(target=self._run_loop, daemon=True)
        self._thread.start()
        logger.info("Pipe server started on %s", self._pipe_name)

    def stop(self) -> None:
        """Stop the server and close all connections."""
        self._running = False
        if self._loop is not None:
            self._loop.call_soon_threadsafe(self._loop.stop)
        if self._thread is not None:
            self._thread.join(timeout=5)
        logger.info("Pipe server stopped")

    def _run_loop(self) -> None:
        """Thread entry: create an event loop and run the accept loop."""
        self._loop = asyncio.new_event_loop()
        asyncio.set_event_loop(self._loop)
        try:
            self._loop.run_until_complete(self._accept_loop())
        except Exception:
            logger.exception("Pipe server loop exited with error")
        finally:
            self._loop.close()

    async def _accept_loop(self) -> None:
        """Accept connections using win32pipe in a thread executor."""
        loop = asyncio.get_event_loop()

        while self._running:
            try:
                handle = await loop.run_in_executor(None, self._create_and_wait)
                if handle is None:
                    await asyncio.sleep(2)
                    continue
                connection = PipeConnection.from_win32_handle(handle, timeout=self._timeout)
                self._connections.append(connection)
                logger.info("New pipe client connected")
                asyncio.ensure_future(self._manage_connection(connection))
            except Exception:
                if self._running:
                    logger.exception("Error accepting pipe connection")
                    await asyncio.sleep(1)

    def _create_and_wait(self) -> Any:
        """Create a named pipe instance and block until a client connects.

        Runs in a thread executor so the event loop stays responsive.
        Uses FILE_FLAG_OVERLAPPED so reads and writes don't block each other.
        """
        try:
            handle = win32pipe.CreateNamedPipe(
                self._pipe_name,
                win32pipe.PIPE_ACCESS_DUPLEX | win32file.FILE_FLAG_OVERLAPPED,
                (
                    win32pipe.PIPE_TYPE_BYTE
                    | win32pipe.PIPE_READMODE_BYTE
                    | win32pipe.PIPE_WAIT
                ),
                win32pipe.PIPE_UNLIMITED_INSTANCES,
                PIPE_BUFFER_SIZE,
                PIPE_BUFFER_SIZE,
                0,
                None,
            )
            # Use overlapped ConnectNamedPipe so the handle is in overlapped mode
            ol = pywintypes.OVERLAPPED()
            ol.hEvent = win32event.CreateEvent(None, True, False, None)
            try:
                rc = win32pipe.ConnectNamedPipe(handle, ol)
                if rc == winerror.ERROR_IO_PENDING:
                    win32event.WaitForSingleObject(ol.hEvent, win32event.INFINITE)
            finally:
                win32api.CloseHandle(ol.hEvent)
            return handle
        except pywintypes.error as e:
            if self._running:
                logger.error("CreateNamedPipe/ConnectNamedPipe error: %s", e)
            return None

    async def _manage_connection(self, connection: PipeConnection) -> None:
        """Manage a connection's lifecycle."""
        if self._on_connect:
            await self._on_connect(connection)

        await connection.start()

        # Wait until disconnected
        while connection.connected:
            await asyncio.sleep(1)

        self._connections.remove(connection)
        logger.info("Pipe client disconnected")

        if self._on_disconnect:
            await self._on_disconnect(connection)
