"""Start the AI service: python -m glassworld_ai --port 0.

The service binds 127.0.0.1 only. With --port 0 the OS assigns a free port, which is announced
on stdout as a single line `AI_SERVICE_READY port=<n>` once the server accepts requests
(ADR-0006). The session token comes from the environment variable GW_AI_TOKEN.
"""

from __future__ import annotations

import argparse
import asyncio
import socket
from pathlib import Path

import uvicorn

from glassworld_ai.app import READY_LINE, create_app
from glassworld_ai.config import load_settings
from glassworld_ai.lifecycle import STALE_LINE, LockFile, watch_parent

HOST = "127.0.0.1"


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(prog="glassworld-ai", description=__doc__)
    parser.add_argument("--port", type=int, default=0, help="0 = let the OS choose (default)")
    parser.add_argument(
        "--config", type=Path, default=None, help="TOML file instead of config.toml"
    )
    parser.add_argument(
        "--parent-pid", type=int, default=None, help="exit when this process (the game) ends"
    )
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> None:
    args = parse_args(argv)
    settings = load_settings(args.config)

    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.bind((HOST, args.port))
    port: int = sock.getsockname()[1]

    lock = LockFile(settings.lock_dir / "ai-service.lock")
    leftover = lock.leftover()
    if leftover is not None:
        print(STALE_LINE.format(pid=leftover.pid, port=leftover.port), flush=True)

    server: uvicorn.Server | None = None
    background: list[asyncio.Task[None]] = []

    async def stop() -> None:
        # Close the model queue BEFORE the server stops: waiting calls end at once with
        # model_unavailable instead of loading the model again (parent gone or /shutdown).
        app.state.stopping = True
        await app.state.queue.close()
        if server is not None:
            server.should_exit = True

    def request_exit() -> None:
        background.append(asyncio.get_running_loop().create_task(stop()))

    def announce() -> None:
        lock.write(port)
        if args.parent_pid is not None:
            loop = asyncio.get_running_loop()
            background.append(loop.create_task(watch_parent(args.parent_pid, request_exit)))
        print(READY_LINE.format(port=port), flush=True)

    app = create_app(
        settings,
        allowed_hosts=frozenset({f"{HOST}:{port}"}),
        on_started=announce,
        request_exit=request_exit,
    )
    server = uvicorn.Server(uvicorn.Config(app, log_level="warning", access_log=False))
    try:
        server.run(sockets=[sock])
    finally:
        lock.remove()


if __name__ == "__main__":
    main()
