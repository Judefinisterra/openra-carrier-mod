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
using OpenRA.Traits;

// Economy lobby option: Classic (as before) / Scarce / Brutal.
//   Scarce & Brutal: income cut (ore/gems via ResourceValueMultiplier, tricklers via CashTricklerMultiplier, trade and
//   oil tankers in code), ore regrows slowly (SeedsResource variants), upkeep on armed units and defences, a storage
//   cap set by your Construction Yard/refineries/silos (lose a silo -> lose the money above the new cap), and the
//   epic stuff costs more (ProductionCostMultiplier on the economy.harsh prerequisite).
namespace OpenRA.Mods.MyRA.Traits
{
	public enum EconomyLevel { Classic, Scarce, Brutal }

	[TraitLocation(SystemActors.World)]
	[Desc("The Economy lobby option.")]
	public class EconomyModeInfo : TraitInfo, ILobbyOptions
	{
		[Desc("Income kept on Scarce / Brutal (percent) for payouts made in code (trade ships, oil tankers).")]
		public readonly int ScarceIncome = 60;
		public readonly int BrutalIncome = 40;

		[Desc("Oil delivered by tankers/haulers is the biggest money-maker, so it's cut harder (percent kept).")]
		public readonly int ScarceOil = 40;
		public readonly int BrutalOil = 25;

		IEnumerable<LobbyOption> ILobbyOptions.LobbyOptions(MapPreview map)
		{
			var values = new Dictionary<string, string>
			{
				{ "classic", "Classic" },
				{ "scarce", "Scarce" },
				{ "brutal", "Brutal" },
			};
			yield return new LobbyOption(map, "economy", "dropdown-economy.label", "dropdown-economy.description", true, 64,
				values, "classic", false);
		}

		public override object Create(ActorInitializer init) { return new EconomyMode(init.World, this); }
	}

	public class EconomyMode
	{
		public readonly EconomyModeInfo Info;
		public readonly EconomyLevel Level;

		public EconomyMode(World w, EconomyModeInfo info)
		{
			Info = info;
			Level = w.LobbyInfo.GlobalSettings.OptionOrDefault("economy", "classic") switch
			{
				"scarce" => EconomyLevel.Scarce,
				"brutal" => EconomyLevel.Brutal,
				_ => EconomyLevel.Classic,
			};
		}

		public static EconomyLevel LevelOf(World w) => w.WorldActor.TraitOrDefault<EconomyMode>()?.Level ?? EconomyLevel.Classic;

		/// <summary>Scale a payout made in code by the economy setting.</summary>
		public static int Scale(World w, int amount, bool oil = false)
		{
			var m = w.WorldActor.TraitOrDefault<EconomyMode>();
			if (m == null || m.Level == EconomyLevel.Classic)
				return amount;
			var pct = m.Level == EconomyLevel.Scarce ? (oil ? m.Info.ScarceOil : m.Info.ScarceIncome) : (oil ? m.Info.BrutalOil : m.Info.BrutalIncome);
			return amount * pct / 100;
		}
	}

	[Desc("Grants conditions depending on the Economy lobby option: economy-scarce / economy-brutal, and economy-harsh for either.")]
	public class GrantConditionOnEconomyInfo : TraitInfo
	{
		[GrantedConditionReference]
		public readonly string HarshCondition = "economy-harsh";

		[GrantedConditionReference]
		public readonly string ScarceCondition = "economy-scarce";

		[GrantedConditionReference]
		public readonly string BrutalCondition = "economy-brutal";

		public override object Create(ActorInitializer init) { return new GrantConditionOnEconomy(this); }
	}

	public class GrantConditionOnEconomy : INotifyCreated
	{
		readonly GrantConditionOnEconomyInfo info;

		public GrantConditionOnEconomy(GrantConditionOnEconomyInfo info) { this.info = info; }

