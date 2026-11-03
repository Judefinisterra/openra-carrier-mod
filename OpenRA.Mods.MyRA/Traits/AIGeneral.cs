#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.BotModules.Squads;
using OpenRA.Traits;

// The AI's chain of command. Every computer player gets one General:
//  - a ROSTER: which units are reserved (El Presidente and his people, the home guard) - no other AI module or
//    squad may take them;
//  - INTEL: what this player has actually seen (fog of war applies - no peeking): enemies in view, where enemy
//    groups were last spotted, enemy buildings it has explored, enemy presidents it has had eyes on;
//  - a POSTURE (build up / attack / defend) and budgets, chosen from the intel and the faction's personality,
//    that the other modules obey: no new attacks while home is threatened or the home guard is too thin.
// SkyNet (a language model) can take over the posture and home-guard decisions for one player.
namespace OpenRA.Mods.MyRA.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("AI chain of command: unit roster, fog-limited intel, posture and home guard. Personalities are per bot type.")]
	public class AIGeneralInfo : TraitInfo
	{
		public readonly int Interval = 50;

		[Desc("How aggressive each AI type is (0-100), by bot type. 'default' for the rest.")]
		public readonly Dictionary<string, int> Aggression = new() { ["default"] = 60 };

		[Desc("Share of the army (by cost) each AI type keeps home in normal games.")]
		public readonly Dictionary<string, int> HomeGuard = new() { ["default"] = 15 };

		[Desc("Home guard share in El Presidente games (normally / just after a threat).")]
		public readonly int PresidentGuard = 30;
		public readonly int AlarmGuard = 50;

		[Desc("Never keep less than this much (by cost) at home, if we have it, in El Presidente games.")]
		public readonly int MinPresidentGuard = 2500;

		public readonly int AlarmMemory = 3000;

		[Desc("Enemy fighters this close to the base count as an attack on it.")]
		public readonly WDist BaseThreatRadius = WDist.FromCells(18);

		[Desc("How long (ticks) a sighting of enemy units is remembered.")]
		public readonly int SightingMemory = 3000;

		[Desc("Units that are never handed to squads or other modules.")]
		public readonly HashSet<string> VipTypes = ["presidente", "bodyguard", "limo"];

		public override object Create(ActorInitializer init) { return new AIGeneral(init.Self, this); }
	}

	public enum AIPosture { Build, Attack, Defend }

	public class AIGeneral : IBotTick, IBotUnitRoster, INotifyCreated
	{
		public readonly AIGeneralInfo Info;
		readonly Player player;
		readonly World world;
		readonly HashSet<Actor> homeGuard = [];
		readonly Dictionary<Actor, (WPos Pos, int Tick, string Type, Player Owner)> sightings = [];
		readonly Dictionary<Player, (WPos Pos, int Tick, string Where)> presidentsSeen = [];
		int ticks, lastAlarm = -100000;
		bool guardOk = true;

		// SkyNet's word, while it stands.
		AIPosture? overridePosture;
		int overrideGuard = -1, overrideUntil;

		public AIPosture Posture { get; private set; } = AIPosture.Build;
		public int Aggression { get; private set; }
		public int GuardPercent { get; private set; }
		public bool HomeThreatened { get; private set; }
		public bool PresidentMode => world.WorldActor.TraitOrDefault<PresidenteManager>()?.Enabled == true;

		public AIGeneral(Actor self, AIGeneralInfo info)
		{
			Info = info;
			player = self.Owner;
			world = self.World;
		}

		void INotifyCreated.Created(Actor self)
		{
			var type = player.BotType ?? "default";
			Aggression = Info.Aggression.TryGetValue(type, out var a) ? a : Info.Aggression.GetValueOrDefault("default", 60);
			GuardPercent = Info.HomeGuard.TryGetValue(type, out var g) ? g : Info.HomeGuard.GetValueOrDefault("default", 15);
		}

		public static AIGeneral For(Player p) => p?.PlayerActor.TraitOrDefault<AIGeneral>();

		/// <summary>Can an AI module (or squad) use this unit? False for VIPs and the home guard.</summary>
		public static bool Free(Actor a) => a != null && (For(a.Owner)?.IsReserved(a) != true);

		public bool IsReserved(Actor a) => Info.VipTypes.Contains(a.Info.Name) || homeGuard.Contains(a);

		/// <summary>Squads, strike groups and invasions may only start a new attack when this is true.</summary>
		public bool AllowNewAttacks => Posture != AIPosture.Defend && (guardOk || !PresidentMode);

		public bool IsHomeGuard(Actor a) => homeGuard.Contains(a);
		public int HomeGuardCount => homeGuard.Count;

		/// <summary>SkyNet takes the posture/home-guard decisions for a while (ticks).</summary>
		public void Override(AIPosture? posture, int guardPercent, int forTicks)
		{
			overridePosture = posture;
			overrideGuard = guardPercent;
			overrideUntil = world.WorldTick + forTicks;
		}

		static bool Alive(Actor a) => a != null && !a.IsDead && a.IsInWorld;
		static int Cost(Actor a) => a.Info.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 100;

		static int Value(Actor a)
		{
			var h = a.TraitOrDefault<IHealth>();
			return h == null || h.MaxHP == 0 ? Cost(a) : (int)((long)Cost(a) * h.HP / h.MaxHP);
		}

		public static bool Fighter(Actor a) => a.Info.HasTraitInfo<AttackBaseInfo>() && a.Info.HasTraitInfo<IMoveInfo>()
			&& !a.Info.HasTraitInfo<HarvesterInfo>() && !a.Info.HasTraitInfo<BuildingInfo>();

		bool Enemy(Player p) => p != null && p.Playable && !p.NonCombatant && p.RelationshipWith(player) == PlayerRelationship.Enemy;

		public WPos BaseCenter => BotHelpers.BaseCenter(player, world.Map.CenterOfCell(player.HomeLocation));

		/// <summary>What the home guard protects: El Presidente (wherever he is) or the base.</summary>
		public WPos Anchor
		{
			get
			{
				var mgr = world.WorldActor.TraitOrDefault<PresidenteManager>();
				if (mgr != null && mgr.Enabled && mgr.Presidents.TryGetValue(player, out var pres) && !pres.IsDead)
				{
					var vip = PresidenteManager.CarrierOf(pres) ?? (pres.IsInWorld ? pres : null);
					if (vip != null)
						return vip.CenterPosition;
				}

				return BaseCenter;
			}
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (++ticks % Info.Interval != 0)
				return;

			UpdateIntel();
			Decide();
			KeepHomeGuard(bot);
		}

		// ------------------------------------------------------------------------------------------------
		// Intel: only what this player can see.
		void UpdateIntel()
		{
			var now = world.WorldTick;
			foreach (var a in world.Actors)
			{
				if (!Alive(a) || !Enemy(a.Owner) || !a.Info.HasTraitInfo<IMoveInfo>() || !a.CanBeViewedByPlayer(player))
					continue;
				sightings[a] = (a.CenterPosition, now, a.Info.Name, a.Owner);
			}

			foreach (var gone in sightings.Where(kv => now - kv.Value.Tick > Info.SightingMemory || kv.Key.IsDead).Select(kv => kv.Key).ToList())
				sightings.Remove(gone);

			var mgr = world.WorldActor.TraitOrDefault<PresidenteManager>();
			if (mgr != null && mgr.Enabled)
				foreach (var kv in mgr.Presidents)
				{
					if (!Enemy(kv.Key) || kv.Value.IsDead)
						continue;
					var carrier = PresidenteManager.CarrierOf(kv.Value);
					var seen = carrier ?? (kv.Value.IsInWorld ? kv.Value : null);
					if (seen != null && seen.CanBeViewedByPlayer(player))
						presidentsSeen[kv.Key] = (seen.CenterPosition, now, carrier != null ? "inside a " + Name(carrier) : "on foot");
				}

			var home = BaseCenter;
			var r2 = (long)Info.BaseThreatRadius.Length * Info.BaseThreatRadius.Length;
			HomeThreatened = world.Actors.Any(a => Alive(a) && Enemy(a.Owner) && Fighter(a) && a.CanBeViewedByPlayer(player)
				&& ((a.CenterPosition - home).HorizontalLengthSquared <= r2 || (a.CenterPosition - Anchor).HorizontalLengthSquared <= r2 / 2));
			if (HomeThreatened)
				lastAlarm = now;
		}

		/// <summary>Where we last saw an enemy's president (fog applies).</summary>
		public bool LastSawPresident(Player p, out WPos pos, out int tick)
		{
			if (presidentsSeen.TryGetValue(p, out var s))
			{
				pos = s.Pos;
				tick = s.Tick;
				return true;
			}

			pos = WPos.Zero;
			tick = 0;
			return false;
		}

		static string Name(Actor a) => a.Info.TraitInfoOrDefault<TooltipInfo>() is TooltipInfo t ? FluentProvider.GetMessage(t.Name) : a.Info.Name;

		/// <summary>Plain-text intel report (for SkyNet), all from this player's own eyes.</summary>
		public string IntelReport(Func<Player, string> label)
		{
			var sb = new StringBuilder();
			var now = world.WorldTick;
			var home = BaseCenter;
			var map = world.Map;
			string Where(WPos p)
			{
				var c = map.CellContaining(p);
				var d = (p - home).HorizontalLength / 1024;
				var dx = p.X - home.X;
				var dy = p.Y - home.Y;
				var dir = Math.Abs(dx) > 2 * Math.Abs(dy) ? (dx > 0 ? "east" : "west") : Math.Abs(dy) > 2 * Math.Abs(dx) ? (dy > 0 ? "south" : "north")
					: (dy > 0 ? "south" : "north") + (dx > 0 ? "east" : "west");
				return $"cell {c.X},{c.Y} ({d} cells {dir} of your base)";
			}

			var explored = map.AllCells.Count(c => player.Shroud.IsExplored(c)) * 100 / Math.Max(1, map.AllCells.Count());
			sb.AppendLine($"You have explored {explored}% of the map. Everything below is what your own units and buildings have seen.");

			// Enemy units in view now, and groups seen recently (grouped by owner and rough area).
			var groups = sightings.GroupBy(kv => (kv.Value.Owner, Bx: map.CellContaining(kv.Value.Pos).X / 8, By: map.CellContaining(kv.Value.Pos).Y / 8))
				.Select(g => (g.Key.Owner, Pos: g.First().Value.Pos, Ago: (now - g.Max(x => x.Value.Tick)) / 25,
					What: string.Join(", ", g.GroupBy(x => x.Value.Type).Select(t => $"{t.Count()} {t.Key}"))))
				.OrderBy(g => g.Ago).Take(12).ToList();
			if (groups.Count == 0)
				sb.AppendLine("Enemy units: none seen recently.");
			foreach (var g in groups)
				sb.AppendLine($"Enemy units of {label(g.Owner)}: {g.What} at {Where(g.Pos)}, " + (g.Ago <= 2 ? "IN VIEW NOW" : $"last seen {g.Ago}s ago"));

			foreach (var p in world.Players.Where(Enemy))
			{
				var known = world.Actors.Where(a => Alive(a) && a.Owner == p && a.Info.HasTraitInfo<BuildingInfo>() && player.Shroud.IsExplored(a.Location)).ToList();
				if (known.Count == 0)
					sb.AppendLine($"{label(p)}'s base: not found yet (scout to find it).");
				else
					sb.AppendLine($"{label(p)}'s buildings you have found ({known.Count}, as last seen): "
						+ string.Join(", ", known.GroupBy(a => a.Info.Name).Select(g => $"{g.Count()} {g.Key}")) + $"; centre near {Where(known[0].CenterPosition)}");
			}

			if (PresidentMode)
				foreach (var p in world.Players.Where(Enemy))
					sb.AppendLine(presidentsSeen.TryGetValue(p, out var s)
						? $"{label(p)}'s El Presidente: last seen {s.Where} at {Where(s.Pos)}, {(now - s.Tick) / 25}s ago"
						: $"{label(p)}'s El Presidente: never seen - find him (he started in a limousine next to their MCV).");

			sb.AppendLine(HomeThreatened ? "YOUR BASE OR PRESIDENT IS UNDER THREAT right now." : "No enemies near your base or president.");
			return sb.ToString();
		}

		// ------------------------------------------------------------------------------------------------
		// Posture and budgets.
		void Decide()
		{
			var now = world.WorldTick;
			if (overridePosture.HasValue && now < overrideUntil)
			{
				Posture = HomeThreatened ? AIPosture.Defend : overridePosture.Value;
				return;
			}

			if (HomeThreatened)
			{
				Posture = AIPosture.Defend;
				return;
			}

			// Attack when our army is a match for what we've seen of the enemy; how much of a match depends on
			// how aggressive this AI is (a horde attacks outnumbered, a turtle waits for a clear edge).
			var ours = world.Actors.Where(a => Alive(a) && a.Owner == player && Fighter(a) && !IsReserved(a)).Sum(Value);
			var theirs = sightings.Where(kv => !kv.Key.IsDead && Fighter(kv.Key) && now - kv.Value.Tick < 1500).Sum(kv => Value(kv.Key));
			var needed = theirs * (160 - Aggression) / 100;
			Posture = ours > 0 && ours >= needed ? AIPosture.Attack : AIPosture.Build;
		}

		// ------------------------------------------------------------------------------------------------
		// The home guard: enough of the army stays near the president (or the base) before anything goes out.
		void KeepHomeGuard(IBot bot)
		{
			homeGuard.RemoveWhere(a => !Alive(a) || a.Owner != player);
			var now = world.WorldTick;
			var army = world.Actors.Where(a => Alive(a) && a.Owner == player && Fighter(a) && !Info.VipTypes.Contains(a.Info.Name)
				&& !a.Info.HasTraitInfo<AircraftInfo>()).ToList();
			var total = army.Sum(Value);
			var anchor = Anchor;
			var radius2 = 25L * 25 * 1024 * 1024;
			var towers = world.Actors.Where(a => Alive(a) && a.Owner == player && a.Info.HasTraitInfo<BuildingInfo>() && a.Info.HasTraitInfo<AttackBaseInfo>()
				&& (a.CenterPosition - anchor).HorizontalLengthSquared <= radius2).Sum(Value);

			int pct;
			var min = 0;
			if (overrideGuard >= 0 && now < overrideUntil)
				pct = overrideGuard;
			else if (PresidentMode)
			{
				pct = now - lastAlarm < Info.AlarmMemory ? Info.AlarmGuard : Info.PresidentGuard;
				min = Info.MinPresidentGuard;
			}
			else
				pct = HomeThreatened ? Math.Max(GuardPercent, 40) : GuardPercent;

			var need = Math.Min(Math.Max(min, (int)((long)(total + towers) * pct / 100)), total + towers);
			var guard = towers + homeGuard.Sum(Value);
			var squads = player.PlayerActor.TraitsImplementing<SquadManagerBotModule>().SelectMany(sm => sm.Squads).ToList();

			if (guard < need)
			{
				// Units still near home first (idle ones before squad members), nearest first.
				// Idle units first, then squad members, nearest first (far-away idle units walk home).
				var near = 22L * 22 * 1024 * 1024;
				var inSquads = squads.SelectMany(s => s.Units).ToHashSet();
				foreach (var a in army.Where(a => !homeGuard.Contains(a) && (a.IsIdle || (a.CenterPosition - anchor).HorizontalLengthSquared <= near))
					.OrderBy(a => inSquads.Contains(a) ? 1 : 0).ThenBy(a => (a.CenterPosition - anchor).HorizontalLengthSquared).ToList())
				{
					if (guard >= need)
						break;
					foreach (var s in squads)
						s.Units.Remove(a);
					homeGuard.Add(a);
					guard += Value(a);
				}
			}
			else if (guard > need * 3 / 2 && homeGuard.Count > 0)
			{
				// Plenty at home: let the extras go back to the attack groups.
				var assault = squads.FirstOrDefault(s => s.Type is SquadType.Assault or SquadType.Rush && s.Units.Count > 0);
				foreach (var a in homeGuard.OrderBy(Value).ToList())
				{
					if (guard - Value(a) < need * 5 / 4)
						break;
					homeGuard.Remove(a);
					assault?.Units.Add(a);
					guard -= Value(a);
				}
			}

			guardOk = guard >= need;

			// The guard waits near what it protects; when there's a threat it goes for the nearest enemy.
			var anchorCell = world.Map.CellContaining(anchor);
			var threat = HomeThreatened ? world.Actors.Where(a => Alive(a) && Enemy(a.Owner) && Fighter(a) && a.CanBeViewedByPlayer(player))
				.MinByOrDefault(a => (a.CenterPosition - anchor).HorizontalLengthSquared) : null;
			foreach (var a in homeGuard)
			{
				if (threat != null && (threat.CenterPosition - anchor).HorizontalLengthSquared <= radius2)
				{
					if (a.IsIdle)
						bot.QueueOrder(new Order("AttackMove", a, Target.FromCell(world, threat.Location), false));
				}
				else if (a.IsIdle && (a.CenterPosition - anchor).HorizontalLengthSquared > 7L * 7 * 1024 * 1024)
					bot.QueueOrder(new Order("AttackMove", a, Target.FromCell(world, anchorCell), false));
			}
		}
	}
}
