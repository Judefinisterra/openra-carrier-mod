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
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// ================================================================================================
	// Zombie apocalypse: every infantryman who dies, on any side, leaves a grave where he fell. The Zombie Lab
	// superweapon raises them all at once: a zombie climbs out of every grave and goes for the nearest living
	// unit. Zombies belong to the hostile Creeps player, so they attack everyone.
	// ================================================================================================
	[TraitLocation(SystemActors.World)]
	[Desc("Keeps a grave for every infantry death and raises them as zombies on demand. Attach to the world actor.")]
	public class ZombieGraveyardInfo : TraitInfo
	{
		[ActorReference]
		[Desc("Actor that climbs out of each grave.")]
		public readonly string Zombie = "risen";

		[Desc("Player the zombies belong to. Should be hostile to everyone.")]
		public readonly string ZombiePlayer = "Creeps";

		[Desc("At most this many graves are kept (the oldest are forgotten first).")]
		public readonly int MaxGraves = 400;

		[Desc("The dead climb out over this many ticks.")]
		public readonly int RiseTicks = 125;

		public readonly string RiseImage = "explosion";

		[SequenceReference(nameof(RiseImage))]
		public readonly string RiseSequence = "piffs";

		[PaletteReference]
		public readonly string RisePalette = "effect";

		public readonly string RiseSound = "zombierise.wav";

		public override object Create(ActorInitializer init) { return new ZombieGraveyard(this); }
	}

	public class ZombieGraveyard : ITick
	{
		readonly ZombieGraveyardInfo info;
		readonly List<CPos> graves = [];
		readonly List<(int Due, CPos Cell)> rising = [];

		public ZombieGraveyard(ZombieGraveyardInfo info)
		{
			this.info = info;
		}

		public int Graves => graves.Count;

		public void Record(CPos cell)
		{
			graves.Add(cell);
			if (graves.Count > info.MaxGraves)
				graves.RemoveAt(0);
		}

		// Raises every grave. Returns how many zombies are coming.
		public int Rise(World world)
		{
			var now = world.WorldTick;
			foreach (var cell in graves)
				rising.Add((now + world.SharedRandom.Next(info.RiseTicks + 1), cell));

			var count = graves.Count;
			graves.Clear();
			if (count > 0)
				Game.Sound.Play(SoundType.UI, info.RiseSound);

			return count;
		}

		void ITick.Tick(Actor self)
		{
			if (rising.Count == 0)
				return;

			var w = self.World;
			var owner = w.Players.FirstOrDefault(p => p.InternalName == info.ZombiePlayer);
			if (owner == null)
			{
				rising.Clear();
				return;
			}

			for (var i = rising.Count - 1; i >= 0; i--)
			{
				var (due, cell) = rising[i];
				if (w.WorldTick < due)
					continue;

				rising.RemoveAt(i);
				w.AddFrameEndTask(ww =>
				{
					var sub = ww.ActorMap.FreeSubCell(cell);
					if (sub == SubCell.Invalid)
						return;

					ww.CreateActor(info.Zombie,
					[
						new LocationInit(cell),
						new SubCellInit(sub),
						new OwnerInit(owner),
						new FacingInit(WAngle.FromFacing(ww.SharedRandom.Next(256))),
					]);
					ww.Add(new SpriteEffect(ww.Map.CenterOfCell(cell), ww, info.RiseImage, info.RiseSequence, info.RisePalette));
				});
			}
		}
	}

	[Desc("Superweapon with no target: raises every grave on the map as a zombie.")]
	public class ZombieApocalypsePowerInfo : SupportPowerInfo
	{
		public override object Create(ActorInitializer init) { return new ZombieApocalypsePower(init.Self, this); }
	}

	public class ZombieApocalypsePower : SupportPower
	{
		public ZombieApocalypsePower(Actor self, ZombieApocalypsePowerInfo info)
			: base(self, info) { }

		// Nothing to aim: clicking the icon fires it.
		public override void SelectTarget(Actor self, string order, SupportPowerManager manager)
		{
			self.World.IssueOrder(new Order(order, manager.Self, false));
		}

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			base.Activate(self, order, manager);
			PlayLaunchSounds();

			var raised = self.World.WorldActor.TraitOrDefault<ZombieGraveyard>()?.Rise(self.World) ?? 0;
			var who = self.Owner.ResolvedPlayerName;
			self.World.AddFrameEndTask(w =>
			{
				if (w.LocalPlayer != null)
					TextNotificationsManager.AddSystemLine("Zombie Apocalypse", raised > 0
						? $"{who} has raised the dead: {raised} zombies are clawing out of their graves!"
						: $"{who} tried to raise the dead, but nobody has died yet.");
			});
		}
	}

	[Desc("Leaves a grave in the ZombieGraveyard when this actor dies.")]
	public class LeavesGraveInfo : TraitInfo<LeavesGrave> { }

	public class LeavesGrave : INotifyKilled
	{
		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			if (self.Info.HasTraitInfo<ZombieBrainInfo>())
				return;

			self.World.WorldActor.TraitOrDefault<ZombieGraveyard>()?.Record(self.Location);
		}
	}

	[Desc("Every so often, goes for the nearest living enemy unit (or building, if no unit is in reach).")]
	public class ZombieBrainInfo : TraitInfo, Requires<AttackBaseInfo>
	{
		[Desc("Ticks between looks around.")]
		public readonly int Interval = 40;

		[Desc("How far it looks for prey.")]
		public readonly WDist Range = WDist.FromCells(60);

		public override object Create(ActorInitializer init) { return new ZombieBrain(init.Self, this); }
	}

	public class ZombieBrain : ITick
	{
		readonly ZombieBrainInfo info;
		readonly AttackBase attack;
		int wait;
		Actor prey;

		public ZombieBrain(Actor self, ZombieBrainInfo info)
		{
			this.info = info;
			attack = self.Trait<AttackBase>();
			wait = self.World.SharedRandom.Next(info.Interval);
		}

		void ITick.Tick(Actor self)
		{
			if (--wait > 0)
				return;

			wait = info.Interval;
			if (prey != null && !prey.IsDead && prey.IsInWorld && !self.IsIdle)
				return;

			bool Living(Actor a) => !a.IsDead && a.IsInWorld && a != self && !a.Owner.NonCombatant
				&& self.Owner.RelationshipWith(a.Owner) == PlayerRelationship.Enemy && a.IsTargetableBy(self);

			var near = self.World.FindActorsInCircle(self.CenterPosition, info.Range).Where(Living).ToList();
			prey = near.Where(a => a.Info.HasTraitInfo<IMoveInfo>()).MinByOrDefault(a => (a.CenterPosition - self.CenterPosition).LengthSquared)
				?? near.MinByOrDefault(a => (a.CenterPosition - self.CenterPosition).LengthSquared);

			if (prey != null)
				attack.AttackTarget(Target.FromActor(prey), AttackSource.Default, false, true);
		}
	}
}
