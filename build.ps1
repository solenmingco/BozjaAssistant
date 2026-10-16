param(
    [string]$Project = "D:\work\DSH\BozjaAssistant\BozjaAssistant",
    [string]$Config = "Release"
)

$env:NUGET_PACKAGES        = "D:\work\DSH\_setup\nuget"
$env:NUGET_HTTP_CACHE_PATH = "D:\work\DSH\_setup\httpcache"
$env:DOTNET_CLI_HOME       = "D:\work\DSH\_setup\clihome"
$env:APPDATA               = "D:\work\DSH\_setup\appdata"
$env:LOCALAPPDATA          = "D:\work\DSH\_setup\localappdata"
$env:TEMP                  = "D:\work\DSH\_setup\temp"
$env:TMP                   = "D:\work\DSH\_setup\temp"
$env:DALAMUD_HOME          = "D:\work\DSH\_setup\dalamud-dev"

Push-Location $Project
try {
    & "D:\work\DSH\_setup\dotnet\dotnet.exe" build -c $Config --nologo 2>&1 | Out-String
} finally {
    Pop-Location
}
