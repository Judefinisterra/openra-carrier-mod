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
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

// Extra AI brains for MyRA: counter-building, economy guard, strike groups, scouting and oil outposts.
// All of this runs only on the host (bot modules), and only issues normal orders, so online games stay in sync.
namespace OpenRA.Mods.MyRA.Traits
{
	static class BotHelpers
	{
		public static List<ProductionQueue> Queues(Player p)
		{
			return p.PlayerActor.TraitsImplementing<ProductionQueue>()
				.Concat(p.World.ActorsWithTrait<ProductionQueue>().Where(x => x.Actor.Owner == p && !x.Actor.IsDead).Select(x => x.Trait))
				.Where(q => q.Enabled)
				.Distinct()
				.ToList();
		}

		public static int Queued(Player p, string type)
		{
			return Queues(p).Sum(q => q.AllQueued().Count(i => i.Item == type));
		}

		public static bool CanBuild(Player p, string type)
		{
			return Queues(p).Any(q => q.BuildableItems().Any(b => b.Name == type));
		}

		/// <summary>Add one item to whichever queue can build it. Returns false if nobody can build it right now.</summary>
		public static bool Queue(IBot bot, Player p, string type)
		{
			var q = Queues(p).FirstOrDefault(x => x.BuildableItems().Any(b => b.Name == type));
			if (q == null)
				return false;

			bot.QueueOrder(Order.StartProduction(q.Actor, type, 1));
			return true;
		}

		public static SquadManagerBotModule Squads(Player p)
		{
			return p.PlayerActor.TraitsImplementing<SquadManagerBotModule>().FirstOrDefault(m => m.IsTraitEnabled());
		}

		public static bool IsEnemy(Player me, Actor a)
		{
			return a != null && !a.IsDead && a.IsInWorld && a.Owner != null && a.Owner.Playable && !a.Owner.NonCombatant
				&& me.RelationshipWith(a.Owner) == PlayerRelationship.Enemy;
		}

		/// <summary>What the AI is allowed to know: buildings it has explored, units it can see.</summary>
		public static bool Known(Player me, Actor a)
		{
			// Everyone knows where each side started, so buildings around an enemy's start position count as known
			// even before they've been scouted.
			if (a.Info.HasTraitInfo<BuildingInfo>())
				return me.Shroud.IsExplored(a.Location) || (a.Location - a.Owner.HomeLocation).LengthSquared <= 16 * 16;
			return a.CanBeViewedByPlayer(me);
		}

		public static int Cost(ActorInfo ai) => ai.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0;

		public static bool IsInfantry(Actor a) => a.GetEnabledTargetTypes().Contains("Infantry");

		public static bool IsAircraft(ActorInfo ai) => ai.HasTraitInfo<AircraftInfo>();

		public static bool IsNaval(Actor a) => a.GetEnabledTargetTypes().Contains("Ship") || a.GetEnabledTargetTypes().Contains("Submarine");

		public static WPos BaseCenter(Player p, WPos fallback)
		{
			var yard = p.World.ActorsHavingTrait<BaseBuilding>().FirstOrDefault(a => a.Owner == p && !a.IsDead && a.IsInWorld);
			if (yard != null)
				return yard.CenterPosition;

			var b = p.World.ActorsHavingTrait<Building>().FirstOrDefault(a => a.Owner == p && !a.IsDead && a.IsInWorld);
			return b?.CenterPosition ?? fallback;
		}

		public static bool CanAttackAir(Actor a)
		{
			foreach (var ar in a.TraitsImplementing<Armament>())
				if (ar.Weapon.ValidTargets.Contains("AirborneActor") || ar.Weapon.ValidTargets.Contains("Air"))
					return true;
			return false;
		}
	}

	// ------------------------------------------------------------------------------------------------
	[TraitLocation(SystemActors.Player)]
	[Desc("AI: watches what the enemy fields and builds counters (anti-air, anti-tank, anti-infantry);",
		"keeps the economy healthy early on; builds anti-air defences only once enemy aircraft show up;",
		"and can scale one building type with the size of the army (e.g. Tribal Hideouts).")]
	public class AdaptiveProductionBotModuleInfo : ConditionalTraitInfo
	{
		[Desc("How often to re-evaluate, in ticks.")]
		public readonly int ScanInterval = 375;

		[Desc("Forget enemy units not seen for this long (ticks).")]
		public readonly int MemoryTicks = 4500;

		[Desc("Units that shoot down aircraft.")]
		public readonly ImmutableArray<string> AntiAirUnits = [];

		[Desc("Units that kill tanks and vehicles.")]
		public readonly ImmutableArray<string> AntiArmorUnits = [];

		[Desc("Units that kill infantry.")]
		public readonly ImmutableArray<string> AntiInfantryUnits = [];

		[Desc("Units that fight ships.")]
		public readonly ImmutableArray<string> AntiNavalUnits = [];

		[Desc("Anti-aircraft defence buildings, built only once enemy aircraft have been seen.")]
		public readonly ImmutableArray<string> AntiAirDefenses = [];

		[Desc("Never build more than this many anti-air defences.")]
		public readonly int MaxAntiAirDefenses = 8;

		[Desc("Aim for our counter units to be worth this percentage of the enemy's matching forces.")]
		public readonly int CounterPercent = 70;

		[Desc("Most counter units queued per evaluation.")]
		public readonly int MaxCounterOrders = 2;

		[Desc("Economy: until this tick, hold back army production until we have MinimumRefineries.")]
		public readonly int EarlyGameTicks = 7500;

		public readonly int MinimumRefineries = 2;

		[Desc("Refinery types.")]
		public readonly ImmutableArray<string> RefineryTypes = ["proc"];

		[Desc("Harvester type to keep topped up.")]
		public readonly string HarvesterType = "harv";

		public readonly int HarvestersPerRefinery = 2;

		public readonly int MaxHarvesters = 10;

		[Desc("Keep this much cash in reserve once the early game is over (holds back army production below it).")]
		public readonly int CashReserve = 0;

		[Desc("Optional: a building to scale with army size (e.g. tribalhouse).")]
		public readonly string ScaledBuilding = null;

		[Desc("Units counted for ScaledBuilding.")]
		public readonly ImmutableArray<string> ScaledBuildingUnits = [];

		[Desc("One ScaledBuilding for every this many ScaledBuildingUnits (plus one).")]
		public readonly int UnitsPerScaledBuilding = 10;

		public readonly int MaxScaledBuildings = 100;

		[Desc("Buildings to keep at least this many of whenever they can be built (e.g. fix: 1 so an MCV can always be replaced).")]
		public readonly Dictionary<string, int> KeepBuildings = [];

		[Desc("Don't start KeepBuildings before this tick.")]
		public readonly int KeepBuildingsDelay = 3000;

		[Desc("When cash piles up above this, spend it on SurplusBuildings (extra factories, defences, economy).")]
		public readonly int SurplusCash = 5000;

		[Desc("Buildings to add while rich, with the most of each we want in total.")]
		public readonly Dictionary<string, int> SurplusBuildings = [];

		[Desc("While power is negative, pause unit training so the money goes to power plants.")]
		public readonly bool FixPowerFirst = true;

		[Desc("Unit production queues to pause during a power shortage.")]
		public readonly ImmutableArray<string> UnitQueueTypes = ["Infantry", "Vehicle", "Ship", "Aircraft", "Plane"];

		public override object Create(ActorInitializer init) { return new AdaptiveProductionBotModule(init.Self, this); }
	}

	public class AdaptiveProductionBotModule : ConditionalTrait<AdaptiveProductionBotModuleInfo>, IBotTick, IBotRequestPauseUnitProduction
	{
		enum Kind { Air, Armor, Infantry, Naval }

		readonly World world;
		readonly Player player;
		readonly Dictionary<Actor, (Kind Kind, int Cost, int Seen)> seen = [];
		PlayerResources resources;
		PowerManager power;
		int cooldown;
		bool holdArmy;
		bool pausedForPower;

