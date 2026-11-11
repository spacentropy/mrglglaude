# mrglglaude

A World of Warcraft: Forever addon that shows what your local Claude Code sessions are doing: status, title, current action and progress.

## How it works

Addons can't read files at runtime, but a load-on-demand addon's files are read from disk when it's loaded. A small bridge (`ClaudeForeverBridge.cs`, .NET 11 NativeAOT) reads `~/.claude` and writes the status into 1,000 tiny slot addons. Every 10 seconds the panel loads the slot for the current time. Data only flows into the game; nothing reads the game or controls it.

## Setup (macOS)

```sh
./deploy.sh   # builds the bridge, copies the addon into WoW, (re)starts the bridge as a LaunchAgent
```

The bridge then starts at login, restarts if it crashes, and idles while WoW isn't running. Log: `bridge.log`. Stop it: `launchctl bootout gui/$(id -u)/com.spacentropy.mrglglaude`.

First run creates the slot folders: fully restart WoW once. After later deploys, `/reload` is enough. If deploy says it can't write to AddOns, allow `ClaudeForeverBridge` in System Settings → Privacy & Security → App Management.

Set `ADDONS=<path to Interface/AddOns>` if WoW isn't in `/Applications`.

## Use

- `/claude` toggles the panel. Drag to move; drag the corner grip to resize.
- About every 2¾ hours with the panel open it runs out of slots and asks for `/reload`.
- Bash commands, search patterns and queries are never shown; conversation titles and file names are, so be careful when streaming.
