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
using OpenRA.Graphics;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Commands;
using OpenRA.Mods.Common.Orders;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Widgets;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// Cheat codes, typed into the chat box (with or without a leading '/'):
	//   cash     -> +$10,000 every time
	//   clear    -> removes the fog of war and black shroud (type again to bring it back)
	//   nuke     -> instantly charges every nuke you own (Missile Silos and Sub-Carriers)
	//   guns     -> 100 infantry appear around your Construction Yard
	//   sadam    -> 20 tanks appear around your Construction Yard
	//   faster   -> everything builds instantly, same as the debug menu's instant build (toggle)
	//   power    -> unlimited power, same as the debug menu's unlimited power (toggle)
	//   anywhere -> place buildings anywhere, same as the debug menu's build anywhere (toggle)
	//   heal     -> fully heals all your units and buildings
	//   lips     -> shows every order the AI Commander's model gives the computer players in the chat (toggle)
	//   god      -> every faction's buildings and units become buildable for you (toggle)
	//   jubilee  -> every player in the game (you, allies, enemies and AIs) gets +$10,000
	//   all      -> every cheat at once; type again to switch the on/off ones back off
	// Works for every faction. They go through the normal order system so online games stay in sync.
	[TraitLocation(SystemActors.World)]
	[IncludeStaticFluentReferences(typeof(CheatCodes))]
	[Desc("Adds the cheat words (cash, clear, nuke, guns, sadam, faster, power, anywhere, heal, jubilee, god, lips, all) to the chat box.",
		"Attach this to the world actor.")]
	public class CheatCodesInfo : TraitInfo<CheatCodes> { }

	public class CheatCodes : IChatCommand, IWorldLoaded
	{
		[FluentReference]
		const string CashDescription = "description-cheat-cash";

		[FluentReference]
		const string ClearDescription = "description-cheat-clear";

		[FluentReference]
		const string NukeDescription = "description-cheat-nuke";

		[FluentReference]
		const string GunsDescription = "description-cheat-guns";

		[FluentReference]
		const string SadamDescription = "description-cheat-sadam";

		[FluentReference]
		const string FasterDescription = "description-cheat-faster";

		[FluentReference]
		const string PowerDescription = "description-cheat-power";

		[FluentReference]
		const string AnywhereDescription = "description-cheat-anywhere";

		[FluentReference]
		const string HealDescription = "description-cheat-heal";

		[FluentReference]
		const string AllDescription = "description-cheat-all";

		[FluentReference]
		const string JubileeDescription = "description-cheat-jubilee";

		[FluentReference]
		const string GodDescription = "description-cheat-god";

		[FluentReference]
		const string LipsDescription = "description-cheat-lips";

		// Cheat word -> (order sent, help text).
		static readonly (string Word, string Order, string Help)[] Cheats =
		[
			("cash", "CheatCash", CashDescription),
			("clear", "CheatClear", ClearDescription),
			("nuke", "CheatNuke", NukeDescription),
			("guns", "CheatGuns", GunsDescription),
			("sadam", "CheatSadam", SadamDescription),
			("faster", "CheatFaster", FasterDescription),
			("power", "CheatPower", PowerDescription),
			("anywhere", "CheatAnywhere", AnywhereDescription),
			("heal", "CheatHeal", HealDescription),
			("all", "CheatAll", AllDescription),
			("jubilee", "CheatJubilee", JubileeDescription),
			("god", "CheatGod", GodDescription),
			("lips", "CheatLips", LipsDescription),
		];

		World world;

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			world = w;
			var console = w.WorldActor.TraitOrDefault<ChatCommands>();
			var help = w.WorldActor.TraitOrDefault<HelpCommand>();
			if (console == null)
				return;

			foreach (var c in Cheats)
			{
				// Registered after the debug menu's commands, so our "heal" and "all" replace its versions
				// (which only work with the lobby's debug menu switched on).
				console.RegisterCommand(c.Word, this);

				ChatCommands.BareCommands.Add(c.Word);
				help?.RegisterHelp(c.Word, c.Help);
			}
		}

		public void InvokeCommand(string name, string arg)
		{
			var me = world.LocalPlayer;
			if (me == null || me.WinState != WinState.Undefined)
				return;

			foreach (var c in Cheats)
				if (c.Word == name)
					world.IssueOrder(new Order(c.Order, me.PlayerActor, false));
		}
	}

	[TraitLocation(SystemActors.Player)]
	[Desc("Carries out cheat-code orders for this player. Attach this to the player actor.")]
	public class CheatCodeOrdersInfo : TraitInfo<CheatCodeOrders>
	{
		[Desc("Cash given by the 'cash' cheat.")]
		public readonly int Cash = 10000;

		[Desc("How many infantry the 'guns' cheat creates.")]
		public readonly int GunsCount = 100;

		[ActorReference]
		[Desc("Infantry the 'guns' cheat creates.")]
		public readonly string GunsActor = "e1";

		[Desc("Faction-specific infantry for the 'guns' cheat (faction: actor).")]
		public readonly Dictionary<string, string> GunsFactionActors = [];

		[Desc("How many tanks the 'sadam' cheat creates.")]
		public readonly int TanksCount = 20;

		[ActorReference]
		[Desc("Tank the 'sadam' cheat creates.")]
		public readonly string TanksActor = "3tnk";

		[Desc("Faction-specific tanks for the 'sadam' cheat (faction: actor).")]
		public readonly Dictionary<string, string> TanksFactionActors = [];

		[Desc("Actors the new units gather around, in order of preference.")]
		public readonly ImmutableArray<string> GunsAroundTypes = ["fact", "mcv"];
	}

	public class CheatCodeOrders : IResolveOrder
	{
		bool allOn;

		static void Say(Actor self, string text)
		{
			if (self.World.LocalPlayer == self.Owner)
				TextNotificationsManager.AddSystemLine("Cheat", text);
		}

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			var dev = self.TraitOrDefault<DeveloperMode>();
			switch (order.OrderString)
			{
				case "CheatCash":
					GiveCash(self);
					break;

				case "CheatClear":
					SetFog(self, !self.Owner.Shroud.Disabled);
					break;

				case "CheatNuke":
					ChargeNukes(self);
					break;

				case "CheatGuns":
					Spawn(self, false);
					break;

				case "CheatSadam":
					Spawn(self, true);
					break;

				case "CheatFaster":
					if (dev != null)
						SetFaster(self, dev, !dev.CheatFastBuild);

					break;

				case "CheatPower":
					if (dev != null)
						SetPower(self, dev, !dev.CheatUnlimitedPower);

					break;

				case "CheatAnywhere":
					if (dev != null)
						SetAnywhere(self, dev, !dev.CheatBuildAnywhere);

					break;

				case "CheatHeal":
					HealAll(self);
					break;

				case "CheatLips":
					// Only the player who typed it sees the AI's orders (they are printed on the host's screen,
					// where the computer players' AI runs).
					if (self.World.LocalPlayer == self.Owner)
					{
						AICommanderLink.ShowDirections ^= true;
						Say(self, AICommanderLink.ShowDirections
							? "LIPS ON: every order the AI Commander gives the computer players will show here. Type 'lips' again to hide them."
							: "Lips OFF.");
					}

					break;

				case "CheatGod":
					if (dev != null)
					{
						dev.CheatAllTech ^= true;
						Say(self, dev.CheatAllTech
							? "GOD MODE ON: every faction's buildings and units are in your build menus (units can come out of any factory of the right kind). Type 'god' again to switch it off."
							: "God mode OFF.");
					}

					break;

				case "CheatJubilee":
				{
					var amount = self.Info.TraitInfo<CheatCodeOrdersInfo>().Cash;
					foreach (var p in self.World.Players.Where(p => p.Playable && p.WinState == WinState.Undefined))
						p.PlayerActor.Trait<PlayerResources>().GiveCash(amount);

					if (self.World.LocalPlayer != null)
						TextNotificationsManager.AddSystemLine("Cheat", $"JUBILEE! {self.Owner.ResolvedPlayerName} gave every player +${amount:N0}.");
					break;
				}

				case "CheatAll":
					allOn = !allOn;
					if (allOn)
					{
						Say(self, "ALL CHEATS ON! Type 'all' again to switch the on/off ones back off.");
						GiveCash(self);
						SetFog(self, true);
						if (dev != null)
						{
							SetFaster(self, dev, true);
							SetPower(self, dev, true);
							SetAnywhere(self, dev, true);
							dev.CheatAllTech = true;
						}

						HealAll(self);
						ChargeNukes(self);
						Spawn(self, false);
						Spawn(self, true);
					}
					else
					{
						Say(self, "All on/off cheats switched off.");
						SetFog(self, false);
						if (dev != null)
						{
							SetFaster(self, dev, false);
							SetPower(self, dev, false);
							SetAnywhere(self, dev, false);
							dev.CheatAllTech = false;
						}
					}

					break;
			}
		}

		static void GiveCash(Actor self)
		{
			var info = self.Info.TraitInfo<CheatCodeOrdersInfo>();
			self.Trait<PlayerResources>().GiveCash(info.Cash);
			Say(self, $"+${info.Cash:N0}");
		}

		static void SetFog(Actor self, bool removed)
		{
			self.Owner.Shroud.Disabled = removed;
			Say(self, removed ? "Fog of war removed. Type 'clear' again to bring it back." : "Fog of war restored.");
		}

		static void SetFaster(Actor self, DeveloperMode dev, bool on)
		{
			dev.CheatFastBuild = on;
			Say(self, on ? "Instant build ON: everything you build is ready immediately. Type 'faster' again to switch it off." : "Instant build OFF.");
		}

		static void SetPower(Actor self, DeveloperMode dev, bool on)
		{
			dev.CheatUnlimitedPower = on;
			Say(self, on ? "Unlimited power ON. Type 'power' again to switch it off." : "Unlimited power OFF.");
		}

		static void SetAnywhere(Actor self, DeveloperMode dev, bool on)
		{
			dev.CheatBuildAnywhere = on;
			Say(self, on ? "Build anywhere ON. Type 'anywhere' again to switch it off." : "Build anywhere OFF.");
		}

		static void HealAll(Actor self)
		{
			var healed = 0;
			foreach (var a in self.World.ActorsWithTrait<IHealth>())
			{
				if (a.Actor.Owner != self.Owner || a.Actor.IsDead || !a.Actor.IsInWorld)
					continue;

				var health = a.Trait;
				if (health.HP <= 0 || health.HP >= health.MaxHP)
					continue;

				health.InflictDamage(a.Actor, a.Actor, new Damage(-(health.MaxHP - health.HP)), true);
				healed++;
			}

			Say(self, healed > 0 ? $"Healed {healed} units and buildings." : "Everything is already at full health.");
		}

		static void Spawn(Actor self, bool tanks)
		{
			var spawnInfo = self.Info.TraitInfo<CheatCodeOrdersInfo>();
			var faction = self.Owner.Faction.InternalName;
			var unitType = tanks
				? spawnInfo.TanksFactionActors.GetValueOrDefault(faction, spawnInfo.TanksActor)
				: spawnInfo.GunsFactionActors.GetValueOrDefault(faction, spawnInfo.GunsActor);
			var count = tanks ? spawnInfo.TanksCount : spawnInfo.GunsCount;
			SpawnNamed(self, unitType, count, n => tanks ? $"{n} tanks rolling out!" : $"{n} soldiers reporting for duty!");
		}

		static void SpawnNamed(Actor self, string unitType, int count, System.Func<int, string> message, CPos? spot = null)
		{
			var spawnInfo = self.Info.TraitInfo<CheatCodeOrdersInfo>();
			self.World.AddFrameEndTask(w =>
			{
				var made = SpawnUnits(w, self.Owner, spawnInfo, unitType, count, spot);
				Say(self, made > 0 ? message(made) : spot != null ? "Nowhere for them to stand there." : "You need a Construction Yard for that.");
			});
		}

		static void ChargeNukes(Actor self)
		{
			var charged = 0;

			// Missile Silo (and any other sidebar nuke)
			var powers = self.TraitOrDefault<SupportPowerManager>();
			if (powers != null)
			{
				foreach (var p in powers.Powers.Values)
				{
					if (p.Info is NukePowerInfo && p.Instances.Any(i => !i.IsTraitDisabled))
					{
						p.ChargeFully();
						charged++;
					}
				}
			}

			// Sub-Carriers: their nuke is an ammo pool called "nuke"
			foreach (var a in self.World.ActorsWithTrait<AmmoPool>())
			{
				if (a.Actor.Owner != self.Owner || a.Actor.IsDead || a.Trait.Info.Name != "nuke")
					continue;

				if (a.Trait.GiveAmmo(a.Actor, a.Trait.Info.Ammo))
					charged++;
			}

			Say(self, charged > 0 ? "All nukes charged!" : "You don't have any nukes to charge.");
		}

		static int SpawnUnits(World w, Player owner, CheatCodeOrdersInfo info, string unitType, int count, CPos? spot = null)
		{
			Actor home = null;
			if (spot == null)
			{
				var mine = w.Actors.Where(a => a.Owner == owner && !a.IsDead && a.IsInWorld).ToList();
				foreach (var type in info.GunsAroundTypes)
				{
					home = mine.FirstOrDefault(a => a.Info.Name == type);
					if (home != null)
						break;
				}

				home ??= mine.FirstOrDefault(a => a.Info.HasTraitInfo<BuildingInfo>());
				if (home == null)
					return 0;
			}

			if (!w.Map.Rules.Actors.TryGetValue(unitType, out var unitInfo))
				return 0;

			var mobile = unitInfo.TraitInfoOrDefault<MobileInfo>();
			var sharesCell = mobile != null && w.WorldActor.TraitsImplementing<Locomotor>()
				.Any(l => l.Info.Name == mobile.Locomotor && l.Info.SharesCell);
			var maxPerCell = sharesCell ? 5 : 1;
			CPos center;
			if (spot != null)
				center = spot.Value;
			else
			{
				center = home.Location;
				var bi = home.Info.TraitInfoOrDefault<BuildingInfo>();
				if (bi != null)
					center = home.Location + new CVec(bi.Dimensions.X / 2, bi.Dimensions.Y / 2);
			}

			var cells = w.Map.FindTilesInAnnulus(center, spot != null ? 0 : 1, 30).Where(c => w.Map.Contains(c)).ToList();
			var made = 0;

			// One soldier per cell first so they ring the base, then pack more in if there's no room.
			for (var perCell = 1; perCell <= maxPerCell && made < count; perCell++)
			{
				foreach (var cell in cells)
				{
					if (made >= count)
						break;

					if (mobile != null && !mobile.CanEnterCell(w, null, cell, SubCell.FullCell, null, BlockedByActor.None))
						continue;

					if (w.ActorMap.GetActorsAt(cell).Count() >= perCell)
						continue;

					var sub = sharesCell ? w.ActorMap.FreeSubCell(cell) : SubCell.FullCell;
					if (sub == SubCell.Invalid || (!sharesCell && w.ActorMap.GetActorsAt(cell).Any()))
						continue;

					w.CreateActor(unitType, new TypeDictionary
					{
						new LocationInit(cell),
						new SubCellInit(sub),
						new OwnerInit(owner),
						new FacingInit(WAngle.FromFacing(w.SharedRandom.Next(256))),
					});
					made++;
				}
			}

			return made;
		}
	}
}
