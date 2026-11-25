$ErrorActionPreference = "Stop"

$logDirectory = Join-Path $PSScriptRoot "logs"
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logFile = Join-Path $logDirectory ("mt5-bridge-{0}.log" -f (Get-Date -Format "yyyyMMdd-HHmmss"))

Start-Transcript -Path $logFile -Append

try {
    Set-Location "E:\Projects_MT5\XYN_Trader\mt5-bridge"

    & uv run mt5-bridge server --host 127.0.0.1 --port 8000
}
catch {
    Write-Error $_
    exit 1
}
finally {
    Stop-Transcript
}
