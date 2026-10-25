# Uses only a new disposable container. Never targets the developer database.
$ErrorActionPreference = 'Stop'
$migrationContainer = 'orderfleet-migration-audit-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$env:POSTGRES_PASSWORD = [Guid]::NewGuid().ToString('N')
try {
    docker run --detach --rm --name $migrationContainer --env POSTGRES_PASSWORD `
        --env POSTGRES_USER=orderfleet_audit --env POSTGRES_DB=orderfleet_audit `
        --publish 127.0.0.1::5432 postgres:17 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Disposable database startup failed.' }
    $migrationReady = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        docker exec $migrationContainer pg_isready -U orderfleet_audit -d orderfleet_audit *> $null
        if ($LASTEXITCODE -eq 0) { $migrationReady = $true; break }
        Start-Sleep -Seconds 1
    }
    if (-not $migrationReady) { throw 'Disposable database readiness timed out.' }
    $migrationPort = (docker inspect --format '{{(index (index .NetworkSettings.Ports "5432/tcp") 0).HostPort}}' $migrationContainer).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not read temporary database port.' }
    $env:ConnectionStrings__OrderFleet = "Host=127.0.0.1;Port=$migrationPort;Database=orderfleet_audit;Username=orderfleet_audit;Password=$env:POSTGRES_PASSWORD"
    dotnet ef database update --project src/OrderFleet.Infrastructure --startup-project src/OrderFleet.Api --configuration Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Migration CLI failed.' }
    docker exec $migrationContainer psql -U orderfleet_audit -d orderfleet_audit `
        -c 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";'
    if ($LASTEXITCODE -ne 0) { throw 'Could not verify migration history.' }
}
finally {
    docker stop $migrationContainer | Out-Null
    Remove-Item Env:POSTGRES_PASSWORD,Env:ConnectionStrings__OrderFleet -ErrorAction SilentlyContinue
}
