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

using System.Collections.Immutable;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	[Desc("Cancels the current attack order as soon as one of the listed armaments fires,",
		"so a one-off weapon (like a nuke) fires once per order instead of every time it reloads.")]
	public class StopAttackAfterFiringInfo : TraitInfo
	{
		[FieldLoader.Require]
		[Desc("Armaments that end the attack order when they fire.")]
		public readonly ImmutableArray<string> Armaments = [];

		public override object Create(ActorInitializer init) { return new StopAttackAfterFiring(this); }
	}

	public class StopAttackAfterFiring : INotifyAttack
	{
		readonly StopAttackAfterFiringInfo info;

		public StopAttackAfterFiring(StopAttackAfterFiringInfo info)
		{
			this.info = info;
		}

		void INotifyAttack.PreparingAttack(Actor self, in Target target, Armament a, Barrel barrel) { }

		void INotifyAttack.Attacking(Actor self, in Target target, Armament a, Barrel barrel)
		{
			if (!info.Armaments.Contains(a.Info.Name))
				return;

			self.World.AddFrameEndTask(w =>
			{
				if (self.IsDead)
					return;

				foreach (var attack in self.TraitsImplementing<AttackBase>())
					attack.OnStopOrder(self);
			});
		}
	}
}
