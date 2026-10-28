# Playing the modded game from Claude Code

When the `myra` mod is running a game, it serves the local player's view on
`http://localhost:7777` (code: `OpenRA.Mods.MyRA/Traits/ClaudeControl.cs`, port
set in `mods/myra/rules/claude-control.yaml`). Joe gives orders in plain words
("attack the enemy with infantry"); turn them into calls against this server.

Start a game: `.\launch-game.cmd Game.Mod=myra` and start a skirmish, or jump
straight into a map with `Launch.Map=<map folder>` (no AI opponents that way).
The in-game chat shows "Control server listening" once it is up. Requests time
out with 503 while the game is paused.

## Read the game

`curl -s http://localhost:7777/state` returns:

- `units`: your units and buildings: `id`, `type`, `name`, `category`
  (infantry, vehicle, ship, aircraft, building, other), `cell` [x, y], `hp`,
  `idle`, `can_attack`, `can_move`.
- `enemies`: enemy actors your player can see, plus buildings seen before and
  now under fog (`last_seen_under_fog: true`). Same fields plus `owner`.
- `production`: each queue, what it can build now (`buildable`, with `cost`)
  and what is in progress (`percent_done`, `done`).
- `cash`, `power`, `map` size, `tick`, `win_state`.

Only what the player can see is reported. If `enemies` is empty, scout first.

## Give orders

`curl -s -X POST http://localhost:7777/command -d '<json>'`. Every reply has
`ok`, and `error` when it fails.

Units are chosen with `"units": [ids]` or `"select"`: `all`, a category
(`infantry`, `vehicle`, `ship`, `aircraft`), an actor type (`e1`, `3tnk`), or a
comma list (`"infantry,vehicle"`). Add `"idle_only": true` to skip busy units
and `"queued": true` to add to their order list. Harvesters, buildings and
carrier planes are never selected.

| action | fields | does |
|---|---|---|
| `attack_move` | units, `cell` | move, fighting anything met on the way |
| `assault_move` | units, `cell` | attack-move that also hits buildings on the way |
| `move` | units, `cell` | move without stopping to fight |
| `attack` | units, `target` (enemy id) | attack one enemy |
| `attack_ground` | units, `cell` | force-fire at a spot; sends a Drone Swarm (`dswarm`) that way |
| `stop` | units | stop |
| `deploy` | units (MCV) | unpack an MCV into a construction yard |
| `build` | `type`, `count` | queue production (`type` from `production[].buildable`) |
| `cancel` | `type`, `count` | cancel queued production |
| `place` | `type`, optional `cell` | place a finished building; with no cell it picks a spot near the base |

Examples:

```
curl -s -X POST localhost:7777/command -d '{"action":"attack_move","select":"infantry","cell":[31,49]}'
curl -s -X POST localhost:7777/command -d '{"action":"build","type":"e1","count":5}'
curl -s -X POST localhost:7777/command -d '{"action":"place","type":"powr"}'
```

"Attack the enemy with infantry": read `/state`, take the nearest enemy
building or group in `enemies`, and `attack_move` the infantry to its `cell`.
Each call takes a second or more, so give squad-level orders and check back on
`/state`, rather than trying to micro a fight.

Opening build in RA: `deploy` the MCV, then `powr`, `proc` (refinery),
`barr` or `tent` (barracks, by side), `weap` (war factory). Buildings take
time: poll `/state` until the item shows `done: true`, then `place` it.
