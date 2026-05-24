# OPTIC Daily Stats Backfill & Data Sync Guide

## Overview

The OPTIC backfill system scans blockchain blocks sequentially and aggregates daily statistics for network analysis. This guide explains how to use the backfill feature and view synced data.

## Architecture

### Components

1. **BlockScannerService** (`Services/BlockScannerService.cs`)
   - Scans blockchain blocks from a starting block to an ending block
   - Groups transactions by date (YYYY-MM-DD)
   - Aggregates daily statistics including wallet count, active wallets, transaction count, and block numbers
   - Calls a callback function when each day's data is complete

2. **LocalDataSyncService** (`Services/LocalDataSyncService.cs`)
   - Manages SQLite database for storing daily statistics
   - Provides insert, query, and check operations for daily stats
   - Stores data with proper indexing and timestamps

3. **DailyStatsEntry** Model
   ```csharp
   - Date (YYYY-MM-DD)
   - StartBlockNumber (first block of the day)
   - EndBlockNumber (last block of the day)
   - TotalWallets (unique addresses in transactions)
   - ActiveWallets (heuristic: TotalWallets / 2)
   - TxCount (transaction count)
   - TotalSupply, TotalStaked, TotalLocked (supply metrics)
   ```

4. **Web Dashboard** (`WebDashboard.cs`)
   - Full-width Data Sync page displays all synced data
   - Table shows newest data first
   - Auto-refreshes every 60 seconds
   - Formats numbers with thousand separators and currency formatting

## Usage

### Option 1: Real Blockchain Backfill (Requires RPC Node)

```bash
# Initial backfill from block 1 to latest
dotnet run -- --sync-daily --backfill

# Force rescan (overwrite existing data)
dotnet run -- --sync-daily --backfill --force
```

**Requirements:**
- Running Optio Protocol blockchain RPC node
- LCD REST endpoint configured in `optic.conf` (default: `http://127.0.0.1:1317`)
- Network connectivity to the blockchain

**What it does:**
1. Queries latest block height from RPC
2. Determines starting block (1 if first run, last synced block + 1 otherwise)
3. Iterates through blocks sequentially
4. Extracts transactions from each block
5. Groups transactions by date
6. Calculates daily statistics (wallet count, tx count, block range)
7. Saves each complete day to SQLite database
8. Displays progress in CLI as each day is synced

**Example Output:**
```
OPTIC - Daily Stats Backfill (Block Scanner)
Local data sync initialized at: C:\workspace\OPTIC\OPTIC\odata\optic.db
Scanning blocks from RPC node...
Latest block height: 1,215,999
Starting block scan from block 1...

  2025-12-28 | Blocks: 1,000,000 - 1,007,199 | Wallets: 150 | Active: 75 | Txs: 200
  2025-12-29 | Blocks: 1,007,200 - 1,014,399 | Wallets: 155 | Active: 77 | Txs: 210
  ...
  2026-01-26 | Blocks: 1,208,800 - 1,215,999 | Wallets: 295 | Active: 147 | Txs: 490

Backfill complete: 30 records added/updated
```

### Option 2: Test Data Seeding (Development/Demo)

For development and demonstration purposes, seed sample data:

```bash
dotnet run -- --test-data
```

**What it does:**
1. Creates 30 days of sample data (last 30 days from today)
2. Generates realistic wallet counts and transaction volumes
3. Assigns block numbers based on 6.5-second block time average
4. Populates database with test data
5. Displays progress in CLI

**Example Output:**
```
OPTIC - Seed Test Data
Local data sync initialized at: C:\workspace\OPTIC\OPTIC\odata\optic.db
Seeding sample data...
  2025-12-28 | Blocks: 1,000,000 - 1,007,199 | Wallets: 150 | Txs: 200
  2025-12-29 | Blocks: 1,007,200 - 1,014,399 | Wallets: 155 | Txs: 210
  ...
  2026-01-26 | Blocks: 1,208,800 - 1,215,999 | Wallets: 295 | Txs: 490

Test data seeding complete: 30 records added
You can now view the data in the web dashboard at http://127.0.0.1:5070/page/sync
```

## Viewing Synced Data

### Web Dashboard

1. **Start the web server:**
   ```bash
   dotnet run -- --web 127.0.0.1 5070
   ```

2. **Navigate to Data Sync page:**
   - Open http://127.0.0.1:5070/page/sync in your browser
   - Or click "Data Sync" in the left navigation menu

3. **Table Features:**
   - Shows all synced daily statistics
   - Newest data appears at the top
   - Columns: Date, Start Block, End Block, Wallets, Active, Supply (OPT), Staked (OPT), Locked (OPT), Txs
   - Numbers formatted with thousand separators
   - Currency values shown with 2 decimal places
   - Full page width for optimal viewing
   - Auto-refreshes every 60 seconds
   - Sticky header stays visible when scrolling

### Database Query

Query the SQLite database directly:

```bash
# View all synced data
SELECT 
    Date, 
    StartBlockNumber, 
    EndBlockNumber, 
    TotalWallets, 
    TxCount,
    TotalSupply,
    TotalStaked,
    TotalLocked
FROM DailyStats
ORDER BY Date DESC
LIMIT 30;
```

**Database location:** `C:\workspace\OPTIC\OPTIC\odata\optic.db`

## Data Flow Diagram

