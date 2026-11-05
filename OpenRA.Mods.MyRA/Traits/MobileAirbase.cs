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
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Orders;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

// Ships as mobile airbases. The Aircraft Carrier is a floating airfield (F-14s and Harriers) and the Littoral
// Combat Ship a floating helipad (any helicopter, or a Harrier). Select an aircraft and right-click the ship:
// it flies over, sets down on a free deck spot and stays there - riding along as the ship sails - while it is
// rearmed and repaired. Give it any other order and it lifts off again. If the ship sinks, whatever is parked on
// its deck goes down with it.
namespace OpenRA.Mods.MyRA.Traits
{
	[Desc("Aircraft can land on this ship's deck, sit there while it moves, and get rearmed and repaired.")]
	public class MobileAirbaseInfo : TraitInfo, Requires<BodyOrientationInfo>
	{
		[Desc("Aircraft types (actor names) that may land here.")]
		public readonly ImmutableArray<string> AircraftTypes = [];

		[Desc("Also accept every helicopter (any aircraft that can hover).")]
		public readonly bool AnyHelicopter = false;

		[Desc("Deck spots in the ship's local coordinates (forward, right, up). One aircraft per spot.")]
		public readonly ImmutableArray<WVec> Spots = [];

		[Desc("How high the deck is drawn above the water.")]
		public readonly int DeckHeight = 256;

		[Desc("Ticks between rounds of ammo given to a parked aircraft.")]
		public readonly int RearmInterval = 40;

		[Desc("Percent of max HP repaired per second on deck.")]
		public readonly int RepairPercent = 2;

		[ActorReference]
		[Desc("Aircraft the ship comes with, parked on its deck.")]
		public readonly ImmutableArray<string> InitialAircraft = [];

		public override object Create(ActorInitializer init) { return new MobileAirbase(init.Self, this); }
	}

	public class MobileAirbase : INotifyAddedToWorld, INotifyKilled, INotifyActorDisposing, INotifyOwnerChanged
	{
		public readonly MobileAirbaseInfo Info;
		readonly Actor self;
		readonly BodyOrientation body;
		readonly Actor[] holders;
		readonly bool[] onDeck;
		bool spawned;

		public MobileAirbase(Actor self, MobileAirbaseInfo info)
		{
			Info = info;
			this.self = self;
			body = self.Trait<BodyOrientation>();
			holders = new Actor[info.Spots.Length];
			onDeck = new bool[info.Spots.Length];
		}

		public bool Accepts(Actor aircraft)
		{
			if (Info.AircraftTypes.Contains(aircraft.Info.Name))
				return true;
			return Info.AnyHelicopter && aircraft.Info.TraitInfoOrDefault<AircraftInfo>()?.CanHover == true;
		}

		void Clean()
		{
			for (var i = 0; i < holders.Length; i++)
				if (holders[i] != null && (holders[i].IsDead || holders[i].Disposed || holders[i].CurrentActivity is not DeckLanding))
				{
					holders[i] = null;
					onDeck[i] = false;
				}
		}

		public bool HasRoomFor(Actor aircraft)
		{
			Clean();
			return holders.Contains(aircraft) || holders.Any(h => h == null);
		}

		public int Reserve(Actor aircraft)
		{
			Clean();
			var i = System.Array.IndexOf(holders, aircraft);
			if (i >= 0)
				return i;
			i = System.Array.IndexOf(holders, null);
			if (i >= 0)
				holders[i] = aircraft;
			return i;
		}

		public void Release(Actor aircraft)
		{
			for (var i = 0; i < holders.Length; i++)
				if (holders[i] == aircraft)
				{
					holders[i] = null;
					onDeck[i] = false;
				}
		}

		public void SetOnDeck(int spot, bool value) { onDeck[spot] = value; }

		public int Parked => holders.Where((h, i) => h != null && onDeck[i]).Count();

		/// <summary>Where a deck spot is right now (at water level; the deck height is added when drawing).</summary>
		public WPos SpotPosition(int spot)
		{
			var local = Info.Spots[spot];
			var p = self.CenterPosition + body.LocalToWorld(new WVec(local.X, local.Y, 0).Rotate(body.QuantizeOrientation(self.Orientation)));
			return new WPos(p.X, p.Y, self.CenterPosition.Z);
		}

