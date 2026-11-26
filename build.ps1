# Compila no Docker e deixa o exe sempre em dist\PokeDeck.exe (dist\ fica fora do git).
# Sem ErrorActionPreference=Stop: o docker escreve o progresso no stderr, que o PowerShell 5.1 trataria como erro.
Set-Location $PSScriptRoot

if (Get-Process PokeDeck -ErrorAction SilentlyContinue) {
    Write-Host 'Feche o PokeDeck antes de compilar (o exe em uso não pode ser substituído).' -ForegroundColor Yellow
    exit 1
}

docker build --target export --output dist .
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Pronto: $PSScriptRoot\dist\PokeDeck.exe" -ForegroundColor Green
