$ErrorActionPreference = "Stop"
Push-Location (Join-Path $PSScriptRoot "..")
try {
    & dotnet run --project src/IntellectBot -- cloud @args
    exit $LASTEXITCODE
} finally { Pop-Location }
