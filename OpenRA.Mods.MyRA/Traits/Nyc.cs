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
using OpenRA.Activities;
using OpenRA.Graphics;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	static class NycHelpers
	{
		public static Player Neutral(World w) => w.Players.FirstOrDefault(p => p.InternalName == "Neutral") ?? w.WorldActor.Owner;

		public static bool Alive(Actor a) => a != null && !a.IsDead && a.IsInWorld;

		public static bool Hostile(Player me, Player them)
		{
			if (them == null || them.NonCombatant || me.RelationshipWith(them) != PlayerRelationship.Enemy)
				return false;

			var truces = me.World.WorldActor.TraitOrDefault<TruceManager>();
			return truces == null || !truces.InTruce(me, them);
		}
	}

	// ================================================================================================
	// Carrier deck: one parked plane is drawn on the deck for every plane in the hangar.
	// ================================================================================================
	[Desc("Draws a parked aircraft on the deck for each aircraft docked in this actor's CarrierParent.")]
	public class WithCarrierParkedPlanesInfo : TraitInfo, Requires<RenderSpritesInfo>, Requires<BodyOrientationInfo>, Requires<CarrierParentInfo>
	{
		public readonly string Image = "f14parked";

		[SequenceReference(nameof(Image))]
		public readonly string Sequence = "idle";

		[Desc("Deck spots, in local coordinates, filled in order.")]
		public readonly ImmutableArray<WVec> Offsets = [];

		public override object Create(ActorInitializer init) { return new WithCarrierParkedPlanes(init.Self, this); }
	}

	public class WithCarrierParkedPlanes
	{
		public WithCarrierParkedPlanes(Actor self, WithCarrierParkedPlanesInfo info)
		{
			var rs = self.Trait<RenderSprites>();
			var body = self.Trait<BodyOrientation>();
			var facing = self.TraitOrDefault<IFacing>();
			var parent = self.Trait<CarrierParent>();

			for (var i = 0; i < info.Offsets.Length; i++)
			{
				var index = i;
				var offset = info.Offsets[i];
				var anim = new Animation(self.World, info.Image, () => facing == null ? WAngle.Zero : body.QuantizeFacing(facing.Facing));
				anim.PlayRepeating(info.Sequence);
				rs.Add(new AnimationWithOffset(anim,
					() => body.LocalToWorld(offset.Rotate(body.QuantizeOrientation(self.Orientation))),
					() => parent.DockedCount <= index,
					p => RenderUtils.ZOffsetFromCenter(self, p, 2)));
			}
		}
	}

	// ================================================================================================
	// Seaport: hands its owner one free Container Ship, once, on open water next to the dock.
	// ================================================================================================
	[Desc("Gives the owner one free ship next to this building, once, if there is open water for it.")]
	public class FreeShipInfo : TraitInfo
	{
		[ActorReference]
		public readonly string Actor = "containership";

		[Desc("Ticks after the building appears before the ship is launched.")]
		public readonly int Delay = 50;

		public readonly int SearchRadius = 8;

		public override object Create(ActorInitializer init) { return new FreeShip(this); }
	}

	public class FreeShip : ITick, INotifyAddedToWorld
	{
		readonly FreeShipInfo info;
		int timer = -1;
		bool given;

		public FreeShip(FreeShipInfo info) { this.info = info; }

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			if (!given)
				timer = info.Delay;
		}

		void ITick.Tick(Actor self)
		{
			if (given || timer < 0 || --timer > 0)
				return;

			given = true;
			if (self.Owner.NonCombatant || !self.Owner.Playable)
				return;

			var w = self.World;
			var mobile = w.Map.Rules.Actors[info.Actor].TraitInfoOrDefault<MobileInfo>();
			if (mobile == null)
				return;

			var cell = w.Map.FindTilesInAnnulus(self.Location, 1, info.SearchRadius)
				.Where(c => w.Map.Contains(c) && mobile.CanEnterCell(w, null, c))
				.OrderBy(c => (c - self.Location).LengthSquared)
				.Cast<CPos?>()
				.FirstOrDefault();

			// No water for it (shouldn't happen for a water building): quietly skip.
			if (cell == null)
				return;

			w.AddFrameEndTask(ww => ww.CreateActor(info.Actor, new TypeDictionary
			{
				new LocationInit(cell.Value),
				new OwnerInit(self.Owner),
				new FacingInit(WAngle.FromFacing(ww.SharedRandom.Next(256))),
			}));

			if (w.LocalPlayer == self.Owner)
				TextNotificationsManager.AddSystemLine("Seaport", "Your new Seaport came with a free Container Ship.");
		}
	}

	// ================================================================================================
	// Riots: Psy-Op converts go on the offensive - the nearest enemy building, then the next, then any
	// enemy troops nearby. Only switched on for units created by a Psy-Op uprising.
	// ================================================================================================
	[Desc("Once activated, the unit keeps attacking the nearest enemy building, then nearby enemy units.")]
	public class RiotAssaultInfo : TraitInfo
	{
		public readonly int ScanInterval = 40;

		[Desc("Enemy units this close are attacked when no building is in reach.")]
		public readonly WDist UnitRange = WDist.FromCells(10);

		[Desc("Buildings further than this are ignored while there are enemy units nearby.")]
		public readonly WDist BuildingRange = WDist.FromCells(40);

		public override object Create(ActorInitializer init) { return new RiotAssault(this); }
	}

	public class RiotAssault : ITick
	{
		readonly RiotAssaultInfo info;
		bool active;
		int timer;

		public RiotAssault(RiotAssaultInfo info) { this.info = info; }

		public void Activate() { active = true; }

		void ITick.Tick(Actor self)
		{
			if (!active || !self.IsInWorld || --timer > 0)
				return;

			timer = info.ScanInterval;
			if (!self.IsIdle)
				return;

			var me = self.Owner;
			var pos = self.CenterPosition;
			var attacks = self.TraitsImplementing<AttackBase>().ToArray();
			if (attacks.Length == 0)
				return;

			bool CanHit(Actor a) => attacks.Any(at => at.HasAnyValidWeapons(Target.FromActor(a)));

			var building = self.World.ActorsHavingTrait<Building>()
				.Where(a => NycHelpers.Alive(a) && NycHelpers.Hostile(me, a.Owner) && a.Info.HasTraitInfo<IHealthInfo>() && CanHit(a))
				.MinByOrDefault(a => (a.CenterPosition - pos).HorizontalLengthSquared);

			var nearBuilding = building != null && (building.CenterPosition - pos).HorizontalLengthSquared <= info.BuildingRange.LengthSquared;
			Actor target = nearBuilding ? building : null;

			if (target == null)
			{
				target = self.World.FindActorsInCircle(pos, info.UnitRange)
					.Where(a => NycHelpers.Alive(a) && NycHelpers.Hostile(me, a.Owner) && !a.Info.HasTraitInfo<BuildingInfo>()
						&& a.Info.HasTraitInfo<IHealthInfo>() && CanHit(a))
					.MinByOrDefault(a => (a.CenterPosition - pos).HorizontalLengthSquared);
			}

			target ??= building;
			if (target == null)
				return;

			foreach (var attack in attacks)
				attack.AttackTarget(Target.FromActor(target), AttackSource.Default, false, true, true);
		}
	}

	// ================================================================================================
	// Rooftop garrisons: neutral city buildings that any side's infantry can climb into. The building
	// joins that side while it holds troops, goes back to neutral when the last one leaves, and draws
	// its occupants standing on the roof.
	// ================================================================================================
	[Desc("Neutral building that switches to the side of whoever's infantry is inside, and back to neutral when empty.")]
	public class GarrisonableInfo : TraitInfo, Requires<CargoInfo>
	{
		public override object Create(ActorInitializer init) { return new Garrisonable(); }
	}

	public class Garrisonable : INotifyPassengerEntered, INotifyPassengerExited
	{
		void INotifyPassengerEntered.OnPassengerEntered(Actor self, Actor passenger)
		{
			if (self.Owner != passenger.Owner)
				self.ChangeOwnerSync(passenger.Owner);
		}

		void INotifyPassengerExited.OnPassengerExited(Actor self, Actor passenger)
		{
			var cargo = self.Trait<Cargo>();
			if (!cargo.IsEmpty())
				return;

			self.World.AddFrameEndTask(w =>
			{
				if (!self.IsDead && cargo.IsEmpty() && !self.Owner.NonCombatant)
					self.ChangeOwner(NycHelpers.Neutral(w));
			});
		}
	}

	[Desc("Draws the infantry garrisoned in this building standing on its roof.")]
	public class WithRooftopGarrisonInfo : TraitInfo, Requires<RenderSpritesInfo>, Requires<CargoInfo>
	{
		[Desc("Where the troops stand, in world offset from the building centre (Z = roof height).")]
		public readonly ImmutableArray<WVec> Spots = [new WVec(-256, 0, 2048), new WVec(256, 0, 2048)];

		public readonly string Sequence = "stand";

		public override object Create(ActorInitializer init) { return new WithRooftopGarrison(init.Self, this); }
	}

	public class WithRooftopGarrison : INotifyPassengerEntered, INotifyPassengerExited
	{
		readonly WithRooftopGarrisonInfo info;
		readonly Cargo cargo;
		readonly Animation[] anims;
		readonly string[] images;
		readonly WAngle[] facings;

		public WithRooftopGarrison(Actor self, WithRooftopGarrisonInfo info)
		{
			this.info = info;
			cargo = self.Trait<Cargo>();
			var rs = self.Trait<RenderSprites>();
			anims = new Animation[info.Spots.Length];
			images = new string[info.Spots.Length];
			facings = new WAngle[info.Spots.Length];

			for (var i = 0; i < info.Spots.Length; i++)
			{
				var index = i;
				var spot = info.Spots[i];
				facings[i] = WAngle.FromFacing(96 + 24 * i);
				anims[i] = new Animation(self.World, "e1", () => facings[index]);
				anims[i].PlayRepeating(info.Sequence);
				rs.Add(new AnimationWithOffset(anims[i],
					() => spot,
					() => images[index] == null,
					p => spot.Y + spot.Z + 2048), null, true);
			}
		}

		void Refresh(Actor self)
		{
			var passengers = cargo.Passengers.ToList();
			for (var i = 0; i < anims.Length; i++)
			{
				if (i >= passengers.Count)
				{
					images[i] = null;
					continue;
				}

				var p = passengers[i];
				var image = p.TraitOrDefault<RenderSprites>()?.GetImage(p) ?? "e1";
				if (images[i] != image)
				{
					anims[i].ChangeImage(image, info.Sequence);
					anims[i].PlayRepeating(info.Sequence);
				}

				images[i] = image;
			}
		}

		void INotifyPassengerEntered.OnPassengerEntered(Actor self, Actor passenger) { Refresh(self); }

		void INotifyPassengerExited.OnPassengerExited(Actor self, Actor passenger) { Refresh(self); }
	}

	// ================================================================================================
	// Destructible bridges. The intact bridge turns the water cells under its deck into road. When it
	// dies it leaves a wreck that turns them back into water (ships can then sail through) and drowns
	// any ground unit caught on the span. An engineer entering a repair hut at either end rebuilds it.
	// ================================================================================================
	[Desc("Bridge deck over water: the cells it spans are drivable road while this actor stands.")]
	public class BridgeSpanInfo : TraitInfo
	{
		[Desc("Cells covered, as left/top and right/bottom offsets from the actor's cell.")]
		public readonly CVec TopLeft = CVec.Zero;
		public readonly CVec BottomRight = CVec.Zero;

		public readonly string Terrain = "Road";

		public override object Create(ActorInitializer init) { return new BridgeSpan(this); }
	}

	public class BridgeSpan : INotifyAddedToWorld, INotifyRemovedFromWorld
	{
		readonly BridgeSpanInfo info;

		public BridgeSpan(BridgeSpanInfo info) { this.info = info; }

		public IEnumerable<CPos> Cells(Actor self) => SpanCells(self.World.Map, self.Location, info.TopLeft, info.BottomRight);

		public static IEnumerable<CPos> SpanCells(Map map, CPos at, CVec tl, CVec br)
		{
			for (var y = at.Y + tl.Y; y <= at.Y + br.Y; y++)
				for (var x = at.X + tl.X; x <= at.X + br.X; x++)
				{
					var c = new CPos(x, y);
					if (map.Contains(c))
						yield return c;
				}
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			var map = self.World.Map;
			var index = map.Rules.TerrainInfo.GetTerrainIndex(info.Terrain);
			foreach (var c in Cells(self))
				map.CustomTerrain[c] = index;
		}

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self)
		{
			var map = self.World.Map;
			foreach (var c in Cells(self))
				map.CustomTerrain[c] = byte.MaxValue;
		}
	}

	[Desc("The remains of a collapsed bridge. Drowns ground units on the span; can be rebuilt from a repair hut.")]
	public class BridgeWreckInfo : TraitInfo
	{
		[ActorReference]
		[Desc("The intact bridge to put back when repaired.")]
		public readonly string Intact = null;

		public readonly CVec TopLeft = CVec.Zero;
		public readonly CVec BottomRight = CVec.Zero;

		public override object Create(ActorInitializer init) { return new BridgeWreck(this); }
	}

	public class BridgeWreck : INotifyAddedToWorld
	{
		public readonly BridgeWreckInfo Info;

		public BridgeWreck(BridgeWreckInfo info) { Info = info; }

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			var w = self.World;
			var cells = BridgeSpan.SpanCells(w.Map, self.Location, Info.TopLeft, Info.BottomRight).ToHashSet();
			var victims = cells.SelectMany(c => w.ActorMap.GetActorsAt(c)).Distinct()
				.Where(a => a != self && !a.IsDead && a.Info.HasTraitInfo<MobileInfo>() && !a.Info.HasTraitInfo<AircraftInfo>()
					&& !a.Info.TraitInfo<MobileInfo>().Locomotor.Contains("naval"))
				.ToList();

			w.AddFrameEndTask(_ =>
			{
				foreach (var a in victims)
					if (!a.IsDead)
						a.Kill(self);
			});

			if (w.LocalPlayer != null)
				TextNotificationsManager.AddSystemLine("Bridge", "A bridge has collapsed into the river!");
		}

		public void Rebuild(Actor self)
		{
			if (self.IsDead || self.Disposed)
				return;

			var location = self.Location;
			var owner = self.Owner;
			self.World.AddFrameEndTask(w =>
			{
				if (self.Disposed)
					return;

				self.Dispose();
				w.CreateActor(Info.Intact, new TypeDictionary { new LocationInit(location), new OwnerInit(owner) });
				if (w.LocalPlayer != null)
					TextNotificationsManager.AddSystemLine("Bridge", "Engineers have rebuilt a bridge.");
			});
		}
	}

	[Desc("Hut at the end of a bridge. While the bridge is down an engineer can enter (capture) it to rebuild the bridge.")]
	public class BridgeRepairHutInfo : TraitInfo
	{
		[Desc("Look for a collapsed bridge this close.")]
		public readonly WDist Range = WDist.FromCells(20);

		[GrantedConditionReference]
		[Desc("Granted while a collapsed bridge is in range (enables capturing).")]
		public readonly string Condition = "bridge-down";

		public override object Create(ActorInitializer init) { return new BridgeRepairHut(this); }
	}

	public class BridgeRepairHut : ITick, INotifyOwnerChanged
	{
		readonly BridgeRepairHutInfo info;
		int token = Actor.InvalidConditionToken;
		int timer;

		public BridgeRepairHut(BridgeRepairHutInfo info) { this.info = info; }

		Actor FindWreck(Actor self)
		{
			return self.World.ActorsHavingTrait<BridgeWreck>()
				.Where(a => NycHelpers.Alive(a) && (a.CenterPosition - self.CenterPosition).HorizontalLengthSquared <= info.Range.LengthSquared)
				.MinByOrDefault(a => (a.CenterPosition - self.CenterPosition).HorizontalLengthSquared);
		}

		void ITick.Tick(Actor self)
		{
			if (--timer > 0)
				return;

			timer = 25;
			var down = FindWreck(self) != null;
			if (down && token == Actor.InvalidConditionToken)
				token = self.GrantCondition(info.Condition);
			else if (!down && token != Actor.InvalidConditionToken)
				token = self.RevokeCondition(token);
		}

		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			if (newOwner.NonCombatant)
				return;

			// An engineer got in: rebuild the bridge and hand the hut back to the city.
			var wreck = FindWreck(self);
			wreck?.Trait<BridgeWreck>().Rebuild(wreck);
			self.World.AddFrameEndTask(w =>
			{
				if (!self.IsDead)
					self.ChangeOwner(NycHelpers.Neutral(w));
			});
		}
	}

	// ================================================================================================
	// Space Launch Facility: load infantry into the rocket, then launch it at any spot on the map; the
	// troops come down by parachute there. The pad then rolls out a new rocket.
	// ================================================================================================
	[Desc("Launches this building's infantry passengers to the target; they land by parachute.")]
	public class LaunchTroopsPowerInfo : SupportPowerInfo, Requires<CargoInfo>
	{
		[Desc("Ticks from ignition until the troops arrive over the target.")]
		public readonly int FlightDelay = 75;

		[Desc("Altitude the troops are released at.")]
		public readonly WDist DropAltitude = new(7168);

		[Desc("Spread the troops over this many cells.")]
		public readonly int Spread = 3;

		[SequenceReference]
		public readonly string LaunchSequence = "launch";

		[SequenceReference]
		public readonly string EmptySequence = "empty";

		[SequenceReference]
		public readonly string RolloutSequence = "rollout";

		[Desc("Ticks the pad stands empty before the next rocket rolls out.")]
		public readonly int RolloutDelay = 750;

		[GrantedConditionReference]
		[Desc("Granted while the pad has no rocket (disables loading).")]
		public readonly string EmptyCondition = "pad-empty";

		public override object Create(ActorInitializer init) { return new LaunchTroopsPower(init.Self, this); }
	}

	public class LaunchTroopsPower : SupportPower, ITick
	{
		readonly LaunchTroopsPowerInfo info;
		int emptyToken = Actor.InvalidConditionToken;
		int rolloutTimer = -1;
		int flightTimer = -1;
		List<Actor> inFlight = [];
		CPos target;

		public LaunchTroopsPower(Actor self, LaunchTroopsPowerInfo info)
			: base(self, info) { this.info = info; }

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			var cargo = self.Trait<Cargo>();
			if (cargo.IsEmpty() || flightTimer >= 0)
			{
				if (self.World.LocalPlayer == self.Owner)
					TextNotificationsManager.AddSystemLine("Launch",
						"No troops aboard: select infantry and right-click the launch pad to board them (up to 10), then launch.");

				// Keep the charge: nothing was launched.
				var instance = manager.Powers.Values.FirstOrDefault(p => p.Instances.Contains(this));
				self.World.AddFrameEndTask(_ => instance?.ChargeFully());
				return;
			}

			base.Activate(self, order, manager);
			PlayLaunchSounds();
			target = self.World.Map.CellContaining(order.Target.CenterPosition);

			inFlight = [];
			while (!cargo.IsEmpty())
				inFlight.Add(cargo.Unload(self));

			emptyToken = self.GrantCondition(info.EmptyCondition);
			var body = self.TraitOrDefault<WithSpriteBody>();
			body?.PlayCustomAnimation(self, info.LaunchSequence, () => body.PlayCustomAnimationRepeating(self, info.EmptySequence));
			flightTimer = info.FlightDelay;
			rolloutTimer = info.RolloutDelay;
		}

		void ITick.Tick(Actor self)
		{
			if (flightTimer > 0 && --flightTimer == 0)
			{
				flightTimer = -1;
				Drop(self);
			}

			if (rolloutTimer > 0 && --rolloutTimer == 0)
			{
				rolloutTimer = -1;
				var body = self.TraitOrDefault<WithSpriteBody>();
				if (body != null)
					body.PlayCustomAnimation(self, info.RolloutSequence, () =>
					{
						body.CancelCustomAnimation(self);
						if (emptyToken != Actor.InvalidConditionToken)
							emptyToken = self.RevokeCondition(emptyToken);
					});
				else if (emptyToken != Actor.InvalidConditionToken)
					emptyToken = self.RevokeCondition(emptyToken);
			}
		}

		void Drop(Actor self)
		{
			var w = self.World;
			var troops = inFlight;
			inFlight = [];
			var cells = w.Map.FindTilesInAnnulus(target, 0, info.Spread).Where(w.Map.Contains).ToList();
			var i = 0;

			w.AddFrameEndTask(ww =>
			{
				foreach (var a in troops)
				{
					if (a.IsDead || a.Disposed)
						continue;

					var pos = a.Trait<IPositionable>();
					var cell = cells.Count > 0 ? cells[(i++ * 7) % cells.Count] : target;
					var sub = pos.GetAvailableSubCell(cell);
					pos.SetPosition(a, cell, sub == SubCell.Invalid ? SubCell.Any : sub);
					pos.SetCenterPosition(a, a.CenterPosition + new WVec(0, 0, info.DropAltitude.Length));
					ww.Add(a);
				}
			});

			if (w.LocalPlayer == self.Owner)
				TextNotificationsManager.AddSystemLine("Launch", $"{troops.Count} troops are parachuting into the target zone.");
		}
	}

	// ================================================================================================
	// Air patrol: a plane sent to a spot on the map (not a runway) flies a figure 8 over it and attacks
	// any enemy land, sea or air unit that comes into the area. Any other order ends the patrol.
	// ================================================================================================
	[Desc("After a move order, the aircraft patrols a figure 8 over that spot and attacks enemies entering the area.")]
	public class AirPatrolInfo : TraitInfo, Requires<AircraftInfo>
	{
		[Desc("Half-width of the figure 8.")]
		public readonly WDist Radius = WDist.FromCells(6);

		[Desc("Enemies this close to the patrol centre are attacked.")]
		public readonly WDist ScanRadius = WDist.FromCells(9);

		public readonly int ScanInterval = 10;

		public override object Create(ActorInitializer init) { return new AirPatrol(this); }
	}

	public class AirPatrol : ITick, IResolveOrder, INotifyCreated
	{
		readonly AirPatrolInfo info;
		WPos? center;
		int leg, timer;
		Activity legActivity;
		AttackBase[] attacks = [];

		public AirPatrol(AirPatrolInfo info) { this.info = info; }

		public bool Active => center != null;

		void INotifyCreated.Created(Actor self) { attacks = self.TraitsImplementing<AttackBase>().ToArray(); }

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			switch (order.OrderString)
			{
				case "Move":
					if (order.Target.Type != TargetType.Invalid && !order.Queued)
					{
						var p = order.Target.CenterPosition;
						center = new WPos(p.X, p.Y, 0);
						leg = 0;
						legActivity = null;
					}

					break;

				// Attacking keeps the patrol (it resumes afterwards); anything else ends it.
				case "Attack":
				case "ForceAttack":
					break;

				default:
					if (!order.Queued && order.OrderString is "Stop" or "ReturnToBase" or "Enter" or "ForceEnter" or "Land" or "Repair" or "Guard" or "AttackMove" or "AssaultMove")
						center = null;

					break;
			}
		}

		WPos Point(int k)
		{
			// Lemniscate (figure 8) through the centre: x = R sin t, y = R sin t cos t.
			var t = k * System.Math.PI * 2 / 8;
			var r = info.Radius.Length;
			return center.Value + new WVec((int)(r * System.Math.Sin(t)), (int)(r * System.Math.Sin(t) * System.Math.Cos(t) * 1.2), 0);
		}

		void ITick.Tick(Actor self)
		{
			if (center == null || !self.IsInWorld || --timer > 0)
				return;

			timer = info.ScanInterval;
			var act = self.CurrentActivity;
			var patrolling = act == null || act is Mods.Common.Activities.FlyIdle || act == legActivity;

			// Look for enemies in the patrol area (only while not already fighting).
			if (patrolling)
			{
				var me = self.Owner;
				var c = center.Value;
				var enemy = self.World.FindActorsInCircle(c, info.ScanRadius)
					.Where(a => NycHelpers.Alive(a) && NycHelpers.Hostile(me, a.Owner) && a.CanBeViewedByPlayer(me)
						&& a.Info.HasTraitInfo<IHealthInfo>() && !a.Info.HasTraitInfo<HuskInfo>()
						&& !a.GetEnabledTargetTypes().Contains("NoAutoTarget")
						&& attacks.Any(at => at.HasAnyValidWeapons(Target.FromActor(a))))
					.MinByOrDefault(a => (a.CenterPosition - self.CenterPosition).HorizontalLengthSquared);

				if (enemy != null)
				{
					legActivity = null;
					foreach (var at in attacks)
						at.AttackTarget(Target.FromActor(enemy), AttackSource.Default, false, true);

					return;
				}
			}

			// Fly the next leg of the figure 8.
			if (act == null || act is Mods.Common.Activities.FlyIdle || (act == legActivity && act.State == ActivityState.Done))
			{
				if (act != null)
					self.CancelActivity();

				leg = (leg + 1) % 8;
				legActivity = new Mods.Common.Activities.Fly(self, Target.FromPos(Point(leg)), WDist.FromCells(1));
				self.QueueActivity(false, legActivity);
			}
		}
	}
}
