# OPTIC AI Agent Instructions

## Project Overview
**OPTIC** (Optio Protocol Telemetry & Intelligence Center) is a C# .NET 9 command-line tool that queries blockchain data from the Optio Protocol to generate comprehensive financial reports on addresses, transactions, locks, distributions, and staking.

## Architecture

### Core Components
- **Program.cs**: Monolithic entry point (~7000 lines) containing all executable statements at top-level, followed by method definitions. All major modes (distributions, locks, balances, etc.) are controlled by argument parsing.
- **Services/**: Data-fetching layer communicating with blockchain APIs
  - **BankService**: LCD HTTP queries for balance/spendable amounts (`cosmos/bank/v1beta1`)
  - **StakingService**: LCD HTTP for delegation queries (`cosmos/staking/v1beta1`)
  - **LockService**: Dual-path lock data retrieval (gRPC Protobuf for Optio custom lockup module + HTTP fallback for vesting module)
  - **ServiceUtils**: Static utilities for JSON parsing, coin amount calculation, Unix timestamp conversion
- **WebDashboard.cs**: Embedded ASP.NET Core web server providing HTML UI for running reports
- **Models/LockRow.cs**: Simple record type for lock data
- **protos/**: Protobuf definitions (auto-generated C# code) for Cosmos and Optio modules

### Data Flow
1. Argument parsing determines execution mode
2. Configuration from `optic.conf` (address, denom, gRPC/LCD endpoints, pagination)
3. HTTP calls via `BankService`/`StakingService` → LCD REST API
4. gRPC calls via `LockService` → Blockchain node gRPC endpoint (port 9090)
5. Results processed into CSV/text reports or web UI display

## Key Patterns & Conventions

### Argument Parsing
- Conventions: `Parse{Feature}Arg()` methods scattered through Program.cs
- Returns defaults if not found; some flags are boolean (no value required)
- Example: `ParseMultiSendSumArg(args)` returns bool; `ParseEmitterArg(args)` returns string?

### Mode-Driven Execution
- Mutually exclusive modes controlled by flags: `--multisend-sum`, `--locks`, `--balances`, `--web`, etc.
- Early returns in Program.cs top-level if web mode detected
- Modes often have related parameters (e.g., `BlockScanMultiSend` + `BlockScanStart`/`BlockScanEnd`)

### Error Handling
- Services use `Try*` pattern: `TryGetJsonAsync()`, `TryGetCoinAmount()` return null or bool instead of throwing
- Silent failures common; check for null results before processing
- Fallback logic: LockService tries Optio lockup module first, falls back to vesting derivation

### gRPC + HTTP Hybrid
- Protobuf-generated code in `protos/` (run `buf.exe` to regenerate from .proto files)
- `LockService` uses `GrpcChannel` for custom Optio module, `HttpClient` for LCD endpoints
- Both initialized in top-level Program.cs

### Configuration
- **optic.conf**: Properties file format (addr, denom, grpc, lcd, lookbackDays, pageLimit, maxPages)
- Overridable via code defaults; env var support unknown
- Hard-coded default paths and values in Program.cs for testing/development

## Development Workflow

### Build & Run
```bash
# From workspace root
dotnet clean
dotnet build
dotnet run [arguments]
```

### Common Arguments
- `--web [host] [port]`: Start ASP.NET web dashboard (default 127.0.0.1:5070)
- `--multisend-sum`: Summarize multi-send transactions
- `--locks --address <addr>`: Get lock data for address
- `--help`: Print help (implemented but not shown here)

### Protobuf Regeneration
- `.proto` files in `protos/` auto-included in build via `OPTIC.csproj`
- `buf.exe` available in root directory for manual regeneration
- Generated C# code in `obj/Debug/net9.0/` (not committed)

## Project-Specific Knowledge

### Denom & Amount Handling
- Denom names are case-insensitive (uOPT, uopt treated as same)
- Amounts are always `decimal` for precision (avoiding double-rounding on blockchain values)
- Parsing: `ServiceUtils.TryParseAmount()` uses `CultureInfo.InvariantCulture`

### Time Zones
- Configuration includes timezone field (`America/New_York` in optic.conf)
- Unix timestamps converted via `DateTimeOffset.FromUnixTimeSeconds()`
- Use `System.Globalization.CultureInfo.InvariantCulture` for locale-independent parsing

### Testing & Data Export
- No unit test framework; validation via CSV/text outputs and manual inspection
- Reports written to files (e.g., `optio_daily_2025_cmc.csv`, `wallet_balances.csv`)
- Data files in workspace root used for verification (e.g., `distributions.txt`, `locks.txt`)

### Blockchain Endpoints
- **gRPC**: `127.0.0.1:9090` (Cosmos SDK standard)
- **LCD REST**: `http://127.0.0.1:1317` (Cosmos REST API)
- **RPC** (CometBFT): `http://127.0.0.1:26657` (referenced but less used)

## Integration Points
- **Optio Custom Module**: `Optio.Lockup.Query` gRPC client queries lockup data
- **Cosmos Modules**: `Cosmos.Bank.V1Beta1`, `Cosmos.Tx.V1Beta1`, `Cosmos.Staking.V1Beta1`
- **Google Protobuf**: Wire format for all gRPC communication
- **Grpc.Net.Client**: Handles HTTP/2 and channel management

## When Modifying Code
1. **Adding a new query mode**: Create `Parse{ModeName}Arg()` method, add mode detection in top-level, implement logic in Program.cs
2. **Changing data retrieval**: Update relevant Service class (`BankService`, `LockService`, etc.) and update `optic.conf` if endpoints change
3. **Output format changes**: Most reports are text/CSV; find string.Join() calls or use WriteAllText() for single files
4. **Protobuf updates**: Regenerate via `buf.exe`, verify generated C# code compiles
