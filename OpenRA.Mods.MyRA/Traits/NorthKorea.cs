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
using OpenRA.Effects;
using OpenRA.GameRules;
using OpenRA.Graphics;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

// North Korea (the "hermit" faction): Infiltration Tunnels, People's Army conscripts that train two at a time,
// the Juche Statue (morale), power-plant blackouts (Military First) and Trash Balloons.
namespace OpenRA.Mods.MyRA.Traits
{
	static class NK
	{
		public static void Grant(Actor a, string condition, int duration, object source)
		{
			if (a.IsDead || !a.IsInWorld)
				return;
			var ec = a.TraitsImplementing<ExternalCondition>().FirstOrDefault(t => t.Info.Condition == condition && t.CanGrantCondition(source));
			ec?.GrantCondition(a, source, duration);
		}

		public static void Tell(Player p, string text)
		{
			if (p.World.LocalPlayer == p)
				TextNotificationsManager.AddSystemLine("North Korea", text);
		}
	}

	// ================================================================================================
	// Infiltration Tunnel: select it and right-click the ground to dig a tunnel there (takes a while).
	// Infantry sent into the tunnel come out at the far end.
	// ================================================================================================
	[Desc("Infantry that enter this building come out of a tunnel exit dug wherever the player chooses.")]
	public class InfiltrationTunnelInfo : TraitInfo, Requires<CargoInfo>
	{
		[ActorReference]
		public readonly string ExitActor = "tunnelexit";

		[Desc("Longest tunnel that can be dug (0 = anywhere on the map).")]
		public readonly WDist MaxRange = WDist.Zero;

		[Desc("Ticks to dig a tunnel: this much, plus TicksPerCell for every cell of length.")]
		public readonly int DigTicks = 500;

		public readonly int TicksPerCell = 12;

		[Desc("Ticks between people coming out of the exit.")]
		public readonly int UnloadInterval = 12;

		public override object Create(ActorInitializer init) { return new InfiltrationTunnel(init.Self, this); }
	}

	public class InfiltrationTunnel : IIssueOrder, IResolveOrder, ITick, INotifyKilled, INotifyActorDisposing, IOrderVoice
	{
		const string OrderID = "DigTunnel";
		readonly InfiltrationTunnelInfo info;
		readonly Cargo cargo;
		Actor exit;
		CPos? digging;
		int digLeft, unloadTimer;

		public InfiltrationTunnel(Actor self, InfiltrationTunnelInfo info)
		{
			this.info = info;
			cargo = self.Trait<Cargo>();
		}

		public bool Open => exit != null && !exit.IsDead && exit.IsInWorld;

		IEnumerable<IOrderTargeter> IIssueOrder.Orders
		{
			get { yield return new DigTargeter(this); }
		}

		Order IIssueOrder.IssueOrder(Actor self, IOrderTargeter order, in Target target, bool queued)
		{
			return order.OrderID == OrderID ? new Order(OrderID, self, target, false) : null;
		}

		string IOrderVoice.VoicePhraseForOrder(Actor self, Order order) => null;

		bool CanDig(Actor self, CPos cell)
		{
			var map = self.World.Map;
			if (!map.Contains(cell) || (info.MaxRange.Length > 0 && (map.CenterOfCell(cell) - self.CenterPosition).HorizontalLengthSquared > info.MaxRange.LengthSquared))
				return false;
			var exitInfo = self.World.Map.Rules.Actors[info.ExitActor].TraitInfoOrDefault<BuildingInfo>();
			return exitInfo == null || self.World.CanPlaceBuilding(cell, self.World.Map.Rules.Actors[info.ExitActor], exitInfo, null);
		}

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString != OrderID)
				return;
			var cell = self.World.Map.CellContaining(order.Target.CenterPosition);
			if (!CanDig(self, cell))
				return;

