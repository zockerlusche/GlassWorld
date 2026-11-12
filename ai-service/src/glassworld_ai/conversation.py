# jsonschema ships incomplete type information.
# pyright: reportUnknownMemberType=false, reportUnknownArgumentType=false
# pyright: reportUnknownVariableType=false, reportUnknownLambdaType=false
"""Talking with an inhabitant (B6 R13, A1342): POST /conversation.

The player waits for the answer: priority PLAYER and the request's deadline. The answer is only a
proposal (B2462): the model picks an outcome (reply, gesture, not understood, decline), a short
sentence in the player's language for a reply, and an expression from the vocabulary. Understanding
is the frame (gestures, single words, sentences) - the core computes every change to it. Every
reply passes the content guard; the core checks again.
"""

from __future__ import annotations

import json
import time
from collections.abc import Callable
from pathlib import Path
from typing import Any, ClassVar, Protocol, cast

from jsonschema import Draft202012Validator

from glassworld_ai.calllog import CallLog
from glassworld_ai.grounding import (
    checks_nouns,
    known_words,
    settings_words,
    spoken_names,
    unknown_nouns,
)
from glassworld_ai.llm.jobcall import (
    InvalidOutputError,
    ModelCallSettings,
    ask_model,
    fail,
    recorded_answers,
)
from glassworld_ai.llm.ollama import OllamaClient
from glassworld_ai.prompts import PromptTemplate, load_prompt
from glassworld_ai.queue import ModelQueue, Priority
from glassworld_ai.safety import TextGuard
from glassworld_ai.texts import TextCatalog
from glassworld_ai.words import (
    ContextError,
    bands,
    humanize,
    knowledge_line,
    plain_id,
    quote,
    subject_name,
    without_runtime_ids,
    word,
)
from glassworld_contracts import CONTRACT_VERSION
from glassworld_contracts.models.conversation_request_schema import ConversationRequest
from glassworld_contracts.models.conversation_response_schema import ConversationResponse

ROLE_PROMPT = "conversation/reply"
ROLE = "conversation"
REPLY_MAX = 200
OUTCOMES = ("reply", "gesture", "not_understood", "decline")


class Conversationalist(Protocol):
    async def answer(
        self, request: ConversationRequest, deadline: float
    ) -> ConversationResponse: ...


def _table(settings: dict[str, Any], key: str) -> dict[str, str]:
    raw: object = settings.get(key)
    if not isinstance(raw, dict):
        raise ContextError(f"prompt setting {key} missing")
    return {str(k): str(v) for k, v in cast(dict[object, object], raw).items()}


def output_schema(expressions: list[str]) -> dict[str, Any]:
    return {
        "type": "object",
        "additionalProperties": False,
        "required": ["outcome", "reply", "expression", "rationale"],
        "properties": {
            "outcome": {"type": "string", "enum": list(OUTCOMES)},
            "reply": {
                "anyOf": [
                    {"type": "string", "minLength": 1, "maxLength": REPLY_MAX},
                    {"type": "null"},
                ]
            },
            "expression": {
                "anyOf": [{"type": "string", "enum": expressions}, {"type": "null"}]
                if expressions
                else {"type": "null"}
            },
            "rationale": {"type": "string", "minLength": 1, "maxLength": 280},
        },
    }


def parse(
    raw: str,
    schema: dict[str, Any],
    guard: TextGuard,
    answer_all: bool = False,
    grounding: Callable[[str], list[str]] | None = None,
) -> dict[str, Any]:
    """Validate the model answer. With `answer_all` (prompt v2, Marcel W1701) decline and not
    understood keep their sentence too; a blocked one is dropped (B2644: nothing is shown)."""
    try:
        data: Any = json.loads(raw)
    except json.JSONDecodeError as error:
        raise InvalidOutputError(f"not JSON: {error.msg}") from error
    errors = sorted(Draft202012Validator(schema).iter_errors(data), key=lambda e: list(e.path))
    if errors:
        first = errors[0]
        raise InvalidOutputError(
            f"{'/'.join(str(p) for p in first.path) or '(root)'}: {first.message}"
        )
    values = cast(dict[str, Any], data)
    if values["outcome"] == "reply":
        if not values["reply"]:
            raise InvalidOutputError("a reply needs a sentence")
        verdict = guard.check(values["reply"])
        if not verdict.allowed:
            raise InvalidOutputError(f"reply blocked by the content guard ({verdict.category})")
    elif answer_all and values["outcome"] != "gesture" and values["reply"]:
        if not guard.check(values["reply"]).allowed:
            values["reply"] = None
            values["left_out"] = True  # B2644: no stand-in for a rejected sentence
    else:
        values["reply"] = None  # v1: a sentence only with outcome reply
    if grounding is not None and values["reply"]:
        # W1737 (v3): a sentence naming things the inhabitant cannot know is not said.
        values["ungrounded"] = grounding(values["reply"])
    return values


