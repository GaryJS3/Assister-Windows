$ErrorActionPreference = 'Stop'
$modelName = 'sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01'
$destination = Join-Path $env:LOCALAPPDATA 'Assister\WakeWord'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$archive = Join-Path $destination "$modelName.tar.bz2"
Invoke-WebRequest "https://github.com/k2-fsa/sherpa-onnx/releases/download/kws-models/$modelName.tar.bz2" -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne 'f170013b4716e41b62b9bfd809687c207cef798ef9bc6534d524e17af9b6561a') {
    throw 'Official KWS model archive checksum mismatch.'
}
tar -xf $archive -C $destination
if ($LASTEXITCODE -ne 0) { throw 'Could not extract the KWS model archive.' }
$modelDirectory = Join-Path $destination $modelName
$keywords = Join-Path $destination 'keywords.txt'
if (!(Test-Path -LiteralPath $keywords)) {
    # Official English fixture phrase: "light up". Preserve existing custom keywords.
    $boundary = [char]0x2581
    [System.IO.File]::WriteAllText($keywords, "$boundary L IGHT ${boundary}UP @light_up`n", [System.Text.UTF8Encoding]::new($false))
}
Write-Host "In Assister Settings, enable local wake word listening and enter:"
Write-Host "Model folder: $modelDirectory"
Write-Host "Keyword file: $keywords"
Write-Host 'The initial phrase is "light up". Customize the tokenized keyword file using the sherpa-onnx KWS documentation.'