		public AdaptiveProductionBotModule(Actor self, AdaptiveProductionBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		protected override void Created(Actor self)
		{
			resources = self.TraitOrDefault<PlayerResources>();
			power = self.TraitOrDefault<PowerManager>();
			cooldown = world.LocalRandom.Next(Info.ScanInterval);
			base.Created(self);
		}

		public bool PauseUnitProduction => !IsTraitDisabled && holdArmy;

		static Kind? Classify(Actor a)
		{
			if (BotHelpers.IsAircraft(a.Info))
				return Kind.Air;
			if (!a.Info.HasTraitInfo<MobileInfo>() || !a.Info.HasTraitInfo<AttackBaseInfo>())
				return null;
			if (BotHelpers.IsInfantry(a))
				return Kind.Infantry;
			if (BotHelpers.IsNaval(a))
				return Kind.Naval;
			return Kind.Armor;
		}

		int Count(IEnumerable<string> types) => world.Actors.Count(a => a.Owner == player && !a.IsDead && a.IsInWorld && types.Contains(a.Info.Name));

		int Value(IEnumerable<string> types)
		{
			var set = types.ToHashSet();
			var total = 0;
			foreach (var a in world.Actors)
				if (a.Owner == player && !a.IsDead && a.IsInWorld && set.Contains(a.Info.Name))
					total += BotHelpers.Cost(a.Info);
			return total;
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (--cooldown > 0)
				return;

			cooldown = Info.ScanInterval;
			var now = world.WorldTick;

			// 1. Remember what we have seen of the enemy.
			foreach (var a in world.Actors)
			{
				if (!BotHelpers.IsEnemy(player, a) || !a.CanBeViewedByPlayer(player))
					continue;
				var k = Classify(a);
				if (k != null)
					seen[a] = (k.Value, BotHelpers.Cost(a.Info), now);
			}

			foreach (var a in seen.Keys.ToList())
				if (a.IsDead || !a.IsInWorld || now - seen[a].Seen > Info.MemoryTicks)
					seen.Remove(a);

			var enemyValue = new Dictionary<Kind, int>();
			var enemyAirCount = 0;
			foreach (var v in seen.Values)
			{
				enemyValue[v.Kind] = enemyValue.GetValueOrDefault(v.Kind) + v.Cost;
				if (v.Kind == Kind.Air)
					enemyAirCount++;
			}

			// 2. Economy guard.
			var refineries = Count(Info.RefineryTypes);
			var cash = resources?.GetCashAndResources() ?? 0;
			holdArmy = (now < Info.EarlyGameTicks && refineries < Info.MinimumRefineries && refineries > 0 && cash < 2500)
				|| (now >= Info.EarlyGameTicks && Info.CashReserve > 0 && cash < Info.CashReserve && cash < 400);

			// Power shortage: everything builds slowly, so stop paying for units until power plants are up.
			var lowPower = Info.FixPowerFirst && power != null && power.ExcessPower < 0 && cash < 1500;
			if (lowPower != pausedForPower)
			{
				pausedForPower = lowPower;
				foreach (var q in BotHelpers.Queues(player).Where(q => Info.UnitQueueTypes.Contains(q.Info.Type)))
					foreach (var item in q.AllQueued().Select(i => i.Item).Distinct().ToList())
						bot.QueueOrder(Order.PauseProduction(q.Actor, item, lowPower));
			}

			holdArmy |= lowPower;

			// Spend a cash pile on more factories, defences and economy.
			if (cash > Info.SurplusCash && Info.SurplusBuildings.Count > 0)
			{
				foreach (var kv in Info.SurplusBuildings.Shuffle(world.LocalRandom))
				{
					if (Count([kv.Key]) + BotHelpers.Queued(player, kv.Key) >= kv.Value || !BotHelpers.CanBuild(player, kv.Key))
						continue;
					if (BotHelpers.Queue(bot, player, kv.Key))
						break;
				}
			}

			if (Info.HarvesterType != null && refineries > 0)
			{
				var harv = Count([Info.HarvesterType]) + BotHelpers.Queued(player, Info.HarvesterType);
				if (harv < Math.Min(Info.MaxHarvesters, refineries * Info.HarvestersPerRefinery))
					BotHelpers.Queue(bot, player, Info.HarvesterType);
			}

			// 3. Buildings we always want one of (e.g. a Service Depot so a lost MCV can be replaced).
			if (now >= Info.KeepBuildingsDelay)
			{
				foreach (var kv in Info.KeepBuildings)
					if (Count([kv.Key]) + BotHelpers.Queued(player, kv.Key) < kv.Value && cash > BotHelpers.Cost(world.Map.Rules.Actors[kv.Key]))
						BotHelpers.Queue(bot, player, kv.Key);
			}

			// 4. Scaled building (Hideouts grow with the horde).
			if (Info.ScaledBuilding != null)
			{
				var want = Math.Min(Info.MaxScaledBuildings, 1 + Count(Info.ScaledBuildingUnits) / Math.Max(1, Info.UnitsPerScaledBuilding));
				var have = Count([Info.ScaledBuilding]) + BotHelpers.Queued(player, Info.ScaledBuilding);
				if (have < want)
					BotHelpers.Queue(bot, player, Info.ScaledBuilding);
			}

			// 5. Anti-air defences, only once enemy aircraft have been seen.
			if (enemyAirCount > 0 && Info.AntiAirDefenses.Length > 0)
			{
				var aa = Count(Info.AntiAirDefenses) + Info.AntiAirDefenses.Sum(t => BotHelpers.Queued(player, t));
				var want = Math.Min(Info.MaxAntiAirDefenses, 1 + enemyAirCount / 3);
				if (aa < want)
				{
					var type = Info.AntiAirDefenses.FirstOrDefault(t => BotHelpers.CanBuild(player, t));
					if (type != null)
						BotHelpers.Queue(bot, player, type);
				}
			}

			// 6. Counter units.
			if (holdArmy)
				return;

			var orders = 0;
			void Counter(Kind kind, ImmutableArray<string> units)
			{
				if (orders >= Info.MaxCounterOrders || units.Length == 0)
					return;
				var enemy = enemyValue.GetValueOrDefault(kind);
				if (enemy <= 0)
					return;
				var mine = Value(units) + units.Sum(u => BotHelpers.Queued(player, u) * BotHelpers.Cost(world.Map.Rules.Actors[u]));
				if (mine * 100 >= enemy * Info.CounterPercent)
					return;
				var buildable = units.Where(u => BotHelpers.CanBuild(player, u)).ToList();
				if (buildable.Count == 0)
					return;
				if (BotHelpers.Queue(bot, player, buildable.Random(world.LocalRandom)))
					orders++;
			}

			// Biggest threat first.
			foreach (var k in enemyValue.OrderByDescending(kv => kv.Value).Select(kv => kv.Key))
			{
				switch (k)
				{
					case Kind.Air: Counter(k, Info.AntiAirUnits); break;
					case Kind.Armor: Counter(k, Info.AntiArmorUnits); break;
					case Kind.Infantry: Counter(k, Info.AntiInfantryUnits); break;
					case Kind.Naval: Counter(k, Info.AntiNavalUnits); break;
				}
			}
		}
	}

	// ------------------------------------------------------------------------------------------------
	[TraitLocation(SystemActors.Player)]
	[Desc("AI: forms small strike groups out of the listed unit types and sends them at chosen targets",
		"(drone swarms, boat raids, rocket raids, lion hunts). Put the unit types in the SquadManager's",
		"ExcludeFromSquadsTypes so the normal squads leave them alone.")]
	public class StrikeGroupBotModuleInfo : ConditionalTraitInfo
	{
		[FieldLoader.Require]
		public readonly ImmutableArray<string> UnitTypes = [];

		public readonly int MinGroupSize = 3;
		public readonly int MaxGroupSize = 8;

		[Desc("Ticks between launching strikes.")]
		public readonly int Interval = 750;

		[Desc("Enemy actor types to hit, most wanted first. Special values: ~infantry, ~vehicle, ~ship, ~building, ~any.")]
		public readonly ImmutableArray<string> TargetTypes = [];

		[Desc("Only consider targets within this many cells of our base (0 = anywhere).")]
		public readonly int HomeRadius = 0;

