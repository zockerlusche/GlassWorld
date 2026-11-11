# ADR-0009: Outgoing requests are validated in tests and check runs, not in the running game

- Status: accepted (effective with the merge of the PR that adds it)
- Date: 2026-11-11
- Deciders: TL (decision "A" of PR #240), GW1

## Context

Until now the core validated every request it sent to the AI against its JSON
schema before it left (`ContractSerializer.Serialize`: "the core never sends
an invalid request"), and every answer before it was read ("AI output is
data", engineering handbook D334).

The budget for one simulation step is 4 ms (D334, B2 acceptance 3). The
measurement of PR #240 showed that a single decision request costs about 3 ms
inside the step, about 71 % of it the validation of the outgoing JSON (a
request is 11-15 kB, mostly the perceived objects; the validator needs about
2 ms and 3 MB of garbage for a large one). A tick that asks the decider one
question was over the budget by that alone; without requests the step with
100 inhabitants takes about 1 ms.

Three things make the validation of *outgoing* requests dispensable at run
time, and none of them holds for incoming answers:

- The core builds its requests from typed data (generated contract types),
  not from text. A request can only be invalid through a programming error
  in a request form, and such an error shows in every run, not by chance.
- The AI service validates every request it receives and answers an invalid
  one with a `ServiceError` (`bad_request`); it never acts on it.
- Tests and check runs exercise every request form with the validation on.

## Decision

- **Outgoing requests are validated in tests and check runs, not in the
  running game.** This holds for every request form of the core: decision
  requests, conversation requests, the night's memory and question jobs,
  invention jobs, build-plan jobs and sound jobs.
- **Incoming answers are validated exactly as before**, always and
  everywhere. `ContractSerializer.Deserialize` has no unvalidated form.
- **The switch** is `SimulationHostOptions.ValidateRequests`, handed by the
  host to the systems that send. It is **on by default**, so every host a
  test or a tool builds validates without asking for it, and off only where
  a host says so: the game (`WorldPresenter`) and, by default, the `perf`
  command of the headless runner, which measures the game's step
  (`--validate-requests` measures the other way). The headless `run` and
  `replay` commands - the acceptance and check runs, Release builds included -
  keep it on. A build-configuration switch (Debug/Release) was not chosen:
  the acceptance runs are Release builds and must validate.
- The switch is not world state: the same requests leave either way, and the
  results of a world do not depend on it.

### Conditions

1. **A contract test per request form stays mandatory**: a test that
   validates a really built request of that form against its schema.
   `RequestValidationTests` lists every request form with its contract test
   and scans the core: a place that serializes a request without the switch,
   or a request form that is not in the list, fails the test.
2. **A request the service refuses is a named, counted outcome.** A
   `ServiceError` with code `bad_request` is logged with the note
   `request_rejected` (decision log, job log; before: `service_error` like any
   error), the question's `question_failed` event carries it as its detail,
   `SimulationHost.RejectedRequests` counts them per world from the logs, and
   the headless runner prints the count in its summary (`requests .. rejected
   N`, `perf`: `REQUESTS .. rejected=N`). The number should stay 0; anything
   else means a request form and its contract have come apart.
3. **A port reports a failure also for a request that is not valid** (not
   reachable, an unusable error body): it no longer reads the request with
   validation to write the error, so a question never waits for an answer
   that cannot come.

## Consequences

- A tick that sends a request no longer pays for validating it; the numbers
  are in the PR that adds this ADR.
- An invalid request is no longer stopped inside the game process. It reaches
  the service, is refused there, and appears as `request_rejected` in the log
  of the world. Tests and check runs still stop it at the source with a
  `ContractViolationException`.
- Whoever adds a request form adds its contract test and names both in
  `RequestValidationTests`; the scan enforces it.
- Replays read `request_rejected` like `service_error`: both are error
  messages of the service at the tick they took effect.
