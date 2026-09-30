# Repository Guidelines

## Product Documents

Read `CHECKPOINT.md` first for implemented state and current evidence. `VISION.md`
describes the AI-civilization direction; `ROADMAP.md` is the active version plan;
`TECH_STACK.md` records technology preferences; `RELEASE_PLAN.md` describes future
packaging and publication. Treat planned features and acceptance targets as plans
until implemented and verified. `docs/archive/` preserves historical plans and
does not override the active roadmap. Keep the current working simulation as the
basis for incremental playable slices.

## Project Structure

Hollowbound is a C#/.NET 10 MonoGame DesktopGL simulation. `Program.cs` selects GUI or command-line modes; `Game1.cs` and `Game1.Experience.cs` implement the game UI and player experience. Simulation logic, persistence, analytics, indexing, and regression scenarios live in `Simulation/`. `HeadlessRunner.cs`, `Benchmark.cs`, and `AnalyticsReport.cs` provide CLI workflows. `Content/` contains MonoGame content and fonts. `saves/` may contain local worlds; treat it as user data.

## Build, Run, and Test

- `dotnet restore` restores the pinned project dependencies.
- `dotnet build Hollowbound.sln -v minimal` builds the solution.
- `dotnet run` launches the game window.
- `dotnet run -- --playtest` starts an isolated playtest using a temporary save.
- `dotnet run -- --self-test all` runs the headless regression suite; use a scenario such as `ecology`, `pathfinder`, or `continuation` to target one area.
- `dotnet run -- --test-camera-layout` checks camera and UI layout cases.
- `dotnet run -- --benchmark 12345 4000 500 300` runs a repeatable headless benchmark.

Self-tests report `[PASS]` or `[FAIL]` and return a nonzero exit code on failure. Do not claim a test or performance result unless it was run on the current changes; GUI behavior and FPS require a real window session.

## Style and Architecture

Follow standard C# conventions: four-space indentation, `PascalCase` for types and public members, `camelCase` for parameters and locals, and `_camelCase` for private fields. Keep deterministic simulation behavior independent of rendering and wall-clock timing. Preserve save compatibility when changing serialized state; add or update focused self-test scenarios for behavior and continuation.

## Changes and Reviews

Recent commits use short imperative subjects, often with prefixes such as `feat:` (for example, `feat: add First Cycle performance and impact telemetry`). Keep commits focused. Pull requests should explain behavior and rationale, list validation commands and results, and include screenshots for visible UI changes.

## Local State and Configuration

Preserve existing uncommitted work and files in `saves/`. Do not use destructive Git cleanup/reset commands or overwrite local saves as part of routine development. Keep machine-specific settings and generated logs out of commits.