		public WAngle Facing => self.Orientation.Yaw;

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			if (spawned || self.World.Type != WorldType.Regular)
				return;
			spawned = true;

			// The aircraft it comes with, already parked on the deck.
			self.World.AddFrameEndTask(w =>
			{
				if (self.IsDead || !self.IsInWorld)
					return;
				foreach (var type in Info.InitialAircraft.Take(Info.Spots.Length))
				{
					var a = w.CreateActor(type, new TypeDictionary
					{
						new OwnerInit(self.Owner),
						new FactionInit(self.Owner.Faction.InternalName),
						new LocationInit(self.Location),
						new CenterPositionInit(self.CenterPosition),
						new FacingInit(Facing),
					});
					a.QueueActivity(false, new DeckLanding(a, self, instant: true));
				}
			});
		}

		void SinkParked(AttackInfo e)
		{
			for (var i = 0; i < holders.Length; i++)
			{
				var a = holders[i];
				if (a != null && onDeck[i] && !a.IsDead)
					a.Kill(e?.Attacker ?? self);
				holders[i] = null;
				onDeck[i] = false;
			}
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e) { SinkParked(e); }

		void INotifyActorDisposing.Disposing(Actor self)
		{
			for (var i = 0; i < holders.Length; i++)
				holders[i] = null;
		}

		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			// Whatever is parked stays with its own side: it lifts off.
			for (var i = 0; i < holders.Length; i++)
				holders[i]?.CancelActivity();
		}
	}

	[Desc("This aircraft can land on ships with MobileAirbase (right-click the ship).")]
	public class DeckLanderInfo : TraitInfo, Requires<AircraftInfo>
	{
		public readonly string Cursor = "enter";
		public readonly string BlockedCursor = "enter-blocked";

		[VoiceReference]
		public readonly string Voice = "Action";

		public override object Create(ActorInitializer init) { return new DeckLander(); }
	}

	public class DeckLander : IIssueOrder, IResolveOrder, IOrderVoice, IRenderModifier
	{
		internal const string OrderID = "LandOnDeck";

		IEnumerable<IOrderTargeter> IIssueOrder.Orders
		{
			get { yield return new DeckTargeter(); }
		}

		sealed class DeckTargeter : UnitOrderTargeter
		{
			public DeckTargeter()
				: base(DeckLander.OrderID, 7, "enter", false, true) { }

			public override bool CanTargetActor(Actor self, Actor target, TargetModifiers modifiers, ref string cursor)
			{
				if (modifiers.HasModifier(TargetModifiers.ForceMove) || !self.Owner.IsAlliedWith(target.Owner)
					|| target.TraitOrDefault<MobileAirbase>() is not MobileAirbase m || !m.Accepts(self))
					return false;
				cursor = m.HasRoomFor(self) ? "enter" : "enter-blocked";
				return true;
			}

			public override bool CanTargetFrozenActor(Actor self, FrozenActor target, TargetModifiers modifiers, ref string cursor) => false;
		}

		Order IIssueOrder.IssueOrder(Actor self, IOrderTargeter order, in Target target, bool queued)
		{
			return order.OrderID == OrderID ? new Order(OrderID, self, target, queued) : null;
		}

		string IOrderVoice.VoicePhraseForOrder(Actor self, Order order) => order.OrderString == OrderID ? "Action" : null;

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString != OrderID || order.Target.Type != TargetType.Actor)
				return;
			var ship = order.Target.Actor;
			var airbase = ship.TraitOrDefault<MobileAirbase>();
			if (airbase == null || !airbase.Accepts(self) || !self.Owner.IsAlliedWith(ship.Owner))
				return;

			self.QueueActivity(order.Queued, new DeckLanding(self, ship));
			self.ShowTargetLines();
		}

		// Parked on a deck: drawn at deck height and on top of the ship.
		IEnumerable<IRenderable> IRenderModifier.ModifyRender(Actor self, WorldRenderer wr, IEnumerable<IRenderable> r)
		{
			if (self.CurrentActivity is DeckLanding d && d.DeckOffset > 0)
				return r.Select(x => x.OffsetBy(new WVec(0, 0, d.DeckOffset)).WithZOffset(x.ZOffset + 1024));
			return r;
		}

		IEnumerable<Rectangle> IRenderModifier.ModifyScreenBounds(Actor self, WorldRenderer wr, IEnumerable<Rectangle> r) => r;
	}

	// Fly to the ship, set down on a free spot, then stay parked (riding along) until given another order.
	public class DeckLanding : Activity
	{
		readonly Actor ship;
		readonly Aircraft aircraft;
		readonly MobileAirbase airbase;
		readonly bool instant;
		int spot = -1, ticks;
		bool parked;

		/// <summary>How far up (world units) to draw the aircraft so it sits on the deck.</summary>
		public int DeckOffset { get; private set; }

		public DeckLanding(Actor self, Actor ship, bool instant = false)
		{
			this.ship = ship;
			this.instant = instant;
			aircraft = self.Trait<Aircraft>();
			airbase = ship.TraitOrDefault<MobileAirbase>();
		}

		protected override void OnFirstRun(Actor self)
		{
			spot = airbase?.Reserve(self) ?? -1;
		}

		bool allied = true;

		bool ShipGone => ship.IsDead || !ship.IsInWorld || airbase == null || !allied;

		public override bool Tick(Actor self)
		{
			allied = self.Owner.IsAlliedWith(ship.Owner);
			if (IsCanceling || spot < 0 || ShipGone)
			{
				Leave(self);
				return true;
			}

			var target = airbase.SpotPosition(spot);
			var pos = self.CenterPosition;
			var flat = new WVec(target.X - pos.X, target.Y - pos.Y, 0);
			ticks++;

			if (instant)
				parked = true;

			if (!parked)
			{
				var speed = System.Math.Max(40, aircraft.MovementSpeed);
				var dist = flat.HorizontalLength;
				if (dist > speed)
				{
					// Fly toward the spot at cruise height (climbing if we started low).
					var climb = System.Math.Min(aircraft.Info.CruiseAltitude.Length, pos.Z + 48) - pos.Z;
					aircraft.Facing = flat.Yaw;
					aircraft.SetPosition(self, pos + flat * speed / dist + new WVec(0, 0, climb));
					return false;
				}

				// Over the spot: come straight down onto the deck.
				var down = System.Math.Max(target.Z, pos.Z - 64);
				aircraft.Facing = OpenRA.Mods.Common.Util.TickFacing(aircraft.Facing, airbase.Facing, new WAngle(24));
				aircraft.SetPosition(self, new WPos(target.X, target.Y, down));
				DeckOffset = airbase.Info.DeckHeight;
				if (down > target.Z)
					return false;

				parked = true;
			}

			// Parked: ride along with the ship, nose with its heading.
			airbase.SetOnDeck(spot, true);
			DeckOffset = airbase.Info.DeckHeight;
			aircraft.Facing = airbase.Facing;
			if (self.CenterPosition != target)
				aircraft.SetPosition(self, target);

			// Rearm and repair.
			if (ticks % System.Math.Max(1, airbase.Info.RearmInterval) == 0)
				foreach (var pool in self.TraitsImplementing<AmmoPool>())
					if (!pool.HasFullAmmo)
					{
						pool.GiveAmmo(self, 1);
						break;
					}

			if (ticks % 25 == 0 && self.TraitOrDefault<IHealth>() is IHealth h && h.HP < h.MaxHP)
				self.InflictDamage(ship, new Damage(-System.Math.Max(1, h.MaxHP * airbase.Info.RepairPercent / 100)));

			return false;
		}

		void Leave(Actor self)
		{
			airbase?.Release(self);
			DeckOffset = 0;

			// Lifting off from the deck: start at deck height so it doesn't pop down to the water first.
			if (parked && !self.IsDead && self.IsInWorld && !ShipGone)
				aircraft.SetPosition(self, self.CenterPosition + new WVec(0, 0, airbase.Info.DeckHeight));
		}

		protected override void OnLastRun(Actor self)
		{
			airbase?.Release(self);
			DeckOffset = 0;
		}

		public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self)
		{
			if (!parked && !ship.IsDead && ship.IsInWorld)
				yield return new TargetLineNode(Target.FromActor(ship), Color.Green);
		}
	}
}