		[Desc("Avoid targets protected by anti-air (for aircraft).")]
		public readonly bool AvoidAntiAir = false;

		[Desc("Avoid targets protected by many defences (for ground raids).")]
		public readonly bool AvoidDefenses = true;

		[Desc("Use an Attack order on the target actor (drones, hunters) instead of attack-moving to it (raids).")]
		public readonly bool DirectAttack = false;

		[Desc("Fall back home once the group has lost this percentage of its units.")]
		public readonly int RetreatLossPercent = 50;

		[Desc("Give up on a strike after this many ticks.")]
		public readonly int MaxStrikeTicks = 3000;

		[Desc("Only pick targets the units can actually get to (ground and naval units).")]
		public readonly bool CheckReachable = true;

		[Desc("If we know of no target, send one unit to look at an enemy start position.")]
		public readonly bool ScoutWhenBlind = false;

		public override object Create(ActorInitializer init) { return new StrikeGroupBotModule(init.Self, this); }
	}

	public class StrikeGroupBotModule : ConditionalTrait<StrikeGroupBotModuleInfo>, IBotTick
	{
		readonly World world;
		readonly Player player;
		readonly List<Actor> group = [];
		Actor target;
		int startSize, started, cooldown;
		IPathFinder pathFinder;

		public StrikeGroupBotModule(Actor self, StrikeGroupBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		protected override void Created(Actor self)
		{
			cooldown = world.LocalRandom.Next(Info.Interval);
			pathFinder = world.WorldActor.TraitOrDefault<IPathFinder>();
			base.Created(self);
		}

		bool CanReach(Actor unit, Actor t)
		{
			if (!Info.CheckReachable || pathFinder == null)
				return true;
			var mi = unit.Info.TraitInfoOrDefault<MobileInfo>();
			if (mi == null)
				return true;
			var loc = world.WorldActor.TraitsImplementing<Locomotor>().FirstOrDefault(l => l.Info.Name == mi.Locomotor);
			if (loc == null)
				return true;
			foreach (var c in world.Map.FindTilesInCircle(t.Location, 4))
				if (world.Map.Contains(c) && loc.MovementCostForCell(c) != short.MaxValue && pathFinder.PathExistsForLocomotor(loc, unit.Location, c))
					return true;
			return false;
		}

		void Disband(IBot bot, bool goHome)
		{
			if (goHome && group.Count > 0)
			{
				var home = BotHelpers.BaseCenter(player, group[0].CenterPosition);
				foreach (var u in group)
					bot.QueueOrder(new Order("Move", u, Target.FromCell(world, world.Map.CellContaining(home)), false));
			}

			BotHelpers.Squads(player)?.ReleaseUnits(group);
			group.Clear();
			target = null;
		}

		bool Matches(Actor a, string t)
		{
			switch (t)
			{
				case "~infantry": return BotHelpers.IsInfantry(a);
				case "~ship": return BotHelpers.IsNaval(a);
				case "~building": return a.Info.HasTraitInfo<BuildingInfo>();
				case "~vehicle": return a.Info.HasTraitInfo<MobileInfo>() && !BotHelpers.IsInfantry(a) && !BotHelpers.IsNaval(a);
				case "~any": return true;
				default: return a.Info.Name == t;
			}
		}

		int Threat(Actor t)
		{
			var count = 0;
			foreach (var d in world.FindActorsInCircle(t.CenterPosition, WDist.FromCells(8)))
			{
				if (d.Owner != t.Owner || d.IsDead || !d.Info.HasTraitInfo<AttackBaseInfo>())
					continue;
				if (Info.AvoidAntiAir ? BotHelpers.CanAttackAir(d) : d.Info.HasTraitInfo<BuildingInfo>())
					count++;
			}

			return count;
		}

		Player commandTarget;

		Actor ChooseTarget(WPos from, Actor sample = null)
		{
			var home = BotHelpers.BaseCenter(player, from);
			Actor best = null;
			long bestScore = long.MaxValue;
			foreach (var a in world.Actors)
			{
				if (!BotHelpers.IsEnemy(player, a) || !BotHelpers.Known(player, a) || a.Info.HasTraitInfo<HuskInfo>())
					continue;
				if (!a.Info.HasTraitInfo<ITargetableInfo>())
					continue;

				// The AI Commander's named target comes first.
				if (commandTarget != null && a.Owner != commandTarget)
					continue;

				var rank = -1;
				for (var i = 0; i < Info.TargetTypes.Length; i++)
					if (Matches(a, Info.TargetTypes[i])) { rank = i; break; }
				if (rank < 0)
					continue;

				if (Info.HomeRadius > 0 && (a.CenterPosition - home).LengthSquared > WDist.FromCells(Info.HomeRadius).LengthSquared)
					continue;

				var dist = (a.CenterPosition - from).Length / 1024;
				var threat = (Info.AvoidAntiAir || Info.AvoidDefenses) ? Threat(a) : 0;
				long score = rank * 25 + dist + threat * 12;
				if (score < bestScore && (sample == null || CanReach(sample, a)))
				{
					bestScore = score;
					best = a;
				}
			}

			return best;
		}

		void SendAt(IBot bot, Actor t)
		{
			target = t;
			foreach (var u in group)
			{
				if (Info.DirectAttack)
					bot.QueueOrder(new Order("Attack", u, Target.FromActor(t), false));
				else
					bot.QueueOrder(new Order("AttackMove", u, Target.FromCell(world, t.Location), false));
			}
		}

		void IBotTick.BotTick(IBot bot)
		{
			// Follow the AI Commander: strike only its target, and launch nothing while it says to stay home.
			var commander = AICommanderBotModule.For(player);
			commandTarget = commander?.ActiveTarget;
			if (commander != null && commander.HoldOffensives && Info.HomeRadius == 0)
			{
				if (group.Count > 0)
					Disband(bot, true);
				return;
			}

			if (commandTarget != null && target != null && target.Owner != commandTarget)
				target = null;

			group.RemoveAll(a => a.IsDead || !a.IsInWorld || a.Owner != player);

			if (group.Count > 0)
			{
				var tooLong = world.WorldTick - started > Info.MaxStrikeTicks;
				var mauled = startSize > 0 && group.Count * 100 < startSize * (100 - Info.RetreatLossPercent);
				if (tooLong || mauled)
				{
					Disband(bot, true);
					return;
				}

				if (target == null || target.IsDead || !target.IsInWorld || target.Owner == player || !BotHelpers.IsEnemy(player, target))
				{
					var next = ChooseTarget(group[0].CenterPosition, group[0]);
					if (next == null)
						Disband(bot, true);
					else
						SendAt(bot, next);
				}

				return;
			}

			if (--cooldown > 0)
				return;

			cooldown = Info.Interval;
			var squads = BotHelpers.Squads(player);
			var idle = world.Actors.Where(a => a.Owner == player && !a.IsDead && a.IsInWorld && Info.UnitTypes.Contains(a.Info.Name)
				&& (a.IsIdle || (squads != null && squads.IdleBaseUnits.Contains(a))) && (squads == null || !squads.IsClaimed(a))).ToList();
			var size = world.LocalRandom.Next(Info.MinGroupSize, Info.MaxGroupSize + 1);
			if (idle.Count < Math.Min(size, Info.MinGroupSize) || idle.Count < Info.MinGroupSize)
				return;

			var t = ChooseTarget(idle[0].CenterPosition, idle[0]);
			if (t == null)
			{
				if (Info.ScoutWhenBlind)
				{
					var spots = world.Players.Where(p => p.Playable && p.WinState == WinState.Undefined && player.RelationshipWith(p) == PlayerRelationship.Enemy)
						.Select(p => p.HomeLocation).Where(world.Map.Contains).ToList();
					if (spots.Count > 0)
						bot.QueueOrder(new Order("Move", idle[0], Target.FromCell(world, spots.Random(world.LocalRandom)), false));
				}

				return;
			}

			group.AddRange(idle.OrderBy(a => (a.CenterPosition - t.CenterPosition).LengthSquared).Take(Math.Max(Info.MinGroupSize, Math.Min(size, idle.Count))));
			squads?.ClaimUnits(group);
			startSize = group.Count;
			started = world.WorldTick;
			SendAt(bot, t);
		}
	}

