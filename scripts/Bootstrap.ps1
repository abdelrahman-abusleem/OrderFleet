param([ValidatePattern('^orderfleet-[a-z0-9-]+$')][string]$ProjectName = 'orderfleet-demo', [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$bootstrapRoot = Split-Path $PSScriptRoot -Parent
Push-Location $bootstrapRoot
try {
    docker info --format '{{.ServerVersion}}' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Docker is required.' }
    if (-not (Test-Path -LiteralPath '.env')) { Copy-Item -LiteralPath '.env.example' -Destination '.env' }
    $bootstrapText = [IO.File]::ReadAllText((Join-Path $bootstrapRoot '.env'))
    $generated = @{
        POSTGRES_PASSWORD = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        RABBITMQ_PASSWORD = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        JWT_SIGNING_KEY = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(64))
        DEMO_PASSWORD = 'Aa1!' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    }
    foreach ($key in $generated.Keys) {
        $pattern = '(?m)^' + [regex]::Escape($key) + '=[^\r\n]*'
        $match = [regex]::Match($bootstrapText, $pattern)
        if (-not $match.Success) { $bootstrapText += "`n$key=$($generated[$key])`n" }
        elseif ($match.Value -match '^[^=]+=(?:\s*|""|'''')$') {
            $bootstrapText = [regex]::Replace($bootstrapText, $pattern, "$key=$($generated[$key])")
        }
    }
    [IO.File]::WriteAllText((Join-Path $bootstrapRoot '.env'), $bootstrapText)
    
    docker compose --project-name $ProjectName --profile app config --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Compose configuration validation failed.' }
    if (-not $NoBuild) {
        docker compose --project-name $ProjectName --profile app build api
        if ($LASTEXITCODE -ne 0) { throw 'API build failed.' }
    }
    docker compose --project-name $ProjectName --profile app up -d --wait --wait-timeout 120 postgis rabbitmq
    if ($LASTEXITCODE -ne 0) { throw 'Dependency readiness failed.' }
    docker compose --project-name $ProjectName --profile app stop api
    if ($LASTEXITCODE -ne 0) { throw 'Could not stop API before explicit migration.' }
    foreach ($operation in @('--migrate', '--seed-demo')) {
        docker compose --project-name $ProjectName --profile app run --rm --no-deps api $operation
        if ($LASTEXITCODE -ne 0) { throw "Explicit $operation failed. Existing data and credentials were preserved." }
    }
    docker compose --project-name $ProjectName --profile app up -d --no-deps --wait --wait-timeout 120 api
    if ($LASTEXITCODE -ne 0) { throw 'API readiness failed.' }
    $address = (docker compose --project-name $ProjectName --profile app port api 8080).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not discover API port.' }
    Write-Output "API ready at http://$address. Run scripts/Invoke-Demo.ps1 -BaseUrl http://$address. Credentials stay in ignored .env."
}
finally { Pop-Location }
