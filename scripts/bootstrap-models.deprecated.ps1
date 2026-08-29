param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$modelRoot = Join-Path $root 'models\pp-ocr-v6-small'

$files = @(
    [pscustomobject]@{
        RelativePath = 'det\inference.onnx'
        Url = 'https://huggingface.co/PaddlePaddle/PP-OCRv6_small_det_onnx/resolve/main/inference.onnx?download=true'
        Sha256 = 'D73E0058B7A8086BBD57F3D10B8BCD4FF95363F67E06E2762B5E814FE9C9410E'
    },
    [pscustomobject]@{
        RelativePath = 'det\inference.yml'
        Url = 'https://huggingface.co/PaddlePaddle/PP-OCRv6_small_det_onnx/resolve/main/inference.yml?download=true'
        Sha256 = '193F435274BF9F0B5F71A929BBFBCF148282DF7E633B34E7C373E8F44741B516'
    },
    [pscustomobject]@{
        RelativePath = 'rec\inference.onnx'
        Url = 'https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec_onnx/resolve/main/inference.onnx?download=true'
        Sha256 = '5435FD747C9E0EFE15A96D0B378D5BD157E9492ED8FD80EDF08F30D02FA24634'
    },
    [pscustomobject]@{
        RelativePath = 'rec\inference.yml'
        Url = 'https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec_onnx/resolve/main/inference.yml?download=true'
        Sha256 = 'AB078671BB49F06228EADCCD34F1BB501E157F7A047095FFB943BA81512C77D1'
    }
)

foreach ($file in $files) {
    $destination = Join-Path $modelRoot $file.RelativePath
    if (Test-Path -LiteralPath $destination) {
        $actualHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        if ($actualHash -eq $file.Sha256) {
            Write-Host "Model file already verified: $($file.RelativePath)"
            continue
        }

        Write-Warning "Model checksum changed; downloading a verified replacement: $($file.RelativePath)"
    }

    $directory = Split-Path -Parent $destination
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $temporary = "$destination.download"
    try {
        Write-Host "Downloading $($file.RelativePath)..."
        Invoke-WebRequest -UseBasicParsing $file.Url -OutFile $temporary
        $downloadedHash = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash
        if ($downloadedHash -ne $file.Sha256) {
            throw "Checksum mismatch for $($file.RelativePath). Expected $($file.Sha256), got $downloadedHash."
        }

        Move-Item -LiteralPath $temporary -Destination $destination -Force
    }
    finally {
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "PP-OCRv6-small models are ready: $modelRoot"