	// ------------------------------------------------------------------------------------------------
	[TraitLocation(SystemActors.Player)]
	[Desc("AI: keeps a scout or two driving around enemy start positions and unexplored ground so the AI",
		"actually knows where the enemy is. Put the scout types in ExcludeFromSquadsTypes.")]
	public class ScoutBotModuleInfo : ConditionalTraitInfo
	{
		[FieldLoader.Require]
		public readonly ImmutableArray<string> UnitTypes = [];

		[Desc("Build a scout if we have none (first buildable type in UnitTypes).")]
		public readonly bool BuildScouts = true;

		public readonly int MaxScouts = 1;

		public readonly int Interval = 300;

		[Desc("Don't start scouting before this tick.")]
		public readonly int StartDelay = 1500;

		public override object Create(ActorInitializer init) { return new ScoutBotModule(init.Self, this); }
	}

	public class ScoutBotModule : ConditionalTrait<ScoutBotModuleInfo>, IBotTick
	{
		readonly World world;
		readonly Player player;
		int cooldown;
		int buildCooldown;
		readonly List<Actor> scouts = [];

		public ScoutBotModule(Actor self, ScoutBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		CPos PickDestination()
		{
			// Enemy start positions we haven't seen yet first, then random unexplored ground.
			var spawns = world.Players.Where(p => p.Playable && player.RelationshipWith(p) == PlayerRelationship.Enemy)
				.Select(p => p.HomeLocation).Where(c => world.Map.Contains(c) && !player.Shroud.IsVisible(c)).ToList();
			if (spawns.Count > 0)
				return spawns.Random(world.LocalRandom);

			for (var i = 0; i < 30; i++)
			{
				var c = world.Map.ChooseRandomCell(world.LocalRandom);
				if (!player.Shroud.IsExplored(c))
					return c;
			}

			return world.Map.ChooseRandomCell(world.LocalRandom);
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (world.WorldTick < Info.StartDelay || --cooldown > 0)
				return;

			cooldown = Info.Interval;
			var squads = BotHelpers.Squads(player);
			scouts.RemoveAll(a => a.IsDead || !a.IsInWorld || a.Owner != player);
			if (scouts.Count < Info.MaxScouts)
			{
				var pick = world.Actors.FirstOrDefault(a => a.Owner == player && !a.IsDead && a.IsInWorld && Info.UnitTypes.Contains(a.Info.Name)
					&& !scouts.Contains(a) && (squads == null || !squads.IsClaimed(a)) && (a.IsIdle || (squads != null && squads.IdleBaseUnits.Contains(a))));
				if (pick != null)
				{
					scouts.Add(pick);
					squads?.ClaimUnits([pick]);
				}
			}

			if (scouts.Count < Info.MaxScouts && Info.BuildScouts && --buildCooldown <= 0)
			{
				buildCooldown = 6;
				var type = Info.UnitTypes.FirstOrDefault(t => BotHelpers.CanBuild(player, t));
				if (type != null && Info.UnitTypes.Sum(t => BotHelpers.Queued(player, t)) == 0)
					BotHelpers.Queue(bot, player, type);
			}

			foreach (var s in scouts.Where(s => s.IsIdle))
			{
				var dest = PickDestination();
				bot.QueueOrder(new Order("Move", s, Target.FromCell(world, dest), false));
			}
		}
	}

	// ------------------------------------------------------------------------------------------------
	[TraitLocation(SystemActors.Player)]
	[Desc("AI: puts defences next to its outlying oil derricks. Needs the derrick to give buildable area.")]
	public class OutpostDefenseBotModuleInfo : ConditionalTraitInfo
	{
		[FieldLoader.Require]
		[Desc("Buildings to protect (e.g. oilb.player).")]
		public readonly ImmutableArray<string> OutpostTypes = [];

		[FieldLoader.Require]
		[Desc("Defences to build next to them, first buildable one is used.")]
		public readonly ImmutableArray<string> DefenseTypes = [];

		[Desc("How many defences each outpost should have.")]
		public readonly int DefensesPerOutpost = 2;

		public readonly int Interval = 500;

		[Desc("Only protect outposts at least this far from the base (cells); the base has its own defences.")]
		public readonly int MinDistanceFromBase = 10;

		public override object Create(ActorInitializer init) { return new OutpostDefenseBotModule(init.Self, this); }
	}

	public class OutpostDefenseBotModule : ConditionalTrait<OutpostDefenseBotModuleInfo>, IBotTick, IBotSuggestBuildingLocation
	{
		readonly World world;
		readonly Player player;
		int cooldown;
		Actor pending;

		public OutpostDefenseBotModule(Actor self, OutpostDefenseBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		int DefensesNear(Actor outpost)
		{
			return world.FindActorsInCircle(outpost.CenterPosition, WDist.FromCells(5))
				.Count(a => a.Owner == player && !a.IsDead && Info.DefenseTypes.Contains(a.Info.Name));
		}

		Actor NeediestOutpost()
		{
			var home = BotHelpers.BaseCenter(player, WPos.Zero);
			return world.Actors
				.Where(a => a.Owner == player && !a.IsDead && a.IsInWorld && Info.OutpostTypes.Contains(a.Info.Name)
					&& (a.CenterPosition - home).Length > WDist.FromCells(Info.MinDistanceFromBase).Length
					&& DefensesNear(a) < Info.DefensesPerOutpost)
				.OrderBy(DefensesNear)
				.FirstOrDefault();
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (--cooldown > 0)
				return;

			cooldown = Info.Interval;
			pending = NeediestOutpost();
			if (pending == null)
				return;

			if (Info.DefenseTypes.Sum(t => BotHelpers.Queued(player, t)) > 0)
				return;

			var type = Info.DefenseTypes.FirstOrDefault(t => BotHelpers.CanBuild(player, t));
			if (type != null)
				BotHelpers.Queue(bot, player, type);
		}

		CPos? IBotSuggestBuildingLocation.SuggestLocation(IBot bot, string actorType, ActorInfo actorInfo, BuildingInfo bi)
		{
			if (IsTraitDisabled || !Info.DefenseTypes.Contains(actorType))
				return null;

			var outpost = pending != null && !pending.IsDead && pending.IsInWorld ? pending : NeediestOutpost();
			if (outpost == null)
				return null;

			foreach (var cell in world.Map.FindTilesInAnnulus(outpost.Location, 2, 5).Shuffle(world.LocalRandom))
				if (world.CanPlaceBuilding(cell, actorInfo, bi, null) && bi.IsCloseEnoughToBase(world, player, actorInfo, cell))
					return cell;

			return null;
		}
	}

	// ------------------------------------------------------------------------------------------------
	[TraitLocation(SystemActors.Player)]
	[Desc("AI: sea invasions. When the enemy can't be reached over land, builds transports, loads a group of",
		"troops, sails to an enemy shore, lands them and sends them at the enemy base. Put the transport types",
		"in the SquadManager's ExcludeFromSquadsTypes.")]
	public class AmphibiousAssaultBotModuleInfo : ConditionalTraitInfo
	{
		[FieldLoader.Require]
		[Desc("Transport types, in order of preference to build.")]
		public readonly ImmutableArray<string> TransportTypes = [];

		[Desc("Only carry these unit types (empty = anything that fits).")]
		public readonly ImmutableArray<string> PassengerTypes = [];

		public readonly int MaxTransports = 2;

		[Desc("Shipyards that build the transports; one is built if we have none and need to cross water.")]
		public readonly ImmutableArray<string> ShipyardTypes = ["spen", "syrd", "tribaldock"];

		[Desc("Don't sail with fewer passengers than this.")]
		public readonly int MinPassengers = 3;

		[Desc("How many transports sail together in one wave.")]
		public readonly int BoatsPerWave = 1;

		public readonly int Interval = 100;

		[Desc("Give up loading after this many ticks and go with what we have.")]
		public readonly int LoadTimeout = 900;

		[Desc("Give up the voyage after this many ticks.")]
		public readonly int VoyageTimeout = 4500;