def with_words(data: dict[str, Any], texts: TextCatalog | None, locale: str) -> dict[str, Any]:
    """No fixed sentence in the inhabitant's mouth (B2644, D763 Z26, TL #21712): an outcome without
    a sentence - the model wrote none, or the checks took it back - stays without words. W1701 (the
    voice always gets an answer) is kept by a visible reaction without words that the game shows
    for the outcome (decline, not understood); before, the data's msg.converse.<outcome> was said.
    `texts` and `locale` are kept for the callers; nothing is looked up any more."""
    del texts, locale
    data = dict(data)
    data.pop("left_out", None)
    return data


def values(
    request: ConversationRequest,
    template: PromptTemplate,
    expressions: dict[str, str],
    texts: TextCatalog | None = None,
) -> dict[str, str]:
    settings = template.settings
    locale = plain_id(request.display_locale)
    language = _table(settings, "language_names").get(locale)
    if language is None:
        raise ContextError(f"no language name for {locale}")
    speaker = request.speaker
    relation = speaker.relation_to_player

    need_words = bands(settings, "need_words")
    mood_words = bands(settings, "mood_words")
    trait_words = bands(settings, "trait_words")
    relation_words = bands(settings, "relation_words")

    def levels(items: Any, words: list[tuple[float, str]]) -> str:
        parts = [
            f"{humanize(plain_id(i.id))}: {word(words, float(i.value))} ({float(i.value):.2f})"
            for i in items
        ]
        return ", ".join(parts) or "-"

    # Needs: 0 = urgent, 1 = satisfied (contract and core), most urgent first, one per line.
    needs = sorted(speaker.needs, key=lambda n: (float(n.value), plain_id(n.id)))
    needs_text = "\n".join(
        f"- {humanize(plain_id(n.id))}: {word(need_words, float(n.value))} ({float(n.value):.2f})"
        for n in needs
    )

    def feeling(value: float) -> str:
        return f"{word(relation_words, value)} ({value:.1f})"

    # What was said comes from outside: quoted, so it can never look like part of the prompt.
    said = [
        ("player" if plain_id(line.author) == "player" else "you") + ": " + line.text.text
        for line in request.recent_lines
    ]
    understanding = word(bands(settings, "understanding_words"), float(speaker.understanding))
    grounded = _grounded_values(request, settings, texts, locale) if template.version >= 3 else {}
    made: object = settings.get("made_thing")
    if isinstance(made, str):
        # v5 (#251): an invention by the speaker's own name, never by its runtime id.
        own = spoken_names(request, locale)
        grounded = {
            key: without_runtime_ids(text, own, made, everywhere=True)
            for key, text in grounded.items()
        }
    return grounded | {
        "language": language,
        "understanding": understanding,
        "trust": feeling(float(relation.trust)),
        "awe": feeling(float(relation.awe)),
        "fear": feeling(float(relation.fear)),
        "needs": needs_text or "- (none)",
        "mood": levels(speaker.mood, mood_words),
        "traits": levels(speaker.traits, trait_words),
        "recent": quote("\n".join(said)) or "(nothing yet)",
        "player_text": quote(request.player_text) or "> ...",
        "expressions": "\n".join(f"- {k}: {v}" for k, v in sorted(expressions.items()))
        or "- (none)",
        "reply_max": str(REPLY_MAX),
    }


