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
| jsonschema | 4.25.1 | Python dev/test library | MIT | https://github.com/python-jsonschema/jsonschema | contract tests |
| referencing | 0.37.0 (via jsonschema) | Python dev/test library | MIT | https://github.com/python-jsonschema/referencing | contract tests |
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
