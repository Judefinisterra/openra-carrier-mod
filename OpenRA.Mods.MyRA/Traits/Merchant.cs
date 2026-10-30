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
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// AI sea trade: once it has a refinery and some money, the AI puts Offshore Oil Platforms (which launch
	// Oil Tankers) and a Seaport on the water near its base, and builds Container Ships whenever it has a truce
	// partner to trade with. Also tells the base builder where those water buildings go, so they get placed
	// whether this module, the AI Commander or anything else queued them.
	[TraitLocation(SystemActors.Player)]
	[Desc("AI: builds Seaports, Offshore Oil Platforms and Container Ships to make money at sea.")]
	public class MerchantBotModuleInfo : ConditionalTraitInfo
	{
		public readonly string SeaportType = "seaport";
		public readonly string PlatformType = "oilplatform";
		public readonly string ShipType = "containership";

		[Desc("Buildings counted as somewhere a truce partner's Container Ship can dock.")]
		public readonly HashSet<string> PortTypes = ["seaport", "syrd", "spen", "tribaldock"];

		public readonly int MaxPlatforms = 4;
		public readonly int MaxShips = 3;

		[Desc("Cash kept in reserve on top of the item's cost before buying.")]
		public readonly int Reserve = 2000;

		[Desc("How far (cells) from the base's buildings to look for water.")]
		public readonly int SearchRadius = 20;

		public readonly int StartTick = 6000;

		[Desc("Don't start until the base has at least this many buildings.")]
		public readonly int MinBuildings = 8;
		public readonly int Interval = 200;

		public override object Create(ActorInitializer init) { return new MerchantBotModule(init.Self, this); }
	}

	public class MerchantBotModule : ConditionalTrait<MerchantBotModuleInfo>, IBotTick, IBotSuggestBuildingLocation
	{
		readonly World world;
		readonly Player player;
		int ticks, noWaterUntil;

		public MerchantBotModule(Actor self, MerchantBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		List<Actor> Mine(string type) =>
			world.Actors.Where(a => a.Owner == player && !a.IsDead && a.IsInWorld && a.Info.Name == type).ToList();

		bool HasTradePartner()
		{
			var truces = world.WorldActor.TraitOrDefault<TruceManager>();
			if (truces == null)
				return false;

			return world.Players.Any(p => p != player && p.Playable && truces.InTruce(player, p)
				&& world.Actors.Any(a => a.Owner == p && !a.IsDead && a.IsInWorld && Info.PortTypes.Contains(a.Info.Name)));
		}

		bool Afford(string type)
		{
			if (!world.Map.Rules.Actors.TryGetValue(type, out var ai))
				return false;

			var res = player.PlayerActor.Trait<PlayerResources>();
			return res.GetCashAndResources() >= BotHelpers.Cost(ai) + Info.Reserve;
		}

		bool TryQueue(IBot bot, string type)
		{
			if (!Afford(type) || !BotHelpers.CanBuild(player, type))
				return false;

			if (!BotHelpers.Queue(bot, player, type))
				return false;

			GameRecorder.Note(player, $"sea trade: building {type}");
			return true;
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (IsTraitDisabled || ++ticks < Info.StartTick || ticks % Info.Interval != 0)
				return;

			// One merchant item at a time.
			if (BotHelpers.Queued(player, Info.SeaportType) + BotHelpers.Queued(player, Info.PlatformType) + BotHelpers.Queued(player, Info.ShipType) > 0)
				return;

			if (world.ActorsHavingTrait<Building>().Count(a => a.Owner == player && !a.IsDead) < Info.MinBuildings)
				return;

			var water = ticks >= noWaterUntil;
			var platforms = Mine(Info.PlatformType).Count;
			var seaports = Mine(Info.SeaportType).Count;

			if (water && platforms == 0 && TryQueue(bot, Info.PlatformType))
				return;

			if (water && seaports == 0 && TryQueue(bot, Info.SeaportType))
				return;

			if (seaports > 0 && Mine(Info.ShipType).Count < Info.MaxShips && HasTradePartner() && TryQueue(bot, Info.ShipType))
				return;

			if (water && platforms < Info.MaxPlatforms && TryQueue(bot, Info.PlatformType))
				return;
		}

		bool IsWater(CPos c) => world.Map.Contains(c) && world.Map.GetTerrainInfo(c).Type == "Water";

		CPos? IBotSuggestBuildingLocation.SuggestLocation(IBot bot, string actorType, ActorInfo actorInfo, BuildingInfo bi)
		{
			if (IsTraitDisabled || (actorType != Info.SeaportType && actorType != Info.PlatformType))
				return null;

			var home = world.Map.CellContaining(BotHelpers.BaseCenter(player, world.Map.CenterOfCell(player.HomeLocation)));
			var anchors = world.ActorsHavingTrait<Building>().Where(a => a.Owner == player && !a.IsDead && a.IsInWorld)
				.OrderBy(a => (a.Location - home).LengthSquared).Take(15).Select(a => a.Location).ToList();
			if (anchors.Count == 0)
				anchors.Add(home);

			var platform = actorType == Info.PlatformType;
			var others = world.Actors.Where(a => !a.IsDead && a.IsInWorld && (a.Info.Name == Info.PlatformType || a.Info.Name == Info.SeaportType))
				.Select(a => a.Location).ToList();

			var seen = new HashSet<CPos>();
			CPos? best = null;
			var bestScore = int.MinValue;
			foreach (var c in anchors.SelectMany(a => world.Map.FindTilesInAnnulus(a, 1, Info.SearchRadius)))
			{
				if (!seen.Add(c) || !IsWater(c) || !world.CanPlaceBuilding(c, actorInfo, bi, null) || !bi.IsCloseEnoughToBase(world, player, actorInfo, c))
					continue;

				// Keep merchant buildings apart so tankers and ships can get in.
				if (others.Any(o => (o - c).LengthSquared < 16))
					continue;

				// Open water around it (ships need room; a pond won't do).
				var open = world.Map.FindTilesInCircle(c, 4).Count(IsWater);
				if (open < 30)
					continue;

				var dist = anchors.Min(a => (a - c).LengthSquared);

				// Seaports hug the shore near the base; platforms go a little further out into open water.
				var score = platform ? open * 4 - dist : open - dist * 4;
				if (score > bestScore)
				{
					bestScore = score;
					best = c;
				}
			}

			if (best == null)
				noWaterUntil = ticks + 3000;

			return best;
		}
	}
}
