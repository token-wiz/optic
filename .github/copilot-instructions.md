# Copilot / AI Agent Instructions for OPTIC

This project is a small .NET command-line tool that queries a Cosmos-based chain
via gRPC and CometBFT RPC/REST to produce an address ledger and balances.

Key files & folders
- `OPTIC/Program.cs`: single-file top-level program (most logic lives here). When
  modifying behavior prefer adding helper methods below the top-level statements
  (there are already helper functions present). Example patterns: fallback memo
  resolution (gRPC -> CometRPC -> inferred label), and timestamp fallbacks.
- `OPTIC/OPTIC.csproj`: .NET project file (target `net9.0`), references `Grpc.Net.Client`,
  `Google.Protobuf` and `Grpc.Tools`. Protobuf generator is configured here.
- `protos/`: canonical proto sources (cosmos/tendermint). `protos/.../service.proto`
  is set to generate client stubs during build.
- `build.cmd`: thin wrapper that runs `dotnet clean && dotnet build && dotnet run`.
- `obj/Debug/net9.0/`: contains generated `.protodep` artifacts after build — used
  as proof that proto generation ran successfully.

How to build & run (developer workflows)
- Use the included `build.cmd` for a full clean-build-run cycle on Windows.
- Manual flow:
  - `dotnet restore` (if needed)
  - `dotnet build OPTIC.sln` or `dotnet build OPTIC/OPTIC.csproj`
  - `dotnet run --project OPTIC/OPTIC.csproj`
- The program expects a config file at `C:\workspace\optic\optic\optic.conf` by
  default (see `Program.cs` constant `ConfigFile`). For quick runs either create that
  path or edit the constant to point to a local config. The config keys used:
  `addr` (required), optional `grpc`, `rpc`, `lcd`, `denom`, `lookbackDays`, `pageLimit`, `maxPages`.

Project-specific patterns & gotchas
- Single-file top-level program: much of the logic executes before type/method
  definitions. When adding features, keep top-level behaviour intact and add
  focused helper functions below to preserve readability.
- Protobuf generation: `.cs` stubs are generated during `dotnet build` using
  `Grpc.Tools`. If you add/change `.proto` files, run `dotnet build` and verify
  generated code appears under `obj/` before editing call sites.
- Network fallbacks: the code aggressively uses fallbacks (gRPC -> CometRPC ->
  event-inference). When changing data flow, update all fallback locations and
  tests/helpers that parse `TxResponse` / Comet RPC JSON.
- Windows-centric defaults: file paths and timezone IDs target Windows (e.g.
  `Eastern Standard Time`). Keep that in mind when running on Linux/macOS.

Integration points
- gRPC: created via `GrpcChannel.ForAddress("http://{grpcTarget}")` and uses
  generated `Service.ServiceClient` (see `Program.cs`). If you change proto
  service names, update the `Protobuf` entries in the `.csproj` accordingly.
- REST/RPC: `HttpClient` instances target `lcd` and CometBFT RPC endpoints and
  are used as fallbacks for missing gRPC fields (memo, events, block-time).
- Protos are authoritative. Changes to `protos/` must be validated by rebuilding
  to ensure generated types match usage in `Program.cs`.

What to avoid
- Don't extract major control flow into DI-heavy constructs without a good
  reason — the codebase is intentionally small and imperative.
- Avoid changing the default config path silently; prefer making the path
  configurable via an environment variable or CLI flag and updating `Program.cs`.

Example edits to demonstrate intent
- To add a CLI flag for a custom config path: add a small preamble in top-level
  section that reads `Environment.GetEnvironmentVariable("OPTIC_CONFIG")` before
  falling back to the existing `ConfigFile` constant.

If something's unclear or you want a different focus (tests, refactor, proto
generation), tell me which area and I'll expand or iterate on this file.
