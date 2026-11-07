# Architecture Decision Records

Short records of decisions with weight (context, decision, consequences), MADR short form.
A new decision gets the next free number; a changed decision gets a new ADR that supersedes the old one.

| ADR | Title | Status |
|---|---|---|
| [0001](0001-versions.md) | Pinned tool and runtime versions | proposed |
| [0002](0002-core-without-godot.md) | Simulation core without Godot dependency | proposed |
| [0003](0003-sqlite-per-world.md) | One SQLite file per world | proposed |
| [0004](0004-construction-kit-builder.md) | Construction-kit builder as the standard builder | proposed |
| [0005](0005-one-model-two-roles.md) | One language model for decider and rule guard; core has the final say | proposed |
| [0006](0006-ai-service-http-lifecycle.md) | AI service over HTTP on localhost, started and stopped by the game | proposed |
| [0007](0007-data-files-and-localization.md) | Data file format, schema validation and DE/EN text keys | proposed |
