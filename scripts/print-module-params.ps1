param(
  [string]$LcdBase = "http://127.0.0.1:1317"
)

function Flatten-Object {
  param(
    [Parameter(Mandatory = $true)] $Value,
    [Parameter(Mandatory = $true)] [string]$Prefix,
    [System.Collections.Generic.List[string]]$Output
  )

  if ($null -eq $Value) {
    $Output.Add("$Prefix=") | Out-Null
    return
  }

  if ($Value -is [string] -or $Value -is [ValueType]) {
    $Output.Add("$Prefix=$Value") | Out-Null
    return
  }

  if ($Value -is [System.Collections.IEnumerable] -and -not ($Value -is [string])) {
    $i = 0
    foreach ($item in $Value) {
      $itemPrefix = "$Prefix[$i]"
      Flatten-Object -Value $item -Prefix $itemPrefix -Output $Output
      $i++
    }
    if ($i -eq 0) {
      $Output.Add("$Prefix=") | Out-Null
    }
    return
  }

  $props = $Value.PSObject.Properties
  if ($props.Count -eq 0) {
    $Output.Add("$Prefix=$Value") | Out-Null
    return
  }

  foreach ($p in $props) {
    $name = if ([string]::IsNullOrWhiteSpace($p.Name)) { "value" } else { $p.Name }
    Flatten-Object -Value $p.Value -Prefix "$Prefix.$name" -Output $Output
  }
}

$endpoints = @(
  "/cosmos/mint/v1beta1/params",
  "/cosmos/mint/v1beta1/annual_provisions",
  "/cosmos/distribution/v1beta1/params",
  "/cosmos/staking/v1beta1/params",
  "/cosmos/slashing/v1beta1/params",
  "/cosmos/gov/v1beta1/params?params=deposit",
  "/cosmos/gov/v1beta1/params?params=voting",
  "/cosmos/gov/v1beta1/params?params=tallying"
)

Write-Host "LCD Base: $LcdBase"

foreach ($ep in $endpoints) {
  $url = $LcdBase.TrimEnd("/") + $ep
  Write-Host ""
  Write-Host "== $ep =="
  try {
    $resp = Invoke-RestMethod -Method Get -Uri $url -TimeoutSec 60
    if ($null -ne $resp) {
      $lines = New-Object System.Collections.Generic.List[string]
      Flatten-Object -Value $resp -Prefix "response" -Output $lines
      $lines | ForEach-Object { Write-Host $_ }
    } else {
      Write-Host "response="
    }
  }
  catch {
    Write-Host "ERROR: $($_.Exception.Message)"
  }
}

try {
  $url = $LcdBase.TrimEnd("/") + "/cosmos/auth/v1beta1/module_accounts"
  $resp = Invoke-RestMethod -Method Get -Uri $url -TimeoutSec 60
  Write-Host ""
  Write-Host "== module_accounts (name/address) =="
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

    if ([string]::IsNullOrWhiteSpace($addr)) {
      Write-Host "module_accounts.$name="
      continue
    }

    Write-Host "module_accounts.$name=$addr"
  }

  Write-Host ""
  Write-Host "== module_accounts (balances uOPT) =="
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

    if ([string]::IsNullOrWhiteSpace($addr)) { continue }

    try {
      $bal = Invoke-RestMethod -Method Get -Uri ($LcdBase.TrimEnd("/") + "/cosmos/bank/v1beta1/balances/$addr") -TimeoutSec 60
      $uopt = ($bal.balances | Where-Object { $_.denom -ieq "uopt" -or $_.denom -ieq "uOPT" } | Select-Object -First 1).amount
      if (-not $uopt) { $uopt = "0" }
      Write-Host "module_accounts.$name.uOPT=$uopt"
    }
    catch {
      Write-Host "module_accounts.$name.uOPT=ERROR: $($_.Exception.Message)"
    }
  }
}
catch {
  Write-Host "ERROR: $($_.Exception.Message)"
}