		void INotifyCreated.Created(Actor self)
		{
			var level = EconomyMode.LevelOf(self.World);
			if (level == EconomyLevel.Classic)
				return;
			self.GrantCondition(info.HarshCondition);
			self.GrantCondition(level == EconomyLevel.Scarce ? info.ScarceCondition : info.BrutalCondition);
		}
	}

	[TraitLocation(SystemActors.Player)]
	[Desc("Provides economy.harsh (+ economy.scarce / economy.brutal) prerequisites from the Economy lobby option.")]
	public class EconomyPrerequisiteInfo : TraitInfo, ITechTreePrerequisiteInfo
	{
		static readonly string[] All = ["economy.harsh", "economy.scarce", "economy.brutal"];

		IEnumerable<string> ITechTreePrerequisiteInfo.Prerequisites(ActorInfo info) => All;

		public override object Create(ActorInitializer init) { return new EconomyPrerequisite(init.World); }
	}

	public class EconomyPrerequisite : ITechTreePrerequisite
	{
		readonly string[] provides;

		public EconomyPrerequisite(World w)
		{
			provides = EconomyMode.LevelOf(w) switch
			{
				EconomyLevel.Scarce => ["economy.harsh", "economy.scarce"],
				EconomyLevel.Brutal => ["economy.harsh", "economy.brutal"],
				_ => [],
			};
		}

		public IEnumerable<string> ProvidesPrerequisites => provides;
	}

	[TraitLocation(SystemActors.Player)]
	[Desc("Scarce/Brutal economy: storage cap from Construction Yard/refineries/silos, and upkeep on armed units and defences.")]
	public class EconomyManagerInfo : TraitInfo
	{
		[Desc("Money you can always hold, whatever you own.")]
		public readonly int BaseStorage = 3000;

		[Desc("Extra storage per building type.")]
		public readonly Dictionary<string, int> Storage = new() { { "fact", 3000 }, { "proc", 2000 }, { "silo", 3000 } };

		[Desc("Upkeep per minute, in tenths of a percent of army value, for each band: up to 20000 / up to 50000 / above.")]
		public readonly ImmutableArray<int> ScarceUpkeep = [30, 50, 80];
		public readonly ImmutableArray<int> BrutalUpkeep = [50, 80, 120];
		public readonly ImmutableArray<int> Bands = [20000, 50000];

		[Desc("Actor types that never cost upkeep.")]
		public readonly HashSet<string> NoUpkeep = ["harv", "mcv", "presidente", "bodyguard", "limo"];

		public readonly int UpkeepInterval = 375;

		public override object Create(ActorInitializer init) { return new EconomyManager(init.Self, this); }
	}

	public class EconomyManager : ITick
	{
		readonly EconomyManagerInfo info;
		readonly Player player;
		EconomyLevel level;
		bool started;
		int lastCap, allowed, upkeepTimer;

		public int Capacity { get; private set; }
		public int UpkeepPerMinute { get; private set; }
		public bool Active => level != EconomyLevel.Classic;

		public EconomyManager(Actor self, EconomyManagerInfo info)
		{
			this.info = info;
			player = self.Owner;
		}

		int ComputeCapacity(World w)
		{
			var cap = info.BaseStorage;
			foreach (var a in w.Actors)
				if (a.Owner == player && !a.IsDead && a.IsInWorld && info.Storage.TryGetValue(a.Info.Name, out var s))
					cap += s;
			return cap;
		}

		/// <summary>Armed units and defences: what upkeep is charged on.</summary>
		public int ArmyValue(World w) => w.Actors.Where(a => a.Owner == player && !a.IsDead && a.IsInWorld
			&& a.Info.HasTraitInfo<AttackBaseInfo>() && !info.NoUpkeep.Contains(a.Info.Name))
			.Sum(a => a.Info.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0);

