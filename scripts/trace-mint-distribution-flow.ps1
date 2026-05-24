param(
  [string]$RpcBase = "http://127.0.0.1:26657",
  [string]$LcdBase = "http://127.0.0.1:1317",
  [string]$HotWallet = "optio103r5ejt3gqyghvyel86hs5faru55r3w9kl4dst",
  [int]$PageLimit = 100,
  [int]$MaxPages = 2000,
  [string[]]$Hashes = @(),
  [switch]$TotalDistributed
)

function Invoke-CometTxSearch {
  param(
    [string]$RpcBase,
    [string]$Query,
    [int]$PageLimit,
    [int]$MaxPages,
    [string]$OrderBy = "desc"
  )

  $hashes = New-Object System.Collections.Generic.List[string]
  $perPage = [Math]::Min([Math]::Max($PageLimit, 1), 100)
  $pages = [Math]::Min([Math]::Max($MaxPages, 1), 1000)

  for ($page = 1; $page -le $pages; $page++) {
    $payload = @{
      jsonrpc = "2.0"
      id      = 1
      method  = "tx_search"
      params  = @{
        query    = $Query
        page     = $page.ToString()
        per_page = $perPage.ToString()
        order_by = $OrderBy
      }
    } | ConvertTo-Json -Depth 6

    try {
      $resp = Invoke-RestMethod -Method Post -Uri ($RpcBase.TrimEnd("/") + "/") -ContentType "application/json" -Body $payload -TimeoutSec 60
    } catch {
      break
    }

    if (-not $resp.result -or -not $resp.result.txs) { break }
    $count = 0
    foreach ($tx in $resp.result.txs) {
      if ($tx.hash) { $hashes.Add($tx.hash) | Out-Null; $count++ }
    }
    if ($count -eq 0) { break }
  }

  return $hashes
}

function Invoke-CometTx {
  param([string]$RpcBase, [string]$Hash)
  $clean = $Hash.Trim()
  $hex = if ($clean.StartsWith("0x")) { $clean } else { "0x$clean" }
  $url = $RpcBase.TrimEnd("/") + "/tx?hash=$hex"
  try { return Invoke-RestMethod -Method Get -Uri $url -TimeoutSec 60 } catch { return $null }
}

function Get-ModuleAccounts {
  param([string]$LcdBase)
  $url = $LcdBase.TrimEnd("/") + "/cosmos/auth/v1beta1/module_accounts"
  try { $resp = Invoke-RestMethod -Method Get -Uri $url -TimeoutSec 60 } catch { return @{} }
  $map = @{}
  foreach ($acct in $resp.accounts) {
    $name = $acct.name
    $addr = $null
    if ($acct.base_account -and $acct.base_account.address) {
      $addr = $acct.base_account.address
    } elseif ($acct.base_account -and $acct.base_account.base_account -and $acct.base_account.base_account.address) {
      $addr = $acct.base_account.base_account.address
    } elseif ($acct.address) {
      $addr = $acct.address
    }
    if (-not [string]::IsNullOrWhiteSpace($name) -and -not [string]::IsNullOrWhiteSpace($addr)) {
      $map[$name] = $addr
    }
  }
  return $map
}

function Parse-TransfersFromEvents {
  param([object[]]$Events)
  $rows = @()
  foreach ($ev in $Events) {
    if ($ev.type -ne "transfer") { continue }
    $attrs = @{}
    foreach ($a in $ev.attributes) {
      $k = Decode-CometAttr $a.key
      $v = Decode-CometAttr $a.value
      if ([string]::IsNullOrWhiteSpace($k)) { continue }
      if (-not $attrs.ContainsKey($k)) { $attrs[$k] = @() }
      $attrs[$k] += $v
    }

    $senders = $attrs["sender"]
    $recipients = $attrs["recipient"]
    $amounts = $attrs["amount"]
    if (-not $senders -or -not $recipients -or -not $amounts) { continue }

    for ($i = 0; $i -lt $senders.Count; $i++) {
      $rows += [PSCustomObject]@{
        Sender    = $senders[$i]
        Recipient = $recipients[$i]
        Amount    = $amounts[$i]
      }
    }
  }
  return $rows
}

