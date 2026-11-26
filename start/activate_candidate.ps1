param(
    [Parameter(Mandatory = $true)]
    [string] $Candidate,

    [Parameter(Mandatory = $true)]
    [string] $ApprovedBy,

    [switch] $Replace
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$activationScript = Join-Path $projectRoot "ai-service\activate_candidate.ps1"
$activationArguments = @("-Candidate", $Candidate, "-ApprovedBy", $ApprovedBy)
if ($Replace) {
    $activationArguments += "-Replace"
}

& $activationScript @activationArguments
