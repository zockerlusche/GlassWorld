# Third-party licenses

Every dependency, model and asset used by GlassWorld is listed here.
Allowed without further approval: Apache 2.0, MIT, CC0, and since decision B2482 also BSD-2-Clause,
BSD-3-Clause, PSF-2.0, ISC and MPL-2.0 (MPL files used unmodified). The project's MIT license must not
be affected; GPL/AGPL/LGPL, non-commercial and unknown licenses need Marcel's approval.
Distributions must ship the license notices of all listed components.

| Name | Version | Kind | License | Source | Used in |
|---|---|---|---|---|---|
| Microsoft.NET.Test.Sdk | 18.11.0 | NuGet package (test only) | MIT | https://github.com/microsoft/vstest | tests/ |
| xunit.v3 | 4.0.2 | NuGet package (test only) | Apache-2.0 | https://github.com/xunit/xunit | tests/ |
| xunit.runner.visualstudio | 4.0.1 | NuGet package (test only) | Apache-2.0 | https://github.com/xunit/visualstudio.xunit | tests/ |
| Microsoft.CodeAnalysis.BannedApiAnalyzers | 5.6.0 | NuGet analyzer (build time only) | MIT | https://github.com/dotnet/roslyn | src/Simulation.Core |
| Microsoft.Data.Sqlite | 10.0.14 | NuGet package (runtime, save games) | MIT | https://github.com/dotnet/efcore | src/Simulation.Data |
| SQLitePCLRaw.bundle_e_sqlite3 (+ core, provider) | via Microsoft.Data.Sqlite | NuGet package (runtime, native SQLite) | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw | src/Simulation.Data |
| SQLite | via SQLitePCLRaw | native library | Public Domain | https://sqlite.org | src/Simulation.Data |
| pydantic | >=2.12,<3 (locked 2.14.3) | Python runtime library | MIT | https://github.com/pydantic/pydantic | contracts/python, ai-service |
| jsonschema | 4.25.1 | Python runtime library (ai-service request validation) and tests | MIT | https://github.com/python-jsonschema/jsonschema | ai-service, contract tests |
| referencing | 0.37.0 (via jsonschema) | Python runtime library (via jsonschema) | MIT | https://github.com/python-jsonschema/referencing | ai-service, contract tests |
| datamodel-code-generator | 0.90.0 | Python dev tool | MIT | https://github.com/koxudaxi/datamodel-code-generator | contracts/generate_python.py |
| pytest | 8.4.2 | Python dev tool | MIT | https://github.com/pytest-dev/pytest | tests |
| ruff | 0.14.4 | Python dev tool | MIT | https://github.com/astral-sh/ruff | lint/format |
| pyright | 1.1.414 | Python dev tool | MIT | https://github.com/microsoft/pyright | type check |
| hatchling | 1.27.0 | Python build backend | MIT | https://github.com/pypa/hatch | contracts/python packaging |
| typing-extensions | 4.16.0 (via pydantic) | Python runtime library | PSF-2.0 | https://github.com/python/typing_extensions | contracts/python, ai-service |
| jinja2, markupsafe | 3.1.6, 3.0.4 (via datamodel-code-generator) | Python dev tool | BSD-3-Clause | https://github.com/pallets | contracts/generate_python.py |
| pathspec | 1.1.1 (via black) | Python dev tool | MPL-2.0, unmodified | https://github.com/cpburnz/python-pathspec | contracts/generate_python.py |
| pygments | 2.21.0 (via pytest) | Python dev tool | BSD-2-Clause | https://github.com/pygments/pygments | tests |
| colorama | 0.4.6 (via pytest/click, Windows) | Python dev tool | BSD-3-Clause | https://github.com/tartley/colorama | tests |
| nodeenv | 1.12.0 (via pyright) | Python dev tool | BSD-3-Clause | https://github.com/ekalinin/nodeenv | type check |
| fastapi | >=0.120,<1 (locked 0.162.0) | Python runtime library | MIT | https://github.com/fastapi/fastapi | ai-service |
| pydantic-settings | >=2.11,<3 (locked 2.16.2) | Python runtime library | MIT | https://github.com/pydantic/pydantic-settings | ai-service |
| anyio | 4.16.0 (via starlette/httpx) | Python runtime library | MIT | https://github.com/agronholm/anyio | ai-service |
| h11 | 0.16.0 (via uvicorn/httpcore) | Python runtime library | MIT | https://github.com/python-hyper/h11 | ai-service |
| starlette | 1.10.0 (via fastapi) | Python runtime library | BSD-3-Clause | https://github.com/encode/starlette | ai-service |
| uvicorn | >=0.38,<1 (locked 0.54.0) | Python runtime library | BSD-3-Clause | https://github.com/encode/uvicorn | ai-service |
| httpx | >=0.28,<1 (locked 0.28.1) | Python runtime library | BSD-3-Clause | https://github.com/encode/httpx | ai-service |
| httpcore | 1.0.9 (via httpx) | Python runtime library | BSD-3-Clause | https://github.com/encode/httpcore | ai-service |
| idna | 3.20 (via httpx) | Python runtime library | BSD-3-Clause | https://github.com/kjd/idna | ai-service |
| click | 8.5.1 (via uvicorn) | Python runtime library | BSD-3-Clause | https://github.com/pallets/click | ai-service |
| python-dotenv | 1.2.4 (via pydantic-settings) | Python runtime library | BSD-3-Clause | https://github.com/theskumar/python-dotenv | ai-service |
| certifi | 2026.10.23 (via httpx) | Python runtime library | MPL-2.0, unmodified | https://github.com/certifi/python-certifi | ai-service |
| attrs | locked (via jsonschema) | Python runtime library | MIT | https://github.com/python-attrs/attrs | ai-service |
| rpds-py | locked (via jsonschema) | Python runtime library | MIT | https://github.com/crate-py/rpds | ai-service |
| jsonschema-specifications | locked (via jsonschema) | Python runtime library | MIT | https://github.com/python-jsonschema/jsonschema-specifications | ai-service |
| UI frames (driftwood, comic) | game/ui/frames/*_v1.png | Asset, self-generated | MIT (project) | z-image-turbo (Apache-2.0) via ComfyUI, prompts/ui/ui-frames.v1/v2, seed 20261107, AMW docs D379-D381, D392-D395, processed with tools/ui/nine_slice.py | game UI (A1362, Marcel F5251=B) |
| other transitive Python packages | see `uv.lock` | Python | MIT or Apache-2.0 | PyPI | dev tools |
| NJsonSchema | 11.6.1 | NuGet package (runtime contract validation, generator) | MIT | https://github.com/RicoSuter/NJsonSchema | src/Contracts, tests/, tools/ |
| NJsonSchema.CodeGeneration / .CSharp | 11.6.1 | NuGet package (dev tool) | MIT | https://github.com/RicoSuter/NJsonSchema | tools/ContractGen |
| Newtonsoft.Json | 13.0.3 | NuGet package (via NJsonSchema, runtime) | MIT | https://github.com/JamesNK/Newtonsoft.Json | src/Contracts, tests/, tools/ |
| Namotion.Reflection | 3.5.0 | NuGet package (via NJsonSchema, runtime) | MIT | https://github.com/RicoSuter/Namotion.Reflection | src/Contracts, tests/, tools/ |
| Fluid.Core | 2.30.0 | NuGet package (via generator) | MIT | https://github.com/sebastienros/fluid | tools/ContractGen |
| Parlot | 1.4.0 | NuGet package (via Fluid) | BSD-3-Clause (B2482) | https://github.com/sebastienros/parlot | tools/ContractGen |
| TimeZoneConverter | 7.0.0 | NuGet package (via Fluid) | MIT | https://github.com/mattjohnsonpint/TimeZoneConverter | tools/ContractGen |
| Microsoft.Extensions.FileProviders.Abstractions / .Primitives | 9.0.0 | NuGet package (via Fluid) | MIT | https://github.com/dotnet/runtime | tools/ContractGen |
| Microsoft.CodeAnalysis.CSharp / .Common | 5.6.0 | NuGet package (test only, CI text check) | MIT | https://github.com/dotnet/roslyn | tests/Localization.Tests |
| Atkinson Hyperlegible (Regular, Bold) | google/fonts commit 95f4904 (2026-03-03); SHA-256 Regular 7fb917c8…97669, Bold 5a3b0c8c…ab92f | Font (game UI) | SIL OFL 1.1 (unmodified, approved F5242; license text in `game/ui/fonts/OFL.txt`) | https://github.com/google/fonts/tree/main/ofl/atkinsonhyperlegible (Braille Institute of America) | game/ui/fonts |
| Quaternius Universal Animation Library (Standard) – mannequin and 46 clips | Standard 2.0 via OpenGameArt `universal_animation_librarystandard.zip` (SHA-256 18ff1a72…3ac2); GLB SHA-256 1b7bf678…ee5c | 3D model and animations (look prototype A1435, B2 R27) | CC0 1.0 (license text in `game/assets/characters/quaternius-ual1/License.txt`) | https://opengameart.org/content/universal-animation-library (author: Quaternius, https://quaternius.com) | game/assets/characters/quaternius-ual1 |
| Kenney Impact Sounds – footsteps (grass, wood, concrete, snow), impactMining, impactWood_light | 1.0 (19.12.2019), archive SHA-256 029d734a…c5de77f8, via the official download at kenney.nl (no account) | Sound files (base library, normalised to 16-bit WAV by `tools/SoundBase`) | CC0 1.0 | https://kenney.nl/assets/impact-sounds (author: Kenney) | game/assets/sound/base (step.*, object.knock_*) |
| Kenney RPG Audio – chop, creak1-3 | archive SHA-256 6dbeaf85…a7375f38b, via the official download at kenney.nl (no account) | Sound files (base library, normalised) | CC0 1.0 | https://kenney.nl/assets/rpg-audio (author: Kenney) | game/assets/sound/base (object.chop_wood, object.creak_*) |
| Base sounds generated with MOSS-SoundEffect v2.0 (11 files) | model revision e35df4d; bf16, offload, 50 steps; prompt and seed per file in `content/sound/library.json` | Sound files (own work, generated locally) | MIT (repository licence); model Apache-2.0 (D397) | this repository; model https://huggingface.co/OpenMOSS-Team/MOSS-SoundEffect-v2.0 | game/assets/sound/base (ambience.*, object.fire, object.strike_spark, animal.*, step.sand, step.water, action.rustle) |
| NVorbis | 0.10.5 | NuGet package (tool only, Ogg Vorbis decoding in `tools/SoundBase`), not shipped | MIT | https://github.com/NVorbis/NVorbis | tools/SoundBase |
