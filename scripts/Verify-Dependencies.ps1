$ErrorActionPreference = 'Stop'
$dependencyRoot = Split-Path $PSScriptRoot -Parent
Push-Location $dependencyRoot
try {
    $dependencyJson = (& dotnet list OrderFleet.slnx package --vulnerable --include-transitive --no-restore --format json) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Dependency vulnerability query failed.' }
    $dependencyAudit = $dependencyJson | ConvertFrom-Json
    if (@($dependencyAudit.projects).Count -eq 0) { throw 'No projects were audited.' }
    if ($dependencyAudit.problems) { throw 'Dependency audit reported an incomplete result.' }
    $dependencyFindings = @(
        foreach ($dependencyProject in $dependencyAudit.projects) {
            foreach ($dependencyFramework in $dependencyProject.frameworks) {
                foreach ($dependencyPackage in @($dependencyFramework.topLevelPackages) + @($dependencyFramework.transitivePackages)) {
                    if ($dependencyPackage.vulnerabilities) {
                        [pscustomobject]@{ Package = $dependencyPackage.id; Version = $dependencyPackage.resolvedVersion; Advisories = $dependencyPackage.vulnerabilities }
                    }
                }
            }
        }
    )
    if ($dependencyFindings.Count -gt 0) {
        $dependencyFindings | ConvertTo-Json -Depth 5 | Write-Output
        throw 'Known vulnerable dependencies were found.'
    }
    Write-Output "Audited $(@($dependencyAudit.projects).Count) projects: no known vulnerable direct or transitive packages reported."
}
finally { Pop-Location }
