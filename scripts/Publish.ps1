param([string]$OutputDirectory = 'artifacts/windows')
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$destination = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
Push-Location -LiteralPath $repoRoot
try {
    & dotnet publish OsuHue/OsuHue.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $destination
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES') -Destination $destination -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'QUICKSTART.txt') -Destination $destination -Force
    Write-Output "Published to $destination"
}
finally { Pop-Location }