def _name(texts: TextCatalog | None, locale: str, subject: Any, settings: dict[str, Any]) -> str:
    """A thing by its name in the player's language (content data), else the readable subject."""
    kind = plain_id(subject.kind)
    found = (
        texts.thing(locale, plain_id(subject.id)) if texts is not None and kind == "type" else None
    )
    return f'"{found}"' if found else subject_name(subject, settings)


def _grounded_values(
    request: ConversationRequest,
    settings: dict[str, Any],
    texts: TextCatalog | None,
    locale: str,
) -> dict[str, str]:
    """v3 (W1737): what the inhabitant does, knows and sees - the only things it may talk about."""
    speaker = request.speaker
    doing = speaker.doing
    if doing is None:
        doing_text = "- nothing in particular, you are resting where you are"
    else:
        target = f" ({_name(texts, locale, doing.target, settings)})" if doing.target else ""
        doing_text = f"- now: {humanize(plain_id(doing.action))}{target}"
        if doing.planned is not None:
            step = doing.planned
            then = f" ({_name(texts, locale, step.target, settings)})" if step.target else ""
            doing_text += f"\n- next: {humanize(plain_id(step.action))}{then}"
    known = "\n".join(knowledge_line(entry, settings) for entry in (speaker.knowledge or []))
    sees = ", ".join(
        (texts.thing(locale, plain_id(t)) if texts is not None else None) or humanize(plain_id(t))
        for t in (speaker.sees or [])
    )
    return {
        "doing": doing_text,
        "knowledge": known or "- (nothing yet beyond what you see)",
        "sees": sees or "(nothing special)",
    }


def _response(
    request: ConversationRequest,
    data: dict[str, Any],
    source: str,
    model: str,
    latency_ms: int,
    attempts: int,
) -> ConversationResponse:
    locale = plain_id(request.display_locale)
    return ConversationResponse.model_validate(
        {
            "contract_version": CONTRACT_VERSION,
            "request_id": plain_id(request.request_id),
            "tick": request.tick,
            "outcome": data["outcome"],
            "reply": {"locale": locale, "text": data["reply"]} if data["reply"] else None,
            "expression": data["expression"],
            "rationale": data["rationale"][:280],
            "meta": {
                "source": source,
                "model": model[:64],
                "latency_ms": latency_ms,
                "attempts": attempts,
            },
        }
    )


def grounding_check(
    request: ConversationRequest,
    template: PromptTemplate,
    texts: TextCatalog | None,
    names: tuple[str, ...],
) -> Callable[[str], list[str]] | None:
    """v3 (W1737): the check of the answer's nouns, where the language allows it (settings)."""
    settings = template.settings
    locale = plain_id(request.display_locale)
    if template.version < 3 or not checks_nouns(settings, locale):
        return None
    words = known_words(
        request, texts, locale, names, settings_words(settings, "general_words", locale)
    )
    return lambda reply: unknown_nouns(reply, words)


def grounded(request: ConversationRequest, data: dict[str, Any]) -> dict[str, Any]:
    """A sentence that names unknown things is left out (B2644, Marcel F5296 = B: a rejected AI
    sentence shows nothing; before, the data's stand-in msg.converse.<outcome> took its place).
    The choice itself - reply, decline, not understood - stays the model's: the service only
    takes back words (PR2 check D763 G1). A reply without a sentence is shown by the core as a
    gesture. Needs no text catalog any more (review GW1 #269): also left out without one."""
    unknown = data.pop("ungrounded", None)
    if not unknown:
        return data
    note = f" [left out, unknown: {', '.join(unknown[:3])}]"
    return {
        **data,
        "reply": None,
        "left_out": True,
        "rationale": (str(data.get("rationale", "")) + note)[-280:],
    }