function Parse-Transfers {
  param([object]$TxResult)
  $events = $null
  if ($TxResult -is [System.Collections.IEnumerable] -and -not ($TxResult -is [string])) {
    $events = $TxResult
  } elseif ($TxResult -and $TxResult.tx_result -and $TxResult.tx_result.events) {
    $events = $TxResult.tx_result.events
  }

  if (-not $events) { return @() }
  return Parse-TransfersFromEvents -Events $events
}

function Decode-CometAttr {
  param([string]$Value)
  if ([string]::IsNullOrWhiteSpace($Value)) { return $Value }
  if ($Value -match '^[a-z0-9]+1[0-9a-z]+$') { return $Value }
  if ($Value -match '^[0-9]+(uOPT|uopt)$') { return $Value }
  if ($Value -notmatch '[+/=]') { return $Value }
  if (($Value.Length % 4) -ne 0) { return $Value }
  if ($Value -notmatch '^[A-Za-z0-9+/]+={0,2}$') { return $Value }
  try {
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value))
  } catch {
    return $Value
  }
}

$knownResults = New-Object System.Collections.Generic.List[object]
$hashList = @()
foreach ($h in $Hashes) {
  if ([string]::IsNullOrWhiteSpace($h)) { continue }
  $hashList += ($h -split '[,\\s]+')
}
$trimChars = @(' ', '''', '"')
$hashList = $hashList |
  ForEach-Object { $_.Trim($trimChars) } |
  Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

if ($TotalDistributed) {
  Write-Host "== Total Distributed (MsgMultiSend from hot wallet) =="
  $queryA = "message.action='/cosmos.bank.v1beta1.MsgMultiSend' AND message.sender='$HotWallet'"
  $queryB = "message.action='/cosmos.bank.v1beta1.MsgMultiSend' AND coin_spent.spender='$HotWallet'"
  $hashesA = Invoke-CometTxSearch -RpcBase $RpcBase -Query $queryA -PageLimit $PageLimit -MaxPages $MaxPages
  $hashesB = Invoke-CometTxSearch -RpcBase $RpcBase -Query $queryB -PageLimit $PageLimit -MaxPages $MaxPages

  $hashSet = [System.Collections.Generic.Dictionary[string,bool]]::new([System.StringComparer]::OrdinalIgnoreCase)
  foreach ($h in $hashesA) { if (-not $hashSet.ContainsKey($h)) { $hashSet.Add($h, $true) } }
  foreach ($h in $hashesB) { if (-not $hashSet.ContainsKey($h)) { $hashSet.Add($h, $true) } }
  $hashes = $hashSet.Keys

  if ($hashes.Count -eq 0) {
    Write-Host "No MsgMultiSend txs found via tx_search; RPC index may be missing sender/spender tags."
    exit 0
  }

  [decimal]$totalUnits = 0
  $totalTxs = $hashes.Count
  $processed = 0
  foreach ($h in $hashes) {
    $processed++
    if (($processed % 50) -eq 0) {
      Write-Host ("Progress: {0}/{1} txs processed..." -f $processed, $totalTxs)
    }
    $tx = Invoke-CometTx -RpcBase $RpcBase -Hash $h
    if (-not $tx -or -not $tx.result) { continue }
    $events = $tx.result.tx_result.events
    if (-not $events) { continue }
    $transfers = Parse-TransfersFromEvents -Events $events
    foreach ($t in $transfers) {
      if ($t.Sender -ne $HotWallet) { continue }
      if ($t.Amount -notmatch 'uOPT$') { continue }
      $amt = ($t.Amount -replace 'uOPT$','')
      $parsed = [decimal]0
      if ([decimal]::TryParse($amt, [Globalization.NumberStyles]::Integer, [Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) {
        $totalUnits += $parsed
      }
    }
  }

  $totalOpt = ($totalUnits / 1000000).ToString("0.000000", [Globalization.CultureInfo]::InvariantCulture)
  Write-Host ("Total distributed (all-time): {0} OPT" -f $totalOpt)
  exit 0
}

if ($hashList.Count -gt 0) {
  Write-Host ""
  Write-Host "== Known Hash Summary =="
  Write-Host ("Hashes: " + ($hashList -join ", "))
  foreach ($h in $hashList) {
    $clean = $h.Trim()
    $hex = if ($clean.StartsWith("0x")) { $clean } else { "0x$clean" }
    $url = $RpcBase.TrimEnd("/") + "/tx?hash=$hex"
    try {
      $tx = Invoke-RestMethod -Method Get -Uri $url -TimeoutSec 60
    } catch {
      Write-Host ("Hash not found: {0} ({1})" -f $h, $_.Exception.Message)
      continue
    }

    if (-not $tx -or -not $tx.result) { Write-Host ("Hash not found: {0}" -f $h); continue }
    $time = $tx.result.tx_result.timestamp
    $events = $tx.result.tx_result.events
    $transferCount = 0
    if ($events) {
      $transferCount = ($events | Where-Object { $_.type -eq "transfer" }).Count
    }
    Write-Host ("Hash {0}: transfer events={1}" -f $h, $transferCount)
    $transfers = if ($events) { Parse-TransfersFromEvents -Events $events } else { @() }
    foreach ($t in $transfers) {
      $knownResults.Add([PSCustomObject]@{
        Time   = $time
        Hash   = $h
        Sender = $t.Sender
        To     = $t.Recipient
        Amount = $t.Amount
      }) | Out-Null
    }
  }

  if ($knownResults.Count -gt 0) {
    $knownResults | Sort-Object Time | Format-Table -AutoSize

    $totals = $knownResults |
      Group-Object Sender |
      ForEach-Object {
        $sum = ($_.Group | Where-Object { $_.Amount -match 'uOPT$' } |
          ForEach-Object { ($_."Amount" -replace 'uOPT$','') -as [decimal] } |
          Measure-Object -Sum).Sum
        [PSCustomObject]@{ Sender = $_.Name; Total_uOPT = [string]$sum }
      }

    Write-Host ""
    Write-Host "== Totals by Sender (uOPT) =="
    $totals | Format-Table -AutoSize
  } else {
    Write-Host "No transfers parsed for known hashes."
  }
}

$modules = Get-ModuleAccounts -LcdBase $LcdBase
if (-not $modules.ContainsKey("distribution") -and -not $modules.ContainsKey("distro") -and -not $modules.ContainsKey("mint")) {
  Write-Host "No module accounts found. Check LCD base."
  exit 1
}

$sources = @{}
foreach ($k in @("mint","distribution","distro")) {
  if ($modules.ContainsKey($k)) { $sources[$k] = $modules[$k] }
}

Write-Host "Hot wallet: $HotWallet"
foreach ($kv in $sources.GetEnumerator()) {
  Write-Host ("Source {0}: {1}" -f $kv.Key, $kv.Value)
}

$results = New-Object System.Collections.Generic.List[object]

foreach ($src in $sources.GetEnumerator()) {
  $query = "message.action='/cosmos.bank.v1beta1.MsgSend' AND message.sender='$($src.Value)' AND transfer.recipient='$HotWallet'"
  Write-Host ""
  Write-Host "Query: $query"
  $hashes = Invoke-CometTxSearch -RpcBase $RpcBase -Query $query -PageLimit $PageLimit -MaxPages $MaxPages
  foreach ($h in $hashes) {
    $tx = Invoke-CometTx -RpcBase $RpcBase -Hash $h
    if (-not $tx -or -not $tx.result) { continue }
    $time = $tx.result.tx_result.timestamp
    $transfers = Parse-Transfers -TxResult $tx.result
    foreach ($t in $transfers) {
      if ($t.Sender -eq $src.Value -and $t.Recipient -eq $HotWallet) {
        $results.Add([PSCustomObject]@{
          Time   = $time
          Hash   = $h
          Source = $src.Key
          Sender = $t.Sender
          To     = $t.Recipient
          Amount = $t.Amount
        }) | Out-Null
      }
    }
  }
}

foreach ($src in $sources.GetEnumerator()) {
  $query = "message.action='/cosmos.bank.v1beta1.MsgMultiSend' AND message.sender='$($src.Value)' AND transfer.recipient='$HotWallet'"
  Write-Host ""
  Write-Host "Query: $query"
  $hashes = Invoke-CometTxSearch -RpcBase $RpcBase -Query $query -PageLimit $PageLimit -MaxPages $MaxPages
  foreach ($h in $hashes) {
    $tx = Invoke-CometTx -RpcBase $RpcBase -Hash $h
    if (-not $tx -or -not $tx.result) { continue }
    $time = $tx.result.tx_result.timestamp
    $transfers = Parse-Transfers -TxResult $tx.result
    foreach ($t in $transfers) {
      if ($t.Sender -eq $src.Value -and $t.Recipient -eq $HotWallet) {
        $results.Add([PSCustomObject]@{
          Time   = $time
          Hash   = $h
          Source = $src.Key
          Sender = $t.Sender
          To     = $t.Recipient
          Amount = $t.Amount
        }) | Out-Null
      }
    }
  }
}

Write-Host ""
Write-Host "Query: transfer.recipient='$HotWallet' (any sender)"
$hashes = Invoke-CometTxSearch -RpcBase $RpcBase -Query "transfer.recipient='$HotWallet'" -PageLimit $PageLimit -MaxPages $MaxPages
foreach ($h in $hashes) {
  $tx = Invoke-CometTx -RpcBase $RpcBase -Hash $h
  if (-not $tx -or -not $tx.result) { continue }
  $time = $tx.result.tx_result.timestamp
  $transfers = Parse-Transfers -TxResult $tx.result
  foreach ($t in $transfers) {
    if ($t.Recipient -eq $HotWallet) {
      $results.Add([PSCustomObject]@{
        Time   = $time
        Hash   = $h
        Source = "transfer.recipient"
        Sender = $t.Sender
        To     = $t.Recipient
        Amount = $t.Amount
      }) | Out-Null
    }
  }
}

Write-Host ""
Write-Host "Query: coin_received.receiver='$HotWallet' (any sender)"
$hashes = Invoke-CometTxSearch -RpcBase $RpcBase -Query "coin_received.receiver='$HotWallet'" -PageLimit $PageLimit -MaxPages $MaxPages
foreach ($h in $hashes) {
  $tx = Invoke-CometTx -RpcBase $RpcBase -Hash $h
  if (-not $tx -or -not $tx.result) { continue }
  $time = $tx.result.tx_result.timestamp
  $transfers = Parse-Transfers -TxResult $tx.result
  foreach ($t in $transfers) {
    if ($t.Recipient -eq $HotWallet) {
      $results.Add([PSCustomObject]@{
        Time   = $time
        Hash   = $h
        Source = "coin_received"
        Sender = $t.Sender
        To     = $t.Recipient
        Amount = $t.Amount
      }) | Out-Null
    }
  }
}

Write-Host ""
Write-Host "Query: message.sender='$HotWallet' (outgoing activity)"
$hashes = Invoke-CometTxSearch -RpcBase $RpcBase -Query "message.sender='$HotWallet'" -PageLimit $PageLimit -MaxPages $MaxPages
foreach ($h in $hashes) {
  $tx = Invoke-CometTx -RpcBase $RpcBase -Hash $h
  if (-not $tx -or -not $tx.result) { continue }
  $time = $tx.result.tx_result.timestamp
  $transfers = Parse-Transfers -TxResult $tx.result
  foreach ($t in $transfers) {
    if ($t.Sender -eq $HotWallet) {
      $results.Add([PSCustomObject]@{
        Time   = $time
        Hash   = $h
        Source = "message.sender"
        Sender = $t.Sender
        To     = $t.Recipient
        Amount = $t.Amount
      }) | Out-Null
    }
  }
}

Write-Host ""
Write-Host "Query: transfer.packet_dst_port='transfer' AND transfer.packet_dst_channel (IBC recv)"
$hashes = Invoke-CometTxSearch -RpcBase $RpcBase -Query "transfer.packet_dst_port='transfer'" -PageLimit $PageLimit -MaxPages $MaxPages
foreach ($h in $hashes) {
  $tx = Invoke-CometTx -RpcBase $RpcBase -Hash $h
  if (-not $tx -or -not $tx.result) { continue }
  $time = $tx.result.tx_result.timestamp
  $transfers = Parse-Transfers -TxResult $tx.result
  foreach ($t in $transfers) {
    if ($t.Recipient -eq $HotWallet) {
      $results.Add([PSCustomObject]@{
        Time   = $time
        Hash   = $h
        Source = "ibc.recv"
        Sender = $t.Sender
        To     = $t.Recipient
        Amount = $t.Amount
      }) | Out-Null
    }
  }
}

if ($results.Count -eq 0) {
  Write-Host ""
  Write-Host "No transfers found to hot wallet."
}

Write-Host ""
Write-Host "== Transfers to hot wallet =="
$results | Sort-Object Time | Format-Table -AutoSize

Write-Host ""
Write-Host "== Outgoing activity summary (hot wallet) =="
$outgoingHashes = Invoke-CometTxSearch -RpcBase $RpcBase -Query "message.sender='$HotWallet'" -PageLimit $PageLimit -MaxPages $MaxPages -OrderBy "asc"
if ($outgoingHashes.Count -eq 0) {
  Write-Host "No outgoing message.sender activity found."
  exit 0
}

Write-Host ("Total outgoing txs: {0}" -f $outgoingHashes.Count)
$sample = New-Object System.Collections.Generic.List[object]
$maxSample = [Math]::Min(10, $outgoingHashes.Count)
for ($i = 0; $i -lt $maxSample; $i++) {
  $h = $outgoingHashes[$i]
  $tx = Invoke-CometTx -RpcBase $RpcBase -Hash $h
  if (-not $tx -or -not $tx.result) { continue }
  $time = $tx.result.tx_result.timestamp
  $sample.Add([PSCustomObject]@{ Time = $time; Hash = $h }) | Out-Null
}
if ($sample.Count -gt 0) {
  Write-Host ""
  Write-Host "Earliest outgoing (first 10):"
  $sample | Format-Table -AutoSize
}

$outgoingHashesDesc = Invoke-CometTxSearch -RpcBase $RpcBase -Query "message.sender='$HotWallet'" -PageLimit $PageLimit -MaxPages $MaxPages -OrderBy "desc"
$sample2 = New-Object System.Collections.Generic.List[object]
for ($i = 0; $i -lt $maxSample; $i++) {
  $h = $outgoingHashesDesc[$i]
  $tx = Invoke-CometTx -RpcBase $RpcBase -Hash $h
  if (-not $tx -or -not $tx.result) { continue }
  $time = $tx.result.tx_result.timestamp
  $sample2.Add([PSCustomObject]@{ Time = $time; Hash = $h }) | Out-Null
}
if ($sample2.Count -gt 0) {
  Write-Host ""
  Write-Host "Latest outgoing (last 10):"
  $sample2 | Format-Table -AutoSize
}
