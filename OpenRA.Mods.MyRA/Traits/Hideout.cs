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
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	[Desc("A building that hides units (needs Cargo). Everyone pours out when enemy ground forces",
		"come close or the building is attacked.")]
	public class UnloadWhenThreatenedInfo : TraitInfo, Requires<CargoInfo>
	{
		[Desc("Unload when an enemy ground unit is this close.")]
		public readonly WDist Range = WDist.FromCells(8);

		[Desc("How often to look for enemies, in ticks.")]
		public readonly int ScanInterval = 25;

		public override object Create(ActorInitializer init) { return new UnloadWhenThreatened(init.Self, this); }
	}

	public class UnloadWhenThreatened : ITick, INotifyDamage
	{
		readonly UnloadWhenThreatenedInfo info;
		readonly Cargo cargo;
		int cooldown;

		public UnloadWhenThreatened(Actor self, UnloadWhenThreatenedInfo info)
		{
			this.info = info;
			cargo = self.Trait<Cargo>();
			cooldown = self.World.SharedRandom.Next(info.ScanInterval);
		}

		public static bool ThreatNear(Actor self, WDist range)
		{
			return self.World.FindActorsInCircle(self.CenterPosition, range)
				.Any(a => !a.IsDead && a.IsInWorld && a.Owner.RelationshipWith(self.Owner) == PlayerRelationship.Enemy
					&& a.Info.HasTraitInfo<IMoveInfo>() && !a.Info.HasTraitInfo<AircraftInfo>()
					&& a.Info.HasTraitInfo<AttackBaseInfo>() && a.CanBeViewedByPlayer(self.Owner));
		}

		void Unload(Actor self)
		{
			if (cargo.IsEmpty() || self.CurrentActivity is UnloadCargo)
				return;

			self.QueueActivity(false, new UnloadCargo(self, cargo.Info.LoadRange));
		}

		void ITick.Tick(Actor self)
		{
			if (--cooldown > 0)
				return;

			cooldown = info.ScanInterval;
			if (!cargo.IsEmpty() && ThreatNear(self, info.Range))
				Unload(self);
		}

		void INotifyDamage.Damaged(Actor self, AttackInfo e)
		{
			if (e.Attacker != null && e.Damage.Value > 0 && e.Attacker.Owner.RelationshipWith(self.Owner) == PlayerRelationship.Enemy)
				Unload(self);
		}
	}

	[TraitLocation(SystemActors.Player)]
	[Desc("AI: keeps hideout buildings stocked with idle units from around the base.")]
	public class GarrisonBotModuleInfo : ConditionalTraitInfo
	{
		[FieldLoader.Require]
		[Desc("Buildings to fill.")]
		public readonly ImmutableArray<string> HouseTypes = [];

		[FieldLoader.Require]
		[Desc("Units to put inside.")]
		public readonly ImmutableArray<string> UnitTypes = [];

		[Desc("How often to check, in ticks.")]
		public readonly int ScanInterval = 75;

		[Desc("Only send idle units this close to the building.")]
		public readonly WDist GatherRadius = WDist.FromCells(25);

		[Desc("Don't fill a building while enemies are this close to it.")]
		public readonly WDist ThreatRange = WDist.FromCells(10);

		public override object Create(ActorInitializer init) { return new GarrisonBotModule(init.Self, this); }
	}

	public class GarrisonBotModule : ConditionalTrait<GarrisonBotModuleInfo>, IBotTick
	{
		readonly World world;
		readonly Player player;
		readonly Dictionary<Actor, int> sent = [];
		int cooldown;

		public GarrisonBotModule(Actor self, GarrisonBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (--cooldown > 0)
				return;

			cooldown = Info.ScanInterval;

			// Forget units that got inside, died, or never made it.
			foreach (var a in sent.Keys.ToList())
				if (a.IsDead || !a.IsInWorld || world.WorldTick - sent[a] > 750)
					sent.Remove(a);

			var houses = world.ActorsHavingTrait<Cargo>()
				.Where(h => h.Owner == player && !h.IsDead && h.IsInWorld && Info.HouseTypes.Contains(h.Info.Name))
				.ToList();

			// On an island the warriors are needed for sea invasions: stop filling the houses and send the ones
			// inside back out (unless the house is under threat).
			var atSea = player.PlayerActor.TraitsImplementing<AmphibiousAssaultBotModule>().Any(m => !m.IsTraitDisabled && m.NeedSea);
			if (atSea)
			{
				foreach (var house in houses)
					if (!house.Trait<Cargo>().IsEmpty() && house.IsIdle && !UnloadWhenThreatened.ThreatNear(house, Info.ThreatRange))
						bot.QueueOrder(new Order("Unload", house, false));
				return;
			}

			foreach (var house in houses)
			{
				var cargo = house.Trait<Cargo>();
				if (UnloadWhenThreatened.ThreatNear(house, Info.ThreatRange))
					continue;

				var enRoute = sent.Count(kv => kv.Value >= 0 && kv.Key.CurrentActivity != null
					&& kv.Key.CurrentActivity.TargetLineNodes(kv.Key).Any(n => n.Target.Type == TargetType.Actor && n.Target.Actor == house));
				var room = cargo.Info.MaxWeight - cargo.PassengerCount - enRoute;
				if (room <= 0)
					continue;

				var recruits = world.FindActorsInCircle(house.CenterPosition, Info.GatherRadius)
					.Where(a => a.Owner == player && !a.IsDead && a.IsInWorld && a.IsIdle
						&& Info.UnitTypes.Contains(a.Info.Name) && !sent.ContainsKey(a) && cargo.CanLoad(a))
					.OrderBy(a => (a.CenterPosition - house.CenterPosition).LengthSquared)
					.Take(room)
					.ToList();

				foreach (var unit in recruits)
				{
					bot.QueueOrder(new Order("EnterTransport", unit, Target.FromActor(house), false));
					sent[unit] = world.WorldTick;
				}
			}
		}
	}
}
