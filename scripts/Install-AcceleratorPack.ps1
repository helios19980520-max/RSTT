[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $AppDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-NormalizedPath {
    param([Parameter(Mandatory = $true)][string] $Path)
    return [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
}

$packRoot = Get-NormalizedPath -Path $PSScriptRoot
$appRoot = Get-NormalizedPath -Path $AppDirectory
$appExecutable = Join-Path $appRoot 'RSTT.App.exe'
$manifestPath = Join-Path $packRoot 'manifest.json'
$sourceWorkers = Join-Path $packRoot 'workers'
$targetWorkers = Join-Path $appRoot 'workers'

if (-not (Test-Path -LiteralPath $appExecutable -PathType Leaf)) {
    throw "RSTT.App.exe was not found in '$appRoot'. Pass the CPU application's publish/install directory."
}

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "The accelerator-pack manifest is missing: $manifestPath"
}

if (-not (Test-Path -LiteralPath $sourceWorkers -PathType Container)) {
    throw "The accelerator-pack workers directory is missing: $sourceWorkers"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.formatVersion -ne 1) {
    throw "Unsupported accelerator-pack manifest version '$($manifest.formatVersion)'."
}

Write-Host 'Validating accelerator-pack file sizes and SHA-256 hashes...'
foreach ($file in $manifest.files) {
    $candidate = Get-NormalizedPath -Path (Join-Path $packRoot $file.path)
    if (-not $candidate.StartsWith(
            $packRoot + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Manifest path escapes the accelerator pack: $($file.path)"
    }

    $item = Get-Item -LiteralPath $candidate -ErrorAction Stop
    if ($item.Length -ne [long]$file.size) {
        throw "Size validation failed for '$($file.path)'."
    }

    $actualHash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
    if (-not $actualHash.Equals(
            [string]$file.sha256,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "SHA-256 validation failed for '$($file.path)'."
    }
}

New-Item -ItemType Directory -Force -Path $targetWorkers | Out-Null
$workerVersions = @(
    @{
        Source = Join-Path $sourceWorkers 'sherpa-cuda12\1.13.4'
        Target = Join-Path $targetWorkers 'sherpa-cuda12\1.13.4'
    },
    @{
        Source = Join-Path $sourceWorkers 'whisper-cuda12\1.9.1'
        Target = Join-Path $targetWorkers 'whisper-cuda12\1.9.1'
    }
)

foreach ($worker in $workerVersions) {
    $source = Get-NormalizedPath -Path $worker.Source
    $target = Get-NormalizedPath -Path $worker.Target
    if (-not (Test-Path -LiteralPath $source -PathType Container)) {
        throw "A required worker directory is missing: $source"
    }

    $targetParent = Split-Path -Parent $target
    New-Item -ItemType Directory -Force -Path $targetParent | Out-Null
    $stage = "$target.install-$([guid]::NewGuid().ToString('N'))"
    $backup = "$target.backup-$(Get-Date -Format 'yyyyMMdd-HHmmss')"

    if (-not $stage.StartsWith(
            $targetParent + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing an invalid staging path: $stage"
    }

    try {
        Copy-Item -LiteralPath $source -Destination $stage -Recurse
        if (Test-Path -LiteralPath $target) {
            Move-Item -LiteralPath $target -Destination $backup
        }

        Move-Item -LiteralPath $stage -Destination $target
        Write-Host "Installed: $target"
        if (Test-Path -LiteralPath $backup) {
            Write-Host "Previous version retained for recovery: $backup"
        }
    }
    catch {
        if (Test-Path -LiteralPath $stage) {
            Remove-Item -LiteralPath $stage -Recurse -Force
        }

        if (-not (Test-Path -LiteralPath $target) -and
            (Test-Path -LiteralPath $backup)) {
            Move-Item -LiteralPath $backup -Destination $target
        }

        throw
    }
}

Write-Host ''
Write-Host 'RSTT Accelerator Pack installed successfully.'
Write-Host 'Restart RSTT, select Auto or CUDA in Settings, then run Diagnostics.'

