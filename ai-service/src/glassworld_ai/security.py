"""Access control (ADR-0006): Host check on every request, session token on all but /health."""

from __future__ import annotations

import hmac
from collections.abc import Awaitable, Callable

from fastapi import Request, Response

from glassworld_ai.errors import error_response

TOKEN_HEADER = "x-gw-token"
PUBLIC_PATHS = frozenset({"/health"})

CallNext = Callable[[Request], Awaitable[Response]]


def make_access_guard(
    allowed_hosts: frozenset[str], token: str | None
) -> Callable[[Request, CallNext], Awaitable[Response]]:
    """Reject foreign Host headers (DNS rebinding) and requests without the session token.

    Without a configured token every protected endpoint is closed.
    """

    async def guard(request: Request, call_next: CallNext) -> Response:
        if request.headers.get("host", "") not in allowed_hosts:
            return error_response("forbidden", "Host not allowed.")
        if request.url.path not in PUBLIC_PATHS:
            supplied = request.headers.get(TOKEN_HEADER, "")
            if token is None or not hmac.compare_digest(supplied.encode(), token.encode()):
                return error_response("forbidden", "Missing or wrong session token.")
        return await call_next(request)

    return guard
