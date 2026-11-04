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
using System.Numerics;
using OpenRA.Effects;
using OpenRA.GameRules;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.Orders;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// ================================================================================================
	// The nuclear program. Nukes are hard to get:
	//   1. Build a Nuclear Reactor.  2. Build a Nuclear Lab.  3. Enrich uranium at the lab (15 minutes).
	//   4. Build a test nuke. It comes out as a drum beside the lab; any vehicle can pick it up, drive it somewhere and
	//      set it off (deploy). That completes the test: a small blast nobody far away sees, a faint rumble felt across
	//      the whole map, and a bang heard by any enemy close by.
	//   5. Now tactical nukes (three times the test blast): carried by any vehicle, dropped by any plane, or fired from
	//      artillery. Instead of using one you can upgrade it at the lab to a medium nuke (twice the tactical blast;
	//      vehicles and planes only), and that to a hydrogen bomb (the old silo nuke), which only an ICBM or a B-52
	//      can deliver. The ICBM silo takes up to three warheads of any kind and fires them all at once.
	// The drums, upgrades and progress markers are built from the lab's production list; this file does the rest.
	// ================================================================================================

	// Delivery methods a warhead allows, and that a carrier offers.
	public static class NukeDelivery
	{
		public const string Vehicle = "Vehicle";
		public const string Aircraft = "Aircraft";
		public const string Cannon = "Cannon";
		public const string HeavyBomber = "HeavyBomber";
		public const string Icbm = "Icbm";
	}

	[Desc("Invisible actor that marks a step of the nuclear program as done (give it a ProvidesPrerequisite).")]
	public class NukeMarkerInfo : TraitInfo<NukeMarker> { }

	public class NukeMarker { }

	[Desc("A nuclear warhead in a drum, waiting to be picked up by a carrier or loaded into an ICBM silo.")]
	public class NukePackageInfo : TraitInfo, IRulesetLoaded
	{
		[WeaponReference]
		[FieldLoader.Require]
		[Desc("What it does when it goes off (vehicle detonation, aircraft drop, ICBM).")]
		public readonly string Weapon = null;

		[WeaponReference]
		[Desc("Shell fired from artillery, if it can go in a cannon.")]
		public readonly string ShellWeapon = null;

		[Desc("Ways it can be delivered: Vehicle, Aircraft, Cannon, HeavyBomber, Icbm.")]
		public readonly ImmutableArray<string> Delivery = [];

		[Desc("Setting this one off completes the nuclear test.")]
		public readonly bool IsTest = false;

		[ActorReference]
		[Desc("Marker actor created for the owner when the test goes off.")]
		public readonly string TestDoneMarker = "nuketested";

		[ActorReference]
		[Desc("Building this consumes one of these drums (an upgrade); it appears where that drum was.")]
		public readonly string UpgradesFrom = null;

		[Desc("Name shown in messages.")]
		public readonly string Name = "nuke";

		[Desc("Enriched uranium (whole units) it takes to build.")]
		public readonly int Uranium = 0;

		[Desc("Enemies with anything within this range hear it go off (it isn't heard further away).")]
		public readonly WDist HeardRange = WDist.FromCells(25);

		[Desc("Sound heard by those enemies.")]
		public readonly string HeardSound = "kaboom1.aud";

		public WeaponInfo WeaponInfo { get; private set; }
		public WeaponInfo ShellWeaponInfo { get; private set; }

		public void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			WeaponInfo = rules.Weapons[Weapon.ToLowerInvariant()];
			if (ShellWeapon != null)
				ShellWeaponInfo = rules.Weapons[ShellWeapon.ToLowerInvariant()];
		}

		public override object Create(ActorInitializer init) { return new NukePackage(this); }
	}

	public class NukePackage : IIssueOrder, IResolveOrder
	{
		public const string LoadSiloOrder = "LoadIcbm";
		public readonly NukePackageInfo Info;

		public NukePackage(NukePackageInfo info) { Info = info; }

		IEnumerable<IOrderTargeter> IIssueOrder.Orders
		{
			get
			{
				if (Info.Delivery.Contains(NukeDelivery.Icbm))
					yield return new SiloTargeter();
			}
		}

		Order IIssueOrder.IssueOrder(Actor self, IOrderTargeter order, in Target target, bool queued)
		{
			return order.OrderID == LoadSiloOrder ? new Order(LoadSiloOrder, self, target, queued) : null;
		}

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString != LoadSiloOrder || order.Target.Type != TargetType.Actor)
				return;

			var silo = order.Target.Actor;
			var icbm = silo.TraitOrDefault<IcbmSilo>();
			if (icbm == null || silo.Owner != self.Owner || silo.IsDead || !icbm.Load(silo, self.Info.Name, Info))
				return;

			self.Dispose();
		}

		// Select the drum, right-click your own ICBM silo to load it.
		sealed class SiloTargeter : IOrderTargeter
		{
			public string OrderID => LoadSiloOrder;
			public int OrderPriority => 6;
			public bool IsQueued => false;
			public bool TargetOverridesSelection(Actor self, in Target target, List<Actor> actorsAt, CPos xy, TargetModifiers modifiers) => true;

			public bool CanTarget(Actor self, in Target target, ref TargetModifiers modifiers, ref string cursor)
			{
				if (target.Type != TargetType.Actor || target.Actor.Owner != self.Owner)
					return false;

				var silo = target.Actor.TraitOrDefault<IcbmSilo>();
				if (silo == null || silo.Full)
					return false;

				// (Full also covers "no missile in the silo yet": buy one first.)

				cursor = "enter";
				return true;
			}
		}
	}

	// Setting a warhead off: the blast, a faint rumble all over the map, a bang for enemies close enough to hear it,
	// and (for the test) the next step of the program.
	public static class NukeDetonation
	{
		public static void Detonate(World world, Player owner, Actor source, WPos pos, NukePackageInfo info)
		{
			var weapon = info.WeaponInfo;
			var target = Target.FromPos(new WPos(pos.X, pos.Y, 0));
			weapon.Impact(target, new WarheadArgs { Weapon = weapon, Source = pos, SourceActor = source, WeaponTarget = target });
			AfterBlast(world, owner, pos, info);
		}

		public static void AfterBlast(World world, Player owner, WPos pos, NukePackageInfo info)
		{
			world.AddFrameEndTask(w =>
			{
				w.Add(new Rumble(60));
				var local = w.LocalPlayer;
				var range = info.HeardRange.LengthSquared;
				if (local != null && local != owner && local.RelationshipWith(owner) == PlayerRelationship.Enemy
					&& w.Actors.Any(a => a.Owner == local && !a.IsDead && a.IsInWorld && (a.CenterPosition - pos).HorizontalLengthSquared <= range))
					Game.Sound.Play(SoundType.UI, info.HeardSound);

				if (!info.IsTest)
					return;

				if (!w.Actors.Any(a => a.Owner == owner && a.Info.Name == info.TestDoneMarker && !a.IsDead))
				{
					w.CreateActor(info.TestDoneMarker, [new OwnerInit(owner)]);
					if (local == owner)
						TextNotificationsManager.AddSystemLine("Nuclear Lab", "Nuclear test successful. Tactical nukes can now be built.");
				}
			});
		}
	}

	// The barely-noticeable rumble of a far-off nuclear test, felt wherever each player is looking.
	sealed class Rumble : IEffect
	{
		int ticks;
		bool started;

		public Rumble(int ticks) { this.ticks = ticks; }

		public void Tick(World world)
		{
			if (--ticks <= 0)
				world.AddFrameEndTask(w => w.Remove(this));
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr)
		{
			// Rendering is per player, so this is the one place that knows where each player is looking.
			if (!started)
			{
				started = true;
				var c = wr.Viewport.CenterPosition;
				wr.World.WorldActor.TraitOrDefault<ScreenShaker>()?.AddEffect(ticks, c + new WVec(10240, 0, 0), 1);
			}

			yield break;
		}
	}

	// ================================================================================================
	// The Nuclear Lab's production: progress markers appear quietly, new drums are set down beside the lab, and an
	// upgrade replaces the drum it upgrades.
	// ================================================================================================
	[Desc("Production for the Nuclear Lab: makes NukeMarker and NukePackage actors.")]
	public class NuclearLabProductionInfo : ProductionInfo
	{
		public override object Create(ActorInitializer init) { return new NuclearLabProduction(init, this); }
	}

	public class NuclearLabProduction : Production
	{
		public NuclearLabProduction(ActorInitializer init, NuclearLabProductionInfo info)
			: base(init, info) { }

		public override bool Produce(Actor self, ActorInfo producee, string productionType, TypeDictionary inits, int refundableValue)
		{
			if (IsTraitDisabled || IsTraitPaused)
				return false;

			var w = self.World;
			var owner = self.Owner;
			if (producee.HasTraitInfo<NukeMarkerInfo>())
			{
				w.AddFrameEndTask(ww =>
				{
					ww.CreateActor(producee.Name, [new OwnerInit(owner)]);
					if (ww.LocalPlayer == owner)
						TextNotificationsManager.AddSystemLine("Nuclear Lab", producee.Name == "uranium"
							? "Uranium enrichment complete. A test nuke can now be built."
							: "Nuclear program step complete.");
				});
				return true;
			}

			var pkg = producee.TraitInfoOrDefault<NukePackageInfo>();
			if (pkg == null)
				return false;

			// Paid for in enriched uranium; it waits here until there is enough.
			if (pkg.Uranium > 0 && !UraniumEnricher.Consume(owner, pkg.Uranium * 1000))
				return false;

			// An upgrade takes the place of the drum it upgrades.
			CPos? cell = null;
			if (pkg.UpgradesFrom != null)
			{
				var old = w.Actors.Where(a => a.Owner == owner && !a.IsDead && a.IsInWorld && a.Info.Name == pkg.UpgradesFrom)
					.MinByOrDefault(a => (a.CenterPosition - self.CenterPosition).LengthSquared);
				if (old == null)
					return false;

				cell = old.Location;
				old.Dispose();
			}

			cell ??= FreeCellNear(self);
			if (cell == null)
				return false;

			w.AddFrameEndTask(ww =>
			{
				ww.CreateActor(producee.Name, [new LocationInit(cell.Value), new OwnerInit(owner)]);
				if (ww.LocalPlayer == owner)
					TextNotificationsManager.AddSystemLine("Nuclear Lab", $"{pkg.Name} ready beside the Nuclear Lab.");
			});
			return true;
		}

		static CPos? FreeCellNear(Actor self)
		{
			var w = self.World;
			var map = w.Map;
			var taken = w.ActorsHavingTrait<NukePackage>().Select(a => a.Location).ToHashSet();
			return map.FindTilesInAnnulus(self.Location, 2, 6)
				.Where(c => map.Contains(c) && !taken.Contains(c) && !w.ActorMap.GetActorsAt(c).Any()
					&& map.GetTerrainInfo(c).Type is "Clear" or "Road" or "Rough")
				.Cast<CPos?>().FirstOrDefault();
		}
	}

	// ================================================================================================
	// Carriers: vehicles, planes, artillery and the B-52 can carry one warhead each.
	//   Pick up: select the carrier, right-click the drum.
	//   Vehicles: deploy (F) to set it off where they stand, or ctrl+right-click a spot or enemy to drive there and
	//   set it off on arrival.
	//   Planes and the B-52: attack or right-click a target (or force-fire the ground) to fly there and drop it.
	//   Artillery: right-click a target to fire the shell from range.
	// A carrier that is destroyed loses its warhead (they don't go off by accident).
	// ================================================================================================
	[Desc("Can carry one nuclear warhead (see NukePackage) and deliver it.")]
	public class NukeCarrierInfo : TraitInfo
	{
		[Desc("How this unit can deliver: Vehicle, Aircraft, Cannon, HeavyBomber.")]
		public readonly ImmutableArray<string> Classes = [];

		[GrantedConditionReference]
		[Desc("Condition granted while a warhead is aboard.")]
		public readonly string Condition = "nuke-armed";

		[Desc("Range artillery fires its nuclear shell from.")]
		public readonly WDist CannonRange = WDist.FromCells(14);

		public override object Create(ActorInitializer init) { return new NukeCarrier(this); }
	}

	public class NukeCarrier : IIssueOrder, IResolveOrder, IIssueDeployOrder, IOrderVoice, ITick, INotifyKilled, IRenderAnnotationsWhenSelected
	{
		const string LoadOrder = "LoadNuke";
		const string DetonateOrder = "DetonateNuke";
		const string DropOrder = "DropNuke";
		const string FireOrder = "FireNuke";
		const string DriveOrder = "DriveNuke";

		readonly NukeCarrierInfo info;
		NukePackageInfo warhead;
		int token = Actor.InvalidConditionToken;
		Actor pickup;
		WPos? drop;
		Target driveTo = Target.Invalid;
		Target fireAt = Target.Invalid;

		public NukeCarrier(NukeCarrierInfo info) { this.info = info; }

		public bool Armed => warhead != null;

		bool CanTake(NukePackageInfo p) => !Armed && p.Delivery.Any(d => info.Classes.Contains(d));

		bool Has(string cls) => warhead != null && warhead.Delivery.Contains(cls) && info.Classes.Contains(cls);

		IEnumerable<IOrderTargeter> IIssueOrder.Orders
		{
			get
			{
				yield return new PickupTargeter(this);
				yield return new DeliverTargeter(this, DropOrder, NukeDelivery.Aircraft, NukeDelivery.HeavyBomber);
				yield return new DeliverTargeter(this, FireOrder, NukeDelivery.Cannon);

				// Vehicles: ctrl+right-click a spot or an enemy to drive there and set it off.
				yield return new DeliverTargeter(this, DriveOrder, NukeDelivery.Vehicle) { ForceOnly = true };
			}
		}

		Order IIssueOrder.IssueOrder(Actor self, IOrderTargeter order, in Target target, bool queued)
		{
			return order.OrderID is LoadOrder or DropOrder or FireOrder or DriveOrder ? new Order(order.OrderID, self, target, queued) : null;
		}

		Order IIssueDeployOrder.IssueDeployOrder(Actor self, bool queued) => new(DetonateOrder, self, queued);

		bool IIssueDeployOrder.CanIssueDeployOrder(Actor self, bool queued) => Has(NukeDelivery.Vehicle);

		string IOrderVoice.VoicePhraseForOrder(Actor self, Order order) => null;

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			switch (order.OrderString)
			{
				case LoadOrder:
				{
					if (order.Target.Type != TargetType.Actor)
						return;

					var p = order.Target.Actor.TraitOrDefault<NukePackage>();
					if (p == null || order.Target.Actor.Owner != self.Owner || !CanTake(p.Info))
						return;

					pickup = order.Target.Actor;
					GoTo(self, order.Target, WDist.FromCells(1), order.Queued);
					return;
				}

				case DetonateOrder:
					if (Has(NukeDelivery.Vehicle))
					{
						var w = Unload(self);
						NukeDetonation.Detonate(self.World, self.Owner, self, self.CenterPosition, w);
						if (!self.IsDead)
							self.Kill(self);
					}

					return;

				case DropOrder:
					if (!Has(NukeDelivery.Aircraft) && !Has(NukeDelivery.HeavyBomber))
						return;

					drop = order.Target.CenterPosition;
					GoTo(self, order.Target, WDist.FromCells(1), order.Queued);
					return;

				case DriveOrder:
					if (!Has(NukeDelivery.Vehicle))
						return;

					driveTo = order.Target;
					GoTo(self, order.Target, WDist.FromCells(1), order.Queued);
					return;

				case FireOrder:
					if (!Has(NukeDelivery.Cannon))
						return;

					fireAt = order.Target;
					GoTo(self, order.Target, info.CannonRange, order.Queued);
					return;

				// A plane carrying a nuke told to attack something drops the nuke on it.
				case "Attack" or "ForceAttack" when order.Target.Type != TargetType.Invalid
					&& (Has(NukeDelivery.Aircraft) || Has(NukeDelivery.HeavyBomber)):
				{
					drop = order.Target.CenterPosition;
					var target = order.Target;
					var queued = order.Queued;

					// After its own attack logic has queued its usual run, replace that with the drop.
					self.World.AddFrameEndTask(_ =>
					{
						if (!self.IsDead && drop != null)
							GoTo(self, target, WDist.FromCells(1), queued);
					});
					return;
				}

				// Any other order calls off a pending pickup or delivery.
				default:
					if (!order.Queued && order.OrderString is "Move" or "Stop" or "Attack" or "AttackMove" or "ForceAttack")
					{
						pickup = null;
						drop = null;
						driveTo = Target.Invalid;
						fireAt = Target.Invalid;
					}

					return;
			}
		}

		static void GoTo(Actor self, in Target target, WDist range, bool queued)
		{
			if (!queued)
				self.CancelActivity();

			var aircraft = self.TraitOrDefault<Aircraft>();
			if (aircraft != null)
				self.QueueActivity(new Fly(self, target, WDist.Zero, range));
			else if (self.TraitOrDefault<IMove>() is IMove move)
				self.QueueActivity(move.MoveWithinRange(target, range));

			self.ShowTargetLines();
		}

		void ITick.Tick(Actor self)
		{
			if (!self.IsInWorld)
				return;

			var pos = self.CenterPosition;
			if (pickup != null)
			{
				if (pickup.IsDead || pickup.Disposed || !pickup.IsInWorld)
					pickup = null;
				else if ((pickup.CenterPosition - pos).HorizontalLengthSquared <= WDist.FromCells(1).LengthSquared + 512 * 512)
				{
					warhead = pickup.Trait<NukePackage>().Info;
					token = self.GrantCondition(info.Condition);
					pickup.Dispose();
					pickup = null;
					if (self.World.LocalPlayer == self.Owner)
						TextNotificationsManager.AddSystemLine("Nuclear", $"{(self.Info.TraitInfoOrDefault<TooltipInfo>() is TooltipInfo t ? FluentProvider.GetMessage(t.Name) : self.Info.Name)} is carrying a {warhead.Name}.");
				}
			}

			if (drop != null && Armed && (drop.Value - pos).HorizontalLengthSquared <= WDist.FromCells(1).LengthSquared)
			{
				var w = Unload(self);
				Launch(self, w.WeaponInfo, pos, new WPos(drop.Value.X, drop.Value.Y, 0), w);
				drop = null;
			}

			if (driveTo.Type != TargetType.Invalid && Has(NukeDelivery.Vehicle)
				&& (driveTo.CenterPosition - pos).HorizontalLengthSquared <= WDist.FromCells(1).LengthSquared + 512 * 512)
			{
				driveTo = Target.Invalid;
				var w = Unload(self);
				NukeDetonation.Detonate(self.World, self.Owner, self, pos, w);
				if (!self.IsDead)
					self.Kill(self);

				return;
			}

			if (fireAt.Type != TargetType.Invalid && Armed
				&& (fireAt.CenterPosition - pos).HorizontalLengthSquared <= info.CannonRange.LengthSquared)
			{
				var w = Unload(self);
				var t = fireAt.CenterPosition;
				Launch(self, w.ShellWeaponInfo ?? w.WeaponInfo, pos, new WPos(t.X, t.Y, 0), w);
				fireAt = Target.Invalid;
				self.CancelActivity();
			}
		}

		NukePackageInfo Unload(Actor self)
		{
			var w = warhead;
			warhead = null;
			if (token != Actor.InvalidConditionToken)
				token = self.RevokeCondition(token);

			return w;
		}

		// A bomb falling from a plane or a shell from a gun; the blast and its after-effects when it lands.
		static void Launch(Actor self, WeaponInfo weapon, WPos from, WPos to, NukePackageInfo w)
		{
			var args = new ProjectileArgs
			{
				Weapon = weapon,
				Facing = (to - from).Yaw,
				CurrentMuzzleFacing = () => (to - from).Yaw,
				DamageModifiers = [],
				InaccuracyModifiers = [],
				RangeModifiers = [],
				Source = from,
				CurrentSource = () => from,
				SourceActor = self,
				PassiveTarget = to,
				GuidedTarget = Target.FromPos(to),
			};

			var projectile = weapon.Projectile?.Create(args);
			var world = self.World;
			var owner = self.Owner;
			if (projectile == null)
			{
				NukeDetonation.Detonate(world, owner, self, to, w);
				return;
			}

			world.AddFrameEndTask(ww => ww.Add(projectile));

			// The weapon itself does the damage when the projectile lands; the rumble and the test follow it.
			world.AddFrameEndTask(ww => ww.Add(new WaitForImpact(projectile, () => NukeDetonation.AfterBlast(world, owner, to, w))));
			if (weapon.Report != null && weapon.Report.Length > 0)
				Game.Sound.Play(SoundType.World, weapon.Report, world, from);
		}

		// While selected and armed, a flashing hint says how to use the warhead it carries.
		IEnumerable<IRenderable> IRenderAnnotationsWhenSelected.RenderAnnotations(Actor self, WorldRenderer wr)
		{
			if (!Armed || !self.IsInWorld || self.Owner != wr.World.LocalPlayer)
				yield break;

			var lines = new List<string>();
			if (Has(NukeDelivery.Vehicle))
			{
				lines.Add("PRESS F (DEPLOY) TO DETONATE HERE");
				lines.Add("CTRL + RIGHT-CLICK: DRIVE THERE AND DETONATE");
			}

			if (Has(NukeDelivery.Aircraft) || Has(NukeDelivery.HeavyBomber))
				lines.Add("RIGHT-CLICK A TARGET TO DROP THE NUKE");

			if (Has(NukeDelivery.Cannon))
				lines.Add("RIGHT-CLICK A TARGET TO FIRE THE NUKE");

			if (lines.Count == 0)
				yield break;

			var font = Game.Renderer.Fonts["Bold"];
			var pulse = (self.World.WorldTick / 6) % 2 == 0;
			var color = pulse ? Color.Yellow : Color.Orange;
			for (var i = 0; i < lines.Count; i++)
				yield return new TextAnnotationRenderable(font, self.CenterPosition + new WVec(0, -1200 - 400 * (lines.Count - 1 - i), 0), 0,
					color, lines[i]);
		}

		bool IRenderAnnotationsWhenSelected.SpatiallyPartitionable => false;

		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			if (!Armed)
				return;

			var name = warhead.Name;
			Unload(self);
			if (self.World.LocalPlayer == self.Owner)
				TextNotificationsManager.AddSystemLine("Nuclear", $"A {name} was lost with its carrier.");
		}

		sealed class PickupTargeter : IOrderTargeter
		{
			readonly NukeCarrier carrier;

			public PickupTargeter(NukeCarrier carrier) { this.carrier = carrier; }

			public string OrderID => LoadOrder;
			public int OrderPriority => 7;
			public bool IsQueued { get; private set; }
			public bool TargetOverridesSelection(Actor self, in Target target, List<Actor> actorsAt, CPos xy, TargetModifiers modifiers) => true;

			public bool CanTarget(Actor self, in Target target, ref TargetModifiers modifiers, ref string cursor)
			{
				if (target.Type != TargetType.Actor || target.Actor.Owner != self.Owner)
					return false;

				var p = target.Actor.TraitOrDefault<NukePackage>();
				if (p == null || !carrier.CanTake(p.Info))
					return false;

				IsQueued = modifiers.HasModifier(TargetModifiers.ForceQueue);
				cursor = "enter";
				return true;
			}
		}

		sealed class DeliverTargeter : IOrderTargeter
		{
			readonly NukeCarrier carrier;
			readonly string[] classes;

			public DeliverTargeter(NukeCarrier carrier, string order, params string[] classes)
			{
				this.carrier = carrier;
				OrderID = order;
				this.classes = classes;
			}

			public string OrderID { get; }
			public int OrderPriority => 8;
			public bool ForceOnly { get; init; }
			public bool IsQueued { get; private set; }
			public bool TargetOverridesSelection(Actor self, in Target target, List<Actor> actorsAt, CPos xy, TargetModifiers modifiers) => true;

			public bool CanTarget(Actor self, in Target target, ref TargetModifiers modifiers, ref string cursor)
			{
				if (!classes.Any(carrier.Has))
					return false;

				if (target.Type == TargetType.Terrain)
				{
					if (!modifiers.HasModifier(TargetModifiers.ForceAttack))
						return false;
				}
				else if (target.Type is TargetType.Actor or TargetType.FrozenActor)
				{
					var owner = target.Type == TargetType.Actor ? target.Actor.Owner : target.FrozenActor.Owner;
					if (self.Owner.RelationshipWith(owner) != PlayerRelationship.Enemy && !modifiers.HasModifier(TargetModifiers.ForceAttack))
						return false;

					if (ForceOnly && !modifiers.HasModifier(TargetModifiers.ForceAttack))
						return false;
				}
				else
					return false;

				IsQueued = modifiers.HasModifier(TargetModifiers.ForceQueue);
				cursor = "nuke";
				return true;
			}
		}
	}

	// Runs an action once a projectile has gone (landed).
	sealed class WaitForImpact : IEffect
	{
		readonly IProjectile projectile;
		readonly Action done;

		public WaitForImpact(IProjectile projectile, Action done)
		{
			this.projectile = projectile;
			this.done = done;
		}

		public void Tick(World world)
		{
			if (world.Effects.Contains(projectile))
				return;

			done();
			world.AddFrameEndTask(w => w.Remove(this));
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr) { yield break; }
	}

	// ================================================================================================
	// ICBM silo. Missiles are bought from the Vehicle tab once the silo stands: a Light ICBM (one warhead), an ICBM
	// (three) or a Heavy ICBM (five). Every warhead comes conventional, enough to wreck most buildings; select a nuclear
	// drum and right-click the silo to swap one in. Chaff, also bought, halves the chance of the missile being shot
	// down. Firing asks for a target for each warhead.
	// ================================================================================================
	[Desc("A purchasable ICBM: building it loads a missile into the player's silo.")]
	public class IcbmMissileInfo : TraitInfo<IcbmMissileItem>
	{
		[Desc("Warheads it carries.")]
		public readonly int Warheads = 1;
	}

	public class IcbmMissileItem { }

	[Desc("Chaff for the ICBM in the silo: halves the chance of it being intercepted.")]
	public class IcbmChaffInfo : TraitInfo<IcbmChaffItem> { }

	public class IcbmChaffItem { }

	[Desc("Production for the ICBM silo: loads bought missiles and chaff into it.")]
	public class IcbmSiloProductionInfo : ProductionInfo
	{
		public override object Create(ActorInitializer init) { return new IcbmSiloProduction(init, this); }
	}

	public class IcbmSiloProduction : Production
	{
		public IcbmSiloProduction(ActorInitializer init, IcbmSiloProductionInfo info)
			: base(init, info) { }

		public override bool Produce(Actor self, ActorInfo producee, string productionType, TypeDictionary inits, int refundableValue)
		{
			if (IsTraitDisabled || IsTraitPaused)
				return false;

			var silo = self.TraitOrDefault<IcbmSilo>();
			if (silo == null)
				return false;

			var missile = producee.TraitInfoOrDefault<IcbmMissileInfo>();
			if (missile != null)
				return silo.Arm(self, missile.Warheads, producee.TraitInfoOrDefault<TooltipInfo>()?.Name);

			if (producee.HasTraitInfo<IcbmChaffInfo>())
				return silo.AddChaff(self);

			return false;
		}
	}

	[Desc("Holds an ICBM and its warheads for the ICBM launch power.")]
	public class IcbmSiloInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new IcbmSilo(this); }

		[GrantedConditionReference]
		[Desc("Condition granted while a missile is in the silo.")]
		public readonly string Condition = "icbm-armed";

		[GrantedConditionReference]
		[Desc("Condition granted while the missile in the silo has chaff.")]
		public readonly string ChaffCondition = "icbm-chaff";

		[GrantedConditionReference]
		[Desc("Condition granted while the silo has no missile (so another can be bought).")]
		public readonly string EmptyCondition = "icbm-empty";

		[GrantedConditionReference]
		[Desc("Condition granted while the missile in the silo has no chaff yet.")]
		public readonly string NeedsChaffCondition = "icbm-nochaff";

		[Desc("Biggest missile, for the load conditions below.")]
		public readonly int MaxWarheads = 5;

		[Desc("Condition for the exact load: LoadConditionPrefix + warheads + \"-\" + nuclear warheads, e.g. icbm-3-1.",
			"Each load has its own launch button (with its own icon).")]
		public readonly string LoadConditionPrefix = "icbm-";

		[GrantedConditionReference]
		public IEnumerable<string> LinterLoadConditions =>
			Enumerable.Range(1, MaxWarheads).SelectMany(c => Enumerable.Range(0, c + 1).Select(n => $"{LoadConditionPrefix}{c}-{n}"));

	}

	public class IcbmSilo : INotifyCreated
	{
		readonly IcbmSiloInfo info;

		// One entry per warhead: null is a conventional warhead, otherwise the nuke swapped in.
		readonly List<NukePackageInfo> warheads = [];
		int token = Actor.InvalidConditionToken;
		int chaffToken = Actor.InvalidConditionToken;
		int emptyToken = Actor.InvalidConditionToken;
		int needsChaffToken = Actor.InvalidConditionToken;
		int loadToken = Actor.InvalidConditionToken;

		public IcbmSilo(IcbmSiloInfo info) { this.info = info; }

		void INotifyCreated.Created(Actor self) { UpdateConditions(self); }

		// The "empty", "wants chaff" and exact-load conditions follow what's in the silo.
		public void UpdateConditions(Actor self)
		{
			static void Set(Actor self, ref int token, string condition, bool on)
			{
				if (on && token == Actor.InvalidConditionToken)
					token = self.GrantCondition(condition);
				else if (!on && token != Actor.InvalidConditionToken)
					token = self.RevokeCondition(token);
			}

			Set(self, ref emptyToken, info.EmptyCondition, !HasMissile);
			Set(self, ref needsChaffToken, info.NeedsChaffCondition, HasMissile && !Chaff);
			if (loadToken != Actor.InvalidConditionToken)
				loadToken = self.RevokeCondition(loadToken);

			if (HasMissile)
				loadToken = self.GrantCondition($"{info.LoadConditionPrefix}{Capacity}-{Nukes}");
		}

		public bool HasMissile => warheads.Count > 0;
		public bool Chaff => chaffToken != Actor.InvalidConditionToken;
		public int Capacity => warheads.Count;
		public int Nukes => warheads.Count(w => w != null);
		public bool Full => !HasMissile || Nukes >= warheads.Count;
		public IReadOnlyList<NukePackageInfo> Warheads => warheads;

		public bool Arm(Actor self, int count, string name)
		{
			if (HasMissile)
				return false;

			for (var i = 0; i < count; i++)
				warheads.Add(null);

			token = self.GrantCondition(info.Condition);
			UpdateConditions(self);
			var label = name != null ? FluentProvider.GetMessage(name) : "ICBM";
			if (self.World.LocalPlayer == self.Owner)
				TextNotificationsManager.AddSystemLine("ICBM", $"{label} in the silo with {count} conventional warhead{(count == 1 ? "" : "s")}.");

			return true;
		}

		public bool AddChaff(Actor self)
		{
			if (!HasMissile || Chaff)
				return false;

			chaffToken = self.GrantCondition(info.ChaffCondition);
			UpdateConditions(self);
			if (self.World.LocalPlayer == self.Owner)
				TextNotificationsManager.AddSystemLine("ICBM", "Chaff fitted: the ICBM is half as likely to be shot down.");

			return true;
		}

		// A nuclear warhead takes the place of a conventional one.
		public bool Load(Actor self, string name, NukePackageInfo w)
		{
			var slot = warheads.IndexOf(null);
			if (slot < 0)
				return false;

			warheads[slot] = w;
			UpdateConditions(self);
			if (self.World.LocalPlayer == self.Owner)
				TextNotificationsManager.AddSystemLine("ICBM", $"{w.Name} loaded into the ICBM ({Nukes} of {warheads.Count} warheads nuclear).");

			return true;
		}

		public (List<NukePackageInfo> Warheads, bool Chaff) TakeAll(Actor self)
		{
			var all = warheads.ToList();
			var chaff = Chaff;
			warheads.Clear();
			if (token != Actor.InvalidConditionToken)
				token = self.RevokeCondition(token);

			if (chaffToken != Actor.InvalidConditionToken)
				chaffToken = self.RevokeCondition(chaffToken);

			UpdateConditions(self);
			return (all, chaff);
		}
	}

	[Desc("Launches every warhead of the ICBM in the IcbmSilo, each at its own target (the player picks one per warhead).")]
	public class IcbmPowerInfo : SupportPowerInfo
	{
		[Desc("Image and sequence of the missile climbing out of the silo.")]
		public readonly string MissileImage = "atomic";
		public readonly string MissileUp = "up";

		[PaletteReference(nameof(IsPlayerPalette))]
		public readonly string MissilePalette = "effect";
		public readonly bool IsPlayerPalette = false;

		[Desc("Smoke left behind the climbing missile.")]
		public readonly string TrailImage = "smokey";
		public readonly string TrailSequence = "idle";

		[PaletteReference]
		public readonly string TrailPalette = "effect";

		[Desc("Ticks the missiles climb before they are out of sight.")]
		public readonly int ClimbTicks = 40;

		[Desc("Ticks between leaving sight and the warheads coming down.")]
		public readonly int FlightTicks = 75;

		[Desc("Ticks a warhead takes from the top of the sky to the ground: a lightning strike.")]
		public readonly int StrikeTicks = 5;

		[Desc("Height the warheads come down from.")]
		public readonly WDist StrikeHeight = WDist.FromCells(40);

		[Desc("Each enemy Nike site within range of the impact fires at every warhead, with this chance (per thousand) to",
			"stop it; every Iron Dome in range also fires, with its own chance. More defences, worse odds. Chaff halves both.")]
		public readonly int NikePermille = 800;
		public readonly int IronDomePermille = 500;

		[Desc("Height the interceptors meet the warhead at (on the way down, high over the target).")]
		public readonly WDist InterceptHeight = WDist.FromCells(9);

		[WeaponReference]
		[Desc("What a conventional warhead does.")]
		public readonly string ConventionalWeapon = "IcbmConventional";

		[Desc("Sound as a warhead streaks down.")]
		public readonly string StrikeSound = "whoosh.wav";

		public readonly WDist CameraRange = WDist.FromCells(10);

		[Desc("The missile load this button launches: warheads aboard, and how many of them are nuclear.")]
		public readonly int Warheads = 1;
		public readonly int Nukes = 0;

		public WeaponInfo ConventionalWeaponInfo { get; private set; }

		public override void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			base.RulesetLoaded(rules, ai);
			ConventionalWeaponInfo = rules.Weapons[ConventionalWeapon.ToLowerInvariant()];
		}

		public override object Create(ActorInitializer init) { return new IcbmPower(init.Self, this); }
	}

	public class IcbmPower : SupportPower
	{
		readonly IcbmPowerInfo info;

		public IcbmPower(Actor self, IcbmPowerInfo info)
			: base(self, info)
		{
			this.info = info;
		}

		// One of our silos holding this button's load (the one that fires).
		public static (Actor Silo, IcbmSilo Trait) Loaded(Player owner, IcbmPowerInfo info)
		{
			var s = owner.World.ActorsWithTrait<IcbmSilo>()
				.FirstOrDefault(s => s.Actor.Owner == owner && !s.Actor.IsDead && s.Actor.IsInWorld && s.Trait.HasMissile
					&& s.Trait.Capacity == info.Warheads && s.Trait.Nukes == info.Nukes);
			return (s.Actor, s.Trait);
		}

		// How many silos hold this load.
		public static (int Count, int Chaffed) Count(Player owner, IcbmPowerInfo info) =>
			owner.World.ActorsWithTrait<IcbmSilo>()
				.Where(s => s.Actor.Owner == owner && !s.Actor.IsDead && s.Trait.HasMissile
					&& s.Trait.Capacity == info.Warheads && s.Trait.Nukes == info.Nukes)
				.Aggregate((0, 0), (a, s) => (a.Item1 + 1, a.Item2 + (s.Trait.Chaff ? 1 : 0)));

		public override SupportPowerInstance CreateInstance(string key, SupportPowerManager manager)
		{
			return new IcbmPowerInstance(key, info, manager);
		}

		// One click per warhead before it launches.
		public override void SelectTarget(Actor self, string order, SupportPowerManager manager)
		{
			var count = Math.Max(1, info.Warheads);
			self.World.OrderGenerator = new SelectIcbmTargets(order, manager, info, count);
		}

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			var (silo, icbm) = Loaded(self.Owner, info);
			if (order.Target.Type == TargetType.Invalid || silo == null)
				return;

			base.Activate(self, order, manager);
			PlayLaunchSounds();

			// The targets: one per warhead, in the order they were picked; any extra warheads follow the last one.
			var targets = new List<WPos>();
			foreach (var part in (order.TargetString ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
			{
				var xy = part.Split(',');
				if (xy.Length == 2 && int.TryParse(xy[0], out var x) && int.TryParse(xy[1], out var y) && self.World.Map.Contains(new CPos(x, y)))
					targets.Add(self.World.Map.CenterOfCell(new CPos(x, y)));
			}

			if (targets.Count == 0)
				targets.Add(order.Target.CenterPosition);

			var (warheads, chaff) = icbm.TakeAll(silo);
			var intercept = chaff ? 50 : 100;
			for (var i = 0; i < warheads.Count; i++)
			{
				var at = targets[Math.Min(i, targets.Count - 1)];
				var strike = new IcbmStrike(silo, self.Owner, info, warheads[i], silo.CenterPosition, new WPos(at.X, at.Y, 0), i * 8, intercept, chaff);
				self.World.AddFrameEndTask(w => w.Add(strike));
				if (info.CameraRange != WDist.Zero)
				{
					var t = at;
					self.World.AddFrameEndTask(w => w.Add(new RevealShroudEffect(t, info.CameraRange, Shroud.SourceType.PassiveVisibility,
						self.Owner, PlayerRelationship.Ally, info.ClimbTicks + info.FlightTicks - 10, 150)));
				}
			}
		}
	}

	// The button's icon shows the missile and its warheads (nuclear and conventional). The text adds how many silos hold
	// that load, if more than one, and chaff.
	public class IcbmPowerInstance : SupportPowerInstance
	{
		readonly IcbmPowerInfo info;

		public IcbmPowerInstance(string key, IcbmPowerInfo info, SupportPowerManager manager)
			: base(key, info, manager)
		{
			this.info = info;
		}

		public override string IconOverlayTextOverride()
		{
			var (count, chaffed) = IcbmPower.Count(Manager.Self.Owner, info);
			if (count == 0)
				return null;

			var lines = new List<string>();
			if (Ready)
				lines.Add("READY");

			if (count > 1)
				lines.Add($"x{count}");

			if (chaffed > 0)
				lines.Add("+CHAFF");

			return lines.Count == 0 ? "" : string.Join("\n", lines);
		}
	}

	// Picks a target for every warhead: each click marks one (numbered); the launch goes when all are chosen.
	// Right-click to cancel.
	public class SelectIcbmTargets : OrderGenerator
	{
		protected override MouseActionType ActionType => MouseActionType.SupportPower;
		readonly SupportPowerManager manager;
		readonly IcbmPowerInfo info;
		readonly string order;
		readonly int count;
		readonly List<CPos> picked = [];

		public SelectIcbmTargets(string order, SupportPowerManager manager, IcbmPowerInfo info, int count)
			: base(manager.Self.World)
		{
			this.manager = manager;
			this.info = info;
			this.order = order;
			this.count = count;
			if (count > 1)
				TextNotificationsManager.AddTransientLine(manager.Self.Owner, $"Pick a target for each of the {count} warheads.");
		}

		protected override IEnumerable<Order> OrderInner(World world, CPos cell, int2 worldPixel, MouseInput mi)
		{
			if (mi.Button == MouseButton.Right)
			{
				world.CancelInputMode();
				yield break;
			}

			if (mi.Button != MouseButton.Left || !world.Map.Contains(cell))
				yield break;

			picked.Add(cell);
			if (picked.Count < count)
			{
				TextNotificationsManager.AddTransientLine(manager.Self.Owner, $"Warhead {picked.Count} targeted. Pick a target for warhead {picked.Count + 1}.");
				yield break;
			}

			world.CancelInputMode();
			yield return new Order(order, manager.Self, Target.FromCell(world, picked[0]), false)
			{
				SuppressVisualFeedback = true,
				TargetString = string.Join(";", picked.Select(c => $"{c.X},{c.Y}")),
			};
		}

		protected override void Tick(World world)
		{
			if (!manager.Powers.TryGetValue(order, out var p) || !p.Active || !p.Ready)
				world.CancelInputMode();
		}

		protected override IEnumerable<IRenderable> Render(WorldRenderer wr, World world) { yield break; }
		protected override IEnumerable<IRenderable> RenderAboveShroud(WorldRenderer wr, World world) { yield break; }

		protected override IEnumerable<IRenderable> RenderAnnotations(WorldRenderer wr, World world)
		{
			var font = Game.Renderer.Fonts["Bold"];
			for (var i = 0; i < picked.Count; i++)
			{
				var pos = world.Map.CenterOfCell(picked[i]);
				yield return new CircleAnnotationRenderable(pos, WDist.FromCells(3), 2, Color.Red);
				yield return new TextAnnotationRenderable(font, pos, 0, Color.Red, $"{i + 1}");
			}
		}

		protected override string GetCursor(World world, CPos cell, int2 worldPixel, MouseInput mi)
		{
			return world.Map.Contains(cell) ? info.Cursor : info.BlockedCursor;
		}
	}

	// One ICBM warhead (nuclear, or conventional when warhead is null): climbs out of the silo trailing smoke, is gone
	// for the flight, then comes down out of the sky almost instantly, a white-hot streak like lightning. Just before
	// it arrives, every enemy Nike site and Iron Dome near the target fires an interceptor up at it; if one hits, the
	// warhead dies high over the target in a huge glowing burst instead.
	public class IcbmStrike : IEffect, IEffectAboveShroud
	{
		sealed class Shot
		{
			public WPos From;
			public bool Hit;
			public bool Nike;
		}

		const int InterceptFlight = 24;

		// Chaff is out this long before the warhead arrives, and hangs this long after.
		const int ChaffLead = 50;
		const int ChaffLinger = 75;

		readonly Actor silo;
		readonly Player owner;
		readonly IcbmPowerInfo info;
		readonly NukePackageInfo warhead;
		readonly int oddsPercent;
		readonly bool chaff;
		readonly WPos launch;
		readonly WPos target;
		readonly World world;
		readonly Animation missile;
		readonly List<Shot> shots = [];
		int ticks;
		bool intercepted;

		public IcbmStrike(Actor silo, Player owner, IcbmPowerInfo info, NukePackageInfo warhead, WPos launch, WPos target, int delay,
			int oddsPercent, bool chaff)
		{
			this.oddsPercent = oddsPercent;
			this.chaff = chaff;
			this.silo = silo;
			this.owner = owner;
			this.info = info;
			this.warhead = warhead;
			this.launch = launch;
			this.target = target;
			world = owner.World;
			ticks = -delay;
			missile = new Animation(world, info.MissileImage);
			missile.PlayRepeating(info.MissileUp);
		}

		int StrikeStart => info.ClimbTicks + info.FlightTicks;
		int InterceptLaunch => StrikeStart - InterceptFlight - 4;
		int InterceptArrive => InterceptLaunch + InterceptFlight;
		WPos KillPoint => target + new WVec(0, 0, info.InterceptHeight.Length);

		public void Tick(World w)
		{
			ticks++;
			if (ticks < 0)
				return;

			missile.Tick();
			if (ticks <= info.ClimbTicks && ticks % 2 == 0)
			{
				var p = ClimbPos;
				w.AddFrameEndTask(ww => ww.Add(new SpriteEffect(p, ww, info.TrailImage, info.TrailSequence, info.TrailPalette)));
			}

			if (ticks == InterceptLaunch)
				LaunchInterceptors(w);

			if (ticks == InterceptArrive)
				ResolveInterceptors(w);

			if (ticks == StrikeStart && !intercepted)
				Game.Sound.Play(SoundType.World, info.StrikeSound, target);

			if (ticks == StrikeStart + info.StrikeTicks && !intercepted)
			{
				var source = silo != null && !silo.IsDead && silo.IsInWorld ? silo : owner.PlayerActor;
				var weapon = warhead?.WeaponInfo ?? info.ConventionalWeaponInfo;
				var t = Target.FromPos(target);
				weapon.Impact(t, new WarheadArgs { Weapon = weapon, Source = target, SourceActor = source, WeaponTarget = t });
				if (warhead != null)
					NukeDetonation.AfterBlast(w, owner, target, warhead);
			}

			if (ticks > StrikeStart + Math.Max(info.StrikeTicks + 12, chaff ? ChaffLinger : 0))
				w.AddFrameEndTask(ww => ww.Remove(this));
		}

		WPos ClimbPos
		{
			get
			{
				var k = Math.Min(ticks, info.ClimbTicks);
				var z = 512 + (int)((long)info.StrikeHeight.Length * k * k / (info.ClimbTicks * info.ClimbTicks));
				return launch + new WVec(0, 0, z);
			}
		}

		WPos StrikePos(int t)
		{
			var k = Math.Clamp(t - StrikeStart, 0, info.StrikeTicks);
			return target + new WVec(0, 0, info.StrikeHeight.Length * (info.StrikeTicks - k) / info.StrikeTicks);
		}

		// Every enemy Nike site (that has a round) and Iron Dome within range of the target fires one interceptor.
		void LaunchInterceptors(World w)
		{
			foreach (var d in w.ActorsWithTrait<NikeSite>()
				.Where(n => n.Trait.Ready(n.Actor) && owner.RelationshipWith(n.Actor.Owner) == PlayerRelationship.Enemy
					&& (n.Actor.CenterPosition - target).HorizontalLengthSquared <= n.Trait.Info.Range.LengthSquared).ToList())
			{
				d.Trait.Spend(d.Actor);
				Fire(w, d.Actor, true);
			}

			foreach (var d in w.ActorsWithTrait<IronDome>()
				.Where(d => !d.Trait.IsTraitDisabled && d.Actor.IsInWorld && !d.Actor.IsDead
					&& owner.RelationshipWith(d.Actor.Owner) == PlayerRelationship.Enemy
					&& (d.Actor.CenterPosition - target).HorizontalLengthSquared <= d.Trait.Info.Range.LengthSquared).ToList())
				Fire(w, d.Actor, false);
		}

		void Fire(World w, Actor site, bool nike)
		{
			var odds = (nike ? info.NikePermille : info.IronDomePermille) * oddsPercent / 100;
			shots.Add(new Shot { From = site.CenterPosition + new WVec(0, 0, 512), Hit = w.SharedRandom.Next(1000) < odds, Nike = nike });
			Game.Sound.Play(SoundType.World, "missile1.aud", site.CenterPosition);
		}

		void ResolveInterceptors(World w)
		{
			if (shots.Count == 0)
				return;

			var at = KillPoint;
			if (!shots.Any(s => s.Hit))
			{
				// All missed: they burst harmlessly as the warhead streaks past.
				w.AddFrameEndTask(ww => ww.Add(new SpriteEffect(at, ww, "explosion", "small_explosion_air", "effect")));
				return;
			}

			intercepted = true;
			var by = shots.First(s => s.Hit);
			var defender = w.ActorMap.GetActorsAt(w.Map.CellContaining(by.From)).FirstOrDefault()?.Owner;
			var nuclear = warhead != null;
			w.AddFrameEndTask(ww =>
			{
				ww.Add(new StratosphereKill(ww, at, nuclear));
				TextNotificationsManager.AddSystemLine("ICBM", $"{(defender != null ? defender.ResolvedPlayerName + "'s " : "")}{(by.Nike ? "Nike site" : "Iron Dome")} destroyed an incoming ICBM warhead high in the sky!");
			});
			Game.Sound.Play(SoundType.World, "kaboom25.aud", at);
			Game.Sound.Play(SoundType.World, "whoosh.wav", at);
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr) { yield break; }

		// A storm of glittering foil strips over the impact zone: bursts out a few cells up as the warhead nears,
		// spreads and drifts down, flickering as the strips turn in the light.
		IEnumerable<IRenderable> Chaff(WorldRenderer wr)
		{
			var age = ticks - (StrikeStart - ChaffLead);
			var spread = Math.Min(1f, age / 15f);
			var fade = Math.Clamp((ChaffLead + ChaffLinger - age) / 30f, 0f, 1f);
			for (var i = 0; i < 160; i++)
			{
				// Fixed per strip, so each drifts smoothly.
				var h = (uint)(i * 2654435761u ^ (uint)(target.X * 31 + target.Y * 17));
				var angle = h % 6283 / 1000f;
				var dist = (int)((h >> 8) % 4096 * spread) + 150;
				var height = 2048 + (int)((h >> 16) % 4096) - age * 25;
				if (height < 64)
					continue;

				var flicker = (ticks + (int)(h % 5)) % 3 == 0;
				var c = flicker ? Color.FromArgb((int)(255 * fade), 255, 255, 255)
					: Color.FromArgb((int)(220 * fade), (int)(200 + h % 55), (int)(180 + h % 60), 110);
				var p = target + new WVec((int)(MathF.Cos(angle) * dist), (int)(MathF.Sin(angle) * dist * 0.7f), height);
				var tilt = new WVec((int)((h >> 3) % 240) - 120, (int)((h >> 5) % 120) - 60, flicker ? 120 : 60);
				yield return new BeamRenderable(p, 0, tilt, BeamRenderableShape.Flat, new WDist(flicker ? 70 : 45), c);
			}
		}

		// Where an interceptor is, a moment into its flight: straight up first, then curving over onto the warhead.
		WPos ShotPos(Shot s, float k)
		{
			var top = new WPos(s.From.X, s.From.Y, KillPoint.Z);
			var a = s.From + (top - s.From) * (int)(k * 1000) / 1000;
			var b = top + (KillPoint - top) * (int)(k * 1000) / 1000;
			return a + (b - a) * (int)(k * 1000) / 1000;
		}

		// The interceptors: smoke trails climbing from each site, a bright motor at the head.
		IEnumerable<IRenderable> Interceptors()
		{
			if (ticks < InterceptLaunch || ticks > InterceptArrive + 20)
				yield break;

			var k = Math.Min(1f, (ticks - InterceptLaunch) / (float)InterceptFlight);
			var fade = ticks <= InterceptArrive ? 1f : Math.Max(0f, 1f - (ticks - InterceptArrive) / 20f);
			foreach (var s in shots)
			{
				var prev = s.From;
				const int Segments = 10;
				for (var i = 1; i <= Segments; i++)
				{
					var p = ShotPos(s, k * i / Segments);
					yield return new BeamRenderable(prev, 0, p - prev, BeamRenderableShape.Cylindrical, new WDist(s.Nike ? 150 : 100),
						Color.FromArgb((int)(150 * fade * i / Segments), 235, 235, 240));
					prev = p;
				}

				if (ticks <= InterceptArrive)
					yield return new BeamRenderable(prev, 0, new WVec(0, 0, -200), BeamRenderableShape.Cylindrical, new WDist(s.Nike ? 120 : 80),
						Color.FromArgb(255, 255, 230, 140));
			}
		}

		public IEnumerable<IRenderable> RenderAboveShroud(WorldRenderer wr)
		{
			if (ticks < 0)
				yield break;

			// Climbing out of the silo.
			if (ticks <= info.ClimbTicks)
			{
				var palette = wr.Palette(info.IsPlayerPalette ? info.MissilePalette + owner.InternalName : info.MissilePalette);
				foreach (var r in missile.Render(ClimbPos, palette))
					yield return r;

				yield break;
			}

			// Chaff: a cloud of glittering strips bursting over the impact as the warhead comes in, drifting down.
			if (chaff && ticks >= StrikeStart - ChaffLead && ticks < StrikeStart + ChaffLinger)
				foreach (var r in Chaff(wr))
					yield return r;

			foreach (var r in Interceptors())
				yield return r;

			// The strike: a blinding streak from the top of the sky down to the warhead, lingering a moment after impact.
			if (ticks < StrikeStart || intercepted)
				yield break;

			var head = StrikePos(ticks);
			var top = target + new WVec(0, 0, info.StrikeHeight.Length);
			var after = ticks - StrikeStart - info.StrikeTicks;
			var fade = after <= 0 ? 1f : Math.Max(0f, 1f - after / 12f);
			var length = head - top;
			yield return new BeamRenderable(top, 0, length, BeamRenderableShape.Cylindrical, new WDist(420),
				Color.FromArgb((int)(90 * fade), 255, 190, 80));
			yield return new BeamRenderable(top, 0, length, BeamRenderableShape.Cylindrical, new WDist(170),
				Color.FromArgb((int)(200 * fade), 255, 240, 190));
			yield return new BeamRenderable(top, 0, length, BeamRenderableShape.Flat, new WDist(60),
				Color.FromArgb((int)(255 * fade), 255, 255, 255));
		}
	}

	// A warhead killed high in the sky: a white-hot flash, then a vast glowing shell (blue and violet, like a rocket's
	// second stage seen at twilight) that billows out, sprouts streamers and slowly fades. A nuclear warhead's is bigger.
	public class StratosphereKill : IEffect, IEffectAboveShroud
	{
		const int Life = 125;
		readonly World world;
		readonly WPos pos;
		readonly float size;
		int ticks;

		public StratosphereKill(World world, WPos pos, bool nuclear)
		{
			this.world = world;
			this.pos = pos;
			size = nuclear ? 1.4f : 1f;
		}

		public void Tick(World w)
		{
			if (++ticks > Life)
				w.AddFrameEndTask(ww => ww.Remove(this));
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr) { yield break; }

		public IEnumerable<IRenderable> RenderAboveShroud(WorldRenderer wr)
		{
			var palette = wr.Palette("effect");
			var seqs = world.Map.Sequences;
			var puff = seqs.GetSequence("nukepuff", "a");
			var sprite = puff.GetSprite(0);
			var k = ticks / (float)Life;
			var fade = Math.Max(0f, 1f - k);

			// The flash.
			if (ticks < 10)
			{
				var f = 1f - ticks / 10f;
				yield return new SpriteRenderable(sprite, pos, WVec.Zero, 9000, palette, 6f * size * (1.2f - 0.5f * f), f,
					new Vector3(3f, 3f, 3f), TintModifiers.IgnoreWorldTint, false);
			}

			// The shell: a ring of glowing puffs racing outward, the upper half lifting into a dome, colours shifting from
			// electric cyan to violet as it spreads.
			var radius = (int)((900 + 5200 * MathF.Sqrt(k)) * size);
			var tint = Vector3.Lerp(new Vector3(0.6f, 1.6f, 2.2f), new Vector3(1.4f, 0.7f, 2.0f), k);
			for (var i = 0; i < 36; i++)
			{
				var a = MathF.Tau * i / 36 + k * 0.6f;
				var lift = MathF.Max(0, MathF.Sin(a)) * radius * 0.55f;
				var off = new WVec((int)(MathF.Cos(a) * radius), (int)(MathF.Sin(a) * radius * 0.35f), (int)lift);
				yield return new SpriteRenderable(sprite, pos, off, 8800, palette, (1.4f + 1.2f * k) * size, 0.55f * fade * fade,
					tint, TintModifiers.IgnoreWorldTint, false);
			}

			// A soft glowing dome filling the shell.
			for (var i = 0; i < 6; i++)
			{
				var off = new WVec(0, 0, (int)(radius * (0.15f + 0.1f * i)));
				yield return new SpriteRenderable(sprite, pos, off, 8700, palette, (2.5f + radius / 900f) * size * (1f - 0.1f * i),
					0.18f * fade, tint * 0.8f, TintModifiers.IgnoreWorldTint, false);
			}

			// Streamers: glowing filaments trailing down and out, like an exhaust plume caught by sunlight.
			for (var i = 0; i < 14; i++)
			{
				var a = MathF.Tau * i / 14 + 0.3f;
				var len = radius * (0.6f + 0.4f * MathF.Sin(i * 1.7f));
				var dir = new WVec((int)(MathF.Cos(a) * len), (int)(MathF.Sin(a) * len * 0.4f), -(int)(len * 0.6f));
				var c = Color.FromArgb((int)(170 * fade), (int)(120 + 100 * k), (int)(220 - 80 * k), 255);
				yield return new BeamRenderable(pos, 0, dir, BeamRenderableShape.Cylindrical, new WDist((int)(90 * size)), c);
			}
		}
	}
}
