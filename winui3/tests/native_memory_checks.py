#!/usr/bin/env python3
"""Standalone ycn_rdp.dll native lifecycle regression check.

This tool deliberately loads only the DLL supplied by --dll.  It does not
search, copy from, or load the application/install output directory.  The
connection target is hard-coded to 127.0.0.1 and the credentials are dummy
values; --port must identify a TCP port that is confirmed unused by the
preflight exclusive-bind check.

The native ABI below is kept in the same order as
winui3/src/Yomori.RdpNative/ycn_rdp.h and
winui3/src/Yomori.App/Services/RdpNativeInterop.cs.
"""

from __future__ import annotations

import argparse
import ctypes
import os
from pathlib import Path
import socket
import sys
import threading
import time
from typing import Optional


YCN_OK = 0
YCN_ERR_SESSION_FULL = -3
YCN_DISCONNECT_LOCAL = 1


class YcnRdpParams(ctypes.Structure):
    _fields_ = [
        ("host", ctypes.c_char_p),
        ("port", ctypes.c_uint16),
        ("username", ctypes.c_char_p),
        ("domain", ctypes.c_char_p),
        ("password", ctypes.c_char_p),
        ("desktop_width", ctypes.c_uint32),
        ("desktop_height", ctypes.c_uint32),
        ("color_depth", ctypes.c_uint32),
        ("use_nla", ctypes.c_int),
        ("allow_selfsigned", ctypes.c_int),
        ("enable_audio", ctypes.c_int),
        ("use_gfx", ctypes.c_int),
    ]


OnConnected = ctypes.CFUNCTYPE(
    None, ctypes.c_void_p, ctypes.c_int, ctypes.c_uint32, ctypes.c_uint32
)
OnFrameReady = ctypes.CFUNCTYPE(
    None,
    ctypes.c_void_p,
    ctypes.c_int,
    ctypes.c_uint32,
    ctypes.c_uint32,
    ctypes.c_uint32,
)
OnDesktopResize = ctypes.CFUNCTYPE(
    None, ctypes.c_void_p, ctypes.c_int, ctypes.c_uint32, ctypes.c_uint32
)
OnDisconnected = ctypes.CFUNCTYPE(
    None, ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_char_p
)
OnError = ctypes.CFUNCTYPE(
    None, ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_char_p
)


class YcnRdpCallbacks(ctypes.Structure):
    _fields_ = [
        ("on_connected", OnConnected),
        ("on_frame_ready", OnFrameReady),
        ("on_desktop_resize", OnDesktopResize),
        ("on_disconnected", OnDisconnected),
        ("on_error", OnError),
    ]


class NativeApi:
    """The small ABI surface used by this regression test."""

    def __init__(self, dll_path: Path, decode_policy: int | None = None) -> None:
        if os.name != "nt":
            raise RuntimeError("ycn_rdp.dll lifecycle checks require Windows")
        if dll_path.name.casefold() != "ycn_rdp.dll":
            raise ValueError(f"--dll must name ycn_rdp.dll: {dll_path}")
        if not dll_path.is_file():
            raise FileNotFoundError(dll_path)

        # Do not use ctypes' name search.  The supplied parent is used only
        # for transitive DLL resolution, while the module itself is opened by
        # its exact absolute path.
        self._dll_directory = os.add_dll_directory(str(dll_path.parent))
        self._dll = ctypes.WinDLL(str(dll_path))
        self._dll.ycn_rdp_connect.argtypes = [
            ctypes.POINTER(YcnRdpParams),
            ctypes.POINTER(YcnRdpCallbacks),
            ctypes.c_void_p,
        ]
        self._dll.ycn_rdp_connect.restype = ctypes.c_int
        self.decode_policy = decode_policy
        if decode_policy is not None:
            self._dll.ycn_rdp_connect_ex.argtypes = [ctypes.POINTER(YcnRdpParams),
                ctypes.POINTER(YcnRdpCallbacks), ctypes.c_void_p, ctypes.c_uint32]
            self._dll.ycn_rdp_connect_ex.restype = ctypes.c_int
        self._dll.ycn_rdp_disconnect.argtypes = [ctypes.c_int]
        self._dll.ycn_rdp_disconnect.restype = None
        self._dll.ycn_rdp_last_error.argtypes = [
            ctypes.c_int,
            ctypes.POINTER(ctypes.c_char),
            ctypes.c_size_t,
        ]
        self._dll.ycn_rdp_last_error.restype = None

    def connect(
        self, params: YcnRdpParams, callbacks: YcnRdpCallbacks
    ) -> int:
        if self.decode_policy is not None:
            return int(self._dll.ycn_rdp_connect_ex(ctypes.byref(params), ctypes.byref(callbacks), None, self.decode_policy))
        return int(self._dll.ycn_rdp_connect(ctypes.byref(params), ctypes.byref(callbacks), None))

    def disconnect(self, session: int) -> None:
        self._dll.ycn_rdp_disconnect(session)

    def last_error(self, session: int = -1) -> str:
        buf = ctypes.create_string_buffer(512)
        self._dll.ycn_rdp_last_error(session, buf, len(buf))
        return buf.value.decode("utf-8", errors="replace")


