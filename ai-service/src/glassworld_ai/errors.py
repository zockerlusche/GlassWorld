"""Every non-2xx answer carries a ServiceError body (contracts/schemas/service-error)."""

from __future__ import annotations

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from starlette.exceptions import HTTPException as StarletteHTTPException

from glassworld_contracts import CONTRACT_VERSION, ServiceError

STATUS_BY_CODE: dict[str, int] = {
    "timeout": 504,
    "model_unavailable": 503,
    "overloaded": 503,
    "invalid_model_output": 502,
    "bad_request": 422,
    "forbidden": 403,
    "not_found": 404,
    "internal": 500,
}

RETRYABLE_CODES = frozenset({"timeout", "model_unavailable", "overloaded", "invalid_model_output"})
_RETRYABLE = RETRYABLE_CODES


def error_response(
    code: str,
    message: str,
    *,
    request_id: str | None = None,
    tick: int | None = None,
    retryable: bool | None = None,
) -> JSONResponse:
    body = ServiceError.model_validate(
        {
            "contract_version": CONTRACT_VERSION,
            "request_id": request_id,
            "tick": tick,
            "code": code,
            "message": message[:500],
            "retryable": code in _RETRYABLE if retryable is None else retryable,
        }
    )
    return JSONResponse(status_code=STATUS_BY_CODE[code], content=body.model_dump(mode="json"))


def install_error_handlers(app: FastAPI) -> None:
    async def http_error(_: Request, exc: Exception) -> JSONResponse:
        status = exc.status_code if isinstance(exc, StarletteHTTPException) else 500
        code = {403: "forbidden", 404: "not_found", 405: "not_found"}.get(status, "internal")
        return error_response(code, str(getattr(exc, "detail", "error")))

    async def validation_error(_: Request, exc: Exception) -> JSONResponse:
        return error_response("bad_request", f"Invalid request: {exc}")

    async def unexpected_error(_: Request, exc: Exception) -> JSONResponse:
        return error_response("internal", type(exc).__name__)

    app.add_exception_handler(StarletteHTTPException, http_error)
    app.add_exception_handler(RequestValidationError, validation_error)
    app.add_exception_handler(Exception, unexpected_error)
