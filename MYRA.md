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
- **Zelensky's Little Friend**, the drone operator (Soviet only: Soviet Barracks, needs a Radar Dome, $900): unarmed infantry that fly small one-way FPV drones up to 12 cells away. Each drone climbs off the operator, darts from side to side as it chases its target, then dives onto it. One drone kills an infantryman or a light vehicle and takes about a fifth off a medium tank. Operators are fragile and cannot launch at anything within 2.5 cells, so infantry that reach them win. A Mobile Radar Jammer can throw drones off course.
- **FPV Drone Test** map: a scripted demo of two operators against fleeing infantry, a jeep and a tank, then a rifleman ambushing the operators.

## Where things live

- `mods/myra/rules/carrier.yaml`: unit stats (cost, health, speed, number of planes)
- `mods/myra/weapons/carrier.yaml`: bomb damage and launch range
- `mods/myra/bits/carrier.png`: carrier sprite (32 facings)
- `mods/myra/rules/fpv.yaml`, `mods/myra/weapons/fpv.yaml`: Drone Operator stats and drone damage
- `OpenRA.Mods.MyRA/`: C# code for the carrier launch / return / rearm behaviour, the drone flight (`Projectiles/FPVDrone.cs`) and the Claude Code control server

Editing the `.yaml` files only needs a game restart. Changing the C# code needs `.\make.ps1 all` again.

To get updates: `git pull`, then `.\make.ps1 all`.

## Claude Code control

While a `myra` game is running, the local player can be driven over HTTP on `http://localhost:7777` (`GET /state`, `POST /command`). See `CLAUDE.md` for the commands. The port is set in `mods/myra/rules/claude-control.yaml`. It only listens on localhost and only controls your own side.
