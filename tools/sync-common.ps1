param(
    [string]$Target = 'G:\Unity Projects\NetPulse Test\Packages\com.netpulse.unity\Runtime\Common'
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '..\src\NetPulse.Common' | Resolve-Path | Select-Object -ExpandProperty Path

New-Item -ItemType Directory -Force -Path $Target | Out-Null
$targetFull = (Resolve-Path $Target).Path

$sourceFiles = Get-ChildItem -Path $source -Recurse -Filter *.cs -File |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }

$expected = @{}
$copied = 0

foreach ($file in $sourceFiles)
{
    $relative = $file.FullName.Substring($source.Length).TrimStart('\', '/')
    $destination = Join-Path $targetFull $relative
    $expected[$destination.ToLowerInvariant()] = $true

    New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null

    $same = (Test-Path $destination) -and ((Get-FileHash $destination).Hash -eq (Get-FileHash $file.FullName).Hash)
    if (-not $same)
    {
        Copy-Item -Path $file.FullName -Destination $destination -Force
        $copied++
    }
}

$removed = 0
Get-ChildItem -Path $targetFull -Recurse -Filter *.cs -File | ForEach-Object {
    if (-not $expected.ContainsKey($_.FullName.ToLowerInvariant()))
    {
        Remove-Item -Path $_.FullName -Force
        $meta = $_.FullName + '.meta'
        if (Test-Path $meta)
        {
            Remove-Item -Path $meta -Force
        }
        $removed++
    }
}

Write-Host "sync-common: $($sourceFiles.Count) files, $copied copied, $removed stale removed -> $targetFull"
