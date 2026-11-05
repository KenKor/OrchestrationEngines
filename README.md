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
