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
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// Container Ship: sails on its own between your ports and the ports of a player you're in a truce with.
	// Every time it docks it pays the side whose port it reached - the partner on the way out, you on the way back.
	// No truce partner with a port? It waits at home until there is one. If the truce ends while it's at sea it
	// turns back (and the partner gets nothing).
	[Desc("Trades between our ports and a truce partner's ports, paying whoever's port it arrives at.")]
	public class TradeShipInfo : TraitInfo, Requires<MobileInfo>
	{
		[Desc("Buildings that count as ports (the Seaport).")]
		public readonly HashSet<string> PortTypes = ["seaport"];

		[Desc("Used only for a player who has none of PortTypes, so ships never sit idle.")]
		public readonly HashSet<string> FallbackPortTypes = ["syrd", "spen", "tribaldock"];

		[Desc("Cash paid to the port's owner on every arrival.")]
		public readonly int Payment = 5000;

		[Desc("How close (cells) to the port's footprint counts as docked.")]
		public readonly int DockRange = 3;

		[Desc("Ticks spent unloading at a port.")]
		public readonly int UnloadTicks = 100;

		public override object Create(ActorInitializer init) { return new TradeShip(init.Self, this); }
	}

	public class TradeShip : ITick, ISelectionBar, IIssueDeployOrder, IResolveOrder
	{
		readonly TradeShipInfo info;
		readonly Mobile mobile;
		bool outbound = true;
		Player partner;
		Actor port;
		int timer, wait, failed;
		CPos lastSpot;

		public TradeShip(Actor self, TradeShipInfo info)
		{
			this.info = info;
			mobile = self.Trait<Mobile>();
		}

		static bool InTruce(Actor self, Player other)
		{
			var truces = self.World.WorldActor.TraitOrDefault<TruceManager>();
			return truces != null && other != null && other != self.Owner && truces.InTruce(self.Owner, other);
		}

		IEnumerable<Actor> PortsOf(Actor self, Player owner)
		{
			var buildings = self.World.ActorsHavingTrait<Building>().Where(a => a.Owner == owner && !a.IsDead && a.IsInWorld).ToList();
			var ports = buildings.Where(a => info.PortTypes.Contains(a.Info.Name)).ToList();
			return ports.Count > 0 ? ports : buildings.Where(a => info.FallbackPortTypes.Contains(a.Info.Name)).ToList();
		}

		bool IsPort(Actor a) => info.PortTypes.Contains(a.Info.Name) || info.FallbackPortTypes.Contains(a.Info.Name);

		bool Docked(Actor self, Actor target)
		{
			var r2 = info.DockRange * info.DockRange;
			return target.Trait<Building>().OccupiedCells().Any(c => (c.Item1 - self.Location).LengthSquared <= r2);
		}

		// The nearest water cell beside the port the ship can sail to.
		CPos? Berth(Actor self, Actor target)
		{
			var footprint = target.Trait<Building>().OccupiedCells().Select(c => c.Item1).ToHashSet();
			CPos? best = null;
			var bestDist = int.MaxValue;
			foreach (var f in footprint)
			{
				for (var dy = -2; dy <= 2; dy++)
				{
					for (var dx = -2; dx <= 2; dx++)
					{
						var c = f + new CVec(dx, dy);
						if (footprint.Contains(c) || !self.World.Map.Contains(c))
							continue;

						var d = (c - self.Location).LengthSquared;
						if (d < bestDist && mobile.CanEnterCell(c, self, BlockedByActor.Immovable))
						{
							bestDist = d;
							best = c;
						}
					}
				}
			}

			return best;
		}

		Actor ChoosePort(Actor self)
		{
			if (outbound)
			{
				// Stick with the same partner while the truce lasts; otherwise pick the nearest partner with a port.
				if (partner == null || !InTruce(self, partner) || !PortsOf(self, partner).Any())
					partner = self.World.Players.Where(p => p.Playable && InTruce(self, p) && PortsOf(self, p).Any())
						.MinByOrDefault(p => PortsOf(self, p).Min(a => (a.Location - self.Location).LengthSquared));

				return partner == null ? null : PortsOf(self, partner).MinByOrDefault(a => (a.Location - self.Location).LengthSquared);
			}

			return PortsOf(self, self.Owner).MinByOrDefault(a => (a.Location - self.Location).LengthSquared);
		}

		void ITick.Tick(Actor self)
		{
			if (!self.IsIdle)
				return;

			if (wait > 0)
			{
				wait--;
				return;
			}

			// Truce ended on the way out: head home without delivering.
			if (outbound && port != null && !InTruce(self, port.Owner))
			{
				port = null;
				outbound = false;
			}

			if (port == null || port.IsDead || !port.IsInWorld || (outbound ? port.Owner != partner : port.Owner != self.Owner))
			{
				port = ChoosePort(self);
				timer = 0;
				failed = 0;
				if (port == null)
				{
					// Nobody to trade with (or no home port): wait and look again.
					wait = 125;
					return;
				}
			}

			if (!Docked(self, port))
			{
				var spot = Berth(self, port);
				if (spot == null || (spot == lastSpot && ++failed > 4))
				{
					// Can't get there (blocked or landlocked port): try the other leg later.
					port = null;
					failed = 0;
					wait = 250;
					return;
				}

				lastSpot = spot.Value;
				self.QueueActivity(false, mobile.MoveTo(spot.Value, 0, targetLineColor: Color.Gold));
				return;
			}

			if (++timer < info.UnloadTicks)
				return;

			// Delivered: pay whoever owns this port, then sail the other way.
			timer = 0;
			var receiver = port.Owner;
			receiver.PlayerActor.Trait<PlayerResources>().GiveCash(info.Payment);
			var pos = port.CenterPosition;
			var color = receiver.Color;
			if (self.World.RenderPlayer == null || receiver.IsAlliedWith(self.World.RenderPlayer) || self.Owner.IsAlliedWith(self.World.RenderPlayer))
				self.World.AddFrameEndTask(w => w.Add(new FloatingText(pos, color, FloatingText.FormatCashTick(info.Payment), 30)));

			outbound = !outbound;
			port = null;
		}

		// Deploy (F key or the deploy button): drop whatever it's doing and (re)start the trade route from scratch -
		// for when it got stuck or was sent somewhere by hand.
		Order IIssueDeployOrder.IssueDeployOrder(Actor self, bool queued) => new("StartTradeRoute", self, queued);

		bool IIssueDeployOrder.CanIssueDeployOrder(Actor self, bool queued) => true;

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString != "StartTradeRoute")
				return;

			self.CancelActivity();
			partner = null;
			port = null;
			timer = wait = failed = 0;
			lastSpot = CPos.Zero;

			// If it's sitting in a partner's harbour, head home next; otherwise go out to trade.
			var atPartner = self.World.ActorsHavingTrait<Building>().Any(a => a.Owner != self.Owner && InTruce(self, a.Owner)
				&& IsPort(a) && !a.IsDead && Docked(self, a));
			outbound = !atPartner;
		}

		// Gold bar while unloading in port.
		float ISelectionBar.GetValue() => timer > 0 ? (float)timer / info.UnloadTicks : 0;
		Color ISelectionBar.GetColor() => Color.Gold;
		bool ISelectionBar.DisplayWhenEmpty => false;
	}
}
