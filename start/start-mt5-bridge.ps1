$ErrorActionPreference = "Stop"

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$bridgeRoot = Join-Path $projectRoot "mt5-bridge"
$logDirectory = Join-Path $PSScriptRoot "logs"
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logFile = Join-Path $logDirectory ("mt5-bridge-{0}.log" -f (Get-Date -Format "yyyyMMdd-HHmmss"))

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
    $existingBridge = $null
    try {
        $existingBridge = Invoke-RestMethod -Uri "http://127.0.0.1:8000/health" -TimeoutSec 2
    }
    catch {
        # Nothing is answering on the bridge health endpoint; continue startup.
    }

    $hasBridgeHealth = $null -ne $existingBridge -and
        $existingBridge.status -eq "ok" -and
        $null -ne $existingBridge.PSObject.Properties["mt5_connected"]
    if ($hasBridgeHealth) {
        $existingBridgeApi = $null
        try {
            $existingBridgeApi = Invoke-RestMethod -Uri "http://127.0.0.1:8000/openapi.json" -TimeoutSec 2
        }
        catch {
            # A reachable bridge without an OpenAPI document is not treated as the updated bridge.
        }

        $hasSignalCalculatorRoute = $null -ne $existingBridgeApi -and
            $null -ne $existingBridgeApi.paths.PSObject.Properties["/signal-calculator"]
        $hasPnlLotsInput = $false
        $hasPnlLotsOutput = $false
        if ($null -ne $existingBridgeApi -and $null -ne $existingBridgeApi.components.schemas) {
            $requestSchemaProperty = $existingBridgeApi.components.schemas.PSObject.Properties["SignalRiskRequest"]
            if ($null -ne $requestSchemaProperty -and $null -ne $requestSchemaProperty.Value.properties) {
                $hasPnlLotsInput = $null -ne $requestSchemaProperty.Value.properties.PSObject.Properties["pnl_lots"]
            }
            $responseSchemaProperty = $existingBridgeApi.components.schemas.PSObject.Properties["SignalRiskResponse"]
            if ($null -ne $responseSchemaProperty -and $null -ne $responseSchemaProperty.Value.properties) {
                $hasPnlLotsOutput = $null -ne $responseSchemaProperty.Value.properties.PSObject.Properties["pnl_lots"]
            }
        }
        if (-not $hasSignalCalculatorRoute -or -not $hasPnlLotsInput -or -not $hasPnlLotsOutput) {
            throw "Вече работи стара версия на MT5 bridge без поддръжка на pnl_lots. Натиснете Ctrl+C в прозореца на стария bridge и стартирайте този BAT отново."
        }

        $connectionState = if ($existingBridge.mt5_connected) { "свързан" } else { "несвързан" }
        Write-Host "MT5 bridge вече работи на 127.0.0.1:8000; MT5 е $connectionState."
        return
    }

    $portListeners = @()
    if (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue) {
        try {
            $portListeners = @(Get-NetTCPConnection -LocalPort 8000 -State Listen -ErrorAction Stop)
        }
        catch {
            # If listener inspection is unavailable, let the bridge report its bind error.
        }
    }
    if ($portListeners.Count -gt 0) {
        $processIds = @($portListeners | Select-Object -ExpandProperty OwningProcess -Unique)
        throw "Порт 8000 вече се използва от процес с PID $($processIds -join ', '). Затворете процеса или освободете порта, преди да стартирате MT5 bridge."
    }

    if (-not (Get-Command uv -ErrorAction SilentlyContinue)) {
        throw "Не е намерен uv. Инсталирайте uv, за да бъде създадена средата на MT5 bridge."
    }

    Set-Location $bridgeRoot
    $bridgePython = Join-Path $bridgeRoot ".venv\Scripts\python.exe"
    $environmentReady = $false

    if (Test-Path -LiteralPath $bridgePython -PathType Leaf) {
        try {
            $null = & $bridgePython -c "import MetaTrader5, fastapi, pandas" 2>$null
            $environmentReady = $LASTEXITCODE -eq 0
        }
        catch {
            $environmentReady = $false
        }
    }

    if (-not $environmentReady) {
        Write-Host "Създаване на Python среда за MT5 bridge..."
        Invoke-NativeCommand { & uv venv --clear --python 3.13 ".venv" 2>&1 }
        if ($script:LastNativeExitCode -ne 0) {
            throw "uv не успя да създаде Python среда за MT5 bridge."
        }
    }

    Invoke-NativeCommand { & uv sync --locked 2>&1 }
    if ($script:LastNativeExitCode -ne 0) {
        throw "uv не успя да инсталира зависимостите на MT5 bridge."
    }

    Invoke-NativeCommand { & uv run --locked mt5-bridge server --host 127.0.0.1 --port 8000 2>&1 }
    if ($script:LastNativeExitCode -ne 0) {
        throw "MT5 bridge приключи с код $script:LastNativeExitCode."
    }
}
catch {
    Write-Host "Грешка при стартиране на MT5 bridge: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Подробният лог е записан в: $logFile"
    exit 1
}
finally {
    Stop-Transcript
}
