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

using System.Linq;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.MyRA.Activities;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	[Desc("Aircraft that lives on a CarrierParent. Returns to it when out of ammo or out of work.")]
	public class CarrierChildInfo : TraitInfo, Requires<AircraftInfo>
	{
		[Desc("Horizontal distance from the carrier at which the aircraft lands on it.")]
		public readonly WDist DockDistance = new(512);

		public override object Create(ActorInitializer init) { return new CarrierChild(this); }
	}

	public class CarrierChild : INotifyCreated, ITick, INotifyKilled
	{
		readonly CarrierChildInfo info;
		Actor carrier;
		CarrierParent carrierTrait;
		AmmoPool[] ammoPools;
		Aircraft aircraft;
		bool returning;
		int searchCooldown;

		public CarrierChild(CarrierChildInfo info)
		{
			this.info = info;
		}

		void INotifyCreated.Created(Actor self)
		{
			ammoPools = self.TraitsImplementing<AmmoPool>().ToArray();
			aircraft = self.TraitOrDefault<Aircraft>();
		}

		public void SetCarrier(Actor carrier, CarrierParent carrierTrait)
		{
			this.carrier = carrier;
			this.carrierTrait = carrierTrait;
		}

		public void Launched()
		{
			returning = false;
		}

		void ITick.Tick(Actor self)
		{
			if (!self.IsInWorld)
				return;

			// Built on its own (not by a carrier): once idle, join the nearest friendly carrier with a free slot.
			if (carrier == null)
			{
				if (--searchCooldown > 0)
					return;

				searchCooldown = 25;
				var idle = self.CurrentActivity == null || self.CurrentActivity is FlyIdle;
				if (!idle)
					return;

				var home = self.World.ActorsWithTrait<CarrierParent>()
					.Where(p => p.Actor.Owner == self.Owner && p.Actor.IsInWorld && p.Trait.HasFreeSlotFor(self.Info.Name))
					.MinByOrDefault(p => (p.Actor.CenterPosition - self.CenterPosition).HorizontalLengthSquared);

				if (home.Actor != null && home.Trait.TryAdopt(self))
				{
					returning = true;
					self.QueueActivity(false, new ReturnToCarrier(carrier, carrierTrait, info.DockDistance));
				}
				else if (aircraft != null && !aircraft.AtLandAltitude)
				{
					// No carrier has room: land at the nearest Airfield or Helipad and wait there.
					self.QueueActivity(false, new ReturnToBase(self, null, true));
				}

				return;
			}

			// No ship to land on: ditch.
			if (carrier.IsDead || !carrier.IsInWorld)
			{
				self.Kill(self);
				return;
			}

			var outOfAmmo = ammoPools.Length > 0 && ammoPools.All(p => !p.HasAmmo);
			var activity = self.CurrentActivity;
			var nothingToDo = activity == null || (activity is FlyIdle && !activity.IsCanceling);

			if ((!returning && outOfAmmo) || nothingToDo)
			{
				returning = true;
				self.QueueActivity(false, new ReturnToCarrier(carrier, carrierTrait, info.DockDistance));
			}
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			if (carrier != null && !carrier.IsDead)
				carrierTrait.ChildKilled(self);
		}
	}
}
