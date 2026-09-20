# Copies the public ACME WorldBuilder tree into a clean folder for a new GitHub repo.
# Does not copy scratch, reconstruction, Ghidra, or private tooling.
#
# Example:
#   powershell -File scripts/export-public-repo.ps1 -Destination C:\src\ACME-AC-Map-Editor

param(
    [Parameter(Mandatory = $true)]
    [string] $Destination
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Destination = [System.IO.Path]::GetFullPath($Destination)

if (-not (Test-Path $Destination)) {
    New-Item -ItemType Directory -Path $Destination | Out-Null
}

$dirs = @(
    "Acme.Dat",
    "Acme.Render",
    "Acme.Render.GL",
    "WorldBuilder",
    "WorldBuilder.Shared",
    "WorldBuilder.Tests",
    "WorldBuilder.Windows",
    "WorldBuilder.Linux",
    "WorldBuilder.Mac",
    "WorldBuilder.Browser",
    ".github",
    "native",
    "scripts"
)

$excludeDir = @("bin", "obj", ".vs", ".git")
$excludeAcmeDat = @(
    "gen_records.py",
    "migrate_chorizite.py",
    "migrate_dats_api.py",
    "migrate_usings.py",
    "fix_enum_assigns.py",
    "fix_tree_flags.py"
)

function Copy-Tree([string] $Name) {
    $from = Join-Path $Root $Name
    $to = Join-Path $Destination $Name
    if (-not (Test-Path $from)) {
        Write-Warning "Skip missing $Name"
        return
    }
    robocopy $from $to /E /NFL /NDL /NJH /NJS /nc /ns /np `
        /XD $excludeDir | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed for $Name (exit $LASTEXITCODE)"
    }
}

foreach ($dir in $dirs) {
    Copy-Tree $dir
}

$docsDest = Join-Path $Destination "docs"
New-Item -ItemType Directory -Path $docsDest -Force | Out-Null
Remove-Item (Join-Path $docsDest "data") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $docsDest "specs") -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $Root "docs\USER_GUIDE.md") (Join-Path $docsDest "USER_GUIDE.md") -Force

foreach ($file in $excludeAcmeDat) {
    $path = Join-Path $Destination "Acme.Dat\$file"
    if (Test-Path $path) { Remove-Item $path -Force }
}

$rootFiles = @(
    "LICENSE",
    "README.md",
    "GitVersion.yml",
    "Installer.nsi",
    "DotNetCore.nsh",
    "Directory.Build.props",
    "WorldBuilder.sln",
    "WorldBuilder.slnx",
    "acme-wblogo.ico",
    ".gitignore"
)

foreach ($file in $rootFiles) {
    $from = Join-Path $Root $file
    if (Test-Path $from) {
        Copy-Item $from (Join-Path $Destination $file) -Force
    }
}

$nativeDll = Join-Path $Root "native\acme_dat.dll"
if (Test-Path $nativeDll) {
    Copy-Item $nativeDll (Join-Path $Destination "native\acme_dat.dll") -Force
    $gi = Join-Path $Destination ".gitignore"
    if (Test-Path $gi) {
        Add-Content -Path $gi -Value "`n# Track the compiled DAT library in the public repository`n!native/acme_dat.dll`n"
    }
    Write-Host "Included native/acme_dat.dll"
}
else {
    Write-Warning "native/acme_dat.dll was not found. The public repo will not build until you copy it in."
}

Write-Host "Public tree written to $Destination"
Write-Host "Next:"
Write-Host "  cd $Destination"
Write-Host "  git init -b main"
Write-Host "  git add ."
Write-Host "  git commit -m `"Initial public ACME WorldBuilder.`""
Write-Host "  git remote add origin https://github.com/Vanquish-6/ACME-AC-Map-Editor-WorldBuilder.git"
Write-Host "  git push -u origin main"
Write-Host "Then enable GitHub Pages (deploy from gh-pages) and add secret SPARKLE_PRIVATE_KEY."
