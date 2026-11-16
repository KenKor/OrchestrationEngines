# Orchestration Engines

A dependency-aware engine orchestration library for .NET. It starts, monitors and restarts a set of interdependent runtime engines in a controlled order.

The library is published as a NuGet package. It targets `netstandard2.0` (so it runs on .NET Framework 4.8) and `net10.0`.
Cancellation handlers in an engine's work must not block: on .NET Framework they can run inline on the thread that
stops it, under a library lock, so start one with `await Task.Yield()` (see [Overview](docs/Overview.md), on stopping).

## Repository layout

| Folder | Contents |
| --- | --- |
| `docs/` | Design and usage documentation |
| `src/` | The library itself |
| `tests/` | Unit tests |

## Building

```bash
dotnet build
dotnet test
```

The tests run on both `net10.0` and `net48` (the `netstandard2.0` build on .NET Framework), so `dotnet test` needs Windows.

Packing is blocked until the open packaging decisions in `docs/Releasing.md` are made.

## Code coverage

```bash
dotnet test --coverage --results-directory "$PWD/TestResults"
```

The results directory must be an absolute path: the .NET 10.0.1xx SDK resolves a relative one against the test app's
folder. `$PWD` works in bash and PowerShell; in cmd.exe, use `%CD%` instead. The test project sets the
coverage settings (`tests/CodeCoverage.settings.xml`) and the Cobertura format itself, through
`TestingPlatformCommandLineArguments`. Do not pass `--coverage-settings` or `--coverage-output-format` again: a second
copy of either option makes the test run fail before any test runs, with an error that does not name the option.

This writes one Cobertura report per target framework to `TestResults/`. The settings limit the measurement to the
library itself. Any tool that reads Cobertura can show the report, for example ReportGenerator or a CI coverage task.

## Licence

Copyright (c) 2026 Spatial Eye.

This notice applies to every source file in this repository: This Source Code Form is subject to the terms of the
Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed with this file, You can obtain one at
https://mozilla.org/MPL/2.0/. The full text is in `LICENSE`.

You may use, fork and change the code. If you distribute the library, changed or not, its files stay under the
MPL-2.0: you make their source available to your recipients and keep the copyright and licence notices. Your own code
that uses the library may be under any licence.

The MPL-2.0 grants no trademark rights (section 2.3). Spatial Eye keeps the name `OrchestrationEngines` and the package
id for its own releases; a fork that is distributed uses its own name.
