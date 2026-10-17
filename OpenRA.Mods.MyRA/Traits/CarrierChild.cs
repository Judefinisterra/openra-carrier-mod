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
		bool returning;

		public CarrierChild(CarrierChildInfo info)
		{
			this.info = info;
		}

		void INotifyCreated.Created(Actor self)
		{
			ammoPools = self.TraitsImplementing<AmmoPool>().ToArray();
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
			if (carrier == null || !self.IsInWorld)
				return;

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
