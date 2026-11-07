# ADR-0001: Pinned tool and runtime versions

- Status: proposed
- Date: 2026-11-07
- Deciders: TL, GW1, GW2 (approval: Marcel)

## Context

Builds and AI behaviour must be reproducible on the reference PC and in CI.
"latest" is never allowed (engineering handbook). Versions were read from the
development PC on 2026-11-07 (AMW #18208, #18209, #18238, #18267).

## Decision

| Tool | Version | Notes |
|---|---|---|
| Godot | 4.7.2-stable, .NET (mono) build | `GodotSharp` 4.7.2 requires at least `net8.0` |
| Godot export templates | 4.7.2.stable.mono | exports and CI builds |
| .NET SDK | 10.0.403 (LTS), pinned in `global.json` | |
| C# target framework | `net10.0` for all projects | verified: Godot 4.7.2 runs a `net10.0` project on .NET 10.0.14 |
| NuGet sources | repo-local `nuget.config` (`<clear/>` + nuget.org) | independent of machine-wide NuGet settings |
| Python | 3.12 (`.python-version`; dev PC 3.12.4) | AI service |
| uv | 0.11.10 | `uv.lock` with hashes is the pin source for Python dependencies |
| Ollama | 0.40.14 | external program, detected by the AI service |
| Language model | `qwen3.5:9b`, pinned by digest `56671c2ab938` | decider and rule guard (ADR-0005); tags can move |
| ComfyUI | 0.44.0 (own Python 3.13, PyTorch 2.14.1+cu130) | external program, used from B7 |
| Git | 2.53 | |
| Git LFS | 3.7.1 | binary assets |

C# packages are pinned with exact versions; Python packages through `uv.lock`.

## Consequences

- CI downloads exactly these versions; a version bump is a pull request that
  updates this ADR.
- `net10.0` is supported until November 2028; `net8.0` support ends on
  2026-11-10. `net10.0` also provides `JsonStringEnumMemberName`, used by the
  generated contract types.
- Godot upgrades must re-check the supported target frameworks.
