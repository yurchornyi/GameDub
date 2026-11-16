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

Write-Host 'opus-mt ru->uk - offline translation of Russian subtitles (Helsinki-NLP, CC-BY 4.0), ~110 MB'
Get-Files 'Xenova/opus-mt-ru-uk' "$root\opus-mt-ru-uk" @(
    'config.json', 'generation_config.json', 'tokenizer.json', 'tokenizer_config.json', 'vocab.json',
    'onnx/encoder_model_quantized.onnx', 'onnx/decoder_model_quantized.onnx') 'onnx/'

Write-Host 'Sound effects for the joystick lighting: EfficientAT (JKU Linz, MIT) and CLAP (LAION, Apache-2.0)'
# exported to ONNX by src\tools\sound-models\export.py (the sound-models workflow) and kept in a GameDub release
$rel = 'https://github.com/yurchornyi/GameDub/releases/download/sound-models-1'
New-Item -ItemType Directory -Force "$root\sound" | Out-Null
foreach ($f in 'efficientat.onnx', 'efficientat_class_map.csv', 'clap.onnx', 'clap_prompts.csv') {
    $out = "$root\sound\$f"
    if (Test-Path $out) { continue }
    Write-Host "  $f"
    Invoke-WebRequest "$rel/$f" -OutFile $out -UseBasicParsing
}
# YAMNet, the sound model before 16.11.2026, is no longer used
if (Test-Path "$root\yamnet") { Remove-Item -Recurse -Force "$root\yamnet" }

Write-Host 'Done.'
