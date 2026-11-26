$ErrorActionPreference = "Stop"

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$logDirectory = Join-Path $PSScriptRoot "logs"
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logFile = Join-Path $logDirectory ("xyn-trader-{0}.log" -f (Get-Date -Format "yyyyMMdd-HHmmss"))

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
    Set-Location $projectRoot

    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot "appsettings.json"))) {
        Copy-Item (Join-Path $projectRoot "appsettings.example.json") (Join-Path $projectRoot "appsettings.json")
    }

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "Не е намерен .NET SDK. Инсталирайте .NET 10 SDK на тази машина."
    }

    Write-Host "Стартира се XYN Trader. MT5 bridge и AI service се стартират от отделните BAT файлове."
    Invoke-NativeCommand { & dotnet run --project (Join-Path $projectRoot "XYN_Trader.csproj") 2>&1 }
    if ($script:LastNativeExitCode -ne 0) {
        throw "Уеб приложението приключи с код $script:LastNativeExitCode."
    }
}
catch {
    Write-Error $_
    exit 1
}
finally {
    Stop-Transcript
}
