#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch] $CpuOnly,
    [string] $OutputRoot = '',
    [switch] $NoArchive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = Join-Path $repoRoot 'artifacts'
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $artifactRoot 'release'
}
$outputFullPath = [System.IO.Path]::GetFullPath($OutputRoot)
if (-not $outputFullPath.StartsWith(
        $artifactRoot + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must be inside '$artifactRoot'."
}

$packageName = if ($CpuOnly) { 'RSTT-win-x64-cpu' } else { 'RSTT-win-x64' }
$releaseRoot = Join-Path $outputFullPath $packageName
$zipPath = Join-Path $outputFullPath "$packageName.zip"
foreach ($path in @($releaseRoot, $zipPath, "$zipPath.sha256")) {
    if (Test-Path -LiteralPath $path) {
        throw "Output already exists: $path. Use a new -OutputRoot to preserve previous releases."
    }
}

$packRoot = Join-Path $artifactRoot 'accelerator-pack\RSTT-Accelerator-Pack-1.0.2-win-x64'
if (-not $CpuOnly) {
    $packManifestPath = Join-Path $packRoot 'manifest.json'
    if (-not (Test-Path -LiteralPath $packManifestPath -PathType Leaf)) {
        throw 'Build the CUDA pack with scripts/Build-AcceleratorPack.ps1 first, or use -CpuOnly.'
    }
    $packManifest = Get-Content -LiteralPath $packManifestPath -Raw | ConvertFrom-Json
    if ($packManifest.productVersion -ne '1.0.2' -or $packManifest.speechWorkerProtocol -ne 2) {
        throw 'This release requires Accelerator Pack 1.0.2 with worker protocol 2.'
    }
    # Managed workers are rebuilt below. Validate the pinned native payload reused from the pack.
    $nativeFiles = @($packManifest.files | Where-Object {
        $_.path -match '^workers/sherpa-cuda12/1\.13\.8/(cud.*|cublas.*|cufft.*|nvrtc.*|onnxruntime.*|sherpa-onnx-c-api)\.dll$'
    })
    if ($nativeFiles.Count -eq 0) { throw 'The CUDA pack manifest contains no native runtime files.' }
    foreach ($file in $nativeFiles) {
        $source = Join-Path $packRoot $file.path
        if ((Get-Item -LiteralPath $source).Length -ne $file.size -or
            (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $file.sha256) {
            throw "CUDA pack validation failed: $($file.path)"
        }
    }
}

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$includeCuda = if ($CpuOnly) { 'false' } else { 'true' }
Write-Host "Publishing self-contained Windows application to $releaseRoot..."
& dotnet publish (Join-Path $repoRoot 'src\RSTT.App\RSTT.App.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishProfile=win-x64 "-p:IncludeAcceleratorWorkers=$includeCuda" `
    -o $releaseRoot
if ($LASTEXITCODE -ne 0) { throw "Application publish failed (dotnet exit $LASTEXITCODE)." }

# Whisper's CPU package also supplies DLLs for other Windows architectures.
foreach ($runtimeDirectory in @(Get-ChildItem -LiteralPath $releaseRoot -Directory -Recurse | Where-Object {
    $_.Name -in @('win-arm64', 'win-x86', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')
})) {
    $runtimePath = [System.IO.Path]::GetFullPath($runtimeDirectory.FullName)
    if (-not $runtimePath.StartsWith($releaseRoot + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a runtime outside the release: $runtimePath"
    }
    Remove-Item -LiteralPath $runtimePath -Recurse -Force
}

foreach ($name in @('README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $releaseRoot
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs') -Destination $releaseRoot -Recurse
if (-not $CpuOnly) {
    # Keep the packaged notice in sync with the current dependency versions.
    Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') `
        -Destination (Join-Path $releaseRoot 'notices\accelerator\THIRD_PARTY_NOTICES.md') -Force
}

# Preserve licence/notice files supplied by restored packages, including the bundled .NET runtime.
$licenseRoot = Join-Path $releaseRoot 'notices\packages'
$assetPaths = @(
    'src\RSTT.App\obj\project.assets.json',
    'src\RSTT.Sherpa.Worker\obj\project.assets.json',
    'src\RSTT.Whisper.Worker\obj\project.assets.json'
)
if (-not $CpuOnly) {
    $assetPaths += 'src\RSTT.Sherpa.Cuda12.Worker\obj\project.assets.json'
    $assetPaths += 'src\RSTT.Whisper.Cuda12.Worker\obj\project.assets.json'
}
$seenPackages = [System.Collections.Generic.HashSet[string]]::new()
foreach ($assetPath in $assetPaths) {
    $assets = Get-Content -LiteralPath (Join-Path $repoRoot $assetPath) -Raw | ConvertFrom-Json -AsHashtable
    foreach ($library in $assets.libraries.GetEnumerator()) {
        if ($library.Value.type -ne 'package' -or -not $seenPackages.Add($library.Key)) { continue }
        foreach ($packageFolder in $assets.packageFolders.Keys) {
            $packagePath = Join-Path $packageFolder $library.Value.path
            if (-not (Test-Path -LiteralPath $packagePath -PathType Container)) { continue }
            foreach ($file in $library.Value.files) {
                if ([System.IO.Path]::GetFileName($file) -notmatch '^(licen[sc]e|copying|notice|third[-_. ]?party[-_. ]?notices?)(\.|$)') { continue }
                $destination = Join-Path (Join-Path $licenseRoot $library.Value.path) $file
                New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
                Copy-Item -LiteralPath (Join-Path $packagePath $file) -Destination $destination
            }
            break
        }
    }
    # Runtime packs are restore downloads rather than ordinary library entries.
    foreach ($framework in $assets.project.frameworks.Values) {
        foreach ($dependency in $framework.downloadDependencies) {
            if ($dependency.name -notmatch '^Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.win-x64$') { continue }
            $version = ($dependency.version.Trim('[', ']') -split ',')[0].Trim()
            $relativePath = "$($dependency.name.ToLowerInvariant())/$version"
            if (-not $seenPackages.Add($relativePath)) { continue }
            foreach ($packageFolder in $assets.packageFolders.Keys) {
                $packagePath = Join-Path $packageFolder $relativePath
                if (-not (Test-Path -LiteralPath $packagePath -PathType Container)) { continue }
                $destination = Join-Path $licenseRoot $relativePath
                New-Item -ItemType Directory -Path $destination -Force | Out-Null
                Get-ChildItem -LiteralPath $packagePath -File | Where-Object {
                    $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)(\.|$)'
                } | Copy-Item -Destination $destination
                break
            }
        }
    }
}

# A local HTML guide opens in a browser without a Markdown editor or internet connection.
$guideFiles = @(
    (Join-Path $releaseRoot 'README.md'),
    (Join-Path $releaseRoot 'THIRD_PARTY_NOTICES.md')
) + @(Get-ChildItem -LiteralPath (Join-Path $releaseRoot 'docs') -Filter '*.md' -File | ForEach-Object FullName)
foreach ($guideFile in $guideFiles) {
    $body = (ConvertFrom-Markdown -LiteralPath $guideFile).Html
    $body = $body -replace '(href="(?!https?://)[^"#?]+)\.md(?=[#?"])', '$1.html'
    $html = @"
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>RSTT - $(Split-Path -LeafBase $guideFile)</title>
<style>
body{font:17px/1.65 system-ui,sans-serif;color:#182b3c;background:#f5f8fa;margin:0}
main{max-width:960px;margin:auto;padding:36px;background:white}h1,h2,h3{line-height:1.25;scroll-margin-top:20px}
h2{margin-top:2.2em;border-bottom:1px solid #dce5eb;padding-bottom:.4em}a{color:#006e84}
pre{overflow:auto;background:#eef3f6;padding:16px;border-radius:8px}code{font-size:.88em;overflow-wrap:anywhere}
table{border-collapse:collapse;width:100%;display:block;overflow:auto}th,td{border:1px solid #dce5eb;padding:9px 12px;text-align:left}
th{background:#eef3f6}li{margin:.45em 0}blockquote{border-left:4px solid #008c87;padding:8px 20px;margin-left:0;background:#edf9f7}
@media(max-width:600px){main{padding:20px}}
</style></head><body><main>$body</main></body></html>
"@
    Set-Content -LiteralPath ([System.IO.Path]::ChangeExtension($guideFile, '.html')) -Value $html -Encoding utf8
}

$requiredFiles = @(
    'RSTT.App.exe', 'RSTT.App.dll', 'RSTT.App.runtimeconfig.json',
    'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'PresentationFramework.dll',
    'Assets\Audio\jfk.wav', 'README.html', 'LICENSE', 'THIRD_PARTY_NOTICES.md',
    'workers\sherpa-cpu\1.13.8\RSTT.Speech.Worker.exe',
    'workers\sherpa-cpu\1.13.8\coreclr.dll',
    'workers\sherpa-cpu\1.13.8\sherpa-onnx-c-api.dll',
    'workers\sherpa-cpu\1.13.8\onnxruntime.dll',
    'workers\whisper-cpu\1.9.1\RSTT.Whisper.Worker.exe',
    'workers\whisper-cpu\1.9.1\coreclr.dll',
    'workers\whisper-cpu\1.9.1\runtimes\win-x64\whisper.dll'
)
if (-not $CpuOnly) {
    $requiredFiles += @(
        'workers\sherpa-cuda12\1.13.8\RSTT.Speech.Worker.exe',
        'workers\sherpa-cuda12\1.13.8\coreclr.dll',
        'workers\sherpa-cuda12\1.13.8\onnxruntime_providers_cuda.dll',
        'workers\sherpa-cuda12\1.13.8\cudnn64_9.dll',
        'workers\sherpa-cuda12\1.13.8\cudart64_12.dll',
        'workers\sherpa-cuda12\1.13.8\cublas64_12.dll',
        'workers\sherpa-cuda12\1.13.8\cublasLt64_12.dll',
        'workers\whisper-cuda12\1.9.1\RSTT.Whisper.Cuda12.Worker.exe',
        'workers\whisper-cuda12\1.9.1\coreclr.dll',
        'workers\whisper-cuda12\1.9.1\runtimes\cuda12\win-x64\whisper.dll',
        'notices\accelerator\NVIDIA-CUDA-EULA.txt',
        'notices\accelerator\NVIDIA-cuDNN-LICENSE.txt'
    )
}
foreach ($relative in $requiredFiles) {
    $file = Get-Item -LiteralPath (Join-Path $releaseRoot $relative)
    if ($file.Length -eq 0) { throw "Required release file is empty: $relative" }
}

$files = @(Get-ChildItem -LiteralPath $releaseRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
    [pscustomobject][ordered]@{
        path = [System.IO.Path]::GetRelativePath($releaseRoot, $_.FullName).Replace('\', '/')
        size = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
[ordered]@{
    formatVersion = 1
    product = 'RSTT'
    platform = 'win-x64'
    configuration = 'Release'
    selfContained = $true
    includesCuda = -not [bool]$CpuOnly
    createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
    files = $files
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $releaseRoot 'release-manifest.json') -Encoding utf8

if (-not $NoArchive) {
    Write-Host 'Creating ZIP archive (large CUDA libraries may take several minutes)...'
    & tar -a -cf $zipPath -C $outputFullPath $packageName
    if ($LASTEXITCODE -ne 0) { throw "Archive creation failed (tar exit $LASTEXITCODE)." }
    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$zipPath.sha256" -Value "$zipHash  $(Split-Path -Leaf $zipPath)" -Encoding ascii
    Write-Host "Share: $zipPath"
}
Write-Host "Application: $(Join-Path $releaseRoot 'RSTT.App.exe')"
Write-Host "Setup guide: $(Join-Path $releaseRoot 'README.html')"
Write-Host "Packaged $($files.Count) files; $([math]::Round(($files | Measure-Object -Property size -Sum).Sum / 1GB, 2)) GiB before compression."
