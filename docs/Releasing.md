# Releasing

The library is published as one NuGet package, `OrchestrationEngines`. It holds both builds: `lib/netstandard2.0`
(used by .NET Framework 4.8 and older .NET versions) and `lib/net10.0`. NuGet picks the right one for each consumer, so
there is no package per framework. The symbols go to a separate `.snupkg` next to it.

## Current state

The pipeline is not finished on purpose. The workflow in `.github/workflows/build.yml` runs on every push to `main`,
every pull request and every `v*` tag:

| Step | State |
| --- | --- |
| Build and test (net10.0 and net48, on Windows) | Works |
| Coverage report as a build artifact | Works |
| Pack on a pull request or a push to `main` | Warns until the licence, copyright and repository URL are set |
| Pack on a `v*` tag | Fails until the licence, copyright and repository URL are set |
| Publish (only on a `v*` tag) | Fails until publishing is set up |

The warning on pull requests lets their checks pass. The package those builds produce is for checking only and must not
be published. Packing locally fails, like on a tag, with a message that names what is missing. The check is the
`CheckPackagingDecisions` target in `Directory.Build.targets`; `RequirePackagingDecisions=false` turns the error into the
warning.

## Open decisions

| Decision | Where it goes | Notes |
| --- | --- | --- |
| Licence | `PackageLicenseExpression` in `Directory.Build.props`, plus a `LICENSE` file | An SPDX expression, for example `MIT` or `Apache-2.0`. |
| Copyright holder | `Copyright` in `Directory.Build.props` | |
| Repository home | `RepositoryUrl` in `Directory.Build.props` | Where the repository will live for good. Debuggers fetch source from it through SourceLink. |
| Package feed and owner | `publish` job in the workflow | nuget.org, a private feed, or both. On nuget.org: which account or organisation owns the package. |
| Package ID | `PackageId` in the `.csproj` | Check that `OrchestrationEngines` is still free on nuget.org; an ID prefix can be reserved there. |
| Feed login | `publish` job in the workflow | nuget.org Trusted Publishing (no stored key) or an API key in a GitHub secret. |
| Versioning | `.csproj` or a versioning package | Version from git tags (for example MinVer) or set by hand. Also the first version: 0.1.0 or 1.0.0. |
| Strong naming | `.csproj` and a key file | Needed if any strong-named .NET Framework assembly will reference this package. |

## Finishing the pipeline

1. Set the licence, copyright and repository URL in `Directory.Build.props`, and add the `LICENSE` file.
2. Remove the `CheckPackagingDecisions` target from `Directory.Build.targets` (and the file, if nothing else is in it).
3. Set up versioning. With version tags, a release is then started by pushing a tag such as `v1.0.0`.
4. Replace the step in the `publish` job with a download of the `packages` artifact and a
   `dotnet nuget push --skip-duplicate` of the `.nupkg` and `.snupkg` files. Add a protected GitHub environment for the
   job, and the feed login.
5. Check that the README reads well on the feed: it is packed into the package, and relative links do not work there.
6. Once a first version is out, set `PackageValidationBaselineVersion` in the `.csproj` so that breaking API changes
   against that version fail the build.
