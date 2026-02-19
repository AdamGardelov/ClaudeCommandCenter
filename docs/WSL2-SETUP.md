# Setting Up Claude Command Center on WSL2

A step-by-step guide for getting `ccc` running on Windows, starting from scratch.

## 1. Install WSL2

Open **PowerShell as Administrator** and run:

```powershell
wsl --install
```

This installs WSL2 with Ubuntu by default. Restart your computer when prompted.

After restarting, Ubuntu will launch automatically and ask you to create a username and password.

> If you already have WSL1, upgrade with `wsl --set-default-version 2`.

## 2. Update Ubuntu

```bash
sudo apt update && sudo apt upgrade -y
```

## 3. Install tmux

`ccc` uses tmux to manage Claude Code sessions.

```bash
sudo apt install -y tmux
```

## 4. Install Node.js and npm

Claude Code requires Node.js (v18+). The easiest way is via [nvm](https://github.com/nvm-sh/nvm):

```bash
curl -o- https://raw.githubusercontent.com/nvm-sh/nvm/v0.40.1/install.sh | bash
```

Close and reopen your terminal (or `source ~/.bashrc`), then:

```bash
nvm install --lts
```

Verify:

```bash
node --version   # v22.x or similar
npm --version
```

## 5. Install Claude Code

```bash
npm install -g @anthropic-ai/claude-code
```

Verify it's installed:

```bash
claude --version
```

On first run, `claude` will prompt you to authenticate with your Anthropic account.

## 6. Install ccc

### Option A: From Release (recommended)

```bash
curl -fsSL https://raw.githubusercontent.com/AdamGardelov/ClaudeCommandCenter/main/install.sh | bash
```

### Option B: From Source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download):

```bash
# Install .NET 10 SDK
# See https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu for latest instructions
sudo apt install -y dotnet-sdk-10.0
```

Then build and install:

```bash
git clone https://github.com/AdamGardelov/ClaudeCommandCenter.git
cd ClaudeCommandCenter
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o dist
sudo cp dist/ccc /usr/local/bin/ccc
```

## 7. Run

Make sure you're **not** inside a tmux session, then:

```bash
ccc
```

Press `n` to create your first Claude Code session.

## Troubleshooting

### `ccc` says "tmux not found"

Make sure tmux is installed: `sudo apt install tmux`

### `claude` command not found after installing

Restart your terminal or run `source ~/.bashrc`. If using nvm, make sure it's loaded.

### WSL2 has no internet

This is a known WSL2 issue. Try restarting WSL from PowerShell:

```powershell
wsl --shutdown
```

Then reopen Ubuntu.

### Permission denied on install

The install script uses `sudo` when needed. Make sure your user has sudo access (it does by default in WSL Ubuntu).