class Observation:
    def __init__(self, session: int) -> None:
        self.session = session
        self.terminal = threading.Event()
        self.terminal_count = 0
        self.terminal_callback_session: Optional[int] = None
        self.terminal_reason: Optional[int] = None
        self.terminal_detail = ""
        self.connected_count = 0
        self.errors: list[tuple[int, str]] = []
        self.events: list[str] = []
        self._lock = threading.Lock()

    def record_connected(self, width: int, height: int) -> None:
        with self._lock:
            self.connected_count += 1
            self.events.append(f"connected {width}x{height}")

    def record_error(self, code: int, message: str) -> None:
        with self._lock:
            self.errors.append((code, message))
            self.events.append(f"error {code}: {message}")

    def record_terminal(self, session: int, reason: int, detail: str) -> None:
        with self._lock:
            self.terminal_count += 1
            self.terminal_callback_session = session
            self.terminal_reason = reason
            self.terminal_detail = detail
            self.events.append(f"disconnected reason={reason}: {detail}")
            self.terminal.set()


class LifecycleCheck:
    def __init__(self, api: NativeApi, port: int, iterations: int, timeout: float, use_gfx: bool = False) -> None:
        self.api = api
        self.use_gfx = use_gfx
        self.port = port
        self.iterations = iterations
        self.timeout = timeout
        self.observations: dict[int, Observation] = {}
        self.active_sessions: dict[int, Observation] = {}
        self.observation_lock = threading.Lock()

        # Keep every CFUNCTYPE object strongly reachable until the native DLL
        # has issued the terminal callback for the last session.  ctypes may
        # otherwise collect a callback while native code still owns its
        # function pointer.
        self._on_connected = OnConnected(self._connected)
        self._on_frame_ready = OnFrameReady(self._frame_ready)
        self._on_desktop_resize = OnDesktopResize(self._desktop_resize)
        self._on_disconnected = OnDisconnected(self._disconnected)
        self._on_error = OnError(self._error)
        self.callbacks = YcnRdpCallbacks(
            self._on_connected,
            self._on_frame_ready,
            self._on_desktop_resize,
            self._on_disconnected,
            self._on_error,
        )

    def _observation(self, session: int) -> Observation:
        with self.observation_lock:
            observation = self.observations.get(session)
            if observation is None:
                # The event thread can report an immediate refused connection
                # before ycn_rdp_connect returns to Python.
                observation = Observation(session)
                self.observations[session] = observation
            return observation

    @staticmethod
    def _decode(value: Optional[bytes]) -> str:
        return (value or b"").decode("utf-8", errors="replace")

    def _connected(self, _user: int, session: int, width: int, height: int) -> None:
        self._observation(session).record_connected(width, height)

    def _frame_ready(
        self,
        _user: int,
        _session: int,
        _width: int,
        _height: int,
        _stride: int,
    ) -> None:
        # A frame is not requested by this failure-only test.  In particular,
        # do not call ycn_rdp_grab_frame or disconnect from any callback.
        return

    def _desktop_resize(self, _user: int, session: int, width: int, height: int) -> None:
        self._observation(session).events.append(f"resize {width}x{height}")

    def _disconnected(
        self, _user: int, session: int, reason: int, detail: Optional[bytes]
    ) -> None:
        self._observation(session).record_terminal(session, reason, self._decode(detail))

    def _error(
        self, _user: int, session: int, code: int, message: Optional[bytes]
    ) -> None:
        self._observation(session).record_error(code, self._decode(message))

    def _params(self) -> YcnRdpParams:
        # These are intentionally invalid/dummy credentials.  A confirmed
        # unused loopback port rejects before an RDP account can be consulted.
        host = b"127.0.0.1"
        username = b"native-memory-regression-dummy"
        domain = b""
        password = b"native-memory-regression-dummy"
        return YcnRdpParams(
            host,
            self.port,
            username,
            domain,
            password,
            640,
            480,
            32,
            0,
            1,
            0,
            int(self.use_gfx),
        )

    def _connect(self) -> tuple[int, Observation]:
        # The structure and its byte strings stay alive through this call;
        # ycn_rdp_connect deep-copies them before returning.
        params = self._params()
        session = self.api.connect(params, self.callbacks)
        if session == YCN_ERR_SESSION_FULL:
            raise AssertionError(
                f"ycn_rdp_connect returned YCN_ERR_SESSION_FULL at iteration; "
                f"native error: {self.api.last_error()}"
            )
        if session <= 0:
            raise AssertionError(
                f"ycn_rdp_connect failed with {session}; "
                f"native error: {self.api.last_error()}"
            )
        observation = self._observation(session)
        self.active_sessions[session] = observation
        return session, observation

    def _wait_terminal(self, session: int, observation: Observation, label: str) -> None:
        if not observation.terminal.wait(self.timeout):
            raise AssertionError(
                f"{label}: no terminal callback for session {session} "
                f"within {self.timeout:g}s"
            )
        if observation.terminal_count != 1:
            raise AssertionError(
                f"{label}: session {session} received "
                f"{observation.terminal_count} terminal callbacks"
            )
        if observation.terminal_callback_session != session:
            raise AssertionError(
                f"{label}: terminal callback session "
                f"{observation.terminal_callback_session} != returned {session}"
            )
        self.active_sessions.pop(session, None)

    def _assert_new_session_isolated(
        self, previous_session: Optional[int], session: int, observation: Observation, label: str
    ) -> None:
        if previous_session is not None:
            self.api.disconnect(previous_session)
            if observation.terminal.is_set():
                raise AssertionError(
                    f"{label}: stale session id {previous_session} affected new "
                    f"session {session} before its own teardown "
                    f"(reason={observation.terminal_reason})"
                )

    def _assert_unique(self, session: int, seen_ids: set[int], label: str) -> None:
        if session in seen_ids:
            raise AssertionError(f"{label}: session id {session} was reused")
        seen_ids.add(session)

    def _cleanup_active_sessions(self) -> None:
        for session, observation in list(self.active_sessions.items()):
            try:
                self.api.disconnect(session)
                observation.terminal.wait(min(self.timeout, 5.0))
            except (OSError, RuntimeError):
                pass
            finally:
                self.active_sessions.pop(session, None)

    def run(self) -> None:
        seen_ids: set[int] = set()
        previous_session: Optional[int] = None
        failure_count = 0
        immediate_count = 0

        try:
            # First let the native connector fail on the confirmed unused port.
            # No disconnect is sent here: the terminal callback must come from
            # the failed connection itself.
            for iteration in range(1, self.iterations + 1):
                label = f"failure iteration {iteration}/{self.iterations}"
                session, observation = self._connect()
                self._assert_unique(session, seen_ids, label)
                self._assert_new_session_isolated(previous_session, session, observation, label)
                self._wait_terminal(session, observation, label)
                if observation.terminal_reason == YCN_DISCONNECT_LOCAL:
                    raise AssertionError(
                        f"{label}: expected failed connection, got LOCAL terminal "
                        f"detail={observation.terminal_detail!r}"
                    )
                failure_count += 1
                print(
                    f"PASS {label} session={session} terminal=1 "
                    f"reason={observation.terminal_reason} errors={len(observation.errors)}"
                )
                previous_session = session

            # Then exercise an active CONNECTING -> STOPPING teardown.  This
            # remains a separate cycle so the failure count above is genuine.
            label = "immediate connecting disconnect"
            session, observation = self._connect()
            self._assert_unique(session, seen_ids, label)
            self._assert_new_session_isolated(previous_session, session, observation, label)
            self.api.disconnect(session)
            self._wait_terminal(session, observation, label)
            if observation.terminal_reason != YCN_DISCONNECT_LOCAL:
                raise AssertionError(
                    f"{label}: expected LOCAL terminal, got {observation.terminal_reason} "
                    f"detail={observation.terminal_detail!r}"
                )
            immediate_count += 1
            print(f"PASS {label} session={session} terminal=1 reason=LOCAL")

            print(
                f"PASS lifecycle regression: failed_connections={failure_count}, "
                f"immediate_connecting_disconnects={immediate_count}, "
                f"unique_ids={len(seen_ids)}, no_session_full=1, "
                f"stale_disconnect_isolated=1"
            )
        finally:
            self._cleanup_active_sessions()


