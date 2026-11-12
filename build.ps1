$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet run --project Checks/Mixer.Checks.csproj
    if ($LASTEXITCODE -ne 0) { throw 'Core checks failed' }
    dotnet publish App/MixerStudio.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    Copy-Item README-TH.md dist/
    Write-Host 'Ready: dist/MixerStudio.exe'
} finally { Pop-Location }