			// A new tunnel replaces the old one.
			if (Open)
				exit.Dispose();
			exit = null;
			digging = cell;
			digLeft = info.DigTicks + info.TicksPerCell * (self.World.Map.CenterOfCell(cell) - self.CenterPosition).HorizontalLength / 1024;
			NK.Tell(self.Owner, $"Digging an infiltration tunnel - it opens in {digLeft / 25} seconds.");
			GameRecorder.Note(self.Owner, $"digging an infiltration tunnel to {cell}");
		}

		void ITick.Tick(Actor self)
		{
			if (digging != null && --digLeft <= 0)
			{
				var cell = digging.Value;
				digging = null;
				self.World.AddFrameEndTask(w =>
				{
					if (self.IsDead || !self.IsInWorld)
						return;
					exit = w.CreateActor(info.ExitActor, new TypeDictionary { new LocationInit(cell), new OwnerInit(self.Owner), new FactionInit(self.Owner.Faction.InternalName) });
					NK.Tell(self.Owner, "The infiltration tunnel is open. Send infantry into the tunnel entrance.");
				});
			}

			if (!Open || cargo.IsEmpty() || --unloadTimer > 0)
				return;

			unloadTimer = info.UnloadInterval;
			var w = self.World;
			var spot = w.Map.FindTilesInAnnulus(exit.Location, 1, 4).FirstOrDefault(c => w.Map.Contains(c)
				&& cargo.Passengers.First().Trait<IPositionable>().CanEnterCell(c, null, BlockedByActor.All), CPos.Zero);
			if (spot == CPos.Zero)
				return;

			var passenger = cargo.Unload(self);
			w.AddFrameEndTask(ww =>
			{
				var pos = passenger.Trait<IPositionable>();
				pos.SetPosition(passenger, spot, pos.GetAvailableSubCell(spot));
				ww.Add(passenger);
				passenger.Trait<Passenger>().OnEjectedFromKilledCargo(passenger);
			});
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			if (Open)
				exit.Kill(e.Attacker);
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			if (Open)
				exit.Dispose();
		}

		sealed class DigTargeter : IOrderTargeter
		{
			readonly InfiltrationTunnel owner;

			public DigTargeter(InfiltrationTunnel owner) { this.owner = owner; }

			public string OrderID => InfiltrationTunnel.OrderID;
			public int OrderPriority => 5;
			public bool IsQueued => false;

			public bool TargetOverridesSelection(Actor self, in Target target, List<Actor> actorsAt, CPos xy, TargetModifiers modifiers) => true;

			public bool CanTarget(Actor self, in Target target, ref TargetModifiers modifiers, ref string cursor)
			{
				if (target.Type != TargetType.Terrain)
					return false;
				var cell = self.World.Map.CellContaining(target.CenterPosition);
				cursor = owner.CanDig(self, cell) ? "ability" : "move-blocked";
				return true;
			}
		}
	}

	// ================================================================================================
	// People's Army: each one trained comes with a comrade.
	// ================================================================================================
	[Desc("When this actor appears, a second one (the twin actor) appears next to it and follows it.")]
	public class SpawnTwinInfo : TraitInfo
	{
		[ActorReference]
		public readonly string Actor = null;

		public override object Create(ActorInitializer init) { return new SpawnTwin(this); }
	}

	public class SpawnTwin : INotifyAddedToWorld
	{
		readonly SpawnTwinInfo info;
		bool done;

		public SpawnTwin(SpawnTwinInfo info) { this.info = info; }

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			if (done || self.World.Type != WorldType.Regular)
				return;
			done = true;

			// A couple of ticks later, so the original already has its orders (rally point) to copy.
			self.World.AddFrameEndTask(w => w.AddFrameEndTask(w2 =>
			{
				if (self.IsDead || !self.IsInWorld)
					return;
				var twin = w2.CreateActor(info.Actor, new TypeDictionary
				{
					new LocationInit(self.Location), new OwnerInit(self.Owner), new FactionInit(self.Owner.Faction.InternalName),
				});
				var dest = self.CurrentActivity?.TargetLineNodes(self).Select(n => n.Target).FirstOrDefault(t => t.Type != TargetType.Invalid);
				if (dest is Target t && t.Type != TargetType.Invalid)
					twin.QueueActivity(twin.Trait<IMove>().MoveTo(w2.Map.CellContaining(t.CenterPosition), 2));
			}));
		}
	}

	// ================================================================================================
	// Juche Statue: nearby troops fight harder (ProximityExternalCondition in yaml). If it falls, every one of the
	// owner's units loses heart for a while.
	// ================================================================================================
	[Desc("When this actor is destroyed, all of its owner's units get a condition for a while (low morale).")]
	public class DemoralizeOnDeathInfo : TraitInfo
	{
		public readonly string Condition = "demoralized";
		public readonly int Duration = 1500;

		public override object Create(ActorInitializer init) { return new DemoralizeOnDeath(this); }
	}

	public class DemoralizeOnDeath : INotifyKilled
	{
		readonly DemoralizeOnDeathInfo info;

		public DemoralizeOnDeath(DemoralizeOnDeathInfo info) { this.info = info; }

		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			var owner = self.Owner;
			foreach (var a in self.World.Actors.Where(a => a.Owner == owner && a != self).ToList())
				NK.Grant(a, info.Condition, info.Duration, self);
			NK.Tell(owner, $"The Juche Statue has fallen! Our troops' morale collapses for {info.Duration / 25} seconds.");
			if (e.Attacker?.Owner != null && self.World.LocalPlayer == e.Attacker.Owner)
				TextNotificationsManager.AddSystemLine("North Korea", "You toppled the Juche Statue - their troops are demoralized!");
			GameRecorder.Note(owner, "Juche Statue destroyed - army demoralized");
		}
	}

	// ================================================================================================
	// Military First: power plants of this player sometimes black out for a while.
	// ================================================================================================
	[TraitLocation(SystemActors.Player)]
	[Desc("Every so often one of this player's power plants blacks out (gets a condition) for a while.")]
	public class PowerBlackoutsInfo : TraitInfo
	{
		public readonly HashSet<string> Factions = [];
		public readonly string Condition = "blackout";
		public readonly int MinInterval = 3000;
		public readonly int MaxInterval = 6000;
		public readonly int Duration = 500;

		public override object Create(ActorInitializer init) { return new PowerBlackouts(init.Self, this); }
	}

	public class PowerBlackouts : ITick
	{
		readonly PowerBlackoutsInfo info;
		int timer;

		public PowerBlackouts(Actor self, PowerBlackoutsInfo info)
		{
			this.info = info;
			timer = info.MaxInterval;
		}

		void ITick.Tick(Actor self)
		{
			var p = self.Owner;
			if (!info.Factions.Contains(p.Faction.InternalName) || p.WinState != WinState.Undefined || --timer > 0)
				return;

			var w = self.World;
			timer = info.MinInterval + w.SharedRandom.Next(System.Math.Max(1, info.MaxInterval - info.MinInterval));
			var plants = w.Actors.Where(a => a.Owner == p && !a.IsDead && a.IsInWorld && a.Info.TraitInfos<PowerInfo>().Any(x => x.Amount > 0)
				&& a.TraitsImplementing<ExternalCondition>().Any(c => c.Info.Condition == info.Condition)).ToList();
			if (plants.Count == 0)
				return;
			var plant = plants[w.SharedRandom.Next(plants.Count)];
			NK.Grant(plant, info.Condition, info.Duration, this);
			NK.Tell(p, $"Blackout! A power plant has gone dark for {info.Duration / 25} seconds.");
		}
	}

	// ================================================================================================
	// Trash Balloons: a flight of balloons drifts from your base to the target and drops its junk there:
	// light damage, and refineries under it are clogged for a while.
	// ================================================================================================
	public class TrashBalloonPowerInfo : SupportPowerInfo
	{
		public readonly int Balloons = 8;
		public readonly int Speed = 120;
		public readonly WDist Spread = WDist.FromCells(2);

		[WeaponReference]
		public readonly string Weapon = "TrashDrop";

		public readonly string ClogCondition = "clogged";
		public readonly int ClogDuration = 750;

		public readonly string Image = "trashballoon";

		[SequenceReference(nameof(Image))]
		public readonly string Sequence = "idle";

		public override object Create(ActorInitializer init) { return new TrashBalloonPower(init.Self, this); }
	}

	public class TrashBalloonPower : SupportPower
	{
		readonly TrashBalloonPowerInfo info;

		public TrashBalloonPower(Actor self, TrashBalloonPowerInfo info)
			: base(self, info) { this.info = info; }

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			base.Activate(self, order, manager);
			PlayLaunchSounds();
			var w = self.World;
			var target = order.Target.CenterPosition;
			var from = self.CenterPosition;
			var weapon = w.Map.Rules.Weapons[info.Weapon.ToLowerInvariant()];
			for (var i = 0; i < info.Balloons; i++)
			{
				var off = new WVec(w.SharedRandom.Next(-info.Spread.Length, info.Spread.Length), w.SharedRandom.Next(-info.Spread.Length, info.Spread.Length), 0);
				var start = from + off / 2 + new WVec(0, 0, 2048);
				var dest = target + off;
				var delay = i * 12;
				w.AddFrameEndTask(ww => ww.Add(new TrashBalloon(ww, self.Owner, start, dest, info, weapon, delay)));
			}

			var victim = w.ActorMap.GetActorsAt(w.Map.CellContaining(target)).Select(a => a.Owner).FirstOrDefault(p => p.Playable && p != self.Owner)
				?? w.FindActorsInCircle(target, WDist.FromCells(8)).Select(a => a.Owner).FirstOrDefault(p => p.Playable && p != self.Owner);
			if (victim != null && w.LocalPlayer == victim)
				TextNotificationsManager.AddSystemLine("North Korea", "Balloons are drifting over the border... they're full of TRASH!");
			GameRecorder.Note(self.Owner, $"trash balloons launched at {w.Map.CellContaining(target)}" + (victim != null ? $" ({victim.ResolvedPlayerName.Trim()})" : ""));
		}
	}

	sealed class TrashBalloon : IEffect, ISpatiallyPartitionable
	{
		readonly World world;
		readonly Player owner;
		readonly TrashBalloonPowerInfo info;
		readonly WeaponInfo weapon;
		readonly Animation anim;
		readonly WPos dest;
		WPos pos;
		int delay, ticks;

		public TrashBalloon(World world, Player owner, WPos from, WPos dest, TrashBalloonPowerInfo info, WeaponInfo weapon, int delay)
		{
			this.world = world;
			this.owner = owner;
			this.info = info;
			this.weapon = weapon;
			this.dest = dest + new WVec(0, 0, 2048);
			this.delay = delay;
			pos = from;
			anim = new Animation(world, info.Image);
			anim.PlayRepeating(info.Sequence);
			world.ScreenMap.Add(this, pos, anim.Image);
		}

		public void Tick(World w)
		{
			if (--delay > 0)
				return;
			ticks++;
			var d = dest - pos;
			if (d.Length > info.Speed)
			{
				// Drift, with a gentle bob.
				var bob = new WVec(0, 0, (int)(System.Math.Sin(ticks * 0.15) * 12));
				pos += d * info.Speed / d.Length + bob;
				w.ScreenMap.Update(this, pos, anim.Image);
				return;
			}

			// Pop: the junk falls.
			var ground = new WPos(pos.X, pos.Y, 0);
			w.AddFrameEndTask(ww =>
			{
				ww.Remove(this);
				ww.ScreenMap.Remove(this);
				ww.Add(new SpriteEffect(pos, ww, "explosion", "piff", "effect"));
				weapon.Impact(Target.FromPos(ground), new WarheadArgs { Weapon = weapon, Source = ground, SourceActor = owner.PlayerActor, WeaponTarget = Target.FromPos(ground) });
				foreach (var a in ww.FindActorsInCircle(ground, WDist.FromCells(3)).Where(a => a.Info.HasTraitInfo<RefineryInfo>() && a.Owner != owner))
					NK.Grant(a, info.ClogCondition, info.ClogDuration, this);
			});
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr) => anim.Render(pos, wr.Palette("effect"));
	}
}
