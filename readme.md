# OPTIC

OPTIC is the Optio Protocol Telemetry & Intelligence Center. It is a .NET command-line and local web dashboard application for querying Optio chain data, producing wallet and network reports, and syncing daily statistics into a local SQLite database.

## Required Environment

OPTIC is built and documented for a Windows PowerShell environment.

Required software:

- Windows 10/11 or Windows Server with PowerShell.
- .NET 9 SDK installed and available on `PATH`.
- Git, if you are cloning or updating the repository.
- A browser for the local web dashboard.
- Network access to an Optio Protocol node for live chain queries.

Required node services for live data:

- Cosmos SDK gRPC endpoint, default `127.0.0.1:9090`.
- Cosmos LCD REST endpoint, default `http://127.0.0.1:1317`.
- CometBFT RPC endpoint, default `http://127.0.0.1:26657`.

Local files created by OPTIC:

- SQLite database: `OPTIC\odata\optic.db`.
- CSV/XLSX reports in the current working directory unless an output path is supplied.

## Prerequisites

Verify the .NET SDK:

```powershell
dotnet --version
```

The version should be `9.x`.

Verify the project builds:

The main project is in the `OPTIC` directory.

```powershell
cd OPTIC
dotnet restore
dotnet build
```

For live chain reports, verify your Optio node endpoints are running before using the application:

```powershell
curl http://127.0.0.1:1317/cosmos/base/tendermint/v1beta1/latest_block
dotnet run -- --status
```

If you only want to test the dashboard, you can skip the live node and seed local sample data with `dotnet run -- --test-data`.

## Configuration

OPTIC reads configuration from `OPTIC\optic.conf`.

```properties
addr=optio1...
denom=uOPT
tz=America/New_York

grpc=127.0.0.1:9090
lcd=http://127.0.0.1:1317
rpc=http://127.0.0.1:26657

lookbackDays=3650
pageLimit=100
maxPages=50000
```

Important keys:

- `addr`: default wallet address used by address-specific reports.
- `denom`: chain denomination. `uOPT` is scaled to OPT for display.
- `grpc`: Cosmos SDK gRPC endpoint.
- `lcd`: Cosmos LCD REST endpoint.
- `rpc`: CometBFT RPC endpoint.
- `lookbackDays`: default history window for transaction queries.
- `pageLimit`: query page size, clamped to 100.
- `maxPages`: safety limit for paged queries.

## How To Run

From the `OPTIC` directory:

```powershell
dotnet run -- --help
dotnet run -- --status
dotnet run -- --distributions
dotnet run -- --web --web-host 127.0.0.1 --web-port 5070
```

Then open:

```text
http://127.0.0.1:5070
```

Run a one-time report:

```powershell
dotnet run -- --distributions
```

Run the web dashboard:

```powershell
dotnet run -- --web --web-host 127.0.0.1 --web-port 5070
```

Run with local sample dashboard data:

```powershell
dotnet run -- --test-data
dotnet run -- --web --web-host 127.0.0.1 --web-port 5070
```

Run a daily stats backfill from a live Optio node:

```powershell
dotnet run -- --sync-daily --backfill
```

Backfill only the most recent 30 UTC days:

```powershell
dotnet run -- --sync-daily --backfill --backfill-days 30
```

## Web Dashboard

Start the dashboard with:

```powershell
dotnet run -- --web --web-host 127.0.0.1 --web-port 5070
```

The dashboard provides pages for:

- Dashboard summary
- Distributions and ledger reports
- Locks and staking
- Counterparties
- Network analysis
- Wallet analysis
- MultiSend reports
- CoinMarketCap export
- Custom arguments
- Node status
- Validators and nodes
- Data sync
- Daily statistics

The helper script `webstart.cmd` starts the dashboard on port 80:

```powershell
.\webstart.cmd
```

## Common CLI Reports

Run these commands from `OPTIC`.

