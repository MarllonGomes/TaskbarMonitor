# Builds and publishes TaskbarMonitor to .\dist (framework-dependent, win-x64).
# Requires the .NET 8 SDK.
$ErrorActionPreference = "Stop"

$dotnet = "dotnet"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    foreach ($candidate in "$env:USERPROFILE\.dotnet\dotnet.exe",
                           "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe") {
        if (Test-Path $candidate) { $dotnet = $candidate; break }
    }
}

& $dotnet publish "$PSScriptRoot\TaskbarMonitor.csproj" `
    -c Release -r win-x64 --self-contained false `
    -o "$PSScriptRoot\dist"

Write-Host ""
Write-Host "Published to: $PSScriptRoot\dist\TaskbarMonitor.exe"