		[Desc("Land between these distances (cells) from the target.")]
		public readonly int MinLandingDistance = 6;
		public readonly int MaxLandingDistance = 26;

		[Desc("Keep landed troops under our command for this long before handing them back to the squads.")]
		public readonly int AssaultTicks = 1000;

		[Desc("Warships built (in this order, whichever we can) when the enemy is across the water.")]
		public readonly ImmutableArray<string> WarshipTypes = ["dd", "ca", "pt", "ss", "msub"];

		[Desc("How many warships to keep when the enemy is across the water.")]
		public readonly int MaxWarships = 6;

		[Desc("Send the fleet on its own raid once it has this many ships and no landing is under way.")]
		public readonly int FleetRaidSize = 3;

		public override object Create(ActorInitializer init) { return new AmphibiousAssaultBotModule(init.Self, this); }
	}

	public class AmphibiousAssaultBotModule : ConditionalTrait<AmphibiousAssaultBotModuleInfo>, IBotTick, IBotSuggestBuildingLocation
	{
		enum Stage { Idle, Loading, Sailing, Landing, Assault }

		readonly World world;
		readonly Player player;
		IPathFinder pathFinder;
		Stage stage;
		readonly List<Actor> fleetBoats = [];
		readonly Dictionary<Actor, Actor> seat = [];
		readonly List<Actor> landed = [];
		int landedUntil;
		CPos landing, home, targetCell;
		readonly List<Actor> passengers = [];

		/// <summary>True while this AI needs to cross the sea to reach its enemies.</summary>
		public bool NeedSea => needSea || world.WorldTick < ForceSeaUntil;
		int stageStarted, cooldown, needCheck, shipTurn, fleetOrdered, lastWhy = -10000, pickupChecked = -10000;
		CPos pickup;
		CPos? pickupShore;
		readonly HashSet<CPos> badPickups = [];

		// Busy fighting something close by: leave it be.
		bool Fighting(Actor a) => world.FindActorsInCircle(a.CenterPosition, WDist.FromCells(7))
			.Any(e => BotHelpers.IsEnemy(player, e) && e.Info.HasTraitInfo<AttackBaseInfo>() && e.CanBeViewedByPlayer(player));
		bool needSea;
		TruceManager truces;

