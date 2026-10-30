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
using System.Linq;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// Lets each building tell its owner's AI when it is destroyed (or captured), and when a new one is finished.
	[Desc("Reports this building's loss to the owner's RebuildBotModule. Attach to buildings.")]
	public class RebuildTrackerInfo : TraitInfo<RebuildTracker> { }

	public class RebuildTracker : INotifyKilled, INotifyOwnerChanged, INotifyAddedToWorld
	{
		static RebuildBotModule ModuleFor(Player p)
		{
			return p?.PlayerActor?.TraitsImplementing<RebuildBotModule>().FirstOrDefault(m => !m.IsTraitDisabled);
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			ModuleFor(self.Owner)?.BuildingLost(self);
		}

		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			ModuleFor(oldOwner)?.BuildingLost(self);
			ModuleFor(newOwner)?.BuildingGained(self.Info.Name);
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			ModuleFor(self.Owner)?.BuildingGained(self.Info.Name);
		}
	}

	[TraitLocation(SystemActors.Player)]
	[Desc("AI: rebuilds destroyed buildings once the fighting nearby has stopped, and repairs damaged buildings.")]
	public class RebuildBotModuleInfo : ConditionalTraitInfo
	{
		[Desc("How often to check, in ticks.")]
		public readonly int ScanInterval = 50;

		[Desc("Wait until no enemy ground forces have been this close to the ruin for CalmDelay ticks.")]
		public readonly WDist ThreatRange = WDist.FromCells(12);

		[Desc("Ticks of quiet needed before rebuilding.")]
		public readonly int CalmDelay = 250;

		[Desc("Hold back new unit orders while a rebuild is under way, for at most this many ticks.")]
		public readonly int MaxUnitPauseTicks = 1500;

		[Desc("Never rebuild these.")]
		public readonly HashSet<string> IgnoreTypes = [];

		[Desc("Repair any damaged building when cash is at least this much. -1 disables repairs.")]
		public readonly int RepairMinCash = 200;

		[Desc("How often to look for buildings to repair, in ticks.")]
		public readonly int RepairInterval = 100;

		public override object Create(ActorInitializer init) { return new RebuildBotModule(init.Self, this); }
	}

	public class RebuildBotModule : ConditionalTrait<RebuildBotModuleInfo>, IBotTick, IBotRequestPauseUnitProduction
	{
		sealed class Ruin
		{
			public string Type;
			public WPos Position;
			public int ReadyAt;
		}

		readonly World world;
		readonly Player player;
		readonly List<Ruin> ruins = [];
		PlayerResources resources;

		// The ruin currently being rebuilt.
		Ruin current;
		ProductionQueue currentQueue;
		int currentStarted;
		int scanCooldown;
		int repairCooldown;

		public RebuildBotModule(Actor self, RebuildBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		protected override void Created(Actor self)
		{
			resources = self.TraitOrDefault<PlayerResources>();
			base.Created(self);
		}

		public bool PauseUnitProduction =>
			!IsTraitDisabled && current != null && world.WorldTick - currentStarted < Info.MaxUnitPauseTicks;

		public void BuildingLost(Actor building)
		{
			var type = building.Info.Name;
			if (Info.IgnoreTypes.Contains(type) || building.Info.HasTraitInfo<LineBuildInfo>())
				return;

			var buildable = building.Info.TraitInfoOrDefault<BuildableInfo>();
			if (buildable == null || buildable.Queue.Count == 0)
				return;

			ruins.Add(new Ruin { Type = type, Position = building.CenterPosition, ReadyAt = world.WorldTick + Info.CalmDelay });
		}

		public void BuildingGained(string type)
		{
			// A new building of a lost type (ours or one the base builder chose) replaces the oldest ruin of that type.
			var ruin = ruins.FirstOrDefault(r => r.Type == type);
			if (ruin == null)
				return;

			ruins.Remove(ruin);
			if (ruin == current)
			{
				current = null;
				currentQueue = null;
			}
		}

		bool EnemiesNear(WPos pos)
		{
			return world.FindActorsInCircle(pos, Info.ThreatRange)
				.Any(a => !a.IsDead && a.IsInWorld && a.Owner.RelationshipWith(player) == PlayerRelationship.Enemy
					&& a.Info.HasTraitInfo<IMoveInfo>() && !a.Info.HasTraitInfo<AircraftInfo>()
					&& a.Info.HasTraitInfo<AttackBaseInfo>());
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (--repairCooldown <= 0)
			{
				repairCooldown = Info.RepairInterval;
				RepairDamaged(bot);
			}

			if (--scanCooldown > 0)
				return;

			scanCooldown = Info.ScanInterval;
			var now = world.WorldTick;

			// Fighting near a ruin restarts its quiet timer.
			foreach (var r in ruins)
				if (EnemiesNear(r.Position))
					r.ReadyAt = now + Info.CalmDelay;

			// Keep an eye on the rebuild under way.
			if (current != null)
			{
				var stillQueued = currentQueue != null && currentQueue.Enabled
					&& currentQueue.AllQueued().Any(i => i.Item == current.Type);

				if (stillQueued)
					return;

				// Placed buildings are cleared by BuildingGained. If it vanished from the queue some other way
				// (cancelled, factory lost), forget about it and try again later.
				if (now - currentStarted > 150)
				{
					current.ReadyAt = now + Info.CalmDelay;
					current = null;
					currentQueue = null;
				}
				else
					return;
			}

			var queues = player.PlayerActor.TraitsImplementing<ProductionQueue>()
				.Concat(world.ActorsWithTrait<ProductionQueue>().Where(p => p.Actor.Owner == player && !p.Actor.IsDead).Select(p => p.Trait))
				.Where(q => q.Enabled)
				.Distinct()
				.ToList();

			// Rebuild in the order things were lost, skipping anything we can't build right now (e.g. a missing prerequisite).
			foreach (var ruin in ruins)
			{
				if (ruin.ReadyAt > now)
					continue;

				var queue = queues.FirstOrDefault(q => q.BuildableItems().Any(b => b.Name == ruin.Type));
				if (queue == null)
					continue;

				AIUtils.BotDebug("{0} is rebuilding {1}.", player, ruin.Type);
				bot.QueueOrder(Order.StartProduction(queue.Actor, ruin.Type, 1));
				current = ruin;
				currentQueue = queue;
				currentStarted = now;
				return;
			}
		}

		void RepairDamaged(IBot bot)
		{
			if (Info.RepairMinCash < 0 || resources == null || resources.GetCashAndResources() < Info.RepairMinCash)
				return;

			foreach (var tp in world.ActorsWithTrait<RepairableBuilding>())
			{
				if (tp.Actor.Owner != player || tp.Actor.IsDead || !tp.Actor.IsInWorld || tp.Trait.RepairActive)
					continue;

				var health = tp.Actor.TraitOrDefault<IHealth>();
				if (health == null || health.DamageState == DamageState.Undamaged || health.DamageState == DamageState.Dead)
					continue;

				bot.QueueOrder(new Order("RepairBuilding", player.PlayerActor, Target.FromActor(tp.Actor), false));
			}
		}
	}
}
