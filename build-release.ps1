# Собирает готовый к скачиванию архив: dist\Zapret-<версия>-win-x64.zip
# Внутри Zapret.exe со встроенным .NET - пользователю ничего ставить не нужно.
# Запуск: powershell -ExecutionPolicy Bypass -File .\build-release.ps1

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

[xml]$proj = Get-Content 'src\Zapret.App\Zapret.App.csproj'
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$out = Join-Path $PSScriptRoot "dist\Zapret-$version"
$zip = Join-Path $PSScriptRoot "dist\Zapret-$version-win-x64.zip"

# Тесты лежат только у разработчика - в публичном репозитории их нет.
if (Test-Path 'tests\Zapret.Core.Tests') {
    Write-Host "Тесты..."
    dotnet test tests\Zapret.Core.Tests -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Тесты не прошли - архив не собираю.' }
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

Write-Host "Сборка $version..."
# Без сжатия внутри exe: сжатый single-file антивирусы принимают за «упакованный» файл и проверяют строже.
dotnet publish src\Zapret.App -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false `
    -p:DebugType=none -p:GenerateDocumentationFile=false -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw 'Сборка не удалась.' }

Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
Write-Host "Готово: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) МБ)"