		public AmphibiousAssaultBotModule(Actor self, AmphibiousAssaultBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		protected override void Created(Actor self)
		{
			pathFinder = world.WorldActor.TraitOrDefault<IPathFinder>();
			truces = world.WorldActor.TraitOrDefault<TruceManager>();
			base.Created(self);
		}

		// Enemies we may attack: at war, and not under a truce.
		bool Hostile(Player p) => p != null && p.Playable && !p.NonCombatant && player.RelationshipWith(p) == PlayerRelationship.Enemy
			&& p.WinState == WinState.Undefined && (truces == null || !truces.InTruce(player, p));

		Locomotor LocomotorOf(ActorInfo ai)
		{
			var mi = ai.TraitInfoOrDefault<MobileInfo>();
			return mi == null ? null : world.WorldActor.TraitsImplementing<Locomotor>().FirstOrDefault(l => l.Info.Name == mi.Locomotor);
		}

		bool Reachable(Locomotor loc, CPos from, CPos to) => loc != null && pathFinder != null && pathFinder.PathExistsForLocomotor(loc, from, to);

		Actor ClosestEnemyBuilding(WPos from)
		{
			return world.Actors
				.Where(a => BotHelpers.IsEnemy(player, a) && a.Info.HasTraitInfo<BuildingInfo>() && BotHelpers.Known(player, a) && !a.Info.HasTraitInfo<HuskInfo>())
				.MinByOrDefault(a => (a.CenterPosition - from).LengthSquared);
		}

		// Where to aim: a known enemy building, or else an enemy's start position (everyone knows those).
		/// <summary>Set by the AI Commander's "invade" orders: land on this player first.</summary>
		public Player PreferredTarget;

		/// <summary>Set by the AI Commander's "invade" orders: start sea invasions even if a land route exists.</summary>
		public int ForceSeaUntil;

		public ImmutableArray<string> TransportTypes => Info.TransportTypes;

		// Troops can only attack a building they can walk up to: buildings on the water (shipyards, seaports,
		// oil platforms) are left to the fleet, or the boats hunt forever for a beach next to a harbour.
		List<CPos> EnemyTargetCells()
		{
			if (PreferredTarget != null && Hostile(PreferredTarget))
			{
				var theirs = world.Actors.Where(a => a.Owner == PreferredTarget && !a.IsDead && a.IsInWorld && a.Info.HasTraitInfo<BuildingInfo>() && BotHelpers.Known(player, a)
						&& !IsOnWater(a))
					.Select(a => a.Location).ToList();
				if (theirs.Count > 0)
					return theirs;
				if (world.Map.Contains(PreferredTarget.HomeLocation))
					return [PreferredTarget.HomeLocation];
			}

			var known = world.Actors
				.Where(a => BotHelpers.IsEnemy(player, a) && Hostile(a.Owner) && a.Info.HasTraitInfo<BuildingInfo>() && BotHelpers.Known(player, a) && !a.Info.HasTraitInfo<HuskInfo>()
					&& !IsOnWater(a))
				.Select(a => a.Location).ToList();
			if (known.Count > 0)
				return known;

			return world.Players.Where(Hostile).Select(p => p.HomeLocation).Where(world.Map.Contains).ToList();
		}

		void Say(string text)
		{
			Log.Write("debug", $"Amphibious {player.ResolvedPlayerName} t={world.WorldTick}: {text}");
			GameRecorder.Note(player, "invasion: " + text);
		}

		/// <summary>One line for the game record.</summary>
		public string Status => $"{stage}, needSea {NeedSea}, transports {world.Actors.Count(a => a.Owner == player && !a.IsDead && a.IsInWorld && Info.TransportTypes.Contains(a.Info.Name))}, "
			+ $"wave boats {fleetBoats.Count}, aboard/loading {passengers.Count}, ashore {landed.Count}{(PreferredTarget != null ? ", target " + PreferredTarget.ResolvedPlayerName.Trim() : "")}";

		static bool IsOnWater(Actor a) => a.Info.TraitInfoOrDefault<BuildingInfo>()?.TerrainTypes.Contains("Water") == true;

		CPos? ClosestEnemyCell(CPos from)
		{
			var cells = EnemyTargetCells();
			return cells.Count == 0 ? null : cells.MinBy(c => (c - from).LengthSquared);
		}

		bool FitsIn(Actor unit, Cargo cargo)
		{
			var p = unit.Info.TraitInfoOrDefault<PassengerInfo>();
			if (p == null || !cargo.Info.Types.Contains(p.CargoType))
				return false;
			return Info.PassengerTypes.Length == 0 || Info.PassengerTypes.Contains(unit.Info.Name);
		}

		void Release()
		{
			BotHelpers.Squads(player)?.ReleaseUnits(passengers);
			passengers.Clear();
		}

		void GoHome(IBot bot)
		{
			foreach (var b in fleetBoats.Where(b => !b.IsDead && b.IsInWorld))
				bot.QueueOrder(new Order("Move", b, Target.FromCell(world, home), false));
			fleetBoats.Clear();
			seat.Clear();
			SetStage(Stage.Idle);
		}

		void SetStage(Stage s)
		{
			stage = s;
			stageStarted = world.WorldTick;
		}

		CPos? FindLanding(Locomotor boat, Locomotor troops, CPos boatAt, CPos targetCell)
		{
			var tries = 0;
			var max = Math.Min(Info.MaxLandingDistance, world.Map.Grid.MaximumTileSearchRange);
			var cells = world.Map.FindTilesInAnnulus(targetCell, Info.MinLandingDistance, max);
			if (max < world.Map.Grid.MaximumTileSearchRange)
				cells = cells.Concat(world.Map.FindTilesInAnnulus(targetCell, max + 1, world.Map.Grid.MaximumTileSearchRange));

			foreach (var c in cells)
			{
				if (!world.Map.Contains(c) || boat.MovementCostForCell(c) == short.MaxValue)
					continue;

				// The boat cell must touch land our troops can walk on, connected to the target.
				var shore = false;
				foreach (var d in CVec.Directions)
				{
					var n = c + d;
					if (world.Map.Contains(n) && troops.MovementCostForCell(n) != short.MaxValue)
					{
						shore = true;
						break;
					}
				}

				if (!shore)
					continue;

				if (++tries > 400)
					break;

				if (!Reachable(boat, boatAt, c))
					continue;

				foreach (var d in CVec.Directions)
				{
					var n = c + d;
					if (world.Map.Contains(n) && troops.MovementCostForCell(n) != short.MaxValue && Reachable(troops, n, targetCell))
						return c;
				}
			}

			return null;
		}

		// Put the shipyard on water next to any of our buildings (the default logic only looks around the base
		// centre, which on a big island can be too far from the shore).
		int shoreCheck = -10000;
		CPos? shore;

		// No shipyard spot within reach yet: grow the base toward the nearest sea, one building at a time.
		CPos? TowardsShore(ActorInfo actorInfo, BuildingInfo bi)
		{
			if (!(needSea || world.WorldTick < ForceSeaUntil) || bi.TerrainTypes.Contains("Water") || actorInfo.HasTraitInfo<AttackBaseInfo>())
				return null;

			if (world.Actors.Any(a => a.Owner == player && !a.IsDead && Info.ShipyardTypes.Contains(a.Info.Name)))
				return null;

			if (world.WorldTick - shoreCheck > 500)
			{
				shoreCheck = world.WorldTick;
				shore = null;
				var yard = Info.ShipyardTypes.Select(t => world.Map.Rules.Actors.GetValueOrDefault(t)).FirstOrDefault(a => a != null && BotHelpers.CanBuild(player, a.Name));
				var ybi = yard?.TraitInfoOrDefault<BuildingInfo>();
				if (ybi == null)
					return null;

				var home = world.Map.CellContaining(BotHelpers.BaseCenter(player, world.Map.CenterOfCell(player.HomeLocation)));
				var placeable = world.Map.FindTilesInAnnulus(home, 1, 50).Where(c => world.Map.Contains(c) && world.CanPlaceBuilding(c, yard, ybi, null)).Take(200).ToList();
				if (placeable.Count == 0 || placeable.Any(c => ybi.IsCloseEnoughToBase(world, player, yard, c)))
					return null;

				shore = placeable.MinBy(c => (c - home).LengthSquared);
			}

			if (shore == null)
				return null;

			var mine = world.ActorsHavingTrait<GivesBuildableArea>().Where(a => a.Owner == player && !a.IsDead && a.IsInWorld).Select(a => a.Location).ToList();
			var seen = new HashSet<CPos>();
			CPos? best = null;
			var bestDist = int.MaxValue;
			foreach (var c in mine.SelectMany(m => world.Map.FindTilesInAnnulus(m, 2, 10)).Where(seen.Add))
			{
				var d = (c - shore.Value).LengthSquared;
				if (d < bestDist && world.Map.Contains(c) && world.CanPlaceBuilding(c, actorInfo, bi, null) && bi.IsCloseEnoughToBase(world, player, actorInfo, c))
				{
					bestDist = d;
					best = c;
				}
			}

			return best;
		}

		CPos? IBotSuggestBuildingLocation.SuggestLocation(IBot bot, string actorType, ActorInfo actorInfo, BuildingInfo bi)
		{
			if (IsTraitDisabled)
				return null;

			if (!Info.ShipyardTypes.Contains(actorType))
				return TowardsShore(actorInfo, bi);

			var home = world.Map.CellContaining(BotHelpers.BaseCenter(player, world.Map.CenterOfCell(player.HomeLocation)));
			var mine = world.ActorsHavingTrait<GivesBuildableArea>().Where(a => a.Owner == player && !a.IsDead && a.IsInWorld).Select(a => a.Location).ToList();
			var seen = new HashSet<CPos>();
			foreach (var c in mine.SelectMany(m => world.Map.FindTilesInAnnulus(m, 1, 16)).Where(seen.Add).OrderBy(c => (c - home).LengthSquared))
				if (world.Map.Contains(c) && world.CanPlaceBuilding(c, actorInfo, bi, null) && bi.IsCloseEnoughToBase(world, player, actorInfo, c))
					return c;

			Say($"no spot for {actorType}");
			return null;
		}

		// Warships: build a fleet, escort the landings, and raid the enemy coast in between.
		void RunFleet(IBot bot, SquadManagerBotModule squads)
		{
			var fleet = world.Actors.Where(a => a.Owner == player && !a.IsDead && a.IsInWorld && Info.WarshipTypes.Contains(a.Info.Name)).ToList();
			if (fleet.Count < Info.MaxWarships && Info.WarshipTypes.Sum(t => BotHelpers.Queued(player, t)) == 0)
			{
				var buildable = Info.WarshipTypes.Where(t => BotHelpers.CanBuild(player, t)).ToList();
				if (buildable.Count > 0)
					BotHelpers.Queue(bot, player, buildable[shipTurn++ % buildable.Count]);
			}

			if (fleet.Count == 0)
				return;

			squads.ClaimUnits(fleet);
			var boat = LocomotorOf(fleet[0].Info);
			CPos? goal = null;
			if (stage == Stage.Sailing || stage == Stage.Landing)
				goal = landing;
			else if (stage == Stage.Assault || fleet.Count >= Info.FleetRaidSize)
			{
				// The water closest to the enemy base.
				var aim = ClosestEnemyCell(fleet[0].Location);
				if (aim != null && boat != null)
					goal = world.Map.FindTilesInAnnulus(aim.Value, 1, 20)
						.Where(c => world.Map.Contains(c) && boat.MovementCostForCell(c) != short.MaxValue)
						.Take(40).FirstOrDefault(c => Reachable(boat, fleet[0].Location, c));
			}

			if (goal == null || goal == CPos.Zero)
				return;

			// Re-issue now and then, or when ships have gone idle.
			var refresh = world.WorldTick - fleetOrdered > 1500;
			foreach (var ship in fleet.Where(s => s.IsIdle || refresh))
				bot.QueueOrder(new Order("AttackMove", ship, Target.FromCell(world, goal.Value), false));
			if (refresh)
				fleetOrdered = world.WorldTick;
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (--cooldown > 0)
				return;

			cooldown = Info.Interval;
			passengers.RemoveAll(a => a.IsDead || !a.IsInWorld && !fleetBoats.Any(b => !b.IsDead && b.TraitOrDefault<Cargo>()?.Passengers.Contains(a) == true));
			var squads = BotHelpers.Squads(player);
			if (squads == null)
				return;

			// Troops that already landed keep pushing on while the next wave loads.
			landed.RemoveAll(a => a.IsDead || !a.IsInWorld || a.Owner != player);
			if (landed.Count > 0)
			{
				if (world.WorldTick < landedUntil)
				{
					var goal = ClosestEnemyCell(landed[0].Location);
					if (goal != null)
						foreach (var a in landed.Where(a => a.IsIdle))
							bot.QueueOrder(new Order("AttackMove", a, Target.FromCell(world, goal.Value), false));
				}
				else
				{
					squads.ReleaseUnits(landed);
					landed.Clear();
				}
			}

			fleetBoats.RemoveAll(b => b.IsDead || !b.IsInWorld || b.Owner != player);
			if (stage is Stage.Loading or Stage.Sailing or Stage.Landing && fleetBoats.Count == 0)
			{
				Release();
				seat.Clear();
				SetStage(Stage.Idle);
			}

			if (needSea)
				RunFleet(bot, squads);

			var elapsed = world.WorldTick - stageStarted;
			switch (stage)
			{
				case Stage.Idle:
				case Stage.Assault:
				{
					// Is any enemy still reachable over land? If so the normal squads handle it.
					if (--needCheck <= 0)
					{
						needCheck = 12;
						var baseCell = world.Map.CellContaining(BotHelpers.BaseCenter(player, world.Map.CenterOfCell(player.HomeLocation)));
						var walker = world.Map.Rules.Actors.Values.FirstOrDefault(a => Info.PassengerTypes.Contains(a.Name)) ?? world.Map.Rules.Actors["e1"];
						var walk = LocomotorOf(walker);
						var cells = EnemyTargetCells();
						needSea = cells.Count > 0 && walk != null && !cells.Any(c => Reachable(walk, baseCell, c));
						if (world.WorldTick % 3000 < Info.Interval * 12)
							Say($"needSea={needSea} targets={cells.Count} landed={landed.Count}");
					}

					// Land on the player the AI Commander named.
					var commander = AICommanderBotModule.For(player);
					if (commander?.ActiveTarget != null)
						PreferredTarget = commander.ActiveTarget;

					if (!NeedSea)
						return;

					// Need a shipyard first.
					var haveYard = world.Actors.Any(a => a.Owner == player && !a.IsDead && a.IsInWorld && Info.ShipyardTypes.Contains(a.Info.Name));
					if (!haveYard && Info.ShipyardTypes.Sum(t => BotHelpers.Queued(player, t)) == 0)
					{
						var yardType = Info.ShipyardTypes.FirstOrDefault(t => BotHelpers.CanBuild(player, t));
						if (yardType != null)
							BotHelpers.Queue(bot, player, yardType);
					}

					var boats = world.Actors.Where(a => a.Owner == player && !a.IsDead && a.IsInWorld && Info.TransportTypes.Contains(a.Info.Name)).ToList();
					if (boats.Count < Info.MaxTransports && Info.TransportTypes.Sum(t => BotHelpers.Queued(player, t)) == 0)
					{
						var type = Info.TransportTypes.FirstOrDefault(t => BotHelpers.CanBuild(player, t));
						if (type != null)
							BotHelpers.Queue(bot, player, type);
					}

					// Follow the AI Commander: while it says to stay home, get the yard and boats ready but don't sail.
					if (commander != null && commander.HoldOffensives)
						return;

					// Fill a whole wave of empty transports at once.
					var ready = boats.Where(b => b.IsIdle && b.Trait<Cargo>().IsEmpty()).Take(Math.Max(1, Info.BoatsPerWave)).ToList();
					if (ready.Count == 0)
						return;

					// Pick-up point: water by our own shore, close to the base, that the boats can sail to and the
					// troops can walk to. (Boats left idle out at sea can't be boarded - that stalled every wave.)
					var boatLoco = LocomotorOf(ready[0].Info);
					var walkLoco = LocomotorOf(world.Map.Rules.Actors.Values.FirstOrDefault(x => Info.PassengerTypes.Contains(x.Name)) ?? world.Map.Rules.Actors["e1"]);
					var homeCell = world.Map.CellContaining(BotHelpers.BaseCenter(player, world.Map.CenterOfCell(player.HomeLocation)));
					if (pickupShore == null || world.WorldTick - pickupChecked > 3000 || !Reachable(boatLoco, ready[0].Location, pickup))
					{
						pickupChecked = world.WorldTick;
						pickupShore = null;
						var tries = 0;
						foreach (var c in world.Map.FindTilesInAnnulus(homeCell, 1, world.Map.Grid.MaximumTileSearchRange))
						{
							if (!world.Map.Contains(c) || boatLoco == null || boatLoco.MovementCostForCell(c) == short.MaxValue || badPickups.Contains(c))
								continue;

							CPos? shoreCell = null;
							foreach (var d in CVec.Directions)
								if (world.Map.Contains(c + d) && walkLoco != null && walkLoco.MovementCostForCell(c + d) != short.MaxValue)
								{
									shoreCell = c + d;
									break;
								}

							if (shoreCell == null)
								continue;
							if (++tries > 300)
								break;
							if (Reachable(boatLoco, ready[0].Location, c) && Reachable(walkLoco, homeCell, shoreCell.Value))
							{
								pickup = c;
								pickupShore = shoreCell;
								break;
							}
						}
					}

					if (pickupShore == null)
					{
						if (world.WorldTick - lastWhy > 1500)
						{
							lastWhy = world.WorldTick;
							Say("no pick-up point: our boats can't reach any shore our troops can walk to");
						}

						return;
					}

					// Troops: anyone of the right type on our island who isn't fighting or busy for another module.
					// Squad members count too - their squads can't walk across the sea anyway.
					var cargo0 = ready[0].Trait<Cargo>();
					var pool = world.Actors.Where(x => x.Owner == player && !x.IsDead && x.IsInWorld && !landed.Contains(x) && !squads.IsClaimed(x)
							&& FitsIn(x, cargo0) && x.Info.HasTraitInfo<AttackBaseInfo>() && !Fighting(x)
							&& (x.Location - pickupShore.Value).LengthSquared < 60 * 60)
						.OrderBy(x => (x.Location - pickupShore.Value).LengthSquared)
						.Take(60)
						.Where(x => Reachable(walkLoco, x.Location, pickupShore.Value))
						.ToList();

					var chosen = new Dictionary<Actor, Actor>();
					var queue = new Queue<Actor>(pool);
					foreach (var boat in ready)
					{
						var cargo = boat.Trait<Cargo>();
						var room = cargo.Info.MaxWeight;
						while (room > 0 && queue.Count > 0)
						{
							var x = queue.Dequeue();
							if (!FitsIn(x, cargo))
								continue;
							var w = x.Info.TraitInfo<PassengerInfo>().Weight;
							if (w > room)
								continue;
							chosen[x] = boat;
							room -= w;
						}
					}

					if (chosen.Count < Info.MinPassengers)
					{
						if (world.WorldTick - lastWhy > 1500)
						{
							lastWhy = world.WorldTick;
							Say($"{ready.Count} boats ready but only {chosen.Count} troops free to board (need {Info.MinPassengers})");
						}

						return;
					}

					fleetBoats.Clear();
					fleetBoats.AddRange(chosen.Values.Distinct());
					seat.Clear();
					foreach (var kv in chosen)
						seat[kv.Key] = kv.Value;
					passengers.Clear();
					passengers.AddRange(chosen.Keys);
					home = pickup;
					squads.ClaimUnits(passengers);

					// Boats come in to the pick-up point; troops walk to the shore and board as the boats arrive.
					foreach (var b in fleetBoats)
						bot.QueueOrder(new Order("Move", b, Target.FromCell(world, pickup), false));
					foreach (var x in passengers)
						bot.QueueOrder(new Order("EnterTransport", x, Target.FromActor(seat[x]), false));
					SetStage(Stage.Loading);
					Say($"loading {passengers.Count} troops into {fleetBoats.Count} boats at {pickup}");
					return;
				}

				case Stage.Loading:
				{
					var waiting = passengers.Count(a => a.IsInWorld);
					if (waiting > 0 && elapsed < Math.Max(Info.LoadTimeout, 1500))
					{
						foreach (var b in fleetBoats.Where(b => b.IsIdle && (b.Location - pickup).LengthSquared > 16))
							bot.QueueOrder(new Order("Move", b, Target.FromCell(world, pickup), false));

						// Nudge anyone who has wandered off.
						foreach (var a in passengers.Where(a => a.IsInWorld && a.IsIdle && seat.ContainsKey(a)))
							bot.QueueOrder(new Order("EnterTransport", a, Target.FromActor(seat[a]), false));
						return;
					}

					var aboard = passengers.Count(a => !a.IsInWorld && !a.IsDead);
					if (aboard == 0)
					{
						// Nobody made it: don't use this pick-up point again.
						badPickups.Add(pickup);
						pickupShore = null;
						Say($"nobody boarded at {pickup} in {elapsed} ticks - trying another pick-up point");
					}

					// Leave behind anyone who didn't make it aboard, and any boat that stayed empty.
					var left = passengers.Where(a => a.IsInWorld).ToList();
					squads.ReleaseUnits(left);
					passengers.RemoveAll(left.Contains);
					foreach (var b in fleetBoats.Where(b => b.Trait<Cargo>().IsEmpty()).ToList())
						fleetBoats.Remove(b);

					if (fleetBoats.Count == 0)
					{
						Release();
						SetStage(Stage.Idle);
						return;
					}

					var lead = fleetBoats[0];
					var boatLoc = LocomotorOf(lead.Info);
					var walker = LocomotorOf(passengers.FirstOrDefault(a => !a.IsDead)?.Info ?? world.Map.Rules.Actors["e1"]);
					var aim = ClosestEnemyCell(lead.Location);
					var spot = aim == null ? null : FindLanding(boatLoc, walker, lead.Location, aim.Value);
					if (aim != null)
						targetCell = aim.Value;
					if (spot == null)
					{
						Say($"no landing found near {aim} for boat at {lead.Location} - unloading");
						foreach (var b in fleetBoats)
							bot.QueueOrder(new Order("Unload", b, false));
						Release();
						GoHome(bot);
						return;
					}

					landing = spot.Value;
					foreach (var b in fleetBoats)
						bot.QueueOrder(new Order("Move", b, Target.FromCell(world, landing), false));
					SetStage(Stage.Sailing);
					Say($"sailing {fleetBoats.Count} boats, {passengers.Count} troops to {landing} (target {targetCell})");
					return;
				}

				case Stage.Sailing:
				{
					// Only unload at the landing beach. A boat that stopped short (blocked, or the path changed) is
					// sent on again; if the voyage takes far too long the wave turns back and lands at home instead.
					var close = fleetBoats.Any(b => (b.Location - landing).LengthSquared <= 25);
					if (close)
					{
						foreach (var b in fleetBoats)
							bot.QueueOrder(new Order("Unload", b, false));
						Say($"reached landing {landing} after {elapsed} ticks, unloading {passengers.Count} troops");
						SetStage(Stage.Landing);
					}
					else if (elapsed > Info.VoyageTimeout)
					{
						Say($"voyage to {landing} failed (lead boat stuck at {fleetBoats[0].Location}) - turning back");
						foreach (var b in fleetBoats)
						{
							bot.QueueOrder(new Order("Move", b, Target.FromCell(world, home), false));
							bot.QueueOrder(new Order("Unload", b, true));
						}

						BotHelpers.Squads(player)?.ReleaseUnits(passengers);
						passengers.Clear();
						fleetBoats.Clear();
						seat.Clear();
						SetStage(Stage.Idle);
						cooldown = 750;
					}
					else
						foreach (var b in fleetBoats.Where(b => b.IsIdle))
							bot.QueueOrder(new Order("Move", b, Target.FromCell(world, landing), false));

					return;
				}

				case Stage.Landing:
				{
					if (fleetBoats.Any(b => !b.Trait<Cargo>().IsEmpty()) && elapsed < 600)
					{
						foreach (var b in fleetBoats.Where(b => b.IsIdle && !b.Trait<Cargo>().IsEmpty()))
							bot.QueueOrder(new Order("Unload", b, false));
						return;
					}

					// Hand the troops to the "landed" group and get the boats back for the next wave straight away.
					var goal = ClosestEnemyCell(fleetBoats[0].Location) ?? targetCell;
					var ashore = passengers.Where(a => a.IsInWorld && !a.IsDead).ToList();
					Say($"{ashore.Count} troops ashore near {landing}, attacking {goal}");
					foreach (var a in ashore)
						bot.QueueOrder(new Order("AttackMove", a, Target.FromCell(world, goal), false));
					landed.AddRange(ashore);
					landedUntil = world.WorldTick + Info.AssaultTicks;
					passengers.Clear();
					GoHome(bot);
					return;
				}
			}
		}
	}

