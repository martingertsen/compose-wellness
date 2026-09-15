<#
.SYNOPSIS
    Builds a self-contained Linux release from a Windows machine and packs it with the install script.

.DESCRIPTION
    Requires the .NET SDK on this machine only; the target host needs no .NET installation.
    Output: dist\compose-wellness-<rid>.tar.gz

.EXAMPLE
    .\deploy\publish.ps1
    .\deploy\publish.ps1 -Rid linux-arm64
#>
param(
    [string]$Rid = "linux-x64"
)

$ErrorActionPreference = "Stop"

$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
$name = "compose-wellness-$Rid"
$out = Join-Path $repo "dist\$name"

if (Test-Path $out) {
    Remove-Item -Recurse -Force $out
}
New-Item -ItemType Directory -Force (Join-Path $out "app") | Out-Null

dotnet publish (Join-Path $repo "src\ComposeWellness\ComposeWellness.csproj") `
    --configuration Release `
    --runtime $Rid `
    --self-contained true `
    --output (Join-Path $out "app")
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed"
}

Copy-Item (Join-Path $repo "deploy\install.sh"), (Join-Path $repo "deploy\compose-wellness.service") $out

# Windows tar cannot store the executable bit, which is why the README runs the script as "bash ./install.sh".
$tarball = Join-Path $repo "dist\$name.tar.gz"
tar -czf $tarball -C (Join-Path $repo "dist") $name
if ($LASTEXITCODE -ne 0) {
    throw "tar failed"
}

Write-Host "Created $tarball"
