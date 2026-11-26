$ErrorActionPreference = "Stop"

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$aiRoot = Join-Path $projectRoot "ai-service"
$logDirectory = Join-Path $PSScriptRoot "logs"
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logFile = Join-Path $logDirectory ("ai-service-{0}.log" -f (Get-Date -Format "yyyyMMdd-HHmmss"))

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

    Invoke-NativeCommand { & $python -m uvicorn app:app --host 127.0.0.1 --port 8001 2>&1 }
    if ($script:LastNativeExitCode -ne 0) {
        throw "AI услугата приключи с код $script:LastNativeExitCode."
    }
}
catch {
    Write-Error $_
    exit 1
}
finally {
    Stop-Transcript
}
