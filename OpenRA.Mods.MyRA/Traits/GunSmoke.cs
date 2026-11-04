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
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	[Desc("Every shot from these armaments leaves a puff of gun smoke hanging in the air where it was fired",
		"(the A-10's cannon leaves a trail of it along its strafing run).")]
	public class GunSmokeInfo : TraitInfo
	{
		[Desc("Armaments that smoke.")]
		public readonly ImmutableArray<string> Armaments = ["primary"];

		public readonly string Image = "smoke_m";

		[SequenceReference(nameof(Image))]
		public readonly string Sequence = "idle";

		[PaletteReference]
		public readonly string Palette = "effect";

		[Desc("Puffs per shot.")]
		public readonly int Puffs = 2;

		[Desc("Random spread of the puffs.")]
		public readonly WDist Spread = new(220);

		public override object Create(ActorInitializer init) { return new GunSmoke(this); }
	}

	public class GunSmoke : INotifyAttack
	{
		readonly GunSmokeInfo info;

		public GunSmoke(GunSmokeInfo info) { this.info = info; }

		void INotifyAttack.Attacking(Actor self, in Target target, Armament a, Barrel barrel)
		{
			if (!info.Armaments.Contains(a.Info.Name))
				return;

			var muzzle = self.CenterPosition + a.MuzzleOffset(self, barrel);
			var r = Game.CosmeticRandom;
			var s = info.Spread.Length;
			for (var i = 0; i < info.Puffs; i++)
			{
				var p = muzzle + new WVec(r.Next(-s, s), r.Next(-s, s), r.Next(-s / 2, s / 2));
				self.World.AddFrameEndTask(w => w.Add(new SpriteEffect(p, w, info.Image, info.Sequence, info.Palette)));
			}
		}

		void INotifyAttack.PreparingAttack(Actor self, in Target target, Armament a, Barrel barrel) { }
	}
}
