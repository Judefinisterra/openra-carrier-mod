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
using OpenRA.GameRules;
using OpenRA.Mods.Common.Warheads;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Warheads
{
	[Desc("Spawns an actor (e.g. a burning patch) on the impact cell, or a random cell close to it.")]
	public class SpawnActorWarhead : Warhead
	{
		[FieldLoader.Require]
		[Desc("Actor to spawn. It belongs to the neutral player unless OwnedBySource is set.")]
		public readonly string Actor = null;

		[Desc("Chance (percent) that anything is spawned.")]
		public readonly int Chance = 100;

		[Desc("Pick a random cell up to this many cells away from the impact.")]
		public readonly int Scatter = 0;

		[Desc("Terrain types the actor may be spawned on.")]
		public readonly string[] TerrainTypes = ["Clear", "Road", "Rough", "Ore", "Gems", "Beach"];

		[Desc("Don't spawn if this many of the actor already stand within 1 cell.")]
		public readonly int MaxNearby = 1;

		public readonly bool OwnedBySource = false;

		public override void DoImpact(in Target target, WarheadArgs args)
		{
			var source = args.SourceActor;
			if (source == null)
				return;

			var w = source.World;
			if (w.SharedRandom.Next(100) >= Chance)
				return;

			var cell = w.Map.CellContaining(target.CenterPosition);
			if (Scatter > 0)
				cell += new CVec(w.SharedRandom.Next(-Scatter, Scatter + 1), w.SharedRandom.Next(-Scatter, Scatter + 1));

			if (!w.Map.Contains(cell) || !TerrainTypes.Contains(w.Map.GetTerrainInfo(cell).Type))
				return;

			var name = Actor.ToLowerInvariant();
			var near = w.FindActorsInCircle(w.Map.CenterOfCell(cell), WDist.FromCells(1)).Count(a => a.Info.Name == name);
			if (near >= MaxNearby)
				return;

			var owner = OwnedBySource ? source.Owner : w.Players.FirstOrDefault(p => p.InternalName == "Neutral") ?? source.Owner;
			w.AddFrameEndTask(ww => ww.CreateActor(name, new TypeDictionary { new LocationInit(cell), new OwnerInit(owner) }));
		}
	}
}
