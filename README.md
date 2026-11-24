# Orchestration Engines

A dependency-aware engine orchestration library for .NET. It starts, monitors and restarts a set of interdependent runtime engines in a controlled order.

The library is published as a NuGet package. It targets `net10.0`.
Cancellation handlers in an engine's work must not block: they can run inline on the thread that stops it, under a
library lock, so start one with `await Task.Yield()` and do blocking work in `await Task.Run(...)` (see
[Overview](docs/Overview.md), on stopping).

## Repository layout

| Folder | Contents |
| --- | --- |
| `docs/` | Design and usage documentation |
| `src/` | The library itself |
| `tests/` | Unit tests |

## Building

Before a push, run the gate, the same checks the build on GitHub runs (bash; on Windows, Git Bash):

```bash
scripts/gate.sh
```

It checks the length of the lines the change adds, restores, builds (Release) and runs the tests; it
stops at the first step that fails. `--base <branch>` compares with another branch than `origin/main`, and arguments
after `--` go to `dotnet test`.

Without the gate:

```bash
dotnet build
dotnet test
```

Lines are at most 120 columns. `python scripts/check-line-length.py` checks the lines a change adds, against
`origin/main`, also uncommitted edits and new files. Three kinds of long line are let through: a line with a URL,
a row of a Markdown table, and a long line that only moves from one file to another.

Packing is blocked until the repository URL is set (`RepositoryUrl` in `Directory.Build.props`, see `docs/Releasing.md`).

## Code coverage

```bash
dotnet test --coverage --results-directory "$PWD/TestResults"
scripts/gate.sh -- --coverage --results-directory "$PWD/TestResults"   # the same, after the checks
```

The results directory must be an absolute path: the .NET 10.0.1xx SDK, the lowest band `global.json` allows, resolves a
relative one against the test app's folder (10.0.404 resolves it against the current folder). `$PWD` works in bash
and PowerShell; in cmd.exe, use `%CD%` instead. The test project sets the
coverage settings (`tests/CodeCoverage.settings.xml`) and the Cobertura format itself, through
`TestingPlatformCommandLineArguments`. Do not pass `--coverage-settings` or `--coverage-output-format` again: a second
copy of either option makes the test run fail before any test runs, with an error that does not name the option.

This writes one Cobertura report per target framework to `TestResults/`. The settings limit the measurement to the
library itself. Any tool that reads Cobertura can show the report, for example ReportGenerator or a CI coverage task.

## Licence

Copyright (c) 2026 Spatial Eye B.V.

This notice applies to every source file in this repository: This Source Code Form is subject to the terms of the
Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain one at
https://mozilla.org/MPL/2.0/. The full text is in `LICENSE`.

You may use, fork and change the code. If you distribute the library, changed or not, its source files stay under the
MPL-2.0 and keep their copyright and licence notices, and you make that source available to your recipients, also when
you ship it compiled. Your own code that uses the library may be under any licence.

The MPL-2.0 grants no rights in the trademarks, service marks or logos of Spatial Eye B.V. or any other contributor,
except as needed for the licence notices (section 2.3).

## Contributing

Contributions are welcome. A contribution is accepted under the MPL-2.0, the licence of the rest of the code, and stays
under it. No contributor licence agreement or sign-off is needed.
