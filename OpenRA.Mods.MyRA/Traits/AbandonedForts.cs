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
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

// Abandoned fortifications (Island Hopper): neutral walls, pillboxes and gun emplacements placed by the map. Each
// cluster is one fort. Build a base inside (or right next to) it and the whole fort becomes yours; lose every building
// you had there and it goes back to being abandoned.
namespace OpenRA.Mods.MyRA.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Neutral fortifications placed on the map can be claimed by building inside them.")]
	public class AbandonedFortsInfo : TraitInfo
	{
		[Desc("Actor types that count as fortification pieces.")]
		public readonly ImmutableArray<string> Types = ["brik", "sbag", "pbox", "hbox", "gun", "ftur", "agun", "sam", "tsla"];

		[Desc("Pieces this close to each other belong to the same fort.")]
		public readonly int ClusterDistance = 4;

		[Desc("How far outside a fort's outline a building still counts as 'inside'.")]
		public readonly int Margin = 2;

		public readonly int Interval = 50;

		public override object Create(ActorInitializer init) { return new AbandonedForts(this); }
	}

	public class AbandonedForts : ITick, IWorldLoaded
	{
		sealed class Fort
		{
			public List<Actor> Pieces;
			public Rectangle Box;
			public Player Owner;
		}

		readonly AbandonedFortsInfo info;
		readonly List<Fort> forts = [];
		Player neutral;

		public AbandonedForts(AbandonedFortsInfo info) { this.info = info; }

		void IWorldLoaded.WorldLoaded(World w, OpenRA.Graphics.WorldRenderer wr)
		{
			neutral = w.Players.FirstOrDefault(p => p.InternalName == "Neutral");
			if (neutral == null)
				return;

			var pieces = w.Actors.Where(a => a.Owner == neutral && info.Types.Contains(a.Info.Name)).ToList();
			var left = new HashSet<Actor>(pieces);
			var d2 = info.ClusterDistance * info.ClusterDistance;
			while (left.Count > 0)
			{
				var seed = left.First();
				left.Remove(seed);
				var group = new List<Actor> { seed };
				for (var i = 0; i < group.Count; i++)
				{
					var near = left.Where(a => (a.Location - group[i].Location).LengthSquared <= d2).ToList();
					foreach (var n in near)
					{
						left.Remove(n);
						group.Add(n);
					}
				}

				if (group.Count < 4)
					continue;
				var minX = group.Min(a => a.Location.X);
				var minY = group.Min(a => a.Location.Y);
				var maxX = group.Max(a => a.Location.X);
				var maxY = group.Max(a => a.Location.Y);
				forts.Add(new Fort
				{
					Pieces = group,
					Box = Rectangle.FromLTRB(minX - info.Margin, minY - info.Margin, maxX + info.Margin + 1, maxY + info.Margin + 1),
					Owner = neutral,
				});
			}
		}

		bool Inside(Fort f, Actor a) => f.Box.Contains(a.Location.X, a.Location.Y);

		void ITick.Tick(Actor self)
		{
			var w = self.World;
			if (forts.Count == 0 || w.WorldTick % info.Interval != 0)
				return;

			// Buildings that aren't fort pieces themselves.
			var buildings = w.ActorsHavingTrait<Building>()
				.Where(a => !a.IsDead && a.IsInWorld && a.Owner.Playable && !a.Owner.NonCombatant && !info.Types.Contains(a.Info.Name))
				.ToList();

			foreach (var f in forts)
			{
				f.Pieces.RemoveAll(p => p.IsDead || !p.IsInWorld);
				if (f.Pieces.Count == 0)
					continue;

				var inside = buildings.Where(b => Inside(f, b)).ToList();
				if (f.Owner == neutral)
				{
					var claimer = inside.FirstOrDefault()?.Owner;
					if (claimer != null)
						Hand(w, f, claimer, $"{claimer.ResolvedPlayerName} has taken over an abandoned fort!");
				}
				else if (!inside.Any(b => b.Owner == f.Owner))
					Hand(w, f, neutral, $"{f.Owner.ResolvedPlayerName} lost a fort - it's abandoned again.");
			}
		}

		void Hand(World w, Fort f, Player to, string message)
		{
			var from = f.Owner;
			f.Owner = to;
			foreach (var p in f.Pieces.ToList())
				w.AddFrameEndTask(_ =>
				{
					if (!p.IsDead && p.IsInWorld)
						p.ChangeOwnerSync(to);
				});

			GameRecorder.Note(to == neutral ? from : to, message);
			if (w.LocalPlayer != null && (w.LocalPlayer == to || w.LocalPlayer == from))
				TextNotificationsManager.AddSystemLine("Island Hopper", message);
		}
	}
}
