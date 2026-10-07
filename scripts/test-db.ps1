$ErrorActionPreference = "Stop"
Push-Location (Join-Path $PSScriptRoot "..")
$container = "intellect-test-" + [Guid]::NewGuid().ToString("N")
$oldRun = $env:RUN_LAB1_DB_TESTS
$oldUrl = $env:TEST_DATABASE_URL
try {
    & docker run -d --rm --name $container -e POSTGRES_USER=bot_test -e POSTGRES_PASSWORD=local-test-password -e POSTGRES_DB=bot_test -p 127.0.0.1::5432 postgres:16-bookworm | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Не удалось запустить тестовый PostgreSQL." }
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        & docker exec $container pg_isready -U bot_test -d bot_test 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw "Тестовый PostgreSQL не готов." }
    $mapping = & docker port $container 5432/tcp
    if ($LASTEXITCODE -ne 0) { throw "Не удалось узнать порт БД." }
    $port = ($mapping.Trim() -split ':')[-1]
    $env:RUN_LAB1_DB_TESTS = "1"
    $env:TEST_DATABASE_URL = "Host=127.0.0.1;Port=$port;Database=bot_test;Username=bot_test;Password=local-test-password"
    & dotnet restore --locked-mode
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & dotnet test -c Release --no-restore --filter 'Category=Integration'
    exit $LASTEXITCODE
} finally {
    & docker rm -f $container 2>$null | Out-Null
    $env:RUN_LAB1_DB_TESTS = $oldRun
    $env:TEST_DATABASE_URL = $oldUrl
    Pop-Location
}
