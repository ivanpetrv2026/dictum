<#
.SYNOPSIS
    Downloads the CUDA 12 runtime libraries that Dictum's GPU backend needs.

.DESCRIPTION
    whisper.cpp's CUDA build links against cuBLAS and the CUDA runtime at load
    time. NVIDIA allows redistributing those DLLs, but they are far too large to
    ship inside the installer, so they are fetched on demand.

    The files land in %LOCALAPPDATA%\Dictum\cuda by default, which Dictum checks
    on startup. No administrator rights are required.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\Get-CudaRuntime.ps1
#>
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Dictum\cuda'),
    [string]$CudaVersion = '12.9.2'
)

$ErrorActionPreference = 'Stop'
$redist = 'https://developer.download.nvidia.com/compute/cuda/redist'

Write-Host "Reading CUDA $CudaVersion manifest..."
$manifest = Invoke-RestMethod -Uri "$redist/redistrib_$CudaVersion.json"

$wanted = @('cuda_cudart', 'libcublas')
$work = Join-Path $env:TEMP ("dictum-cuda-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work, $Destination | Out-Null

try {
    foreach ($name in $wanted) {
        $component = $manifest.$name
        if (-not $component) { throw "Component '$name' not found in the manifest." }

        $archive = $component.'windows-x86_64' | Where-Object { $_.relative_path -like '*.zip' } | Select-Object -First 1
        if (-not $archive) { throw "No Windows archive for '$name'." }

        $url = "$redist/$($archive.relative_path)"
        $zip = Join-Path $work (Split-Path $archive.relative_path -Leaf)

        Write-Host ("Downloading {0} ({1:N0} MB)..." -f $name, ($archive.size / 1MB))
        Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing

        $extract = Join-Path $work $name
        Expand-Archive -Path $zip -DestinationPath $extract -Force
    }

    $dlls = @('cudart64_12.dll', 'cublas64_12.dll', 'cublasLt64_12.dll')
    foreach ($dll in $dlls) {
        $source = Get-ChildItem $work -Recurse -Filter $dll | Select-Object -First 1
        if (-not $source) { throw "File '$dll' not found in the downloaded archives." }

        Copy-Item $source.FullName (Join-Path $Destination $dll) -Force
        Write-Host "  -> $dll"
    }

    Write-Host ""
    Write-Host "CUDA runtime installed to: $Destination"
    Write-Host "Restart Dictum — it will switch to the GPU automatically."
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
