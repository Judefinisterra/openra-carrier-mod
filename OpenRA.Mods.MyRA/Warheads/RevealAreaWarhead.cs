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

using OpenRA.GameRules;
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Warheads;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Warheads
{
	[Desc("Lifts the fog of war around the impact for a while, so the shooter can watch the blast.")]
	public class RevealAreaWarhead : Warhead
	{
		[Desc("Radius of the revealed area.")]
		public readonly WDist Range = WDist.FromCells(5);

		[Desc("How long the area stays revealed, in ticks (25 ticks = 1 second).")]
		public readonly int Duration = 150;

		[Desc("Who gets to see the revealed area.")]
		public readonly PlayerRelationship RevealTo = PlayerRelationship.Ally;

		public override void DoImpact(in Target target, WarheadArgs args)
		{
			var source = args.SourceActor;
			if (source == null || source.Owner == null)
				return;

			var pos = target.CenterPosition;
			var effect = new RevealShroudEffect(new WPos(pos.X, pos.Y, 0), Range, Shroud.SourceType.Visibility,
				source.Owner, RevealTo, 0, Duration);

			source.World.AddFrameEndTask(w => w.Add(effect));
		}
	}
}
