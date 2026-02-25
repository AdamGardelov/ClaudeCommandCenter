#Requires -Version 5.1
<#
.SYNOPSIS
    Installs Claude Command Center (ccc) on Windows.
.DESCRIPTION
    Downloads the latest ccc release from GitHub and installs it to a directory in your PATH.
.EXAMPLE
    irm https://raw.githubusercontent.com/AdamGardelov/ClaudeCommandCenter/main/install.ps1 | iex
#>

$ErrorActionPreference = 'Stop'

$repo = 'AdamGardelov/ClaudeCommandCenter'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\ccc'
$binary = 'ccc.exe'

# Get latest version
Write-Host "Fetching latest release..."
$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest"
$version = $release.tag_name
Write-Host "Latest version: $version"

# Find the Windows asset
$asset = $release.assets | Where-Object { $_.name -eq 'ccc-win-x64.zip' }
if (-not $asset) {
    Write-Error "Could not find ccc-win-x64.zip in release $version"
    exit 1
}

# Download and extract
$tmpDir = Join-Path ([System.IO.Path]::GetTempPath()) "ccc-install-$([guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null

try {
    $zipPath = Join-Path $tmpDir 'ccc-win-x64.zip'
    Write-Host "Downloading $($asset.browser_download_url)..."
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath

    Expand-Archive -Path $zipPath -DestinationPath $tmpDir -Force

    # Install
    if (-not (Test-Path $installDir)) {
        New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    }
    Copy-Item (Join-Path $tmpDir $binary) (Join-Path $installDir $binary) -Force

    # Add to PATH if not already there
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($userPath -notlike "*$installDir*") {
        [Environment]::SetEnvironmentVariable('Path', "$userPath;$installDir", 'User')
        $env:Path = "$env:Path;$installDir"
        Write-Host "Added $installDir to your PATH."
    }

    Write-Host "Installed ccc $version to $installDir\$binary"
    Write-Host "Restart your terminal, then run 'ccc' to get started."
}
finally {
    Remove-Item -Path $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
}
