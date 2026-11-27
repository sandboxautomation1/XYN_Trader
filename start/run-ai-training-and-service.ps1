$ErrorActionPreference = "Stop"

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$aiRoot = Join-Path $projectRoot "ai-service"
$logDirectory = Join-Path $PSScriptRoot "logs"
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logFile = Join-Path $logDirectory ("ai-training-service-{0}.log" -f (Get-Date -Format "yyyyMMdd-HHmmss"))

Start-Transcript -Path $logFile -Append

function Invoke-NativeCommand {
    param([scriptblock] $Command)

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        & $Command | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) {
                Write-Host $_.Exception.Message
            }
            else {
                Write-Host $_
            }
        }
        $script:LastNativeExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}

try {
    if (-not (Get-Command uv -ErrorAction SilentlyContinue)) {
        throw "Не е намерен uv. Инсталирайте uv, за да бъде създадена средата на AI услугата."
    }

    Set-Location $aiRoot
    $python = Join-Path $aiRoot ".venv\Scripts\python.exe"
    $environmentReady = $false

    if (Test-Path -LiteralPath $python -PathType Leaf) {
        try {
            $null = & $python -c "import fastapi, uvicorn, lightgbm, numpy, pandas, sklearn, joblib" 2>$null
            $environmentReady = $LASTEXITCODE -eq 0
        }
        catch {
            $environmentReady = $false
        }
    }

    if (-not $environmentReady) {
        Write-Host "Създаване на Python среда за AI услугата..."
        Invoke-NativeCommand { & uv venv --clear --python 3.13 ".venv" 2>&1 }
        if ($script:LastNativeExitCode -ne 0) {
            throw "uv не успя да създаде Python среда за AI услугата."
        }
    }

    Invoke-NativeCommand { & uv pip install --python $python -r (Join-Path $aiRoot "requirements.txt") 2>&1 }
    if ($script:LastNativeExitCode -ne 0) {
        throw "uv не успя да инсталира зависимостите на AI услугата."
    }

    Invoke-NativeCommand { & $python download_candles.py --interval 1h --start 2024-01-01 --end 2026-11-13 --output data\xauusd_h1.csv 2>&1 }
    if ($script:LastNativeExitCode -ne 0) { throw "Изтеглянето на свещите е неуспешно." }
    Invoke-NativeCommand { & $python download_candles.py --interval 5min --start 2024-01-01 --end 2026-11-13 --output data\xauusd_m5.csv 2>&1 }
    if ($script:LastNativeExitCode -ne 0) { throw "Изтеглянето на петминутните свещи е неуспешно." }
    Invoke-NativeCommand { & $python generate_candidates.py --candles data\xauusd_h1.csv --output data\signal_candidates.csv --policy signal_policy.json --interval 1h 2>&1 }
    if ($script:LastNativeExitCode -ne 0) { throw "Генерирането на кандидати е неуспешно." }
    Invoke-NativeCommand { & $python prepare_dataset.py --candles data\xauusd_h1.csv --candidates data\signal_candidates.csv --output data\train_1h.csv --horizon 1h --interval 1h --outcome-candles data\xauusd_m5.csv --outcome-interval 5min 2>&1 }
    if ($script:LastNativeExitCode -ne 0) { throw "Подготовката на набора за обучение е неуспешна." }
    Invoke-NativeCommand { & $python train.py --input data\train_1h.csv --output models\candidates\lightgbm_1h_candidate.joblib --horizon 1h 2>&1 }
    if ($script:LastNativeExitCode -ne 0) { throw "Обучението на модела е неуспешно." }
}
catch {
    Write-Error $_
    exit 1
}
finally {
    Stop-Transcript
}
