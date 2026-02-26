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

    $targetPath = Join-Path $installDir $binary
    $oldPath = Join-Path $installDir 'ccc.old.exe'

    # If the binary is currently running, we can't overwrite it directly.
    # Windows allows renaming a locked file, so move it out of the way first.
    if (Test-Path $targetPath) {
        # Clean up any previous .old file
        if (Test-Path $oldPath) {
            Remove-Item $oldPath -Force -ErrorAction SilentlyContinue
        }
        try {
            Rename-Item $targetPath $oldPath -Force -ErrorAction Stop
        } catch {
            # If rename also fails, the file might be truly locked (unlikely on Windows)
            Write-Error "Cannot replace $targetPath — is it in use? Try closing ccc first."
            exit 1
        }
    }

    Copy-Item (Join-Path $tmpDir $binary) $targetPath -Force

    # Add to PATH if not already there
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($userPath -notlike "*$installDir*") {
        [Environment]::SetEnvironmentVariable('Path', "$userPath;$installDir", 'User')
        $env:Path = "$env:Path;$installDir"
        Write-Host "Added $installDir to your PATH."
    }

    Write-Host "Installed ccc $version to $installDir\$binary"

    # Install notification hook script
    $hookDir = Join-Path $env:USERPROFILE '.ccc\hooks'
    if (-not (Test-Path $hookDir)) {
        New-Item -ItemType Directory -Path $hookDir -Force | Out-Null
    }
    $hookUrl = "https://raw.githubusercontent.com/$repo/main/hooks/ccc-state.sh"
    $hookPath = Join-Path $hookDir 'ccc-state.sh'
    Write-Host "Downloading notification hook..."
    try {
        Invoke-WebRequest -Uri $hookUrl -OutFile $hookPath
        Write-Host "Installed hook to $hookPath"
        Write-Host ""
        Write-Host "To enable notifications, add hooks to ~/.claude/settings.json:"
        Write-Host "  See https://github.com/$repo#notification-hooks"
    } catch {
        Write-Host "Note: Could not download hook script. See README for manual setup."
    }

    Write-Host ""
    Write-Host "Restart your terminal, then run 'ccc' to get started."
}
finally {
    Remove-Item -Path $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
}
