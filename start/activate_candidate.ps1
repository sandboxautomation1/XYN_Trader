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
if ($null -eq $report.holdout_auc -or $null -eq $report.holdout_brier_score -or $null -eq $report.baseline_brier_score) {
    throw 'Отчетът няма нужните holdout AUC и Brier метрики.'
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
$arguments = @(
    'promote_model.py',
    '--candidate', $candidatePath,
    '--destination', $destination,
    '--approved-by', $ApprovedBy,
    '--minimum-auc', '0.5',
    '--maximum-brier', ([string]([double]$report.baseline_brier_score))
)
if ($Replace) {
    $arguments += '--replace'
}

& .\.venv\Scripts\python.exe @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Промотирането се провали с код $LASTEXITCODE."
}
