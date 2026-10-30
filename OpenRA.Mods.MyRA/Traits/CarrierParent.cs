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
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	[Desc("Carries aircraft that launch against whatever this actor attacks, then return to rearm.",
		"Fire the linked armament (usually a dummy weapon with no warheads) to trigger launches.")]
	public class CarrierParentInfo : ConditionalTraitInfo, Requires<ArmamentInfo>
	{
		[ActorReference(typeof(CarrierChildInfo))]
		[FieldLoader.Require]
		[Desc("Aircraft carried, one entry per slot.")]
		public readonly ImmutableArray<string> Actors = [];

		[Desc("Armament whose shots trigger launches.")]
		public readonly string Armament = "primary";

		[Desc("Ticks between two launches.")]
		public readonly int LaunchInterval = 12;

		[Desc("Ticks a docked aircraft needs to rearm and repair before it can launch again.")]
		public readonly int RearmDelay = 100;

		[Desc("Ticks to build a replacement for a destroyed aircraft.")]
		public readonly int RespawnDelay = 500;

		[Desc("Rebuild destroyed aircraft for free. When false, empty slots stay empty until",
			"a new aircraft of the right type (built elsewhere) joins the carrier.")]
		public readonly bool Respawns = true;

		[Desc("Where aircraft appear, relative to the carrier's centre and facing.")]
		public readonly WVec LaunchOffset = WVec.Zero;

		[Desc("Altitude above the carrier at which aircraft appear.")]
		public readonly WDist LaunchAltitude = new(256);

		public override object Create(ActorInitializer init) { return new CarrierParent(init.Self, this); }
	}

	public class CarrierParent : ConditionalTrait<CarrierParentInfo>, ITick, INotifyAttack, INotifyKilled,
		INotifyActorDisposing, INotifyOwnerChanged
	{
		sealed class Slot
		{
			public readonly string ActorType;
			public Actor Actor;
			public bool Docked;
			public int Timer;

			public Slot(string actorType) { ActorType = actorType; }
		}

		readonly Actor self;
		readonly List<Slot> slots;
		IFacing facing;
		BodyOrientation body;
		int launchCooldown;

		public CarrierParent(Actor self, CarrierParentInfo info)
			: base(info)
		{
			this.self = self;
			slots = info.Actors.Select(a => new Slot(a.ToLowerInvariant())).ToList();
		}

		protected override void Created(Actor self)
		{
			base.Created(self);
			facing = self.TraitOrDefault<IFacing>();
			body = self.TraitOrDefault<BodyOrientation>();

			foreach (var slot in slots)
				BuildChild(slot);
		}

		public int DockedCount => slots.Count(s => s.Actor != null && s.Docked);

		void BuildChild(Slot slot)
		{
			slot.Actor = self.World.CreateActor(false, slot.ActorType, [new OwnerInit(self.Owner)]);
			slot.Actor.Trait<CarrierChild>().SetCarrier(self, this);
			slot.Docked = true;
			slot.Timer = 0;
		}

		void ITick.Tick(Actor self)
		{
			if (launchCooldown > 0)
				launchCooldown--;

			foreach (var slot in slots)
			{
				if (slot.Actor == null)
				{
					if (Info.Respawns && --slot.Timer <= 0)
						BuildChild(slot);
				}
				else if (slot.Docked && slot.Timer > 0)
					slot.Timer--;
			}
		}

		void INotifyAttack.PreparingAttack(Actor self, in Target target, Armament a, Barrel barrel) { }

		void INotifyAttack.Attacking(Actor self, in Target target, Armament a, Barrel barrel)
		{
			if (IsTraitDisabled || a.Info.Name != Info.Armament || launchCooldown > 0)
				return;

			var slot = slots.FirstOrDefault(s => s.Actor != null && s.Docked && s.Timer <= 0);
			if (slot == null)
				return;

			launchCooldown = Info.LaunchInterval;
			Launch(slot, target);
		}

		void Launch(Slot slot, Target target)
		{
			var child = slot.Actor;
			slot.Docked = false;

			var yaw = facing?.Facing ?? WAngle.Zero;
			var offset = body != null ? body.LocalToWorld(Info.LaunchOffset.Rotate(body.QuantizeOrientation(self.Orientation))) : WVec.Zero;
			var pos = self.CenterPosition + offset + new WVec(0, 0, Info.LaunchAltitude.Length);

			self.World.AddFrameEndTask(w =>
			{
				if (self.IsDead || child.IsDead)
					return;

				var aircraft = child.Trait<Aircraft>();
				aircraft.SetPosition(child, pos);
				aircraft.Facing = yaw;
				w.Add(child);

				child.Trait<CarrierChild>().Launched();
				child.QueueActivity(false, new FlyAttack(child, AttackSource.Default, target, false, Color.Red));
			});
		}

		public bool HasFreeSlotFor(string actorType)
		{
			return !IsTraitDisabled && !self.IsDead && slots.Any(s => s.Actor == null && s.ActorType == actorType);
		}

		/// <summary>Take in an aircraft that was built elsewhere. It still has to fly here and dock.</summary>
		public bool TryAdopt(Actor child)
		{
			if (!HasFreeSlotFor(child.Info.Name))
				return false;

			var slot = slots.First(s => s.Actor == null && s.ActorType == child.Info.Name);
			slot.Actor = child;
			slot.Docked = false;
			slot.Timer = 0;
			child.Trait<CarrierChild>().SetCarrier(self, this);
			return true;
		}

		public void Dock(Actor child)
		{
			var slot = slots.FirstOrDefault(s => s.Actor == child);
			if (slot == null || slot.Docked)
				return;

			slot.Docked = true;
			slot.Timer = Info.RearmDelay;

			foreach (var pool in child.TraitsImplementing<AmmoPool>())
				pool.GiveAmmo(child, pool.Info.Ammo);

			var health = child.TraitOrDefault<Health>();
			if (health != null && health.HP < health.MaxHP)
				health.InflictDamage(child, self, new Damage(health.HP - health.MaxHP), true);

			child.CancelActivity();
			self.World.AddFrameEndTask(w => w.Remove(child));
		}

		public void ChildKilled(Actor child)
		{
			var slot = slots.FirstOrDefault(s => s.Actor == child);
			if (slot == null)
				return;

			slot.Actor = null;
			slot.Docked = false;
			slot.Timer = Info.RespawnDelay;
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			foreach (var slot in slots)
			{
				var child = slot.Actor;
				slot.Actor = null;
				if (child == null || child.IsDead)
					continue;

				// Docked aircraft go down with the ship; airborne ones crash.
				if (slot.Docked)
					child.Dispose();
				else
					child.Kill(e.Attacker);
			}
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			foreach (var slot in slots)
			{
				if (slot.Actor != null && !slot.Actor.Disposed)
					slot.Actor.Dispose();

				slot.Actor = null;
			}
		}

		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			foreach (var slot in slots)
				if (slot.Actor != null && !slot.Actor.IsDead)
					slot.Actor.ChangeOwner(newOwner);
		}
	}
}