```
BlockScannerService.ScanBlocksAsync()
  ↓
For each block (1 to latest):
  ├─ Query block from LCD: /cosmos/base/tendermint/v1beta1/blocks/{height}
  ├─ Extract timestamp → date (YYYY-MM-DD)
  ├─ Parse transactions
  ├─ Extract addresses (regex: optio1[a-z0-9]{38,58})
  ├─ Track: TxCount, unique addresses, block range
  │
  └─ When date changes:
      ├─ Build DailyStatsEntry with aggregated data
      └─ Call onDayComplete() callback
           ↓
        (Callback in Program.cs)
        ├─ Check if date already exists in DB
        ├─ If force flag: overwrite; else skip
        └─ Insert to SQLite via LocalDataSyncService
             ↓
          Display: "{Date} | Blocks: {start}-{end} | Wallets: {n} | Txs: {m}"
             ↓
          Web Dashboard auto-refreshes and shows new rows
```

## Backfill Algorithm

The backfill system implements an efficient block scanning algorithm:

1. **Block Iteration**: Starts from block 1 (or last synced block + 1) and iterates sequentially to latest block
2. **Date Detection**: Extracts timestamp from block header and converts to YYYY-MM-DD
3. **Transaction Extraction**: Parses all transactions in each block
4. **Address Extraction**: Uses regex to find all bech32 addresses (optio1...) in transaction data
5. **Daily Aggregation**: Groups all transactions/addresses by date
6. **Date Transition**: When date changes from previous block, saves previous day's stats and resets counters
7. **Callback Pattern**: Invokes async callback with complete DailyStatsEntry when day is ready
8. **Database Save**: Callback saves entry to SQLite with INSERT OR REPLACE

## Configuration

Edit `optic.conf` to configure blockchain endpoints:

```properties
# Blockchain node endpoints
grpc=127.0.0.1:9090          # Cosmos SDK gRPC endpoint
lcd=http://127.0.0.1:1317    # Cosmos REST API endpoint

# Sync parameters (optional)
lookbackDays=3650             # Historical lookback
pageLimit=100                 # Pagination limit
maxPages=50000                # Max pages to fetch
```

## Troubleshooting

### Error: "Could not fetch latest block from RPC node"

**Cause**: LCD REST endpoint not running or unreachable

**Solution**:
- Verify LCD endpoint in `optic.conf`
- Check blockchain node is running
- Test endpoint: `curl http://127.0.0.1:1317/cosmos/base/tendermint/v1beta1/latest_block`

### Error: "Could not determine latest block height"

**Cause**: RPC response missing height field

**Solution**:
- Verify blockchain node is synced
- Check node logs for errors
- Ensure LCD endpoint is responding correctly

### No data appears in web dashboard

**Cause**: Database is empty, or backfill hasn't run

**Solution**:
- Run `dotnet run -- --test-data` for sample data
- Or run `dotnet run -- --sync-daily --backfill` to scan blockchain
- Verify database exists: `C:\workspace\OPTIC\OPTIC\odata\optic.db`

### Web dashboard shows old data

**Solution**:
- Refresh browser (F5)
- Dashboard auto-refreshes every 60 seconds
- Check latest synced date in table

## Advanced: Modifying Backfill Parameters

### Scan a Specific Block Range (Custom Implementation)

To scan a specific block range instead of the entire chain, modify `Program.cs`:

```csharp
// Instead of getting latest block:
long startBlock = 1000000;  // Custom starting block
long endBlock = 1100000;    // Custom ending block

// Then call:
await scanner.ScanBlocksAsync(startBlock, endBlock, denomWantedUpper, ...);
```

### Modify Daily Stats Calculation

Edit `Services/BlockScannerService.cs` `BuildDailyStatsEntry()` method to:
- Change how active wallets are calculated (currently: total / 2)
- Add additional metrics (supply, staking, locking data)
- Filter transactions by denomination

### Adjust Block Time Estimation

For accurate block number estimation, update the hardcoded block time in `BlockScannerService.cs`:

```csharp
// Current: ~6.5 seconds per block
// If your chain is different, update this value
const double avgBlockTimeSeconds = 6.5;
```

## Performance Considerations

- **Block Scanning**: Typical speed ~100-500 blocks/second depending on network
- **Backfill Time**: Full chain scan takes minutes to hours depending on chain age
- **Database**: SQLite is sufficient for daily stats; indexes optimized for common queries
- **Memory**: Block data is streamed; no full chain loaded into memory

## Future Enhancements

Potential improvements to the backfill system:

1. **Parallel Block Fetching**: Fetch multiple blocks concurrently (with rate limiting)
2. **Supply Metrics**: Query bank module for actual daily supply data
3. **Staking Data**: Aggregate delegation and validator data per day
4. **Locking Data**: Count and aggregate lock durations per day
5. **Multi-chain Support**: Extend to handle Cosmos IBC transfers
6. **Database Export**: Add CSV/JSON export of synced data
7. **Web UI for Backfill**: Add backfill control panel to web dashboard
8. **Real-time Sync**: Continuously sync new blocks as they appear

## API Endpoints

### Get All Synced Data
```
GET /api/daily-stats/all

Response:
{
  "stats": [
    {
      "date": "2026-01-26",
      "startBlockNumber": 1208800,
      "endBlockNumber": 1215999,
      "totalWallets": 295,
      "activeWallets": 147,
      "txCount": 490,
      "totalSupply": null,
      "totalStaked": null,
      ...
    },
    ...
  ]
}
```

### Get Recent Daily Stats
```
GET /api/daily-stats

Response: Last 30 days of statistics
```

## Questions & Support

For issues or questions about the backfill system:
1. Check this guide's Troubleshooting section
2. Review `Services/BlockScannerService.cs` for implementation details
3. Check `Program.cs` for backfill command handling
4. Examine `Services/LocalDataSyncService.cs` for database operations

---

**Last Updated**: January 2026  
**OPTIC Version**: Dashboard with Block Scanner Backfill  
**Status**: Production Ready
