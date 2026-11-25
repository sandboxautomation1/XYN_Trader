param(
    [Parameter(Mandatory = $true)]
    [string] $Candidate,

    [Parameter(Mandatory = $true)]
    [string] $ApprovedBy,

    [switch] $Replace
)

$ErrorActionPreference = 'Stop'
$candidatePath = (Resolve-Path -LiteralPath $Candidate).Path
$reportPath = "$candidatePath.report.json"
if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
    throw "Не е намерен отчетът за кандидата: $reportPath"
}

$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$requiredMetrics = @('holdout_auc', 'holdout_brier_score', 'baseline_brier_score')
$missingMetrics = @($requiredMetrics | Where-Object { $null -eq $report.$_ })
if ($missingMetrics.Count -gt 0) {
    throw "Отчетът '$reportPath' няма стойност за метриките: $($missingMetrics -join ', ')."
}

# Activation gate: require better-than-random ranking and improvement over
# the training-prevalence baseline before allowing promotion.
if ([double]$report.holdout_auc -lt 0.5) {
    throw ("Кандидатът не минава: holdout AUC {0:N3} е под 0.500." -f [double]$report.holdout_auc)
}
if ([double]$report.holdout_brier_score -ge [double]$report.baseline_brier_score) {
    throw ("Кандидатът не минава: Brier {0:N3} не е по-добър от базовия {1:N3}." -f [double]$report.holdout_brier_score, [double]$report.baseline_brier_score)
}

$horizon = [string]$report.horizon
if ($horizon -notin @('1h', '24h', '48h')) {
    throw "Невалиден хоризонт в отчета: $horizon"
}
$destination = Join-Path $PSScriptRoot "models\lightgbm_$horizon.joblib"
$python = Join-Path $PSScriptRoot '.venv\Scripts\python.exe'
$promoteScript = Join-Path $PSScriptRoot 'promote_model.py'
if (-not (Test-Path -LiteralPath $python -PathType Leaf)) {
    throw "Не е намерен Python virtual environment: $python. Създайте .venv и инсталирайте ai-service\requirements.txt."
}
if (-not (Test-Path -LiteralPath $promoteScript -PathType Leaf)) {
    throw "Не е намерен скриптът за промотиране: $promoteScript"
}
$maximumBrier = ([double]$report.baseline_brier_score).ToString([System.Globalization.CultureInfo]::InvariantCulture)
$arguments = @(
    $promoteScript,
    '--candidate', $candidatePath,
    '--destination', $destination,
    '--approved-by', $ApprovedBy,
    '--minimum-auc', '0.5',
    '--maximum-brier', $maximumBrier
)
if ($Replace) {
    $arguments += '--replace'
}

& $python @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Промотирането се провали с код $LASTEXITCODE."
}
