# Third-party licenses

Every dependency, model and asset used by GlassWorld is listed here.
Allowed without further approval: Apache 2.0, MIT, CC0, and since decision B2482 also BSD-2-Clause,
BSD-3-Clause, PSF-2.0, ISC and MPL-2.0 (MPL files used unmodified). The project's MIT license must not
be affected; GPL/AGPL/LGPL, non-commercial and unknown licenses need Marcel's approval.
Distributions must ship the license notices of all listed components.

| Name | Version | Kind | License | Source | Used in |
|---|---|---|---|---|---|
| CPython (embedded, python-build-standalone) | 3.12.13 | Python runtime of the packaged AI service (B10-S2) | PSF-2.0 | https://github.com/astral-sh/python-build-standalone (project MPL-2.0, unmodified) | release package `ai-service/python/` |
| OpenSSL (in the embedded Python) | 3.x | Library | Apache-2.0 | https://www.openssl.org | `ai-service/python/DLLs` |
| libffi (in the embedded Python) | 3.x | Library | MIT | https://sourceware.org/libffi | `ai-service/python/DLLs` |
| expat (in the embedded Python) | 2.x | Library | MIT | https://libexpat.github.io | `ai-service/python` (pyexpat) |
| mpdecimal (in the embedded Python) | 2.5 | Library | BSD-2-Clause | https://www.bytereef.org/mpdecimal | `ai-service/python` (_decimal) |
| zlib (in the embedded Python) | 1.3 | Library | Zlib - **approval pending (F5265)** | https://zlib.net | inside `python312.dll` |
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
| Base sounds generated with MOSS-SoundEffect v2.0 (28 files) | model revision e35df4d; bf16, offload, 50 steps; prompt and seed per file in `content/sound/library.json` | Sound files (own work, generated locally) | MIT (repository licence); model Apache-2.0 (D397) | this repository; model https://huggingface.co/OpenMOSS-Team/MOSS-SoundEffect-v2.0 | game/assets/sound/base (ambience.*, weather.rain/storm, object.*, animal.*, step.*, action.*) |
| BigSoundBank (author: Joseph Sardin) – single recordings: https://bigsoundbank.com/annoyed-hen-s0453.html, https://bigsoundbank.com/apple-crunched-2-s1114.html, https://bigsoundbank.com/bleating-goat-1-s0279.html, https://bigsoundbank.com/one-frog-s0819.html, https://bigsoundbank.com/singing-insect-1-s3089.html, https://bigsoundbank.com/solitary-bees-or-flies-s0101.html, https://bigsoundbank.com/splash-small-6-s1534.html, https://bigsoundbank.com/steps-in-the-mud-s0495.html, https://bigsoundbank.com/tawny-owl-3-s3459.html | single files, downloaded from the page (no account) | Sound files (base library, normalised by `tools/SoundBase`) | CC0 1.0 | https://bigsoundbank.com | game/assets/sound/base (action.eat, animal.bee.buzz, animal.fish.splash, animal.frog.croak, animal.goat.bleat, animal.owl.hoot, animal.wild_chicken.cluck, step.water, weather.heat) |
| OpenGameArt.org (author: qubodup) – single recordings: https://opengameart.org/content/liquid-bottle-drink-set | single files, downloaded from the page (no account) | Sound files (base library, normalised by `tools/SoundBase`) | CC0 1.0 (page licence field: "CC0", checked 08.11.2026; the page lists no other licence) | https://opengameart.org | game/assets/sound/base (action.drink) |
| Z-Image-Turbo (GGUF Q5_K_M), Qwen3-4B text encoder, Z-Image VAE | as registered in the AMW (D113) | Image model, run locally through ComfyUI (not shipped in the repo) | Apache-2.0 | official sources per AMW register (B2432) | `tools/comfyui/workflows/z-image-turbo-t2i.api.json`, image jobs tier standard (B7 S5) |
| Qwen-Image-Edit-2511 (GGUF Q4_K_M), Qwen2.5-VL-7B (fp8), Qwen-Image VAE | as registered in the AMW (D113) | Image edit model, run locally through ComfyUI (not shipped in the repo) | Apache-2.0 | official sources per AMW register (B2432) | `tools/comfyui/workflows/qwen-image-edit-style.api.json`, image jobs tier comfort (B7 S5) |
| Style reference warm comic (D355) | KI run L593, z-image-turbo, seed 20261110, prompt `prompts/style/s1-style-variants.v1` (warm_comic) | Image (own work, generated locally) | MIT (repository licence); model Apache-2.0 | this repository | `content/image/style-reference-warm-comic.png` (style reference for tier comfort, B2501) |
| NVorbis | 0.10.5 | NuGet package (tool only, Ogg Vorbis decoding in `tools/SoundBase`), not shipped | MIT | https://github.com/NVorbis/NVorbis | tools/SoundBase |
| GlassWorld figure bodies (adult_female, adult_male, child; 4 hair styles) | built by `tools/figures/build_figures.py` from `tools/figures/bodies.json` | 3D models (own work) on the Quaternius CC0 rig with its CC0 clips | MIT (repository license); clips CC0 | this repository | game/assets/characters/glassworld |
| Blender | 4.5.15 LTS (portable, SHA-256 52dd02fd…74f8d), not distributed | Authoring tool (headless model build, B2474) | GPL-3.0-or-later, tool only, output is our own | https://download.blender.org/release/Blender4.5/ | tools/figures |