```powershell
dotnet run -- --distributions
dotnet run -- --distributions=optio1... --show-hash
dotnet run -- --locks
dotnet run -- --locks=optio1...
dotnet run -- --locks-summary
dotnet run -- --counterparties
dotnet run -- --counterparties=optio1...
dotnet run -- --send-recv
dotnet run -- --send-recv optio1... --send-recv-hours 24
```

Useful options:

- `--csv <path>` writes ledger output to CSV.
- `--totals-only` prints totals without the full ledger.
- `--totals` or `--no-totals` controls whether totals are included.
- `--show-hash` includes transaction hashes in ledger output.
- `--include-validators` includes `optiovaloper...` addresses in counterparty output.
- `--verbose` prints additional scan progress.

### CSV Output Examples

Write the default ledger report to a CSV file:

```powershell
dotnet run -- --distributions --csv my-distributions.csv
```

Write a ledger report for a specific wallet and include transaction hashes:

```powershell
dotnet run -- --distributions=optio1... --show-hash --csv wallet-ledger.csv
```

Write all wallet balances to CSV:

```powershell
dotnet run -- --wallet-balances --wallet-balances-csv wallet-balances.csv
```

Write a MultiSend summary to CSV:

```powershell
dotnet run -- --multisend-sum --from optio1... --out multisend-summary.csv
```

Write CoinMarketCap daily export to CSV:

```powershell
dotnet run -- --cmc-daily 2025 --out optio-daily-2025.csv
```

## Command Line Parameters

General options:

| Option | Description |
| --- | --- |
| `--help`, `-h`, `/?`, `help` | Show the built-in help text. |
| `--web`, `web`, `--web-server` | Start the local web dashboard. |
| `--web-host <host>` | Host/IP for the dashboard to bind to. Default is `127.0.0.1`. |
| `--web-port <port>` | Port for the dashboard to bind to. Default is `5070`. |
| `--status`, `--node-status`, `status` | Show CometBFT node sync status and exit. |
| `--validators-nodes`, `validators-nodes` | List bonded validators with best-effort IP addresses and staked OPT. |
| `--wallet-count`, `wallet-count` | Print the total number of wallet addresses and exit. |
| `--verbose`, `--verbose-scan` | Print additional scan progress information. |

Address and ledger reports:

| Option | Description |
| --- | --- |
| `--distributions [addr]`, `--distributions=<addr>`, `distributions` | Run the default balances summary and ledger output. Optional `addr` overrides `addr` from `optic.conf`. |
| `--locks [addr]`, `--locks=<addr>`, `locks` | Print liquid, staked, and active lockups for an address. Optional `addr` overrides `addr` from `optic.conf`. |
| `--locks-summary`, `locks-summary` | Print a summary of all active lockups grouped by remaining lock length. |
| `--lock-extended`, `--lock-extended-report`, `lock-extended` | Export `lock_extended` transactions to `optio-lock-extended.csv`. |
| `--lock-extended-days <n>` | Number of days to look back for the lock-extended export. Default is `7`. |
| `--counterparties [addr]`, `--counterparties=<addr>`, `counterparties` | Print unique addresses the wallet has sent to or received from. Optional `addr` overrides `addr` from `optic.conf`. Validator addresses are excluded by default. |
| `--send-recv [addr]`, `--send-recv=<addr>`, `send-recv` | Print send/receive transactions with comma-grouped whole OPT amounts. Optional `addr` overrides `addr` from `optic.conf`. |
| `--send-recv-hours <n>` | Number of hours to look back for send/receive output. Default is `24`. |
| `--include-validators`, `--validators` | Include validator addresses in counterparty output. |
| `--show-hash`, `--hash` | Show transaction hashes in default ledger output. |
| `--csv <path>`, `--csv=<path>` | Write ledger output to a CSV file. |
| `--totals-only` | Print only totals and skip the full ledger. |
| `--totals` | Include totals in ledger output. |
| `--no-totals` | Exclude totals from ledger output. |
| `--totals=true`, `--totals=false` | Explicitly enable or disable totals in ledger output. |
| `--network-totals`, `--totals-network` | Print all-time network totals and exit. |
| `--dry-totals` | Run the totals accumulator dry run and exit. |
| `--emitter <addr>`, `--emitter=<addr>` | Emission address for totals. Defaults to the built-in distribution source address. |

