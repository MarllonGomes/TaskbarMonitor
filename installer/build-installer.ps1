# Builds the Windows installer:
#   1. publishes the self-contained single-file build (no .NET required)
#   2. compiles installer\TaskbarMonitor.iss with Inno Setup 6 (ISCC.exe)
# Output: installer\output\TaskbarMonitor-Setup-<version>.exe
$ErrorActionPreference = "Stop"

# pick a dotnet that actually has an SDK (a bare runtime host can't publish)
$dotnet = $null
foreach ($candidate in "dotnet",
                       "$env:USERPROFILE\.dotnet\dotnet.exe",
                       "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe") {
    if (-not (Get-Command $candidate -ErrorAction SilentlyContinue)) { continue }
    $sdks = & $candidate --list-sdks 2>$null
    if ($sdks) { $dotnet = $candidate; break }
}
if (-not $dotnet) {
    Write-Error ".NET SDK 8 not found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0"
    exit 1
}

$root = Split-Path $PSScriptRoot -Parent
& $dotnet publish "$root\TaskbarMonitor.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o "$PSScriptRoot\publish"

$iscc = $null
foreach ($candidate in "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
                       "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
                       "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
                       "ISCC.exe") {
    if (Get-Command $candidate -ErrorAction SilentlyContinue) { $iscc = $candidate; break }
}
if (-not $iscc) {
    Write-Error "Inno Setup 6 (ISCC.exe) not found. Install it from https://jrsoftware.org/isinfo.php"
    exit 1
}

& $iscc "$PSScriptRoot\TaskbarMonitor.iss"
Write-Host ""
Get-ChildItem "$PSScriptRoot\output\*.exe" | ForEach-Object {
    Write-Host "Installer: $($_.FullName) ($([math]::Round($_.Length/1MB,1)) MB)"
}
