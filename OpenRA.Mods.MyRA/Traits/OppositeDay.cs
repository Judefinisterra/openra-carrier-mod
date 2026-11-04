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
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Traits;
using OpenRA.Network;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// ================================================================================================
	// Opposite Day: click near an enemy's base. For a minute, everything that player orders happens backwards:
	//   - move / attack-move somewhere: the units go the same distance the opposite way (left is right, forward is back)
	//   - attack an enemy unit: they attack the nearest of their own units instead
	//   - attack an enemy building: they head home and attack the nearest of their own buildings
	//   - build a building (or place a finished one): they sell one of their old buildings instead
	//   - fire a support power at someone (nuke, airstrike...): it lands on their own base
	// Works on AI players too. Every client flips the same orders the same way, so it stays in sync.
	// ================================================================================================
	[Desc("Support power: for a while, the target player's orders do the opposite of what they say.")]
	public class OppositeDayPowerInfo : SupportPowerInfo
	{
		[Desc("How long Opposite Day lasts, in ticks.")]
		public readonly int Duration = 1500;

		public override object Create(ActorInitializer init) { return new OppositeDayPower(init.Self, this); }
	}

	public class OppositeDayPower : SupportPower
	{
		readonly OppositeDayPowerInfo info;

		public OppositeDayPower(Actor self, OppositeDayPowerInfo info)
			: base(self, info)
		{
			this.info = info;
		}

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			// Needs somewhere to aim (an order sent without a target would crash the game).
			if (order.Target.Type == TargetType.Invalid)
				return;

			base.Activate(self, order, manager);
			PlayLaunchSounds();

			var w = self.World;
			var attacker = self.Owner;
			var pos = order.Target.CenterPosition;

			// Victim: the enemy whose building is closest to where we clicked.
			var nearest = w.ActorsHavingTrait<Building>()
				.Where(a => !a.IsDead && a.IsInWorld && !a.Owner.NonCombatant && a.Owner != attacker
					&& attacker.RelationshipWith(a.Owner) != PlayerRelationship.Ally && a.Owner.WinState == WinState.Undefined)
				.MinByOrDefault(a => (a.CenterPosition - pos).LengthSquared);
			if (nearest == null)
				return;

			w.WorldActor.TraitOrDefault<OppositeDayManager>()?.Start(nearest.Owner, attacker, info.Duration);
		}
	}

	[Desc("Turns orders around for players under Opposite Day. Attach to the world actor.")]
	public class OppositeDayManagerInfo : TraitInfo
	{
		[Desc("Ticks between checks for units that started attacking an enemy on their own.")]
		public readonly int RecheckInterval = 25;

		[Desc("A player under Opposite Day sells at most one building this often (ticks), however much they try to build.")]
		public readonly int SellInterval = 75;

		public override object Create(ActorInitializer init) { return new OppositeDayManager(init.Self, this); }
	}

	public class OppositeDayManager : IValidateOrder, ITick
	{
		readonly OppositeDayManagerInfo info;
		readonly World world;
		readonly Dictionary<Player, int> until = [];
		readonly Dictionary<Player, int> nextSell = [];

		public OppositeDayManager(Actor self, OppositeDayManagerInfo info)
		{
			this.info = info;
			world = self.World;
		}

		public bool IsOppositeDay(Player p) => until.ContainsKey(p);

		public void Start(Player victim, Player attacker, int duration)
		{
			until[victim] = world.WorldTick + duration;

			// It starts mid-battle: whatever their units are doing right now turns around too.
			FlipOngoing(victim, true);
			var seconds = duration / 25;
			world.AddFrameEndTask(w =>
			{
				if (w.LocalPlayer == victim)
					TextNotificationsManager.AddSystemLine("Opposite Day",
						$"IT'S OPPOSITE DAY! For {seconds} seconds your army does the opposite of everything you tell it.");
				else if (w.LocalPlayer != null)
					TextNotificationsManager.AddSystemLine("Opposite Day",
						$"{attacker.ResolvedPlayerName} declared Opposite Day on {victim.ResolvedPlayerName} for {seconds} seconds!");

				foreach (var a in w.Actors.Where(a => a.Owner == victim && a.IsInWorld && !a.IsDead && a.OccupiesSpace != null
					&& a.Info.HasTraitInfo<IMoveInfo>()))
					w.Add(new FloatingText(a.CenterPosition, Color.Magenta, "?!", 40));
			});
		}

		void ITick.Tick(Actor self)
		{
			foreach (var (p, end) in until.ToList())
			{
				// Units that lock onto an enemy by themselves (no order involved) get turned around as well.
				if ((world.WorldTick - end) % info.RecheckInterval == 0)
					FlipOngoing(p, false);

				if (world.WorldTick < end)
					continue;

				until.Remove(p);
				world.AddFrameEndTask(w =>
				{
					if (w.LocalPlayer != null)
						TextNotificationsManager.AddSystemLine("Opposite Day", $"Opposite Day is over for {p.ResolvedPlayerName}.");
				});
			}
		}

		// Turns around what the player's units are doing now: anyone attacking an enemy attacks their own side
		// instead, and (when moves is set) anyone on the move heads the opposite way.
		void FlipOngoing(Player p, bool moves)
		{
			foreach (var a in world.Actors.Where(a => a.Owner == p && !a.IsDead && a.IsInWorld && a.OccupiesSpace != null
				&& !a.IsIdle && a.Info.HasTraitInfo<IMoveInfo>()).ToList())
			{
				// What it's doing, from the target lines its activities draw: an enemy it is attacking if there is one,
				// otherwise where it's going.
				var nodes = a.CurrentActivity.TargetLineNodes(a).Select(n => n.Target).Where(t => t.Type != TargetType.Invalid).ToList();
				var target = nodes.FirstOrDefault(t => t.Type is TargetType.Actor or TargetType.FrozenActor);
				if (target.Type == TargetType.Invalid)
					target = nodes.FirstOrDefault();

				if (target.Type == TargetType.Invalid)
					continue;

				Order order;
				if (target.Type is TargetType.Actor or TargetType.FrozenActor)
				{
					var owner = target.Type == TargetType.Actor ? target.Actor.Owner : target.FrozenActor.Owner;
					if (p.RelationshipWith(owner) != PlayerRelationship.Enemy || !a.Info.HasTraitInfo<AttackBaseInfo>())
						continue;

					order = new Order("Attack", a, target, false);
				}
				else if (moves)
					order = new Order("Move", a, target, false);
				else
					continue;

				var flipped = Flip(a, order);
				if (flipped != null && flipped != order)
					a.ResolveOrder(flipped);
			}
		}

		bool IValidateOrder.OrderValidation(OrderManager orderManager, World w, int clientId, Order order)
		{
			var subject = order.Subject;
			if (until.Count == 0 || subject == null || subject.IsDead || !until.ContainsKey(subject.Owner))
				return true;

			// Only flip orders that would otherwise go through.
			if (!w.OrderValidators.Where(v => v != this).All(v => v.OrderValidation(orderManager, w, clientId, order)))
				return true;

			var flipped = Flip(subject, order);
			if (flipped == null)
				return true;

			if (flipped != order)
				subject.ResolveOrder(flipped);

			return false;
		}

		// The opposite order, the same order to let it through, or "order" itself to swallow it (already handled).
		Order Flip(Actor self, Order order)
		{
			var owner = self.Owner;
			switch (order.OrderString)
			{
				case "Move":
				case "AttackMove":
				case "AssaultMove":
				{
					if (order.Target.Type == TargetType.Invalid)
						return null;

					var map = world.Map;
					var here = self.Location;
					var there = map.CellContaining(order.Target.CenterPosition);
					var back = map.Clamp(here - (there - here));
					return Copy(order, Target.FromCell(world, back));
				}

				case "Attack":
				case "ForceAttack":
				{
					if (order.Target.Type != TargetType.Actor && order.Target.Type != TargetType.FrozenActor)
						return null;

					var victim = order.Target.Type == TargetType.Actor ? order.Target.Actor : order.Target.FrozenActor.Actor;
					var building = victim != null && victim.Info.HasTraitInfo<BuildingInfo>();
					var friend = world.Actors
						.Where(a => a != self && a.Owner == owner && !a.IsDead && a.IsInWorld && a.OccupiesSpace != null
							&& a.Info.HasTraitInfo<IHealthInfo>() && a.Info.HasTraitInfo<BuildingInfo>() == building)
						.MinByOrDefault(a => (a.CenterPosition - self.CenterPosition).LengthSquared);
					if (friend == null)
						return order;

					return Copy(order, Target.FromActor(friend), "ForceAttack");
				}

				case "StartProduction":
				case "PlaceBuilding":
				{
					// Building a building? Sell one instead (the oldest that isn't the construction yard). Units still get built.
					if (order.OrderString == "StartProduction"
						&& (!world.Map.Rules.Actors.TryGetValue(order.TargetString ?? "", out var item) || !item.HasTraitInfo<BuildingInfo>()))
						return null;

					if (!nextSell.TryGetValue(owner, out var next) || world.WorldTick >= next)
					{
						nextSell[owner] = world.WorldTick + info.SellInterval;
						var old = world.ActorsHavingTrait<Sellable>()
							.Where(a => a.Owner == owner && !a.IsDead && a.IsInWorld && !a.Info.HasTraitInfo<BaseBuildingInfo>())
							.MinByOrDefault(a => a.ActorID);
						old?.ResolveOrder(new Order("Sell", old, false));
					}

					return order;
				}
			}

			// A support power fired at somewhere: it comes down on their own base instead.
			if (self == owner.PlayerActor && order.Target.Type != TargetType.Invalid
				&& self.TraitOrDefault<SupportPowerManager>()?.Powers.ContainsKey(order.OrderString) == true)
			{
				var home = world.ActorsHavingTrait<Building>()
					.Where(a => a.Owner == owner && !a.IsDead && a.IsInWorld)
					.MaxByOrDefault(a => a.Info.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0);
				if (home == null)
					return null;

				return Copy(order, Target.FromCell(world, home.Location));
			}

			return null;
		}

		static Order Copy(Order order, in Target target, string orderString = null)
		{
			return new Order(orderString ?? order.OrderString, order.Subject, target, order.Queued, order.ExtraActors)
			{
				ExtraData = order.ExtraData,
				ExtraLocation = order.ExtraLocation,
				TargetString = order.TargetString,
			};
		}
	}
}
