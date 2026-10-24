# Portable build. Everything (SDK, NuGet cache, CLI home) lives in C:\GameDub\_build;
# the SDK is downloaded on first use (~270 MB). Delete _build afterwards to free ~800 MB.
$ErrorActionPreference = 'Stop'
$root = 'C:\GameDub'
$sdk = "$root\_build\sdk"
if (-not (Test-Path "$sdk\dotnet.exe")) {
    New-Item -ItemType Directory -Force $sdk | Out-Null
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest 'https://aka.ms/dotnet/8.0/dotnet-sdk-win-x64.zip' -OutFile "$root\_build\sdk.zip" -UseBasicParsing
    Expand-Archive "$root\_build\sdk.zip" $sdk -Force
    Remove-Item "$root\_build\sdk.zip"
}
$env:DOTNET_ROOT = $sdk
$env:DOTNET_CLI_HOME = "$root\_build\home"
$env:NUGET_PACKAGES = "$root\_build\nuget"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:APPDATA = "$root\_build\home"   # keeps NuGet.Config out of the real profile
Get-Process GameDub -ErrorAction SilentlyContinue | Stop-Process -Force   # running exe locks app\*.dll
Start-Sleep -Milliseconds 500
& "$sdk\dotnet.exe" publish "$root\src\GameDub\GameDub.csproj" -c Release -o "$root\app" --nologo -v q -p:UseAppHost=true
if ($LASTEXITCODE -ne 0) { throw "build failed" }
# Start the service on the system .NET runtime, not the build SDK (otherwise _build stays locked).
Remove-Item Env:DOTNET_ROOT, Env:DOTNET_MULTILEVEL_LOOKUP -ErrorAction SilentlyContinue
Start-Process "$root\app\GameDub.exe" -ArgumentList '--tray'
