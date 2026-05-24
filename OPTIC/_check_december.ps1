$rpc = 'http://127.0.0.1:26657'
$csvPath = 'C:\workspace\OPTIC\OPTIC\ledger.csv'
$confPath = 'C:\workspace\optic\optic\optic.conf'

# Load config for addr and tz
$addr = $null
$tzId = $null
if (Test-Path $confPath) {
  Get-Content $confPath | ForEach-Object {
    if ($_ -match '^addr\s*=\s*(.+)$') { $addr = $Matches[1].Trim() }
    if ($_ -match '^tz\s*=\s*(.+)$') { $tzId = $Matches[1].Trim() }
  }
}
if (-not $addr) { throw "addr not found in $confPath" }

$tz = $null
if ($tzId) {
  try { $tz = [TimeZoneInfo]::FindSystemTimeZoneById($tzId) } catch { $tz = $null }
}
if (-not $tz) { $tz = [TimeZoneInfo]::Local }

function Invoke-TxSearch($query, $page, $perPage) {
  $payload = @{
    jsonrpc = '2.0'
    id = 1
    method = 'tx_search'
    params = @{ query = $query; page = "$page"; per_page = "$perPage"; order_by = 'desc' }
  } | ConvertTo-Json -Compress

  $resp = curl.exe -s -X POST $rpc -H 'Content-Type: application/json' -d $payload | ConvertFrom-Json
  return $resp
}

function Get-TxHashesForQuery($query) {
  $perPage = 100
  $maxPages = 500
  $hashes = @{}
  for ($page = 1; $page -le $maxPages; $page++) {
    $resp = Invoke-TxSearch $query $page $perPage
    if (-not $resp.result -or -not $resp.result.txs) { break }
    $count = 0
    foreach ($tx in $resp.result.txs) {
      if ($tx.hash) {
        $hashes[$tx.hash] = $tx.height
        $count++
      }
    }
    if ($count -eq 0) { break }
  }
  return $hashes
}

function Get-BlockTimeUtc($height, $cache) {
  if ($cache.ContainsKey($height)) { return $cache[$height] }
  $resp = curl.exe -s "$rpc/block?height=$height" | ConvertFrom-Json
  $timeStr = $resp.result.block.header.time
  $dto = [DateTimeOffset]::Parse($timeStr)
  $utc = $dto.ToUniversalTime()
  $cache[$height] = $utc
  return $utc
}

$queries = @(
  "transfer.recipient='$addr'",
  "coin_received.receiver='$addr'",
  "transfer.sender='$addr'",
  "coin_spent.spender='$addr'"
)

$txByHash = @{}
foreach ($q in $queries) {
  $set = Get-TxHashesForQuery $q
  foreach ($k in $set.Keys) {
    if (-not $txByHash.ContainsKey($k)) { $txByHash[$k] = $set[$k] }
  }
}

$heightCache = @{}
$rpcDecHashes = New-Object System.Collections.Generic.HashSet[string]
foreach ($kv in $txByHash.GetEnumerator()) {
  $height = $kv.Value
  if (-not $height) { continue }
  $utc = Get-BlockTimeUtc $height $heightCache
  $local = [TimeZoneInfo]::ConvertTime($utc, $tz)
  if ($local.Year -eq 2025 -and $local.Month -eq 12) {
    $null = $rpcDecHashes.Add($kv.Key)
  }
}

$ledger = Import-Csv -Path $csvPath
$ledgerDecHashes = $ledger | Where-Object {
  $t = [DateTime]::Parse($_.Time)
  $t.Year -eq 2025 -and $t.Month -eq 12
} | ForEach-Object { $_.TxHash } | Where-Object { $_ } | Sort-Object -Unique

$ledgerSet = New-Object System.Collections.Generic.HashSet[string]
foreach ($h in $ledgerDecHashes) { $null = $ledgerSet.Add($h) }

$missing = @()
foreach ($h in $rpcDecHashes) {
  if (-not $ledgerSet.Contains($h)) { $missing += $h }
}

"RPC December hashes: $($rpcDecHashes.Count)"
"Ledger December hashes: $($ledgerSet.Count)"
"Missing in ledger.csv: $($missing.Count)"
if ($missing.Count -gt 0) { $missing | Sort-Object }
