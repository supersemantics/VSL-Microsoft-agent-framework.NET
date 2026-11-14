# Changelog

What changed in each release, and why. Newest first. Tagged `vX.Y.Z` on `main` when a pull request merges.

## Unreleased: the publish workflow no longer adds a GitHub build attestation

**Before:** 0.1.2's publish workflow added a GitHub build attestation for the package, and this CHANGELOG said anyone could check the package with `gh attestation verify`.

**After:** the attestation step is gone, and the 0.1.2 entry below is corrected. Publishing is otherwise unchanged: Trusted Publishing, a one-hour key, no stored API key. No new package version: nothing in the package changes.

**Why:** nuget.org adds its own repository signature to every package it accepts, which changes the file. The attestation covered only the file built in GitHub Actions, never the one you download from nuget.org, so it couldn't be used to check that download. To check a package from nuget.org, use its signature (`dotnet nuget verify --all <file>.nupkg`).

## 0.1.2 (2026-11-14, tag `v0.1.2`): published from GitHub Actions through NuGet Trusted Publishing

**Before:** 0.1.1 was packed on a laptop and pushed to nuget.org with a personal API key.

**After:** pushing a `vX.Y.Z` tag runs `.github/workflows/publish.yml`. It runs the tests, checks the tag matches the version, packs only `SuperSemantics.Vsl.AgentFramework` (with symbols; `SuperSemantics.Vsl` is published from VSL-Core.NET), adds a GitHub build attestation (see Unreleased: it did not match the file on nuget.org), and pushes to nuget.org with a key that nuget.org issues for one hour to this repository, workflow and `nuget` environment only. No API key is stored anywhere. The code is unchanged from 0.1.1.

**Why:** PyPI and npm already publish this way. A key stored on a laptop can leak and has to be rotated by hand; a one-hour key tied to this workflow can't be reused elsewhere.

## 0.1.1 (2026-11-12, tag `v0.1.1`): the README works as the NuGet package page; SuperSemantics.Vsl 0.1.1

**Before:** the README, which NuGet shows as the package page, said the package was "not yet published" and that `SuperSemantics.Vsl` wasn't on NuGet. Its LICENSE link was relative, so it broke on nuget.org. The submodule was pinned to VSL-Core.NET `v0.1.0`.

**After:** the README gives the `dotnet add package` command, says NuGet installs `SuperSemantics.Vsl` with this package, keeps the submodule only for building from source, and links to LICENSE on GitHub. The submodule is pinned to VSL-Core.NET `v0.1.1`, so the package depends on `SuperSemantics.Vsl` 0.1.1. The code is unchanged from 0.1.0, and so is SuperSemantics.Vsl's.

**Why:** 0.1.1 is the first version published to NuGet, and a published version's page can't be changed later.

## 0.1.0 (2026-11-09, tag `v0.1.0`)

### First release: VSL gates for Microsoft Agent Framework on .NET

**Before:** VSL gates could be wired into Microsoft Agent Framework from Python only (vsl-maf).

**After:** `SuperSemantics.Vsl.AgentFramework` brings the same behaviour as vsl-maf 0.2.0 to .NET, on `SuperSemantics.Vsl` 0.1.0 (a port of vsl-core 0.3.0) and Microsoft.Agents.AI 1.28.0:
- `MafAdapter(identityKey)`: compiles PreNodes and Invariants and passes the conformance suite.
- `UseVslGate` / `UseVslGates`: gate tool calls before they run. The default `VslDenialMode.Abort` stops the function-invocation loop and throws `VslGateDeniedException` to the caller of `RunAsync` or `RunStreamingAsync`. `ReturnToModel` keeps the framework's tool-error behaviour. A failing gate throws `VslGateFailedException`.
- `VslToolArguments`: a model's JSON arguments as plain values.
- An F2 heuristic that reads a monitor's compiled code for hosted-model calls.

`SuperSemantics.Vsl` comes from the `external/VSL-Core.NET` git submodule, pinned to VSL-Core.NET `v0.1.0`, until it is on NuGet.

**Why:** a throw from function middleware is absorbed by the Agent Framework. The tool does not run, but the model is told "Error: Function failed.", other tools in the batch still run, and `RunAsync` returns normally. Checked against Microsoft.Agents.AI 1.28.0 before this package was written. A gate the application cannot see is fail-open at the level of the run.

**Differs from Python:** separate `UseVslGate` calls run last-registered first (the Agent Framework for .NET's own composition order; MAF for Python runs list order). `UseVslGates` runs gates in the listed order, to match Python.

**Tests:** 19 pass on .NET 10.0.403. They include end-to-end runs through a real `ChatClientAgent` with a scripted chat client: allowed calls, aborts (including the rest of the batch), `ReturnToModel`, Invariant denials, gate failures, scoping, JSON arguments, streaming, chaining order, recorded gate decisions, and the conformance suite. Also checked: the example's JSONL ledger verifies with Python vsl-core.
