# Downloads the offline models into C:\GameDub\models (~740 MB). Both are optional:
# without them GameDub uses online translation and the free Edge voices.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = 'C:\GameDub\models'

# $strip: prefix removed from the repo path when saving locally (the translation model lives under int8/).
function Get-Files($repo, $dir, $files, $strip = '') {
    foreach ($f in $files) {
        $local = if ($strip -and $f.StartsWith($strip)) { $f.Substring($strip.Length) } else { $f }
        $out = Join-Path $dir $local
        New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
        if (Test-Path $out) { continue }
        Write-Host "  $f"
        Invoke-WebRequest "https://huggingface.co/$repo/resolve/main/$f" -OutFile $out -UseBasicParsing
    }
}

Write-Host 'Supertonic 3 - offline voices (Supertone, OpenRAIL-M), ~380 MB'
Get-Files 'Supertone/supertonic-3' "$root\supertonic3" (@(
    'onnx/duration_predictor.onnx', 'onnx/text_encoder.onnx', 'onnx/tts.json', 'onnx/unicode_indexer.json',
    'onnx/vector_estimator.onnx', 'onnx/vocoder.onnx', 'config.json', 'LICENSE') +
    (1..5 | ForEach-Object { "voice_styles/M$_.json"; "voice_styles/F$_.json" }))

Write-Host 'opus-mt-tc-big en->uk - offline translation (Helsinki-NLP, CC-BY 4.0), ~350 MB'
Get-Files 'TigreGotico/opus-mt-tc-big-en-zle-onnx' "$root\opus-mt-tc-big-en-uk" @(
    'int8/config.json', 'int8/generation_config.json', 'int8/source.spm', 'int8/target.spm', 'int8/vocab.json',
    'int8/tokenizer_config.json', 'int8/special_tokens_map.json', 'int8/encoder_model.onnx', 'int8/decoder_model.onnx') 'int8/'

Write-Host 'Done.'