MultiSend options:

| Option | Description |
| --- | --- |
| `--multisend-sum`, `multisend-sum` | Summarize MultiSend totals for a sender and exit. |
| `--from <addr>`, `--from=<addr>` | Sender address for MultiSend summary mode. |
| `--lookback-blocks <n>`, `--lookback-blocks=<n>` | Look back `n` blocks from the latest height. |
| `--out <path>`, `--out=<path>` | Write MultiSend CSV output. Also used by CMC export for the output CSV path. |
| `--start-height <n>`, `--start-height=<n>` | Start block height for modes that support block ranges. |
| `--end-height <n>`, `--end-height=<n>` | End block height for modes that support block ranges. |
| `--height <n>`, `--height=<n>` | Query a specific block height for wallet balances or total staked. Default is `1062`. |
| `--scan-multisend-blocks`, `--blockscan-multisend` | Scan blocks for MultiSend activity and exit. |
| `--top-senders <n>`, `--top-senders=<n>` | Number of top senders to display in block scan mode. Default is `20`. |
| `--senders <addr1,addr2,...>`, `--senders=<addr1,addr2,...>` | Comma-separated sender whitelist for MultiSend block scanning. |

Wallet, staking, and totals options:

| Option | Description |
| --- | --- |
| `--wallet-balances` | Print balances for all wallets. |
| `--wallet-balances-csv <path>`, `--wallet-balances-csv=<path>` | Write wallet balances to CSV. |
| `--wallet-locks-report`, `wallet-locks-report` | Write wallet balances plus lock bucket CSV reports. |
| `--wallet-locks-summary [addr]`, `--wallet-locks-summary=<addr>`, `wallet-locks-summary` | Print a balance and lock bucket summary for one address. Optional `addr` overrides `addr` from `optic.conf`. |
| `--total-staked`, `total-staked` | Print total staked OPT across all wallets. |
| `--total-distributed`, `total-distributed` | Print total distributed OPT across all wallets. |
| `--totals-all`, `totals-all` | Print total distributed, staked, and locked OPT across all wallets. |
| `--emission-address <addr>`, `--emission-address=<addr>` | Emission address for distributed-total calculations. |

CoinMarketCap export options:

| Option | Description |
| --- | --- |
| `--cmc-daily <year>`, `--cmc-daily=<year>` | Export daily CoinMarketCap OHLC data for the given year. |
| `--cmc-id <id>`, `--cmc-id=<id>` | CoinMarketCap asset ID. Default is `35828`. |
| `--out <path>`, `--out=<path>` | Output CSV path. Default is `optio_daily_2025_cmc.csv`. |

Daily sync and analytics options:

| Option | Description |
| --- | --- |
| `--daily-sync`, `--sync-daily`, `daily-sync` | Record today's daily statistics to the local SQLite database. |
| `--backfill`, `backfill` | With daily sync mode, fill missing daily statistics from chain history through today. |
| `--backfill-days <days>`, `--backfill-days=<days>` | With `--backfill`, only scan the past number of UTC days. |
| `--force` | With `--backfill`, recompute and overwrite existing dates. |
| `--test-data` | Seed 30 days of local sample daily statistics for dashboard testing without a live node. |

## Network And Wallet Totals

```powershell
dotnet run -- --network-totals
dotnet run -- --wallet-count
dotnet run -- --validators-nodes
dotnet run -- --wallet-balances
dotnet run -- --wallet-balances --wallet-balances-csv wallet_balances.csv
dotnet run -- --wallet-locks-report
dotnet run -- --wallet-locks-summary
dotnet run -- --total-staked
dotnet run -- --total-distributed
dotnet run -- --totals-all
```