		int Upkeep(int value)
		{
			var rates = level == EconomyLevel.Brutal ? info.BrutalUpkeep : info.ScarceUpkeep;
			long total = 0, low = 0;
			for (var i = 0; i < rates.Length; i++)
			{
				var high = i < info.Bands.Length ? info.Bands[i] : int.MaxValue;
				if (value > low)
					total += (Math.Min(value, high) - low) * rates[i] / 1000;
				low = high;
			}

			return (int)total;
		}

		void ITick.Tick(Actor self)
		{
			var w = self.World;
			if (!started)
			{
				started = true;
				level = EconomyMode.LevelOf(w);
				var pr0 = self.Trait<PlayerResources>();
				allowed = pr0.Cash + pr0.Resources;
			}

			if (level == EconomyLevel.Classic || !player.Playable || player.WinState != WinState.Undefined)
				return;

			var pr = self.Trait<PlayerResources>();

			// Storage cap: income above it is wasted; losing a silo/refinery drops the cap and the money above it.
			if (w.WorldTick % 10 == 0)
			{
				Capacity = ComputeCapacity(w);
				var total = pr.Cash + pr.Resources;
				var lostStorage = Capacity < lastCap && lastCap > 0;
				if (lostStorage)
					allowed = Math.Min(allowed, Capacity);
				allowed = Math.Max(Capacity, Math.Min(allowed, total));
				if (total > allowed)
				{
					var excess = total - allowed;
					pr.TakeCash(excess);
					MoneyLedger.Loss(player, lostStorage ? "lost with destroyed storage" : "wasted (storage full)", excess);
					if (lostStorage)
					{
						if (w.LocalPlayer == player)
							TextNotificationsManager.AddSystemLine("Economy", $"Storage destroyed: ${excess} lost!");
						GameRecorder.Note(player, $"lost ${excess} when storage was destroyed (cap now ${Capacity})");
					}
				}

				lastCap = Capacity;
			}

			// Upkeep, charged every 15 seconds.
			if (++upkeepTimer >= info.UpkeepInterval)
			{
				upkeepTimer = 0;
				UpkeepPerMinute = Upkeep(ArmyValue(w));
				var due = UpkeepPerMinute * info.UpkeepInterval / 1500;
				if (due > 0)
				{
					var paid = Math.Min(due, pr.Cash + pr.Resources);
					if (paid > 0 && pr.TakeCash(paid))
						MoneyLedger.Loss(player, "upkeep", paid);
				}
			}
		}
	}

	// AI: under the storage cap, build silos before money is wasted.
	[TraitLocation(SystemActors.Player)]
	[Desc("Scarce/Brutal economy: the AI builds silos as it nears its storage cap.")]
	public class EconomyBotModuleInfo : ConditionalTraitInfo
	{
		public readonly string SiloType = "silo";
		public readonly int MaxSilos = 6;
		public readonly int Interval = 200;

		public override object Create(ActorInitializer init) { return new EconomyBotModule(init.Self, this); }
	}

	public class EconomyBotModule : ConditionalTrait<EconomyBotModuleInfo>, IBotTick
	{
		readonly Player player;
		int ticks;

		public EconomyBotModule(Actor self, EconomyBotModuleInfo info)
			: base(info)
		{
			player = self.Owner;
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (IsTraitDisabled || ++ticks % Info.Interval != 0)
				return;
			var em = player.PlayerActor.TraitOrDefault<EconomyManager>();
			if (em == null || !em.Active || em.Capacity == 0)
				return;
			var pr = player.PlayerActor.Trait<PlayerResources>();
			var w = player.World;
			var silos = w.Actors.Count(a => a.Owner == player && !a.IsDead && a.Info.Name == Info.SiloType);
			if ((pr.Cash + pr.Resources) * 100 / em.Capacity >= 75 && silos < Info.MaxSilos
				&& BotHelpers.Queued(player, Info.SiloType) == 0 && BotHelpers.CanBuild(player, Info.SiloType))
				BotHelpers.Queue(bot, player, Info.SiloType);
		}
	}
}
