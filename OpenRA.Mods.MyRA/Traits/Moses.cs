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

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using OpenRA.Activities;
using OpenRA.Effects;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// ================================================================================================
	// Moses (Israel): right-click any water with him selected. He walks to the shore and parts the sea: a
	// three-cell-wide strip of dry seabed opens out to the far shore, walls of water foaming on either side.
	// Land units can cross it; ships and submarines can't (any caught in it are pushed aside). It stays open for
	// as long as he lives. When he dies, or parts the sea somewhere else, the water crashes back and anything
	// still on the seabed drowns.
	// ================================================================================================
	[Desc("Right-click water to part it: a strip of dry land to the far shore that lasts while this actor lives.")]
	public class PartsTheSeaInfo : TraitInfo, Requires<MobileInfo>
	{
		[Desc("Terrain types that can be parted.")]
		public readonly ImmutableArray<string> WaterTypes = ["Water", "River"];

		[Desc("What the parted cells become.")]
		public readonly string DryTerrain = "Clear";

		[Desc("Longest crossing, in cells.")]
		public readonly int MaxLength = 64;

		[Desc("He must be this close to the water to part it; further away, he walks to the shore first.")]
		public readonly int ReachCells = 2;

		[Desc("The seabed opens one cell further out every this many ticks.")]
		public readonly int TicksPerCell = 2;

		public readonly string OpenSound = "seapart.wav";

		public readonly string CloseSound = "seaclose.wav";

		public readonly string Image = "seapath";

		[PaletteReference]
		public readonly string Palette = "effect";

		[VoiceReference]
		public readonly string Voice = "Action";

		public override object Create(ActorInitializer init) { return new PartsTheSea(this); }
	}

	public class PartsTheSea : IIssueOrder, IResolveOrder, IOrderVoice, INotifyKilled, INotifyActorDisposing
	{
		public const string OrderID = "PartTheSea";
		readonly PartsTheSeaInfo info;
		SeaPath path;

		public PartsTheSea(PartsTheSeaInfo info)
		{
			this.info = info;
		}

		bool IsWater(Map map, CPos cell) => map.Contains(cell) && info.WaterTypes.Contains(map.GetTerrainInfo(cell).Type);

		IEnumerable<IOrderTargeter> IIssueOrder.Orders
		{
			get { yield return new PartTargeter(this); }
		}

		Order IIssueOrder.IssueOrder(Actor self, IOrderTargeter order, in Target target, bool queued)
		{
			return order.OrderID == OrderID ? new Order(OrderID, self, target, queued) : null;
		}

		string IOrderVoice.VoicePhraseForOrder(Actor self, Order order) => order.OrderString == OrderID ? info.Voice : null;

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString != OrderID)
				return;

			var map = self.World.Map;
			var target = map.CellContaining(order.Target.CenterPosition);
			var (shore, crossing, step) = Crossing(map, self.Location, target);
			if (crossing.Count == 0)
				return;

			// Walk to the near shore of the crossing, then raise the staff.
			if (!order.Queued)
				self.CancelActivity();

			if ((self.Location - crossing[0]).Length > info.ReachCells)
				self.QueueActivity(self.Trait<Mobile>().MoveTo(shore, 1));

			self.QueueActivity(new CallFunc(() => Part(self, target, step)));
			self.ShowTargetLines();
		}

		// The crossing always runs straight east-west or north-south (whichever way the target lies from him) through the
		// target cell, from the near shore to the far one. Returns the land cell on the near shore and the water cells.
		(CPos Shore, List<CPos> Cells, CVec Step) Crossing(Map map, CPos from, CPos target, CVec? axis = null)
		{
			var d = target - from;
			var step = axis ?? (Math.Abs(d.X) >= Math.Abs(d.Y) ? new CVec(d.X < 0 ? -1 : 1, 0) : new CVec(0, d.Y < 0 ? -1 : 1));
			var cells = new List<CPos>();
			if (!IsWater(map, target))
				return (from, cells, step);

			var start = target;
			for (var n = 0; n < info.MaxLength && IsWater(map, start - step); n++)
				start -= step;

			for (var c = start; IsWater(map, c) && cells.Count < info.MaxLength; c += step)
				cells.Add(c);

			return (start - step, cells, step);
		}

		void Part(Actor self, CPos target, CVec step)
		{
			if (self.IsDead)
				return;

			var map = self.World.Map;
			var (_, crossing, _) = Crossing(map, self.Location, target, step);
			if (crossing.Count == 0)
				return;

			path?.Close();

			// Three cells wide: the line plus a cell either side.
			var side = step.X != 0 ? new CVec(0, 1) : new CVec(1, 0);
			var cells = new List<CPos>();
			foreach (var c in crossing)
				foreach (var s in new[] { c, c - side, c + side })
					if (IsWater(map, s) && !cells.Contains(s))
						cells.Add(s);

			path = new SeaPath(self.World, info, cells);
			self.World.AddFrameEndTask(w => w.Add(path));
			Game.Sound.Play(SoundType.World, info.OpenSound, self.CenterPosition);

			// Step aside so he doesn't block the way onto the seabed.
			var mobile = self.Trait<Mobile>();
			var entrance = crossing[0];
			var aside = map.FindTilesInAnnulus(self.Location, 1, 3)
				.Where(c => !IsWater(map, c) && (c - entrance).LengthSquared >= 5 && mobile.CanEnterCell(c))
				.Cast<CPos?>().FirstOrDefault();
			if (aside != null)
				self.QueueActivity(mobile.MoveTo(aside.Value, 0));

			if (self.World.LocalPlayer != null)
			{
				var who = self.Owner.ResolvedPlayerName;
				var count = crossing.Count;
				self.World.AddFrameEndTask(_ => TextNotificationsManager.AddSystemLine("Moses",
					$"{who}'s Moses has parted the waters: a {count}-cell crossing is opening."));
			}
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e) => CloseOnDeath(self);

		void INotifyActorDisposing.Disposing(Actor self) => CloseOnDeath(self);

		void CloseOnDeath(Actor self)
		{
			if (path == null)
				return;

			path.Close();
			path = null;
			if (self.World.LocalPlayer != null)
				TextNotificationsManager.AddSystemLine("Moses", "Moses has fallen. The waters come crashing back!");
		}

		sealed class PartTargeter : IOrderTargeter
		{
			readonly PartsTheSea owner;

			public PartTargeter(PartsTheSea owner) { this.owner = owner; }

			public string OrderID => PartsTheSea.OrderID;
			public int OrderPriority => 6;
			public bool IsQueued { get; private set; }

			public bool TargetOverridesSelection(Actor self, in Target target, List<Actor> actorsAt, CPos xy, TargetModifiers modifiers) => true;

			public bool CanTarget(Actor self, in Target target, ref TargetModifiers modifiers, ref string cursor)
			{
				if (target.Type != TargetType.Terrain)
					return false;

				var map = self.World.Map;
				if (!owner.IsWater(map, map.CellContaining(target.CenterPosition)))
					return false;

				IsQueued = modifiers.HasModifier(TargetModifiers.ForceQueue);
				cursor = "ability";
				return true;
			}
		}
	}

	// The parted strip of sea: turns its cells into dry land one by one from the shore outwards, draws the seabed and
	// the walls of water standing up either side, and when closed floods everything back.
	// Each water cell beside the strip becomes a block of sea lifted to WallHeight: its surface, foam along the crest
	// where it drops to the seabed, and on its camera (south) side either the glassy face seen across the crossing or,
	// on the far side, the back slope melting into the flat sea. The art tiles along a row, so a wall reads as one body
	// of water.
	public class SeaPath : IEffect
	{
		const int CloseTicks = 40;
		const int RiseTicks = 15;

		// 44 pixels: just under two cells of standing water. The wall on the camera side of the crossing stands lower and
		// lets a little of the seabed show through, so it doesn't hide the units crossing behind it.
		const int WallHeight = 1877;
		const int NearWallHeight = 1024;
		const float NearWallAlpha = 0.85f;

		static readonly (CVec Dir, string Sequence)[] Shades = [(new CVec(1, 0), "shade-e"), (new CVec(-1, 0), "shade-w")];

		static readonly (Edge Flag, string Sequence, WVec Offset)[] Lips =
		[
			(Edge.LipN, "lip", new WVec(0, -597, 0)),
			(Edge.LipE, "lip-e", new WVec(427, 0, 0)),
			(Edge.LipW, "lip-w", new WVec(-427, 0, 0)),
		];

		[Flags]
		enum Edge { None = 0, InnerFace = 1, OuterFace = 2, LipN = 4, LipE = 8, LipW = 16 }

		readonly World world;
		readonly PartsTheSeaInfo info;
		readonly List<CPos> cells;
		readonly HashSet<CPos> cellSet;
		readonly List<(CPos Cell, int OpensAt, Edge Edges)> walls = [];
		readonly HashSet<CPos> wallSet = [];
		readonly byte[] previous;
		readonly byte dry;
		int opened;
		int ticks;
		int closing = -1;

		public SeaPath(World world, PartsTheSeaInfo info, List<CPos> cells)
		{
			this.world = world;
			this.info = info;
			this.cells = cells;
			cellSet = [.. cells];
			previous = new byte[cells.Count];
			dry = world.Map.Rules.TerrainInfo.GetTerrainIndex(info.DryTerrain);

			// Walls of water: the water cells right beside the strip. Each rises when the nearest part of the strip opens.
			var ring = new Dictionary<CPos, int>();
			var map = world.Map;
			bool Sea(CPos c) => map.Contains(c) && !cellSet.Contains(c) && info.WaterTypes.Contains(map.GetTerrainInfo(c).Type);
			for (var i = 0; i < cells.Count; i++)
				foreach (var d in CVec.Directions)
					if (Sea(cells[i] + d) && !ring.ContainsKey(cells[i] + d))
						ring[cells[i] + d] = i;

			foreach (var (c, opensAt) in ring)
			{
				var edges = Edge.None;
				var south = c + new CVec(0, 1);
				if (cellSet.Contains(south))
					edges |= Edge.InnerFace;
				else if (!ring.ContainsKey(south))
					edges |= Edge.OuterFace;

				if (cellSet.Contains(c + new CVec(0, -1)))
					edges |= Edge.LipN;
				if (cellSet.Contains(c + new CVec(1, 0)))
					edges |= Edge.LipE;
				if (cellSet.Contains(c + new CVec(-1, 0)))
					edges |= Edge.LipW;

				walls.Add((c, opensAt, edges));
				wallSet.Add(c);
			}
		}

		public void Close()
		{
			if (closing >= 0)
				return;

			closing = 0;
			var map = world.Map;
			for (var i = 0; i < opened; i++)
				map.CustomTerrain[cells[i]] = previous[i];

			// Everything still on the seabed that can't swim goes under.
			var drowned = 0;
			for (var i = 0; i < opened; i++)
			{
				foreach (var a in world.ActorMap.GetActorsAt(cells[i]).ToList())
				{
					if (a.IsDead || a.Info.HasTraitInfo<AircraftInfo>())
						continue;

					var mobile = a.TraitOrDefault<Mobile>();
					if (mobile != null && mobile.CanStayInCell(cells[i]))
						continue;

					a.Kill(a);
					drowned++;
				}
			}

			if (cells.Count > 0)
				Game.Sound.Play(SoundType.World, info.CloseSound, map.CenterOfCell(cells[cells.Count / 2]));

			if (drowned > 0 && world.LocalPlayer != null)
				world.AddFrameEndTask(_ => TextNotificationsManager.AddSystemLine("Moses", $"The sea swallowed {drowned} units."));
		}

		public void Tick(World w)
		{
			ticks++;
			if (closing >= 0)
			{
				if (++closing > CloseTicks)
					w.AddFrameEndTask(ww => ww.Remove(this));

				return;
			}

			// Open the next cell out from the shore.
			if (opened < cells.Count && ticks % info.TicksPerCell == 0)
			{
				var cell = cells[opened];
				previous[opened] = w.Map.CustomTerrain[cell];
				w.Map.CustomTerrain[cell] = dry;
				opened++;

				// Ships and submarines in the way are swept aside into open water.
				foreach (var a in w.ActorMap.GetActorsAt(cell).ToList())
				{
					var mobile = a.TraitOrDefault<Mobile>();
					if (mobile == null || mobile.CanStayInCell(cell))
						continue;

					var escape = w.Map.FindTilesInAnnulus(cell, 1, 10)
						.Where(c => !cellSet.Contains(c) && mobile.CanEnterCell(c))
						.Cast<CPos?>().FirstOrDefault();
					if (escape != null)
					{
						a.CancelActivity();
						mobile.SetPosition(a, escape.Value);
					}
				}
			}
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr)
		{
			var map = world.Map;
			var seqs = map.Sequences;
			var palette = wr.Palette(info.Palette);
			var flood = closing < 0 ? 0f : Math.Min(1f, closing / (float)CloseTicks);

			// Dry seabed (fading back under the water as it closes). Drawn under everything standing on it.
			var bed = seqs.GetSequence(info.Image, "seabed");
			for (var i = 0; i < opened; i++)
			{
				var c = cells[i];
				if (world.ShroudObscures(c))
					continue;

				var age = Math.Min(1f, (ticks - i * info.TicksPerCell) / 12f);
				var alpha = age * (1 - flood);
				if (alpha <= 0.01f)
					continue;

				// The seabed art is one texture cut into 4 x 4 cells, so neighbours join up.
				yield return new SpriteRenderable(bed.GetSprite(c.Y % 4 * 4 + c.X % 4), map.CenterOfCell(c), WVec.Zero, -2047,
					palette, 1f, alpha, Vector3.One, TintModifiers.IgnoreWorldTint, false);

				// The shadow at the foot of a wall running north-south, whose face the camera sees side-on.
				foreach (var (dir, seq) in Shades)
				{
					if (!wallSet.Contains(c + dir))
						continue;

					yield return new SpriteRenderable(seqs.GetSequence(info.Image, seq).GetSprite(0),
						map.CenterOfCell(c) + new WVec(dir.X * 341, 0, 0), WVec.Zero, -2046, palette, 1f, alpha,
						Vector3.One, TintModifiers.IgnoreWorldTint, false);
				}
			}

			// The walls. Sorted so that a wall south of a unit is drawn in front of it and a wall north of it behind.
			// Every cell uses the same animation frame so the water runs down the whole wall together.
			var top = seqs.GetSequence(info.Image, "top");
			var anim = ticks / 3;
			foreach (var (c, opensAt, edges) in walls)
			{
				if (opensAt >= opened || world.ShroudObscures(c))
					continue;

				var rise = Math.Clamp((ticks - opensAt * info.TicksPerCell) / (float)RiseTicks, 0f, 1f) * (1 - flood);
				if (rise <= 0.01f)
					continue;

				var near = (edges & Edge.LipN) != 0;
				var full = near ? NearWallHeight : WallHeight;
				var h = (int)(full * rise);
				var surfaceAlpha = near ? NearWallAlpha : 1f;
				var centre = map.CenterOfCell(c);
				if ((edges & (Edge.InnerFace | Edge.OuterFace)) != 0)
				{
					// The face hangs down from the raised surface to the cell's south edge.
					// The inner face is a strip 8 cells wide with 48 frames per cell, so the fish swim along the wall.
					var inner = (edges & Edge.InnerFace) != 0;
					var face = seqs.GetSequence(info.Image, inner ? "wall" : "wall-outer");
					var frame = inner ? c.X % 8 * 48 + ticks / 4 % 48 : anim % face.Length;
					var z = h - full / 2;
					yield return new SpriteRenderable(face.GetSprite(frame), centre + new WVec(0, 512, z),
						WVec.Zero, -384 - z, palette, 1f, rise, Vector3.One, TintModifiers.IgnoreWorldTint, false);
				}

				yield return new SpriteRenderable(top.GetSprite(anim % top.Length), centre + new WVec(0, 0, h),
					WVec.Zero, 256 - h, palette, 1f, surfaceAlpha, Vector3.One, TintModifiers.IgnoreWorldTint, false);

				// Foam along the crest where the wall drops away to the seabed (the lip sprites have the crest 2 pixels
				// off centre, toward the edge).
				foreach (var (flag, seq, offset) in Lips)
				{
					if ((edges & flag) == 0)
						continue;

					var lip = seqs.GetSequence(info.Image, seq);
					yield return new SpriteRenderable(lip.GetSprite(anim % lip.Length), centre + offset + new WVec(0, 0, h),
						WVec.Zero, 257 - h - offset.Y, palette, 1f, rise * surfaceAlpha, Vector3.One, TintModifiers.IgnoreWorldTint, false);
				}
			}

			// While it closes, white water rushes over the strip.
			if (closing >= 0)
			{
				var foam = seqs.GetSequence(info.Image, "foam");
				for (var i = 0; i < opened; i++)
				{
					var c = cells[i];
					if (world.ShroudObscures(c))
						continue;

					var frame = (ticks / 3 + c.X * 3 + c.Y * 5) % foam.Length;
					yield return new SpriteRenderable(foam.GetSprite(frame), map.CenterOfCell(c), WVec.Zero, 300, palette,
						1f, 0.95f * (1 - flood * flood), Vector3.One, TintModifiers.IgnoreWorldTint, false);
				}
			}
		}
	}
}
