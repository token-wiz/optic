# Real-Time Stats Accumulation Feature

## Overview

The sync system now supports real-time accumulation and display of statistics as blocks are analyzed, starting from block 1.

## Key Features

### 1. Real-Time Progress Display
- Shows current day statistics as blocks are being analyzed
- Updates every 100 blocks processed
- Displays running totals for wallets, transactions, and block ranges

### 2. Checkmark on Completion
- When a day is fully complete and saved to database, shows with ✓ symbol
- When in-progress, shows with provisional stats that update as blocks continue

### 3. Non-Breaking Progress Updates
- Uses `\r` carriage return to update same line (no spam)
- Only creates new line when day is complete
- Clean, readable output even with long-running scans

## How It Works

### BlockScannerService Changes

The `ScanBlocksAsync` method now accepts an optional `onProgress` callback:

```csharp
public async Task<int> ScanBlocksAsync(
    long startBlock,
    long endBlock,
    string denomWantedUpper,
    Func<DailyStatsEntry, Task> onDayComplete,
    Func<DailyStatsEntry, Task>? onProgress = null,  // NEW
    CancellationToken ct = default)
```

### Progress Callback Invocation

- Called every 100 blocks to show current day accumulation
- Provides same DailyStatsEntry data structure as completion callback
- Shows running totals for the current day being analyzed

## Usage Examples

### Starting from Block 1

When you run the backfill command, it starts from block 1 and begins accumulating stats:

```bash
dotnet run -- --sync-daily --backfill
```

**Output Example:**
```
Latest block height: 1,215,999
Starting block scan from block 1...

  2025-12-28 | Blocks: 1,000,000 - 1,000,100 | Wallets: 145 | Txs: 195
  2025-12-28 | Blocks: 1,000,000 - 1,000,200 | Wallets: 147 | Txs: 197
  2025-12-28 | Blocks: 1,000,000 - 1,000,300 | Wallets: 149 | Txs: 199
✓ 2025-12-28 | Blocks: 1,000,000 - 1,007,199 | Wallets: 150 | Active: 75 | Txs: 200
  2025-12-29 | Blocks: 1,007,200 - 1,007,300 | Wallets: 153 | Txs: 208
  2025-12-29 | Blocks: 1,007,200 - 1,007,400 | Wallets: 155 | Txs: 210
✓ 2025-12-29 | Blocks: 1,007,200 - 1,014,399 | Wallets: 155 | Active: 77 | Txs: 210
```

### Key Points

1. **Starting Point**: Scanning always starts from block 1 or from last synced position
2. **Accumulation**: Stats accumulate as blocks are processed
3. **Progress Updates**: Every 100 blocks, current running totals are shown
4. **Day Completion**: When day boundary is crossed, final stats are saved (✓ symbol)
5. **New Day**: Automatically resets counters and continues with next day

## Data Accumulation Strategy

As blocks are scanned from block 1 forward:

1. **Block Iteration**: For each block height (1, 2, 3, ... latest)
2. **Data Extraction**: Extract timestamp, transactions, addresses
3. **Accumulation**: Add to current day's statistics
4. **Running Display**: Every 100 blocks, show current totals
5. **Date Change Detection**: When date changes, save day to DB and reset

## Example Data Flow

```
Block 1,000,000 (2025-12-28):
  ├─ Extract 5 transactions
  ├─ Find 10 unique addresses
  ├─ Add to day stats: Wallets: 10, TxCount: 5, BlockRange: 1M - 1M
  └─ Continue to next block

Block 1,000,100:
  ├─ Extract 6 transactions
  ├─ Find 8 unique addresses
  ├─ Add to day stats: Wallets: 18, TxCount: 11, BlockRange: 1M - 1M+100
  ├─ [PROGRESS UPDATE every 100 blocks]
  └─ Continue to next block

Block 1,007,199 (still 2025-12-28):
  ├─ Extract 4 transactions
  ├─ Find 12 unique addresses
  ├─ Add to day stats: Wallets: 150, TxCount: 200, BlockRange: 1M - 1.007M
  └─ Continue to next block

Block 1,007,200 (2025-12-29):
  ├─ Date changed! Save previous day to DB
  ├─ [DISPLAY]: ✓ 2025-12-28 | Blocks: 1M - 1.007M | Wallets: 150 | Txs: 200
  ├─ Reset counters for new day
  └─ Continue scanning

Block 1,007,201 (2025-12-29):
  ├─ Extract 3 transactions
  ├─ Find 6 unique addresses
  ├─ Add to day stats: Wallets: 6, TxCount: 3, BlockRange: 1.007M - 1.007M+1
  └─ Continue to next block
```

## Benefits

1. **Real-Time Visibility**: See progress as it happens
2. **No Data Loss**: All stats accumulated from block 1
3. **Clear Completion**: Checkmark (✓) shows when day is finalized
4. **Resumable**: If interrupted, resumes from last synced block + 1
5. **Efficient Output**: Progress line updates don't spam the console

## Technical Implementation

### Progress Callback in Program.cs

```csharp
// Progress callback to show current day being analyzed
async (progressStats) =>
{
    if (progressStats.Date != lastDisplayedDate)
    {
        var wallets = progressStats.TotalWallets?.ToString("N0") ?? "0";
        var txs = progressStats.TxCount?.ToString("N0") ?? "0";
        var blockStart = progressStats.StartBlockNumber?.ToString("N0") ?? "?";
        var blockEnd = progressStats.EndBlockNumber?.ToString("N0") ?? "?";
        
        // Carriage return updates same line
        Console.Write($"\r  {progressStats.Date} | Blocks: {blockStart} - {blockEnd} | Wallets: {wallets} | Txs: {txs}          ");
    }
    await Task.CompletedTask;
}
```

### Completion Callback in Program.cs

```csharp
// Callback when day is complete
async (dailyStats) =>
{
    // ... validation and DB save ...
    
    // Display with checkmark
    Console.WriteLine($"✓ {dailyStats.Date} | Blocks: {blockStart} - {blockEnd} | Wallets: {wallets} | Active: {active} | Txs: {txs}");
    lastDisplayedDate = dailyStats.Date;
}
```

## Command Usage

### Full Chain Backfill from Block 1
```bash
dotnet run -- --sync-daily --backfill
```

### Force Rescan (Overwrite with New Data)
```bash
dotnet run -- --sync-daily --backfill --force
```

### Test Data (No RPC Required)
```bash
dotnet run -- --test-data
```

## When Stats Are Added to Database

1. **Day Complete**: When scanning reaches a new date, previous day's complete stats are saved
2. **Force Mode**: Re-scans overwrite existing entries with fresh data
3. **Resume Mode**: Continues from where it left off (block 1 or last synced + 1)

## Monitoring Long-Running Scans

For very long scans (full chain from genesis):
- Progress updates every 100 blocks
- No output spam
- Clear checkmark when each day is finalized
- Can safely Ctrl+C and resume later

## Future Enhancements

1. **ETA Calculation**: Estimate time remaining based on scan speed
2. **Percentage Display**: Show overall progress percentage
3. **Speed Display**: Show blocks/second scanning rate
4. **Database Stats**: Show total bytes written to database
5. **Memory Monitor**: Show memory usage during scan

---

This real-time accumulation feature ensures that:
- All stats are captured starting from block 1
- Progress is visible as blocks are analyzed
- No data is lost even if process is interrupted
- Each completed day is immediately saved to database