class OllamaConversation:
    def __init__(
        self,
        *,
        client: OllamaClient,
        queue: ModelQueue,
        template: PromptTemplate,
        settings: ModelCallSettings,
        log: CallLog,
        guard: TextGuard,
        expressions: dict[str, str],
        texts: TextCatalog | None = None,
        names: tuple[str, ...] = (),
    ) -> None:
        self._client, self._queue, self._template = client, queue, template
        self._settings, self._log, self._guard = settings, log, guard
        self._expressions = expressions
        # Prompt v2 (W1701): every outcome except a gesture carries a sentence.
        self._texts = texts if template.version >= 2 else None
        self._names = names

    async def answer(self, request: ConversationRequest, deadline: float) -> ConversationResponse:
        schema = output_schema(sorted(self._expressions))
        rendered = self._template.render(
            values(request, self._template, self._expressions, self._texts)
        )
        locale = plain_id(request.display_locale)
        check = grounding_check(request, self._template, self._texts, self._names)
        answer = await ask_model(
            role=ROLE,
            job=request,
            rendered=rendered,
            schema=schema,
            parse=lambda raw: parse(raw, schema, self._guard, self._texts is not None, check),
            client=self._client,
            queue=self._queue,
            settings=self._settings,
            log=self._log,
            priority=Priority.PLAYER,
            deadline=deadline,
        )
        data = with_words(grounded(request, answer.value), self._texts, locale)
        return _response(
            request, data, "model", self._client.model, answer.latency_ms, answer.attempts
        )


class MockConversation:
    """Deterministic stand-in, the same rule as the core's MockConversationPort.

    Little understanding gives gestures or not understood, more a short reply in the player's
    language.
    """

    TEXTS: ClassVar[dict[str, str]] = {"de": "Hm, ja.", "en": "Hm, yes."}

    async def answer(self, request: ConversationRequest, deadline: float) -> ConversationResponse:
        started = time.monotonic()
        roll = (int(request.seed) % 100) / 100
        understanding = float(request.speaker.understanding)
        if roll > understanding + 0.6:
            outcome, reply, expression = "not_understood", None, None
        elif understanding < 0.3:
            outcome, reply, expression = "gesture", None, "wave"
        else:
            locale = plain_id(request.display_locale)
            outcome, reply, expression = "reply", self.TEXTS.get(locale, self.TEXTS["en"]), None
        data = {"outcome": outcome, "reply": reply, "expression": expression, "rationale": "mock"}
        return _response(request, data, "mock", "mock", int((time.monotonic() - started) * 1000), 1)


class ReplayConversation:
    """Answers from the call log: the first valid model answer recorded for the request id."""

    def __init__(
        self,
        log: CallLog,
        guard: TextGuard,
        expressions: dict[str, str],
        texts: TextCatalog | None = None,
        prompts_dir: Path | None = None,
        names: tuple[str, ...] = (),
    ) -> None:
        self._log, self._guard, self._expressions = log, guard, expressions
        # v3 rows (W1737) are checked with their own prompt's settings, as when they were played.
        self._prompts_dir, self._names = prompts_dir, names
        self._texts = texts  # used for rows recorded with prompt v2 or later (W1701)
        self._answers: dict[str, dict[str, object]] | None = None

    async def answer(self, request: ConversationRequest, deadline: float) -> ConversationResponse:
        request_id = plain_id(request.request_id)
        if self._answers is None:  # read once; replays use a finished log
            self._answers = recorded_answers(self._log, ROLE)
        row = self._answers.get(request_id)
        if row is None:
            raise fail("model_unavailable", f"no recorded answer for request {request_id}")
        recorded = str(row.get("prompt_version", "")).rpartition(".v")[2]
        texts = self._texts if recorded.isdigit() and int(recorded) >= 2 else None
        check = None
        if recorded.isdigit() and int(recorded) >= 3 and self._prompts_dir is not None:
            template = load_prompt(self._prompts_dir, ROLE_PROMPT, int(recorded))
            check = grounding_check(request, template, texts, self._names)
        try:
            data = parse(
                str(row["raw_output"]),
                output_schema(sorted(self._expressions)),
                self._guard,
                texts is not None,
                check,
            )
            data = with_words(grounded(request, data), texts, plain_id(request.display_locale))
        except InvalidOutputError as error:
            raise fail(
                "model_unavailable", f"recorded answer does not fit request {request_id}"
            ) from error
        attempts = row.get("attempt", 1)
        return _response(
            request,
            data,
            "replay",
            str(row.get("model", "replay")),
            0,
            attempts if isinstance(attempts, int) else 1,
        )
