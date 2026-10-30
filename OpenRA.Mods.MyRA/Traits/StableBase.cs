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
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// ================================================================================================
	// Stable Society AI town planner. The base is laid out on a street grid: blocks of buildings with two-cell
	// driving lanes between them. The town centre is walled in with sandbags, with checkpoint gates where the
	// main roads leave town, guard towers beside the gates and anti-air at the corners. When the walled centre is
	// full the grid carries on outside the wall (the suburbs). Farms (too big for a block) go out in the fields,
	// refineries next to the ore, shipyards on the water, and defences along the inside of the wall.
	// ================================================================================================
	[TraitLocation(SystemActors.Player)]
	[Desc("AI: lays the base out on a street grid inside a walled perimeter with checkpoint gates.")]
	public class StableBasePlannerBotModuleInfo : ConditionalTraitInfo
	{
		[Desc("Block size including the 2-cell lane (cells).")]
		public readonly int Step = 6;

		[Desc("The perimeter wall runs this many blocks out from the town centre.")]
		public readonly int Blocks = 2;

		[Desc("The street grid carries on outside the wall up to this many blocks out.")]
		public readonly int SuburbBlocks = 4;

		[Desc("Construction yards (the town centre).")]
		public readonly ImmutableArray<string> CentreTypes = ["fact"];

		[Desc("Perimeter wall piece.")]
		public readonly string WallType = "sbag";

		[Desc("Gate for the north and south roads (2 cells wide, 1 deep). Empty = leave the road open.")]
		public readonly string GateType = null;

		[Desc("Gate for the east and west roads (1 cell wide, 2 deep). Empty = leave the road open.")]
		public readonly string SideGateType = null;

		[Desc("Guard towers beside the gates (cycled).")]
		public readonly ImmutableArray<string> GateDefenseTypes = ["pbox", "gun"];

		[Desc("Towers at the corners of the perimeter (cycled).")]
		public readonly ImmutableArray<string> CornerDefenseTypes = ["agun", "gun"];

		[Desc("Buildings left to the normal placement logic (e.g. refineries, which go next to the ore).")]
		public readonly ImmutableArray<string> IgnoreTypes = ["proc"];

		[Desc("Don't start the gates before this tick, or with less cash than this.")]
		public readonly int StartTick = 3000;

		public readonly int MinimumCash = 800;

		[Desc("Wall segments laid per round (each costs the wall's normal price).")]
		public readonly int WallsPerRound = 6;

		public readonly int Interval = 60;

		public override object Create(ActorInitializer init) { return new StableBasePlannerBotModule(init.Self, this); }
	}

	public class StableBasePlannerBotModule : ConditionalTrait<StableBasePlannerBotModuleInfo>, IBotTick, IBotSuggestBuildingLocation, IResolveOrder
	{
		const string BuildOrder = "StableCivicWorks";

		readonly World world;
		readonly Player player;
		readonly Dictionary<string, CPos> pending = [];
		int ticks;

		public StableBasePlannerBotModule(Actor self, StableBasePlannerBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		// Grid origin: lanes run along x = ox, ox+1 (mod Step) and y = oy, oy+1 (mod Step).
		bool TryOrigin(out CPos origin)
		{
			var yard = world.Actors.Where(a => a.Owner == player && !a.IsDead && a.IsInWorld && Info.CentreTypes.Contains(a.Info.Name))
				.OrderBy(a => a.ActorID).FirstOrDefault();
			origin = yard != null ? yard.Location - new CVec(2, 2) : CPos.Zero;
			return yard != null;
		}

		static int Mod(int a, int m) => ((a % m) + m) % m;

		bool IsLane(CPos origin, CPos c) => Mod(c.X - origin.X, Info.Step) < 2 || Mod(c.Y - origin.Y, Info.Step) < 2;

		int Reach => Info.Blocks * Info.Step;

		static bool Within(CPos origin, CPos c, int r)
		{
			var dx = c.X - origin.X;
			var dy = c.Y - origin.Y;
			return dx > -r && dx < r && dy > -r && dy < r;
		}

		// The wall itself plus one cell either side of it is kept free of buildings.
		bool OnWallBand(CPos origin, CPos c)
		{
			var r = Reach;
			var dx = Math.Abs(c.X - origin.X);
			var dy = Math.Abs(c.Y - origin.Y);
			return (dx >= r - 1 && dx <= r + 1 && dy <= r + 1) || (dy >= r - 1 && dy <= r + 1 && dx <= r + 1);
		}

		bool Placeable(string type, CPos c)
		{
			var ai = world.Map.Rules.Actors[type];
			var bi = ai.TraitInfoOrDefault<BuildingInfo>();
			return bi != null && world.CanPlaceBuilding(c, ai, bi, null) && bi.IsCloseEnoughToBase(world, player, ai, c);
		}

		bool Built(string type, CPos cell) => world.ActorMap.GetActorsAt(cell).Any(a => a.Owner == player && a.Info.Name == type);

		IEnumerable<(string Type, CPos Cell)> Gates(CPos o)
		{
			var r = Reach;
			if (!string.IsNullOrEmpty(Info.GateType))
			{
				yield return (Info.GateType, new CPos(o.X, o.Y - r));
				yield return (Info.GateType, new CPos(o.X, o.Y + r));
			}

			if (!string.IsNullOrEmpty(Info.SideGateType))
			{
				yield return (Info.SideGateType, new CPos(o.X - r, o.Y));
				yield return (Info.SideGateType, new CPos(o.X + r, o.Y));
			}
		}

		IEnumerable<(string Type, CPos Cell)> Towers(CPos o)
		{
			var r = Reach;
			var k = 0;
			foreach (var c in new[]
			{
				new CPos(o.X + 2, o.Y - r + 2), new CPos(o.X - 4, o.Y - r + 2), new CPos(o.X + 2, o.Y + r - 4), new CPos(o.X - 4, o.Y + r - 4),
				new CPos(o.X - r + 2, o.Y + 2), new CPos(o.X - r + 2, o.Y - 4), new CPos(o.X + r - 4, o.Y + 2), new CPos(o.X + r - 4, o.Y - 4),
			})
				yield return (Info.GateDefenseTypes[k++ % Info.GateDefenseTypes.Length], c);

			k = 0;
			foreach (var c in new[] { new CPos(o.X - r + 2, o.Y - r + 2), new CPos(o.X + r - 4, o.Y - r + 2), new CPos(o.X - r + 2, o.Y + r - 4), new CPos(o.X + r - 4, o.Y + r - 4) })
				yield return (Info.CornerDefenseTypes[k++ % Info.CornerDefenseTypes.Length], c);
		}

		// The wall square, gates left open, nearest the gates first so the gates get their wings early.
		IEnumerable<CPos> WallCells(CPos o)
		{
			var r = Reach;
			var walls = new HashSet<CPos>();
			for (var i = -r; i <= r; i++)
			{
				walls.Add(new CPos(o.X + i, o.Y - r));
				walls.Add(new CPos(o.X + i, o.Y + r));
				walls.Add(new CPos(o.X - r, o.Y + i));
				walls.Add(new CPos(o.X + r, o.Y + i));
			}

			return walls.Where(c => !IsGate(o, c)).OrderBy(c => Math.Min(Math.Abs(c.X - o.X), Math.Abs(c.Y - o.Y))).ThenBy(c => c.X).ThenBy(c => c.Y);
		}

		bool IsGate(CPos o, CPos c)
		{
			var r = Reach;
			return ((c.Y == o.Y - r || c.Y == o.Y + r) && (c.X == o.X || c.X == o.X + 1))
				|| ((c.X == o.X - r || c.X == o.X + r) && (c.Y == o.Y || c.Y == o.Y + 1));
		}

		bool CanPlace(string type, CPos cell)
		{
			var ai = world.Map.Rules.Actors[type];
			return world.Map.Contains(cell) && world.CanPlaceBuilding(cell, ai, ai.TraitInfo<BuildingInfo>(), null);
		}

		// Our own units loitering where a gate or wall should go: move them into town.
		bool Nudge(IBot bot, CPos o, string type, CPos cell)
		{
			var ai = world.Map.Rules.Actors[type];
			var bi = ai.TraitInfo<BuildingInfo>();
			var blockers = bi.Tiles(cell).SelectMany(t => world.ActorMap.GetActorsAt(t)).Distinct().ToList();
			if (blockers.Count == 0 || blockers.Any(a => a.Owner != player || !a.Info.HasTraitInfo<MobileInfo>()))
				return false;

			var inside = o + new CVec(Info.Step / 2, Info.Step / 2);
			foreach (var a in blockers)
				bot.QueueOrder(new Order("Move", a, Target.FromCell(world, inside + new CVec(world.LocalRandom.Next(-3, 4), world.LocalRandom.Next(-3, 4))), false));
			return true;
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (++ticks < Info.Interval)
				return;

			ticks = 0;
			if (world.WorldTick < Info.StartTick || !TryOrigin(out var o))
				return;

			var res = player.PlayerActor.Trait<PlayerResources>();
			var cash = res.Cash + res.Resources;
			if (cash < Info.MinimumCash)
				return;

			var spend = cash - Info.MinimumCash / 2;
			var build = new List<(string Type, CPos Cell)>();

			// 1. Gates.
			foreach (var (type, cell) in Gates(o))
			{
				if (Built(type, cell))
					continue;

				if (CanPlace(type, cell))
				{
					build.Add((type, cell));
					spend -= BotHelpers.Cost(world.Map.Rules.Actors[type]);
					break;
				}

				Nudge(bot, o, type, cell);
			}

			// 2. The wall, a few segments at a time.
			var wallCost = Math.Max(1, BotHelpers.Cost(world.Map.Rules.Actors[Info.WallType]));
			var walls = 0;
			foreach (var c in WallCells(o))
			{
				if (walls >= Info.WallsPerRound || spend < wallCost)
					break;

				if (Built(Info.WallType, c))
					continue;

				if (!CanPlace(Info.WallType, c))
				{
					Nudge(bot, o, Info.WallType, c);
					continue;
				}

				build.Add((Info.WallType, c));
				spend -= wallCost;
				walls++;
			}

			// 3. Towers, once the wall is (as good as) done.
			if (walls == 0)
			{
				foreach (var (type, cell) in Towers(o))
				{
					if (Built(type, cell) || !CanPlace(type, cell))
						continue;

					var cost = BotHelpers.Cost(world.Map.Rules.Actors[type]);
					if (cost <= spend)
						build.Add((type, cell));
					break;
				}
			}

			if (build.Count > 0)
				bot.QueueOrder(new Order(BuildOrder, player.PlayerActor, false)
				{
					TargetString = string.Join(";", build.Select(b => $"{b.Type}:{b.Cell.X},{b.Cell.Y}")),
				});
		}

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString != BuildOrder || string.IsNullOrEmpty(order.TargetString))
				return;

			var res = self.Owner.PlayerActor.Trait<PlayerResources>();
			var owner = self.Owner;
			var faction = owner.Faction.InternalName;
			var allowed = new HashSet<string>(Gates(CPos.Zero).Concat(Towers(CPos.Zero)).Select(g => g.Type)) { Info.WallType };
			foreach (var part in order.TargetString.Split(';'))
			{
				var tc = part.Split(':');
				if (tc.Length != 2 || !allowed.Contains(tc[0]) || !self.World.Map.Rules.Actors.TryGetValue(tc[0], out var ai))
					continue;

				var xy = tc[1].Split(',');
				if (xy.Length != 2 || !int.TryParse(xy[0], out var x) || !int.TryParse(xy[1], out var y))
					continue;

				var type = tc[0];
				var cell = new CPos(x, y);
				var bi = ai.TraitInfoOrDefault<BuildingInfo>();
				if (bi == null || !self.World.Map.Contains(cell) || !self.World.CanPlaceBuilding(cell, ai, bi, null))
					continue;

				var cost = BotHelpers.Cost(ai);
				if (!res.TakeCash(cost))
					break;

				self.World.AddFrameEndTask(w =>
				{
					if (!w.CanPlaceBuilding(cell, ai, bi, null))
					{
						res.GiveCash(cost);
						return;
					}

					w.CreateActor(type, new TypeDictionary
					{
						new LocationInit(cell),
						new OwnerInit(owner),
						new FactionInit(faction),
						new PlaceBuildingInit(),
					});
				});
			}
		}

		CPos? IBotSuggestBuildingLocation.SuggestLocation(IBot bot, string actorType, ActorInfo actorInfo, BuildingInfo bi)
		{
			if (IsTraitDisabled || Info.IgnoreTypes.Contains(actorType) || !TryOrigin(out var o))
				return null;

			// Shipyards and other water buildings: leave to the normal logic.
			if (bi.TerrainTypes.Contains("Water"))
				return null;

			// Gates and towers go exactly where the plan says.
			if (pending.TryGetValue(actorType, out var spot))
			{
				pending.Remove(actorType);
				if (Placeable(actorType, spot))
					return spot;

				foreach (var (type, cell) in Gates(o).Concat(Towers(o)))
					if (type == actorType && !Built(type, cell) && Placeable(type, cell))
						return cell;
			}

			var size = bi.Dimensions;
			var centre = o + new CVec(Info.Step / 2, Info.Step / 2);

			// Other defences: along the inside of the wall, as far as possible from the towers already there.
			if (actorInfo.HasTraitInfo<AttackBaseInfo>() && size.X <= 2 && size.Y <= 2)
			{
				var r = Reach - 3;
				var ring = new HashSet<CPos>();
				for (var i = -r; i <= r; i++)
				{
					ring.Add(new CPos(o.X + i, o.Y - r));
					ring.Add(new CPos(o.X + i, o.Y + r));
					ring.Add(new CPos(o.X - r, o.Y + i));
					ring.Add(new CPos(o.X + r, o.Y + i));
				}

				var towers = world.Actors.Where(a => a.Owner == player && !a.IsDead && a.Info.HasTraitInfo<AttackBaseInfo>() && a.Info.HasTraitInfo<BuildingInfo>())
					.Select(a => a.Location).ToList();
				CPos? bestCell = null;
				var bestGap = -1;
				foreach (var c in ring.OrderBy(c => c.X).ThenBy(c => c.Y))
				{
					var gap = towers.Count == 0 ? 99 : towers.Min(t => (t - c).Length);
					if (gap <= bestGap || bi.Tiles(c).Any(t => IsLane(o, t)))
						continue;
					if (!world.CanPlaceBuilding(c, actorInfo, bi, null) || !bi.IsCloseEnoughToBase(world, player, actorInfo, c))
						continue;
					bestGap = gap;
					bestCell = c;
				}

				if (bestCell != null)
					return bestCell;
			}

			// Too big for a block (farms): out in the fields beyond the wall.
			if (size.X > Info.Step - 2 || size.Y > Info.Step - 2)
			{
				var r0 = Reach + 3;
				foreach (var c in world.Map.FindTilesInAnnulus(centre, r0, Math.Min(r0 + 18, 50)).OrderBy(c => (c - centre).LengthSquared).ThenBy(c => c.X).ThenBy(c => c.Y))
				{
					if (bi.Tiles(c).Any(t => Within(o, t, Reach + 2)))
						continue;
					if (world.CanPlaceBuilding(c, actorInfo, bi, null) && bi.IsCloseEnoughToBase(world, player, actorInfo, c))
						return c;
				}

				return null;
			}

			// Everything else: inside a block, never on a lane, inside the walls if there's room, else the suburbs.
			foreach (var limit in new[] { Reach, Info.SuburbBlocks * Info.Step })
			{
				var best = FindInBlocks(o, centre, limit, actorInfo, bi);
				if (best != null)
					return best;
			}

			return null;
		}

		// Buildings sit at fixed spots in a block (its corner, or one of four 2x2 slots) so the rows line up.
		CPos? FindInBlocks(CPos o, CPos centre, int limit, ActorInfo actorInfo, BuildingInfo bi)
		{
			var size = bi.Dimensions;
			var inner = Info.Step - 2;
			var xs = size.X <= inner / 2 ? new[] { 0, inner / 2 } : new[] { 0 };
			var ys = size.Y <= inner / 2 ? new[] { 0, inner / 2 } : new[] { 0 };
			var blocks = limit / Info.Step;
			CPos? best = null;
			var bestDist = int.MaxValue;
			for (var j = -blocks; j < blocks; j++)
			{
				for (var i = -blocks; i < blocks; i++)
				{
					foreach (var dy in ys)
					{
						foreach (var dx in xs)
						{
							var c = new CPos(o.X + i * Info.Step + 2 + dx, o.Y + j * Info.Step + 2 + dy);
							var d = (c - centre).LengthSquared;
							if (d >= bestDist || !world.Map.Contains(c))
								continue;

							var tiles = bi.Tiles(c).ToList();
							if (tiles.Any(t => IsLane(o, t) || !Within(o, t, limit) || OnWallBand(o, t)))
								continue;

							if (!world.CanPlaceBuilding(c, actorInfo, bi, null) || !bi.IsCloseEnoughToBase(world, player, actorInfo, c))
								continue;

							bestDist = d;
							best = c;
						}
					}
				}
			}

			return best;
		}
	}

	// ================================================================================================
	// Checkpoint gates come with a short sandbag wall on each side, so a gate is never just standing in a field.
	// ================================================================================================
	[Desc("When this building is placed, lay a short wall out from each side of it (along its long side).")]
	public class WallWingsInfo : TraitInfo, Requires<BuildingInfo>
	{
		[ActorReference]
		public readonly string WallType = "sbag";

		[Desc("Wall segments on each side.")]
		public readonly int Length = 4;

		public override object Create(ActorInitializer init) { return new WallWings(this); }
	}

	public class WallWings : INotifyAddedToWorld
	{
		readonly WallWingsInfo info;

		public WallWings(WallWingsInfo info) { this.info = info; }

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			var dim = self.Info.TraitInfo<BuildingInfo>().Dimensions;
			var horizontal = dim.X >= dim.Y;
			var step = horizontal ? new CVec(1, 0) : new CVec(0, 1);
			var first = self.Location - step;
			var last = self.Location + (horizontal ? new CVec(dim.X, 0) : new CVec(0, dim.Y));
			var owner = self.Owner;
			var faction = owner.Faction.InternalName;

			self.World.AddFrameEndTask(w =>
			{
				var wallInfo = w.Map.Rules.Actors[info.WallType];
				var wallBi = wallInfo.TraitInfo<BuildingInfo>();
				foreach (var (start, dir) in new[] { (first, -step), (last, step) })
				{
					for (var k = 0; k < info.Length; k++)
					{
						var cell = start + dir * k;
						if (!w.Map.Contains(cell) || !w.CanPlaceBuilding(cell, wallInfo, wallBi, null))
							break;

						w.CreateActor(info.WallType, new TypeDictionary
						{
							new LocationInit(cell),
							new OwnerInit(owner),
							new FactionInit(faction),
						});
					}
				}
			});
		}
	}
}
