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
	[Desc("Plays a firing sound whenever these armaments shoot and the last one has finished, so the gun is never",
		"silent (a weapon's StartBurstReport is skipped when a burst is cut short and picked up again).")]
	public class FiringSoundInfo : TraitInfo
	{
		[Desc("Armaments that make the sound.")]
		public readonly ImmutableArray<string> Armaments = ["primary"];

		[FieldLoader.Require]
		public readonly string Sound = null;

		[Desc("Length of the sound in ticks: a new one starts once this has passed.")]
		public readonly int Interval = 25;

		public override object Create(ActorInitializer init) { return new FiringSound(this); }
	}

	public class FiringSound : INotifyAttack
	{
		readonly FiringSoundInfo info;
		long nextTick;

		public FiringSound(FiringSoundInfo info) { this.info = info; }

		void INotifyAttack.Attacking(Actor self, in Target target, Armament a, Barrel barrel)
		{
			if (!info.Armaments.Contains(a.Info.Name) || self.World.WorldTick < nextTick)
				return;

			nextTick = self.World.WorldTick + info.Interval;
			Game.Sound.Play(SoundType.World, info.Sound, self.CenterPosition);
		}

		void INotifyAttack.PreparingAttack(Actor self, in Target target, Armament a, Barrel barrel) { }
	}
}