Some total modes support height ranges:

```powershell
dotnet run -- --total-distributed --start-height 1 --end-height 500000
dotnet run -- --totals-all --start-height 1 --end-height 500000
```

## MultiSend Analysis

Summarize MultiSend totals:

```powershell
dotnet run -- --multisend-sum
dotnet run -- --multisend-sum --from optio1... --lookback-blocks 100000
dotnet run -- --multisend-sum --from optio1... --out multisend.csv
```

Scan block ranges for MultiSend activity:

```powershell
dotnet run -- --scan-multisend-blocks
dotnet run -- --scan-multisend-blocks --start-height 1 --end-height 500000
dotnet run -- --scan-multisend-blocks --top-senders 20
dotnet run -- --scan-multisend-blocks --senders optio1addr1,optio1addr2
```

## Daily Stats Sync

OPTIC stores synced daily statistics in SQLite at:

```text
OPTIC\odata\optic.db
```

Record today's daily statistics:

```powershell
dotnet run -- --sync-daily
```

Backfill daily statistics from the chain:

```powershell
dotnet run -- --sync-daily --backfill
```

Backfill only the past 14 UTC days:

```powershell
dotnet run -- --sync-daily --backfill --backfill-days 14
```

Force a rescan and overwrite existing dates:

```powershell
dotnet run -- --sync-daily --backfill --force
```

Seed sample data for local dashboard testing without a live RPC node:

```powershell
dotnet run -- --test-data
```

After syncing, start the web dashboard and open:

```text
http://127.0.0.1:5070/page/sync
```

More detail is available in `OPTIC\BACKFILL_GUIDE.md` and `OPTIC\REALTIME_ACCUMULATION.md`.

## CoinMarketCap Daily Export

Export daily OHLC data for a year:

```powershell
dotnet run -- --cmc-daily 2025
dotnet run -- --cmc-daily 2025 --cmc-id 35828 --out optio_daily_2025_cmc.csv
```

## Output Files

Reports are written to the current working directory unless an output path is supplied. Common generated files include:

- `wallet_balances.csv`
- `wallet_balances.xlsx`
- `optic-wallet-totals.csv`
- `optic-wallet-details.csv`
- `optio-lock-extended.csv`
- `optio_daily_2025_cmc.csv`
- `odata\optic.db`

## Local API Endpoints

When the web dashboard is running:

- `GET /api/sync-status`: current sync status and recent sync logs.
- `GET /api/daily-stats`: recent daily statistics.
- `GET /api/daily-stats/all`: all synced daily statistics.
- `GET /api/summary`: cached dashboard summary.

Example:

```text
http://127.0.0.1:5070/api/daily-stats/all
```

## Troubleshooting

If OPTIC reports that `optic.conf` is missing, run commands from the `OPTIC` directory or create the file at `OPTIC\optic.conf`.

If chain queries fail, verify the configured endpoints:

```powershell
dotnet run -- --status
```

You can also test the LCD endpoint directly:

```powershell
curl http://127.0.0.1:1317/cosmos/base/tendermint/v1beta1/latest_block
```

If the dashboard has no daily statistics, seed local test data or run a backfill:

```powershell
dotnet run -- --test-data
dotnet run -- --sync-daily --backfill
```

If port 5070 is already in use, choose another port:

```powershell
dotnet run -- --web --web-host 127.0.0.1 --web-port 5080
```

## Development

Build and run:

```powershell
dotnet build
dotnet run -- --help
```

The `build.cmd` helper runs:

```powershell
dotnet clean
dotnet build
dotnet run
```

Project layout:

- `Program.cs`: CLI entry point and command handling.
- `WebDashboard.cs`: local dashboard and HTTP API.
- `Services\`: chain query, lock, staking, bank, block scanning, and local data sync services.
- `Models\`: report and data models.
- `protos\`: Cosmos and Optio protocol definitions used for gRPC clients.
