$procs = Get-Process dotnet,OPTIC -ErrorAction SilentlyContinue
if (-not $procs) {
    Write-Host "No dotnet or OPTIC processes found."
    return
}

$ids = $procs.Id | Sort-Object -Unique
Write-Host ("Stopping process IDs: {0}" -f ($ids -join ', '))

$ids | ForEach-Object {
    try {
        Stop-Process -Id $_ -Force -ErrorAction Stop
        Write-Host ("Stopped PID {0}" -f $_)
    }
    catch {
        Write-Warning ("Failed to stop PID {0}: {1}" -f $_, $_.Exception.Message)
    }
}
