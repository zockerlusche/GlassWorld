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
| other transitive Python packages | see `uv.lock` | Python | MIT or Apache-2.0 | PyPI | dev tools |
