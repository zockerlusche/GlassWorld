# Changelog

All notable changes to this project are documented in this file.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- Repository foundation: license, readme, contributing guide, folder layout, Git attributes and ignore rules.
- Architecture Decision Records ADR-0001 to ADR-0007 (versions, core without Godot, SQLite per world, construction-kit builder, one model for two roles, AI service lifecycle, data files and localization).
- .NET solution `GlassWorld.slnx` with `Simulation.Core` (no Godot) and xUnit v3 tests; shared build settings (net10.0, nullable, warnings as errors, analyzers, code style in build), central package versions, `.editorconfig`, `global.json` and repo-local `nuget.config`.
