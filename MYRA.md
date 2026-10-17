# Red Alert (Modded)

OpenRA with a modded copy of Red Alert (`mods/myra`) that adds an Aircraft Carrier.

## Setup (Windows)

1. Install the .NET 10 SDK: `winget install Microsoft.DotNet.SDK.10`
2. Clone this repo and build: `.\make.ps1 all` (run from the repo folder in PowerShell)
3. Run the modded game: `.\launch-game.cmd Game.Mod=myra`

The first launch asks to download the original Red Alert files. Pick the quick download.

## What's added

- **Aircraft Carrier** (Allied Naval Yard, $2000): carries 4 bombers that launch at whatever it attacks, bomb it, fly back, and rearm. Lost bombers are rebuilt.
- **Carrier Test** map: a scripted demo of the carrier attacking tanks on a shoreline.

## Where things live

- `mods/myra/rules/carrier.yaml`: unit stats (cost, health, speed, number of planes)
- `mods/myra/weapons/carrier.yaml`: bomb damage and launch range
- `mods/myra/bits/carrier.png`: carrier sprite (32 facings)
- `OpenRA.Mods.MyRA/`: C# code for the launch / return / rearm behaviour

Editing the `.yaml` files only needs a game restart. Changing the C# code needs `.\make.ps1 all` again.

To get updates: `git pull`, then `.\make.ps1 all`.
