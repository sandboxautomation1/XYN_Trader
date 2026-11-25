$ErrorActionPreference = "Stop"

$logDirectory = Join-Path $PSScriptRoot "logs"
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logFile = Join-Path $logDirectory ("ai-training-service-{0}.log" -f (Get-Date -Format "yyyyMMdd-HHmmss"))

Start-Transcript -Path $logFile -Append

try {
    Set-Location "E:\Projects_MT5\XYN_Trader\ai-service"

    & python -m venv .venv
    & .\.venv\Scripts\python.exe -m pip install -r requirements.txt
    & .\.venv\Scripts\python.exe download_candles.py --interval 1h --start 2024-01-01 --end 2026-11-13 --output data\xauusd_h1.csv
    & .\.venv\Scripts\python.exe generate_candidates.py --candles data\xauusd_h1.csv --output data\signal_candidates.csv --policy signal_policy.json --interval 1h
    & .\.venv\Scripts\python.exe prepare_dataset.py --candles data\xauusd_h1.csv --candidates data\signal_candidates.csv --output data\train_1h.csv --horizon 1h --interval 1h
    & .\.venv\Scripts\python.exe train.py --input data\train_1h.csv --output models\candidates\lightgbm_1h_candidate.joblib --horizon 1h

    $env:XYN_MODEL_DIR = "models"
    & .\.venv\Scripts\python.exe -m uvicorn app:app --host 127.0.0.1 --port 8001
}
catch {
    Write-Error $_
    exit 1
}
finally {
    Stop-Transcript
}