	// ------------------------------------------------------------------------------------------------
	[TraitLocation(SystemActors.Player)]
	[Desc("AI: keeps a couple of engineers around while there are neutral buildings worth capturing (farms, oil",
		"derricks), so the CaptureManagerBotModule has someone to send.")]
	public class EngineerBuilderBotModuleInfo : ConditionalTraitInfo
	{
		public readonly ImmutableArray<string> EngineerTypes = ["e6"];
		public readonly ImmutableArray<string> TargetTypes = ["farm", "oilb"];
		public readonly int MaxEngineers = 2;
		public readonly int Interval = 500;

		public override object Create(ActorInitializer init) { return new EngineerBuilderBotModule(init.Self, this); }
	}

	public class EngineerBuilderBotModule : ConditionalTrait<EngineerBuilderBotModuleInfo>, IBotTick
	{
		readonly World world;
		readonly Player player;
		int ticks;

		public EngineerBuilderBotModule(Actor self, EngineerBuilderBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (++ticks < Info.Interval)
				return;

			ticks = 0;
			if (!world.Actors.Any(a => !a.IsDead && a.IsInWorld && Info.TargetTypes.Contains(a.Info.Name) && a.Owner.NonCombatant))
				return;

			var have = world.Actors.Count(a => a.Owner == player && !a.IsDead && Info.EngineerTypes.Contains(a.Info.Name))
				+ Info.EngineerTypes.Sum(t => BotHelpers.Queued(player, t));
			if (have >= Info.MaxEngineers)
				return;

			var type = Info.EngineerTypes.FirstOrDefault(t => BotHelpers.CanBuild(player, t));
			if (type != null)
				BotHelpers.Queue(bot, player, type);
		}
	}

