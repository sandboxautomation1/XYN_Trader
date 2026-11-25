$ErrorActionPreference = "Stop"

$logDirectory = Join-Path $PSScriptRoot "logs"
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logFile = Join-Path $logDirectory ("ai-service-{0}.log" -f (Get-Date -Format "yyyyMMdd-HHmmss"))

Start-Transcript -Path $logFile -Append

try {
    Set-Location "E:\Projects_MT5\XYN_Trader\ai-service"

    & python -m venv .venv
    & .\.venv\Scripts\python.exe -m pip install -r requirements.txt
    & .\.venv\Scripts\python.exe -m uvicorn app:app --host 127.0.0.1 --port 8001
}
catch {
    Write-Error $_
    exit 1
}
finally {
    Stop-Transcript
}