def confirm_unused_loopback_port(port: int) -> None:
    """Confirm no listener owns the exact IPv4 loopback endpoint.

    Some Windows environments return WSAEWOULDBLOCK (10035) for a refused
    loopback connect, even when the TCP table has no listener. An exclusive
    bind is the useful preflight here: it fails with WSAEADDRINUSE if another
    process owns the endpoint, and it does not create a listening service.
    """
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        if hasattr(socket, "SO_EXCLUSIVEADDRUSE"):
            probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        try:
            probe.bind(("127.0.0.1", port))
        except OSError as exc:
            raise RuntimeError(
                f"cannot exclusively bind 127.0.0.1:{port}; a listener or "
                f"reserved endpoint may exist (winerror={getattr(exc, 'winerror', None)}, "
                f"errno={exc.errno})"
            ) from exc

    # Keep the result as evidence, but do not reject WSAEWOULDBLOCK: the bind
    # above is the authoritative listener check on this host.
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        probe.settimeout(0.5)
        result = probe.connect_ex(("127.0.0.1", port))
    if result == 0:
        raise RuntimeError(f"127.0.0.1:{port} became listening; refusing to run")
    print(f"preflight 127.0.0.1:{port}: no listener (connect_ex={result})")


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Run the standalone ycn_rdp.dll lifecycle regression check."
    )
    parser.add_argument(
        "--dll",
        required=True,
        type=Path,
        help="absolute path to the exact ycn_rdp.dll to test",
    )
    parser.add_argument(
        "--port",
        required=True,
        type=int,
        help="confirmed unused TCP port on 127.0.0.1",
    )
    parser.add_argument(
        "--iterations",
        type=int,
        default=12,
        help="number of connect/disconnect cycles (default: 12)",
    )
    parser.add_argument(
        "--timeout",
        type=float,
        default=15.0,
        help="seconds to wait for each terminal callback (default: 15)",
    )
    parser.add_argument("--decode-policy", type=int, choices=(0, 1, 2), default=None,
                        help="exercise the versioned connect_ex entry (default: legacy connect)")
    parser.add_argument("--gfx", action="store_true", help="request GFX for versioned connection lifecycle coverage")
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    if args.port < 1 or args.port > 65535:
        raise ValueError(f"--port must be between 1 and 65535: {args.port}")
    if args.iterations <= 8:
        raise ValueError("--iterations must be greater than 8")
    if args.timeout <= 0:
        raise ValueError("--timeout must be positive")

    dll_path = args.dll.expanduser().resolve(strict=True)
    confirm_unused_loopback_port(args.port)
    api = NativeApi(dll_path, args.decode_policy)
    LifecycleCheck(api, args.port, args.iterations, args.timeout, args.gfx).run()
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except (AssertionError, FileNotFoundError, OSError, RuntimeError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)