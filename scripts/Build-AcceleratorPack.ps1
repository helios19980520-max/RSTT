[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $OutputRoot = '',
    [string] $SherpaArchivePath = '',
    [string] $CudaToolkitRoot = '',
    [string] $CudnnBinPath = '',
    [switch] $NoArchive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$sherpaVersion = '1.13.4'
$whisperVersion = '1.9.1'
$packVersion = '1.0.0'
$archiveName = "sherpa-onnx-v$sherpaVersion-cuda-12.x-cudnn-9.x-win-x64-cuda.tar.bz2"
$archiveSha256 = '11b56076060c109e16d85eba3acfb03f6d0c4a738ecd2a99dd45eb689b2051a4'
$archiveUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/v$sherpaVersion/$archiveName"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $artifactRoot 'accelerator-pack'
}

$outputFullPath = [System.IO.Path]::GetFullPath($OutputRoot)
if (-not $outputFullPath.StartsWith(
        $artifactRoot + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must be inside '$artifactRoot'."
}

$packName = "RSTT-Accelerator-Pack-$packVersion-win-x64"
$packRoot = Join-Path $outputFullPath $packName
$cacheRoot = Join-Path $artifactRoot 'cache\accelerator-pack'
$extractRoot = Join-Path $cacheRoot "sherpa-$sherpaVersion"
if ([string]::IsNullOrWhiteSpace($SherpaArchivePath)) {
    $SherpaArchivePath = Join-Path $cacheRoot $archiveName
}

if ([string]::IsNullOrWhiteSpace($CudaToolkitRoot)) {
    $CudaToolkitRoot = [Environment]::GetEnvironmentVariable('CUDA_PATH_V12_8')
}

if ([string]::IsNullOrWhiteSpace($CudaToolkitRoot)) {
    $CudaToolkitRoot = 'C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8'
}

if ([string]::IsNullOrWhiteSpace($CudnnBinPath)) {
    $cudnnCandidate = Get-ChildItem -Path 'C:\Python*\Lib\site-packages\nvidia\cudnn\bin' `
        -Directory -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $cudnnCandidate) {
        $CudnnBinPath = $cudnnCandidate.FullName
    }
}

$cudaBin = Join-Path $CudaToolkitRoot 'bin'
if (-not (Test-Path -LiteralPath $cudaBin -PathType Container)) {
    throw "CUDA 12 toolkit bin directory was not found: $cudaBin"
}

if ([string]::IsNullOrWhiteSpace($CudnnBinPath) -or
    -not (Test-Path -LiteralPath $CudnnBinPath -PathType Container)) {
    throw 'cuDNN 9 bin directory was not found. Pass -CudnnBinPath explicitly.'
}

New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null
if (-not (Test-Path -LiteralPath $SherpaArchivePath -PathType Leaf)) {
    Write-Host "Downloading pinned sherpa CUDA runtime $sherpaVersion..."
    Invoke-WebRequest -Uri $archiveUrl -OutFile $SherpaArchivePath
}

$actualArchiveHash = (Get-FileHash -LiteralPath $SherpaArchivePath -Algorithm SHA256).Hash
if (-not $actualArchiveHash.Equals(
        $archiveSha256,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Sherpa CUDA archive SHA-256 mismatch. Expected $archiveSha256, got $actualArchiveHash."
}

$sherpaDistribution = Join-Path $extractRoot `
    "sherpa-onnx-v$sherpaVersion-cuda-12.x-cudnn-9.x-win-x64-cuda"
if (-not (Test-Path -LiteralPath $sherpaDistribution -PathType Container)) {
    New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
    & tar -xf $SherpaArchivePath -C $extractRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to extract the pinned sherpa CUDA archive (tar exit $LASTEXITCODE)."
    }
}

if (Test-Path -LiteralPath $packRoot) {
    $resolvedPackRoot = [System.IO.Path]::GetFullPath($packRoot)
    if (-not $resolvedPackRoot.StartsWith(
            $outputFullPath + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean an invalid pack path: $resolvedPackRoot"
    }

    Remove-Item -LiteralPath $resolvedPackRoot -Recurse -Force
}

$sherpaWorker = Join-Path $packRoot "workers\sherpa-cuda12\$sherpaVersion"
$whisperWorker = Join-Path $packRoot "workers\whisper-cuda12\$whisperVersion"
New-Item -ItemType Directory -Force -Path $sherpaWorker, $whisperWorker | Out-Null

Write-Host 'Publishing versioned CUDA workers...'
& dotnet publish `
    (Join-Path $repoRoot 'src\RSTT.Sherpa.Cuda12.Worker\RSTT.Sherpa.Cuda12.Worker.csproj') `
    -c $Configuration -r win-x64 --self-contained true `
    -o $sherpaWorker
if ($LASTEXITCODE -ne 0) {
    throw "Sherpa CUDA worker publish failed (dotnet exit $LASTEXITCODE)."
}

& dotnet publish `
    (Join-Path $repoRoot 'src\RSTT.Whisper.Cuda12.Worker\RSTT.Whisper.Cuda12.Worker.csproj') `
    -c $Configuration -r win-x64 --self-contained true `
    -o $whisperWorker
if ($LASTEXITCODE -ne 0) {
    throw "Whisper CUDA worker publish failed (dotnet exit $LASTEXITCODE)."
}

$sherpaBin = Join-Path $sherpaDistribution 'bin'
$sherpaLib = Join-Path $sherpaDistribution 'lib'
$sherpaNativeFiles = @{
    'onnxruntime.dll' = Join-Path $sherpaBin 'onnxruntime.dll'
    'onnxruntime_providers_cuda.dll' = Join-Path $sherpaBin 'onnxruntime_providers_cuda.dll'
    'onnxruntime_providers_shared.dll' = Join-Path $sherpaBin 'onnxruntime_providers_shared.dll'
    'sherpa-onnx-c-api.dll' = Join-Path $sherpaLib 'sherpa-onnx-c-api.dll'
}
foreach ($entry in $sherpaNativeFiles.GetEnumerator()) {
    Copy-Item -LiteralPath $entry.Value -Destination (Join-Path $sherpaWorker $entry.Key) -Force
}

$cudaFiles = @(
    'cudart64_12.dll',
    'cublas64_12.dll',
    'cublasLt64_12.dll',
    'cufft64_11.dll',
    'nvrtc64_120_0.dll',
    'nvrtc-builtins64_128.dll'
)
foreach ($fileName in $cudaFiles) {
    $source = Join-Path $cudaBin $fileName
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required CUDA redistributable is missing: $source"
    }

    Copy-Item -LiteralPath $source -Destination $sherpaWorker -Force
}

$cudnnFiles = Get-ChildItem -LiteralPath $CudnnBinPath -Filter 'cudnn*64_9.dll' -File
if (-not ($cudnnFiles.Name -contains 'cudnn64_9.dll')) {
    throw "Required cuDNN entry library is missing from '$CudnnBinPath'."
}

foreach ($file in $cudnnFiles) {
    Copy-Item -LiteralPath $file.FullName -Destination $sherpaWorker -Force
}

$linuxWhisperRuntime = Join-Path $whisperWorker 'runtimes\cuda12\linux-x64'
if (Test-Path -LiteralPath $linuxWhisperRuntime) {
    Remove-Item -LiteralPath $linuxWhisperRuntime -Recurse -Force
}

$noticeRoot = Join-Path $packRoot 'notices'
New-Item -ItemType Directory -Force -Path $noticeRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') `
    -Destination (Join-Path $noticeRoot 'RSTT-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') `
    -Destination (Join-Path $noticeRoot 'THIRD_PARTY_NOTICES.md')
Copy-Item -LiteralPath (Join-Path $CudaToolkitRoot 'EULA.txt') `
    -Destination (Join-Path $noticeRoot 'NVIDIA-CUDA-EULA.txt')

$cudnnPackageRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $CudnnBinPath))
$cudnnLicense = Get-ChildItem -Path "$cudnnPackageRoot\nvidia_cudnn_cu12-*.dist-info\licenses\License.txt" `
    -File -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($null -eq $cudnnLicense) {
    throw 'The installed cuDNN license file could not be located.'
}

Copy-Item -LiteralPath $cudnnLicense.FullName `
    -Destination (Join-Path $noticeRoot 'NVIDIA-cuDNN-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-AcceleratorPack.ps1') `
    -Destination (Join-Path $packRoot 'Install-AcceleratorPack.ps1')

$manifestFiles = Get-ChildItem -LiteralPath $packRoot -Recurse -File |
    Where-Object Name -ne 'manifest.json' |
    Sort-Object FullName |
    ForEach-Object {
        $relative = (
            [System.IO.Path]::GetRelativePath($packRoot, $_.FullName)
        ).Replace([System.IO.Path]::DirectorySeparatorChar, '/')
        [ordered]@{
            path = $relative
            size = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }

$manifest = [ordered]@{
    formatVersion = 1
    product = 'RSTT Accelerator Pack'
    productVersion = $packVersion
    platform = 'win-x64'
    sherpaOnnxVersion = $sherpaVersion
    whisperRuntimeVersion = $whisperVersion
    cudaRuntime = '12.8'
    cudnnRuntime = '9.24.0.43'
    sherpaArchiveSha256 = $archiveSha256
    createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
    files = @($manifestFiles)
}
$manifest | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $packRoot 'manifest.json') -Encoding utf8

$summary = Get-ChildItem -LiteralPath $packRoot -Recurse -File |
    Measure-Object -Property Length -Sum
Write-Host "Pack directory: $packRoot"
Write-Host "Files: $($summary.Count)"
Write-Host "Bytes: $($summary.Sum)"

if (-not $NoArchive) {
    $zipPath = Join-Path $outputFullPath "$packName.zip"
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Write-Host "Creating Zip64 archive: $zipPath"
    & tar -a -cf $zipPath -C $outputFullPath $packName
    if ($LASTEXITCODE -ne 0) {
        throw "Accelerator archive creation failed (tar exit $LASTEXITCODE)."
    }

    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$zipPath.sha256" `
        -Value "$zipHash  $(Split-Path -Leaf $zipPath)" `
        -Encoding ascii
    Write-Host "Archive SHA-256: $zipHash"
}