	// ------------------------------------------------------------------------------------------------
	[Desc("Grants a condition once the game has run for Delay ticks (same on every computer, so it stays in sync).",
		"Used to switch an AI into its late-game style.")]
	public class GrantConditionAfterTicksInfo : TraitInfo
	{
		[FieldLoader.Require]
		[GrantedConditionReference]
		public readonly string Condition = null;

		public readonly int Delay = 15000;

		public override object Create(ActorInitializer init) { return new GrantConditionAfterTicks(this); }
	}

	public class GrantConditionAfterTicks : ITick
	{
		readonly GrantConditionAfterTicksInfo info;
		bool granted;

		public GrantConditionAfterTicks(GrantConditionAfterTicksInfo info) { this.info = info; }

		void ITick.Tick(Actor self)
		{
			if (granted || self.World.WorldTick < info.Delay)
				return;

			granted = true;
			self.GrantCondition(info.Condition);
		}
	}

	// ------------------------------------------------------------------------------------------------
	[TraitLocation(SystemActors.Player)]
	[Desc("Reads the lobby's AI difficulty option. For computer players it grants the condition and provides the",
		"prerequisite ai-<level> (ai-easy, ai-normal, ai-hard, ai-brutal), which the AI rules hook into.")]
	public class AIDifficultyInfo : TraitInfo, ITechTreePrerequisiteInfo
	{
		[Desc("Lobby option id (from a ScriptLobbyDropdown on the world actor).")]
		public readonly string OptionID = "aidifficulty";

		public readonly string Default = "normal";

		[Desc("The levels, used to declare conditions and prerequisites.")]
		public readonly ImmutableArray<string> Levels = ["easy", "normal", "hard", "brutal"];

		[Desc("Levels that also grant a condition on the player (for switching AI modules on/off).")]
		public readonly ImmutableArray<string> ConditionLevels = ["easy"];

		[GrantedConditionReference]
		public IEnumerable<string> LinterConditions => ConditionLevels.Select(l => "ai-" + l);

		IEnumerable<string> ITechTreePrerequisiteInfo.Prerequisites(ActorInfo info) => Levels.Select(l => "ai-" + l);

		public override object Create(ActorInitializer init) { return new AIDifficulty(this); }
	}

	public class AIDifficulty : INotifyCreated, ITechTreePrerequisite
	{
		readonly AIDifficultyInfo info;
		string[] provided = [];

		public AIDifficulty(AIDifficultyInfo info) { this.info = info; }

		void INotifyCreated.Created(Actor self)
		{
			if (!self.Owner.IsBot)
				return;

			var level = self.World.LobbyInfo.GlobalSettings.OptionOrDefault(info.OptionID, info.Default);
			if (!info.Levels.Contains(level))
				level = info.Default;

			provided = ["ai-" + level];
			if (info.ConditionLevels.Contains(level))
				self.GrantCondition("ai-" + level);
		}

		IEnumerable<string> ITechTreePrerequisite.ProvidesPrerequisites => provided;
	}
}
