param(
    [string]$Target = 'G:\Unity Projects\NetPulse Test\Packages\com.netpulse.unity\Runtime\Analyzers'
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\NetPulse.Unity.Generators\NetPulse.Unity.Generators.csproj' | Resolve-Path | Select-Object -ExpandProperty Path

dotnet build $project -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0)
{
    throw 'Generator build failed'
}

$built = Join-Path (Split-Path $project) 'bin\Release\netstandard2.0\NetPulse.Generators.dll'
New-Item -ItemType Directory -Force -Path $Target | Out-Null
Copy-Item -Path $built -Destination (Join-Path $Target 'NetPulse.Generators.dll') -Force

Write-Host "build-generators: copied NetPulse.Generators.dll -> $Target"
