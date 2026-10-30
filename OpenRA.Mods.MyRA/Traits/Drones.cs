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

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// AI drone commander for one-way attack drones (Shaheds). Drones only do damage by crashing into things, so:
	//  - they are never escorts and never join a squad or boat pack: this module claims every drone the moment
	//    it exists, so the squad manager, strike groups, invasions and the AI Commander's army can't take them;
	//  - spotting: when one of our boat packs (SpotterTypes) is fighting, idle drones near it launch straight at
	//    what the boats are shooting - the boats spot, the drones strike;
	//  - otherwise they fly in their own groups of 5-10 against power, refineries and factories. A target nobody
	//    can currently see is struck by its position (a drone can't lock onto what it can't see, and would
	//    otherwise just circle);
	//  - a drone left idle away from home (target gone before it arrived) is called back, so none circle at sea.
	[TraitLocation(SystemActors.Player)]
	[Desc("AI: commands one-way attack drones - strike groups against key buildings, and quick strikes on whatever our boats are fighting.")]
	public class DroneStrikeBotModuleInfo : ConditionalTraitInfo
	{
		public readonly HashSet<string> DroneTypes = ["shahed"];

		[Desc("Our units that spot for the drones (boat packs).")]
		public readonly HashSet<string> SpotterTypes = ["fab"];

		[Desc("Buildings the strike groups go for, most wanted first.")]
		public readonly ImmutableArray<string> TargetTypes = ["powr", "apwr", "npp", "proc", "weap", "fact"];

		public readonly int MinGroupSize = 5;
		public readonly int MaxGroupSize = 10;

		[Desc("Ticks between strike-group launches.")]
		public readonly int StrikeInterval = 500;

		[Desc("Drones sent at each target a boat pack is fighting.")]
		public readonly int DronesPerSpottedTarget = 2;

		[Desc("Idle drones within this many cells of a fighting boat are launched at its target.")]
		public readonly int SupportRange = 30;

		[Desc("How close (cells) an enemy must be to a boat for the boat to count as fighting it.")]
		public readonly int SpotRange = 8;

		[Desc("Keep this many drones back for spotting support instead of sending them all on strike groups.")]
		public readonly int SupportReserve = 2;

		public readonly int Interval = 25;

		public override object Create(ActorInitializer init) { return new DroneStrikeBotModule(init.Self, this); }
	}

	public class DroneStrikeBotModule : ConditionalTrait<DroneStrikeBotModuleInfo>, IBotTick
	{
		readonly World world;
		readonly Player player;

		// Drone -> what it was sent at (an actor, or null for a position strike).
		readonly Dictionary<Actor, Actor> busy = [];
		readonly Dictionary<Actor, int> launched = [];
		int ticks, nextStrike;

		public DroneStrikeBotModule(Actor self, DroneStrikeBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		List<Actor> Drones() => world.Actors.Where(a => a.Owner == player && !a.IsDead && a.IsInWorld && Info.DroneTypes.Contains(a.Info.Name)).ToList();

		bool Hostile(Actor a) => BotHelpers.IsEnemy(player, a) && !a.Info.HasTraitInfo<HuskInfo>();

		void Launch(IBot bot, Actor drone, Actor target)
		{
			// A drone can only lock onto what we can see; otherwise it dives on the spot where the target is.
			var t = target.CanBeViewedByPlayer(player) ? Target.FromActor(target) : Target.FromCell(world, target.Location);
			bot.QueueOrder(new Order("Attack", drone, t, false));
			busy[drone] = target;
			launched[drone] = world.WorldTick;
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (IsTraitDisabled || ++ticks % Info.Interval != 0)
				return;

			var drones = Drones();
			if (drones.Count == 0)
				return;

			// Keep every drone out of squads, escorts and invasions.
			var squads = BotHelpers.Squads(player);
			squads?.ClaimUnits(drones.Where(d => !squads.IsClaimed(d)));

			foreach (var d in busy.Keys.ToList())
			{
				var t = busy[d];
				var stale = d.IsDead || !d.IsInWorld || t == null || t.IsDead || !t.IsInWorld || !Hostile(t)
					|| (d.IsIdle && world.WorldTick - launched.GetValueOrDefault(d) > 50);
				if (stale)
				{
					busy.Remove(d);
					launched.Remove(d);
				}
			}

			var home = BotHelpers.BaseCenter(player, drones[0].CenterPosition);
			var free = drones.Where(d => !busy.ContainsKey(d)).ToList();

			// 1. Boats spot, drones strike: whatever our boat packs are fighting right now.
			var spotters = world.Actors.Where(a => a.Owner == player && !a.IsDead && a.IsInWorld && Info.SpotterTypes.Contains(a.Info.Name)).ToList();
			foreach (var boat in spotters)
			{
				if (free.Count == 0)
					break;

				var foe = world.FindActorsInCircle(boat.CenterPosition, WDist.FromCells(Info.SpotRange))
					.Where(a => Hostile(a) && a.CanBeViewedByPlayer(player) && a.Info.HasTraitInfo<ITargetableInfo>()
						&& !a.Info.HasTraitInfo<AircraftInfo>() && !BotHelpers.IsInfantry(a))     // a drone is wasted on one soldier
					.MinByOrDefault(a => (a.CenterPosition - boat.CenterPosition).LengthSquared);
				if (foe == null)
					continue;

				var already = busy.Values.Count(t => t == foe);
				var want = Info.DronesPerSpottedTarget - already;
				if (want <= 0)
					continue;

				var near = free.Where(d => (d.CenterPosition - boat.CenterPosition).LengthSquared <= WDist.FromCells(Info.SupportRange).LengthSquared)
					.OrderBy(d => (d.CenterPosition - foe.CenterPosition).LengthSquared).Take(want).ToList();
				foreach (var d in near)
				{
					Launch(bot, d, foe);
					free.Remove(d);
				}

				if (near.Count > 0)
					GameRecorder.Note(player, $"drones: {near.Count} launched at {foe.Info.Name} that our {boat.Info.Name} is fighting");
			}

			// 2. Strike groups of 5-10 at power, refineries and factories (following the AI Commander).
			var commander = AICommanderBotModule.For(player);
			var hold = commander != null && commander.HoldOffensives;
			var reserve = spotters.Count > 0 ? Info.SupportReserve : 0;
			if (!hold && world.WorldTick >= nextStrike && free.Count - reserve >= Info.MinGroupSize)
			{
				var target = ChooseTarget(free[0].CenterPosition, commander?.ActiveTarget);
				if (target != null)
				{
					var size = world.LocalRandom.Next(Info.MinGroupSize, Info.MaxGroupSize + 1);
					var group = free.OrderBy(d => (d.CenterPosition - target.CenterPosition).LengthSquared).Take(System.Math.Min(size, free.Count - reserve)).ToList();
					foreach (var d in group)
					{
						Launch(bot, d, target);
						free.Remove(d);
					}

					nextStrike = world.WorldTick + Info.StrikeInterval;
					GameRecorder.Note(player, $"drones: strike group of {group.Count} at {target.Owner.ResolvedPlayerName.Trim()}'s {target.Info.Name}{(target.CanBeViewedByPlayer(player) ? "" : " (by position)")}");
				}
			}

			// 3. Strays: anything idle away from home comes back (drones can't land on water and would circle).
			foreach (var d in free.Where(d => d.IsIdle && (d.CenterPosition - home).LengthSquared > WDist.FromCells(12).LengthSquared))
				bot.QueueOrder(new Order("Move", d, Target.FromCell(world, world.Map.CellContaining(home)), false));
		}

		Actor ChooseTarget(WPos from, Player commandTarget)
		{
			Actor best = null;
			long bestScore = long.MaxValue;
			foreach (var a in world.Actors)
			{
				if (!Hostile(a) || !BotHelpers.Known(player, a) || (commandTarget != null && a.Owner != commandTarget))
					continue;

				// Already has drones on the way: pick something else.
				if (busy.ContainsValue(a))
					continue;

				var rank = Info.TargetTypes.IndexOf(a.Info.Name);
				if (rank < 0)
					continue;

				// Stay away from anti-air.
				var aa = world.FindActorsInCircle(a.CenterPosition, WDist.FromCells(8))
					.Count(d => d.Owner == a.Owner && !d.IsDead && d.Info.HasTraitInfo<AttackBaseInfo>() && BotHelpers.CanAttackAir(d));
				long score = rank * 20 + (a.CenterPosition - from).Length / 1024 + aa * 15;
				if (score < bestScore)
				{
					bestScore = score;
					best = a;
				}
			}

			return best;
		}
	}
}
