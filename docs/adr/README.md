# Architecture Decision Records

Short records of decisions with weight (context, decision, consequences), MADR short form.
A new decision gets the next free number; a changed decision gets a new ADR that supersedes the old one.

| ADR | Title | Status |
|---|---|---|
| [0001](0001-versions.md) | Pinned tool and runtime versions | accepted |
| [0002](0002-core-without-godot.md) | Simulation core without Godot dependency | accepted |
| [0003](0003-sqlite-per-world.md) | One SQLite file per world | accepted |
| [0004](0004-construction-kit-builder.md) | Construction-kit builder as the standard builder | accepted |
| [0005](0005-one-model-two-roles.md) | One language model for decider and rule guard; core has the final say | accepted |
| [0006](0006-ai-service-http-lifecycle.md) | AI service over HTTP on localhost, started and stopped by the game | accepted |
| [0007](0007-data-files-and-localization.md) | Data file format, schema validation and DE/EN text keys | accepted |
| [0008](0008-presentation-logic-in-csharp.md) | UI and presentation logic in a Godot-free C# library | accepted |
| [0009](0009-outgoing-requests-validated-in-tests-not-in-the-game.md) | Outgoing requests are validated in tests and check runs, not in the running game | accepted |
