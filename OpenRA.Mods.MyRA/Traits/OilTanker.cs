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
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// Oil Tanker: works like an ore harvester, but for Offshore Oil Platforms. It sails to a platform, fills up,
	// sails to the nearest friendly Naval Yard / Sub Pen / Boat Dock and unloads the oil for cash, then repeats.
	// You can give it orders; as soon as it's idle again it goes back to work.
	[Desc("Ferries oil from oil platforms to a friendly dock for money.")]
	public class OilTankerInfo : TraitInfo, Requires<MobileInfo>
	{
		[Desc("Buildings it collects oil from.")]
		public readonly HashSet<string> PlatformTypes = ["oilplatform"];

		[Desc("Buildings it unloads at.")]
		public readonly HashSet<string> DockTypes = ["spen", "syrd", "tribaldock"];

		[Desc("Docks used first when the owner has one (e.g. the Seaport for merchant ships); DockTypes are the fallback.")]
		public readonly HashSet<string> PreferredDockTypes = [];

		[Desc("Other sources (e.g. farms) whose loads go to AltDockTypes instead.")]
		public readonly HashSet<string> AltPlatformTypes = [];

		[Desc("Where loads from AltPlatformTypes are delivered (e.g. barracks for farm food).")]
		public readonly HashSet<string> AltDockTypes = [];

		[Desc("Cash value of a full load.")]
		public readonly int Capacity = 500;

		[Desc("Ticks to fill up at a platform.")]
		public readonly int LoadTicks = 250;

		[Desc("Ticks to pump the oil out at a dock.")]
		public readonly int UnloadTicks = 75;

		[Desc("How close (cells) it has to be to a platform or dock to load/unload.")]
		public readonly int WorkRange = 3;

		public override object Create(ActorInitializer init) { return new OilTanker(init.Self, this); }
	}

	public class OilTanker : ITick, ISelectionBar
	{
		readonly OilTankerInfo info;
		readonly IMove move;
		int cargo;
		int timer;
		int retryDelay;
		Actor platform;
		Actor lastTarget;
		int failedMoves;
		bool altLoad;
		readonly HashSet<string> allSources;

		public OilTanker(Actor self, OilTankerInfo info)
		{
			this.info = info;
			move = self.Trait<IMove>();
			allSources = [.. info.PlatformTypes, .. info.AltPlatformTypes];
		}

		/// <summary>Make this tanker work for a particular platform (set when the platform launches it).</summary>
		public void AssignPlatform(Actor p) { platform = p; }

		bool Usable(Actor self, Actor a) => a != null && !a.IsDead && a.IsInWorld && a.Owner == self.Owner;

		Actor Nearest(Actor self, HashSet<string> types)
		{
			return self.World.ActorsHavingTrait<Building>()
				.Where(a => a.Owner == self.Owner && !a.IsDead && a.IsInWorld && types.Contains(a.Info.Name))
				.MinByOrDefault(a => (a.CenterPosition - self.CenterPosition).LengthSquared);
		}

		bool Near(Actor self, Actor target)
		{
			// Measured to the nearest cell of the building's footprint, so big buildings (farms, shipyards) work
			// whatever their hit shape is.
			var building = target.TraitOrDefault<Building>();
			if (building != null)
			{
				var r2 = info.WorkRange * info.WorkRange;
				foreach (var (cell, _) in building.OccupiedCells())
					if ((cell - self.Location).LengthSquared <= r2)
						return true;
				return false;
			}

			return Target.FromActor(target).IsInRange(self.CenterPosition, WDist.FromCells(info.WorkRange));
		}

		// Returns true when we're there; otherwise starts (or retries) the trip.
		bool GoTo(Actor self, Actor target)
		{
			if (Near(self, target))
			{
				failedMoves = 0;
				return true;
			}

			// If the last trip ended without getting there, it can't reach - give up on that target for a while.
			if (lastTarget == target && ++failedMoves > 3)
			{
				failedMoves = 0;
				retryDelay = 250;
				if (target == platform)
					platform = null;
				return false;
			}

			lastTarget = target;

			// Drive to an actual free cell beside the building. (Moving "within range" of a big building's centre
			// fails for farms: every cell that close is inside the farm, so the truck never set off.)
			var spot = Approach(self, target);
			if (spot != null)
				self.QueueActivity(false, move.MoveTo(spot.Value, 0, targetLineColor: Color.Gold));
			else
				self.QueueActivity(false, move.MoveWithinRange(Target.FromActor(target), WDist.FromCells(info.WorkRange - 1), targetLineColor: Color.Gold));
			return false;
		}

		CPos? Approach(Actor self, Actor target)
		{
			var mobile = self.TraitOrDefault<Mobile>();
			var building = target.TraitOrDefault<Building>();
			if (mobile == null || building == null)
				return null;

			var footprint = building.OccupiedCells().Select(c => c.Item1).ToHashSet();
			var reach = info.WorkRange - 1;
			var best = (Cell: (CPos?)null, Dist: int.MaxValue);
			foreach (var f in footprint)
			{
				for (var dy = -reach; dy <= reach; dy++)
				{
					for (var dx = -reach; dx <= reach; dx++)
					{
						var c = f + new CVec(dx, dy);
						if (footprint.Contains(c) || !self.World.Map.Contains(c))
							continue;

						var d = (c - self.Location).LengthSquared;
						if (d >= best.Dist || !mobile.CanEnterCell(c, self, BlockedByActor.Immovable))
							continue;

						best = (c, d);
					}
				}
			}

			return best.Cell;
		}

		void ITick.Tick(Actor self)
		{
			if (!self.IsIdle)
				return;

			if (retryDelay > 0)
			{
				retryDelay--;
				return;
			}

			if (cargo == 0)
			{
				if (!Usable(self, platform))
					platform = Nearest(self, allSources);

				if (platform == null)
				{
					retryDelay = 100;
					return;
				}

				if (!GoTo(self, platform))
					return;

				if (++timer >= info.LoadTicks)
				{
					timer = 0;
					cargo = info.Capacity;
					altLoad = info.AltPlatformTypes.Contains(platform.Info.Name);
				}

				return;
			}

			var dock = altLoad ? Nearest(self, info.AltDockTypes)
				: (info.PreferredDockTypes.Count > 0 ? Nearest(self, info.PreferredDockTypes) : null) ?? Nearest(self, info.DockTypes);
			if (dock == null)
			{
				retryDelay = 100;
				return;
			}

			if (!GoTo(self, dock))
				return;

			if (++timer >= info.UnloadTicks)
			{
				timer = 0;
				var amount = cargo;
				cargo = 0;
				self.Owner.PlayerActor.Trait<PlayerResources>().GiveCash(amount);

				var pos = dock.CenterPosition;
				var color = self.Owner.Color;
				if (self.Owner.IsAlliedWith(self.World.RenderPlayer))
					self.World.AddFrameEndTask(w => w.Add(new FloatingText(pos, color, FloatingText.FormatCashTick(amount), 30)));
			}
		}

		// Yellow bar under the health bar: how full the tank is.
		float ISelectionBar.GetValue()
		{
			if (cargo > 0)
				return timer > 0 ? 1f - (float)timer / info.UnloadTicks : 1f;
			return (float)timer / info.LoadTicks;
		}

		Color ISelectionBar.GetColor() => Color.Gold;
		bool ISelectionBar.DisplayWhenEmpty => false;
	}

	[Desc("Launches a free Oil Tanker onto nearby water when this building is finished.")]
	public class OilTankerSpawnerInfo : TraitInfo
	{
		[ActorReference]
		public readonly string Actor = "oiltanker";

		[Desc("Search for a free spot this far (cells) around Location + SearchCenter.")]
		public readonly CVec SearchCenter = new(1, 1);
		public readonly int MinRange = 2;
		public readonly int MaxRange = 6;

		public override object Create(ActorInitializer init) { return new OilTankerSpawner(this); }
	}

	public class OilTankerSpawner : INotifyAddedToWorld, INotifyOwnerChanged
	{
		readonly OilTankerSpawnerInfo info;

		public OilTankerSpawner(OilTankerSpawnerInfo info) { this.info = info; }

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			// Only for platforms that were just built (not ones placed by the map at game start).
			if (self.World.WorldTick < 2)
				return;

			Launch(self);
		}

		// A neutral farm/platform captured by a player's engineer comes with a truck for its new owner.
		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			if (newOwner.Playable && !newOwner.NonCombatant)
				Launch(self);
		}

		void Launch(Actor self)
		{
			self.World.AddFrameEndTask(w =>
			{
				if (self.IsDead || !self.IsInWorld)
					return;

				var ai = w.Map.Rules.Actors[info.Actor];
				var mobile = ai.TraitInfoOrDefault<MobileInfo>();
				var center = self.Location + info.SearchCenter;
				foreach (var cell in w.Map.FindTilesInAnnulus(center, info.MinRange, info.MaxRange))
				{
					if (!w.Map.Contains(cell) || (mobile != null && !mobile.CanEnterCell(w, null, cell)))
						continue;

					var tanker = w.CreateActor(info.Actor, new TypeDictionary
					{
						new LocationInit(cell),
						new OwnerInit(self.Owner),
						new FacingInit(WAngle.FromFacing(w.SharedRandom.Next(256))),
					});
					tanker.Trait<OilTanker>().AssignPlatform(self);
					return;
				}
			});
		}
	}
}
