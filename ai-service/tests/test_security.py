from __future__ import annotations

from fastapi import FastAPI

from conftest import TOKEN, get, make_app
from glassworld_ai.config import Settings
from glassworld_contracts import ServiceError


def test_foreign_host_is_rejected_even_for_health(app: FastAPI) -> None:
    response = get(app, "/health", headers={"host": "evil.example:80"})
    assert response.status_code == 403
    assert ServiceError.model_validate(response.json()).code == "forbidden"


def test_protected_path_without_token_is_forbidden(app: FastAPI) -> None:
    response = get(app, "/decide")
    assert response.status_code == 403
    assert response.json()["code"] == "forbidden"


def test_protected_path_with_wrong_token_is_forbidden(app: FastAPI) -> None:
    assert get(app, "/decide", headers={"x-gw-token": "wrong"}).status_code == 403


def test_unknown_path_with_token_is_not_found_service_error(app: FastAPI) -> None:
    response = get(app, "/decide", headers={"x-gw-token": TOKEN})
    assert response.status_code == 404
    error = ServiceError.model_validate(response.json())
    assert error.code == "not_found"
    assert error.retryable is False


def test_without_configured_token_everything_but_health_is_closed(settings: Settings) -> None:
    app = make_app(settings.model_copy(update={"token": None}))
    assert get(app, "/health").status_code == 200
    assert get(app, "/decide", headers={"x-gw-token": ""}).status_code == 403
