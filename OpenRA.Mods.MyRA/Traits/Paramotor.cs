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
using OpenRA.Activities;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.Orders;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// ================================================================================================
	// Paramotors: the Airfield builds them and leaves them on the ground beside it. Any ground unit can pick one up
	// (select it, right-click the paramotor), then ctrl+right-click anywhere to take off and buzz over land and
	// water to it, two-stroke screaming all the way, and land. One flight per paramotor.
	// ================================================================================================
	[Desc("A paramotor waiting on the ground to be picked up.")]
	public class ParamotorItemInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new ParamotorItem(); }
	}

	public class ParamotorItem { }

	[Desc("Production that leaves the item on open ground beside the building (paramotors at the Airfield).")]
	public class GroundItemProductionInfo : ProductionInfo
	{
		public override object Create(ActorInitializer init) { return new GroundItemProduction(init, this); }
	}

	public class GroundItemProduction : Production
	{
		public GroundItemProduction(ActorInitializer init, GroundItemProductionInfo info)
			: base(init, info) { }

		public override bool Produce(Actor self, ActorInfo producee, string productionType, TypeDictionary inits, int refundableValue)
		{
			if (IsTraitDisabled || IsTraitPaused || !producee.HasTraitInfo<ParamotorItemInfo>())
				return false;

			var w = self.World;
			var map = w.Map;
			var taken = w.ActorsHavingTrait<ParamotorItem>().Select(a => a.Location).ToHashSet();
			var cell = map.FindTilesInAnnulus(self.Location, 2, 7)
				.Where(c => map.Contains(c) && !taken.Contains(c) && !w.ActorMap.GetActorsAt(c).Any()
					&& map.GetTerrainInfo(c).Type is "Clear" or "Road" or "Rough")
				.Cast<CPos?>().FirstOrDefault();
			if (cell == null)
				return false;

			var owner = self.Owner;
			w.AddFrameEndTask(ww => ww.CreateActor(producee.Name, [new LocationInit(cell.Value), new OwnerInit(owner)]));
			return true;
		}
	}

	[Desc("This unit can pick up a paramotor and fly with it. Attach to ground units.")]
	public class ParamotorPilotInfo : TraitInfo
	{
		[Desc("Flying speed.")]
		public readonly int Speed = 75;

		[Desc("Cruising height.")]
		public readonly WDist Altitude = WDist.FromCells(2);

		[Desc("Engine sound, looped while flying.")]
		public readonly string EngineSound = "moster185.wav";

		public readonly string Image = "paramotor";

		[SequenceReference(nameof(Image))]
		public readonly string WingSequence = "wing";

		[PaletteReference]
		public readonly string Palette = "effect";

		[GrantedConditionReference]
		[Desc("Granted while carrying a paramotor.")]
		public readonly string Condition = null;

		[GrantedConditionReference]
		[Desc("Granted while flying.")]
		public readonly string FlyingCondition = null;

		[ActorReference]
		[Desc("Left on the ground where the pilot lands, ready to be picked up again.")]
		public readonly string ItemActor = "paramotor";

		public override object Create(ActorInitializer init) { return new ParamotorPilot(init.Self, this); }
	}

	public class ParamotorPilot : IIssueOrder, IResolveOrder, IOrderVoice, ITick, IRender, IRenderAnnotationsWhenSelected
	{
		const string PickupOrder = "PickUpParamotor";
		const string FlyOrder = "Paramotor";

		public readonly ParamotorPilotInfo Info;
		readonly Animation wing;
		Actor pickup;
		UnitStance stance;
		int token = Actor.InvalidConditionToken;
		int flyingToken = Actor.InvalidConditionToken;

		public ParamotorPilot(Actor self, ParamotorPilotInfo info)
		{
			Info = info;
			wing = new Animation(self.World, info.Image);
			wing.PlayRepeating(info.WingSequence);
		}

		public bool Has { get; private set; }
		public bool Flying { get; private set; }

		IEnumerable<IOrderTargeter> IIssueOrder.Orders
		{
			get
			{
				yield return new PickupTargeter(this);
				yield return new FlyTargeter(this);
			}
		}

		Order IIssueOrder.IssueOrder(Actor self, IOrderTargeter order, in Target target, bool queued)
		{
			return order.OrderID is PickupOrder or FlyOrder ? new Order(order.OrderID, self, target, queued) : null;
		}

		string IOrderVoice.VoicePhraseForOrder(Actor self, Order order) => order.OrderString is PickupOrder or FlyOrder ? self.TraitOrDefault<Mobile>()?.Info.Voice : null;

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (Flying)
				return;

			if (order.OrderString == PickupOrder)
			{
				if (Has || !self.Info.HasTraitInfo<WithInfantryBodyInfo>() || order.Target.Type != TargetType.Actor || !order.Target.Actor.Info.HasTraitInfo<ParamotorItemInfo>()
					|| order.Target.Actor.Owner != self.Owner)
					return;

				pickup = order.Target.Actor;
				if (!order.Queued)
					self.CancelActivity();

				self.QueueActivity(self.Trait<IMove>().MoveWithinRange(order.Target, WDist.FromCells(1)));
				self.ShowTargetLines();
				return;
			}

			if (order.OrderString == FlyOrder)
			{
				if (!Has || order.Target.Type == TargetType.Invalid)
					return;

				if (!order.Queued)
					self.CancelActivity();

				self.QueueActivity(new ParamotorFlight(self, this, order.Target.CenterPosition));
				self.ShowTargetLines();
				return;
			}

			if (!order.Queued && order.OrderString is "Move" or "Stop" or "Attack" or "AttackMove")
				pickup = null;
		}

		void ITick.Tick(Actor self)
		{
			wing.Tick();
			if (Has && !Flying && self.CurrentActivity != null && self.CurrentActivity is not ParamotorFlight)
				self.CancelActivity();

			if (pickup == null || !self.IsInWorld)
				return;

			if (pickup.IsDead || pickup.Disposed || !pickup.IsInWorld || Has)
				pickup = null;
			else if ((pickup.CenterPosition - self.CenterPosition).HorizontalLengthSquared <= WDist.FromCells(1).LengthSquared + 512 * 512)
			{
				pickup.Dispose();
				pickup = null;
				Has = true;
				self.CancelActivity();
				if (Info.Condition != null)
					token = self.GrantCondition(Info.Condition);

				// Strapped in: hands full, no shooting until the paramotor is left behind.
				var autoTarget = self.TraitOrDefault<AutoTarget>();
				if (autoTarget != null)
				{
					stance = autoTarget.Stance;
					autoTarget.SetStance(self, UnitStance.HoldFire);
				}
			}
		}

		internal void TakeOff(Actor self)
		{
			if (Flying)
				return;

			Flying = true;
			if (Info.FlyingCondition != null)
				flyingToken = self.GrantCondition(Info.FlyingCondition);
		}

		// Landed: the paramotor stays on the ground where it came down, and the pilot is free to fight again.
		internal void Landed(Actor self)
		{
			if (!Has)
				return;

			Has = Flying = false;
			var autoTarget = self.TraitOrDefault<AutoTarget>();
			autoTarget?.SetStance(self, stance);

			if (self.IsInWorld && !self.IsDead)
			{
				var cell = self.Location;
				var owner = self.Owner;
				self.World.AddFrameEndTask(w => w.CreateActor(Info.ItemActor, [new LocationInit(cell), new OwnerInit(owner)]));
			}

			if (flyingToken != Actor.InvalidConditionToken)
				flyingToken = self.RevokeCondition(flyingToken);

			if (token != Actor.InvalidConditionToken)
				token = self.RevokeCondition(token);
		}

		IEnumerable<IRenderable> IRender.Render(Actor self, WorldRenderer wr)
		{
			if (!Flying)
				return [];

			// The canopy overhead.
			return wing.Render(self.CenterPosition, new WVec(0, 0, 700), 0, wr.Palette(Info.Palette));
		}

		IEnumerable<Rectangle> IRender.ScreenBounds(Actor self, WorldRenderer wr) { yield break; }

		IEnumerable<IRenderable> IRenderAnnotationsWhenSelected.RenderAnnotations(Actor self, WorldRenderer wr)
		{
			if (!Has || Flying || !self.IsInWorld || self.Owner != wr.World.LocalPlayer)
				yield break;

			var font = Game.Renderer.Fonts["Bold"];
			var color = (self.World.WorldTick / 6) % 2 == 0 ? Color.Yellow : Color.Orange;
			yield return new TextAnnotationRenderable(font, self.CenterPosition + new WVec(0, -1200, 0), 0, color,
				"PARAMOTOR: RIGHT-CLICK TO FLY THERE");
		}

		bool IRenderAnnotationsWhenSelected.SpatiallyPartitionable => false;

		sealed class PickupTargeter : IOrderTargeter
		{
			readonly ParamotorPilot pilot;

			public PickupTargeter(ParamotorPilot pilot) { this.pilot = pilot; }

			public string OrderID => PickupOrder;
			public int OrderPriority => 7;
			public bool IsQueued { get; private set; }
			public bool TargetOverridesSelection(Actor self, in Target target, List<Actor> actorsAt, CPos xy, TargetModifiers modifiers) => true;

			public bool CanTarget(Actor self, in Target target, ref TargetModifiers modifiers, ref string cursor)
			{
				if (pilot.Has || !self.Info.HasTraitInfo<WithInfantryBodyInfo>() || target.Type != TargetType.Actor || target.Actor.Owner != self.Owner
					|| !target.Actor.Info.HasTraitInfo<ParamotorItemInfo>())
					return false;

				IsQueued = modifiers.HasModifier(TargetModifiers.ForceQueue);
				cursor = "enter";
				return true;
			}
		}

		sealed class FlyTargeter : IOrderTargeter
		{
			readonly ParamotorPilot pilot;

			public FlyTargeter(ParamotorPilot pilot) { this.pilot = pilot; }

			public string OrderID => FlyOrder;
			public int OrderPriority => 100;
			public bool IsQueued { get; private set; }
			public bool TargetOverridesSelection(Actor self, in Target target, List<Actor> actorsAt, CPos xy, TargetModifiers modifiers) => true;

			public bool CanTarget(Actor self, in Target target, ref TargetModifiers modifiers, ref string cursor)
			{
				if (!pilot.Has || pilot.Flying || target.Type == TargetType.Invalid)
					return false;

				IsQueued = modifiers.HasModifier(TargetModifiers.ForceQueue);
				cursor = "move";
				return true;
			}
		}
	}

	// Take off, buzz straight there at cruising height (over water too), then come down on the nearest open ground.
	public class ParamotorFlight : Activity
	{
		readonly ParamotorPilot pilot;
		readonly Mobile mobile;
		WPos destination;
		ISound engine;
		int height;

		// Where the paramotor is, kept here: Mobile.SetPosition snaps to the cell centre, which would pin it in place.
		int x, y;
		bool landingPicked;

		public ParamotorFlight(Actor self, ParamotorPilot pilot, WPos destination)
		{
			this.pilot = pilot;
			mobile = self.Trait<Mobile>();
			this.destination = destination;
			IsInterruptible = false;
		}

		protected override void OnFirstRun(Actor self)
		{
			x = self.CenterPosition.X;
			y = self.CenterPosition.Y;
			pilot.TakeOff(self);
			engine = Game.Sound.PlayLooped(SoundType.World, pilot.Info.EngineSound, self.CenterPosition);
		}

		public override bool Tick(Actor self)
		{
			var map = self.World.Map;
			var to = new WVec(destination.X - x, destination.Y - y, 0);
			var left = to.HorizontalLength;
			var cruise = pilot.Info.Altitude.Length;

			// Climb out, cruise, and glide down over the last few cells.
			var wanted = Math.Min(cruise, left * cruise / 3072);
			height += Math.Clamp(wanted - height, -40, 40);

			var step = Math.Min(left, pilot.Info.Speed);
			if (left > 0)
			{
				x += to.X * step / left;
				y += to.Y * step / left;
			}

			var cell = map.CellContaining(new WPos(x, y, 0));
			if (map.Contains(cell))
				mobile.SetLocation(cell, mobile.FromSubCell, cell, mobile.FromSubCell);

			mobile.SetCenterPosition(self, new WPos(x, y, map.CenterOfCell(cell).Z + height));
			Game.Sound.SetPosition(engine, self.CenterPosition);

			if (left > 64 || height > 64)
				return false;

			// Down: stand in the nearest open cell, gliding on to it if it is not right here.
			var spot = map.FindTilesInCircle(map.CellContaining(destination), 20)
				.Where(c => map.Contains(c) && mobile.CanEnterCell(c, self))
				.Cast<CPos?>().FirstOrDefault();
			if (spot == null)
			{
				Game.Sound.StopSound(engine);
				pilot.Landed(self);
				self.Kill(self);
				return true;
			}

			var at = map.CenterOfCell(spot.Value);
			if (!landingPicked && (at - new WPos(x, y, at.Z)).HorizontalLengthSquared > 724 * 724)
			{
				landingPicked = true;
				destination = at;
				return false;
			}

			mobile.SetPosition(self, spot.Value);
			return true;
		}

		protected override void OnLastRun(Actor self)
		{
			Game.Sound.StopSound(engine);
			pilot.Landed(self);
		}

		protected override void OnActorDispose(Actor self)
		{
			Game.Sound.StopSound(engine);
		}
	}
}
