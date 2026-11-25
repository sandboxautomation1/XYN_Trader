$ErrorActionPreference = "Stop"

$logDirectory = Join-Path $PSScriptRoot "logs"
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logFile = Join-Path $logDirectory ("xyn-trader-{0}.log" -f (Get-Date -Format "yyyyMMdd-HHmmss"))

Start-Transcript -Path $logFile -Append

try {
    Set-Location "E:\Projects_MT5\XYN_Trader"

    if (-not (Test-Path "appsettings.json")) {
        Copy-Item "appsettings.example.json" "appsettings.json"
    }

    & dotnet run --project XYN_Trader.csproj
}
catch {
    Write-Error $_
    exit 1
}
finally {
    Stop-Transcript
}
