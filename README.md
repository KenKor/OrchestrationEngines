# Orchestration Engines

A dependency-aware engine orchestration library for .NET. It starts, monitors and restarts a set of interdependent runtime engines in a controlled order.

The library is published as a NuGet package. It targets `netstandard2.0` (so it runs on .NET Framework 4.8) and `net10.0`.

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

Packing is blocked until the licence and other packaging details are decided. See `docs/Releasing.md`.

## Code coverage

```bash
dotnet test --coverage --results-directory "$PWD/TestResults"
```

The results directory must be an absolute path: the .NET 10.0.1xx SDK resolves a relative one against the test app's
folder. `$PWD` works in bash and PowerShell; in cmd.exe, use `%CD%` instead. The test project sets the
coverage settings (`tests/CodeCoverage.settings.xml`) and the Cobertura format itself, through
`TestingPlatformCommandLineArguments`.

This writes one Cobertura report per target framework to `TestResults/`. The settings limit the measurement to the
library itself. Any tool that reads Cobertura can show the report, for example ReportGenerator or a CI coverage task.
