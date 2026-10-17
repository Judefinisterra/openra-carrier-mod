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
using OpenRA.Activities;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.MyRA.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Activities
{
	public class ReturnToCarrier : Activity
	{
		readonly Actor carrier;
		readonly CarrierParent carrierTrait;
		readonly WDist dockDistance;

		public ReturnToCarrier(Actor carrier, CarrierParent carrierTrait, WDist dockDistance)
		{
			this.carrier = carrier;
			this.carrierTrait = carrierTrait;
			this.dockDistance = dockDistance;
			ChildHasPriority = false;
		}

		public override bool Tick(Actor self)
		{
			if (IsCanceling || carrier.IsDead || !carrier.IsInWorld)
				return true;

			if ((carrier.CenterPosition - self.CenterPosition).HorizontalLengthSquared <= dockDistance.LengthSquared)
			{
				ChildActivity?.Cancel(self);
				carrierTrait.Dock(self);
				return true;
			}

			// The carrier moves, so keep re-aiming at it.
			if (ChildActivity == null)
				QueueChild(new Fly(self, Target.FromActor(carrier)));

			TickChild(self);
			return false;
		}

		public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self)
		{
			yield return new TargetLineNode(Target.FromActor(carrier), Color.Green);
		}
	}
}
