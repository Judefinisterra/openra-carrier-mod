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
using System.Linq;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// ================================================================================================
	// Cyber Attack: click near an enemy's base; half of that player's money is transferred to you.
	// ================================================================================================
	[Desc("Support power that steals a share of the money of the player whose base is nearest the target.")]
	public class CyberAttackPowerInfo : SupportPowerInfo
	{
		[Desc("Percentage of the victim's money (cash + stored resources) that is stolen.")]
		public readonly int StealPercent = 50;

		[Desc("Condition granted to the victim's radar buildings (they must list it as an ExternalCondition).")]
		public readonly string JamCondition = "jammed";

		[Desc("How long the victim's radar is knocked out (ticks).")]
		public readonly int JamTicks = 1500;

		[Desc("Fake 'base under attack' alarms the victim hears, spread over the jamming time.")]
		public readonly int FakeAlarms = 3;

		public override object Create(ActorInitializer init) { return new CyberAttackPower(init.Self, this); }
	}

	public class CyberAttackPower : SupportPower, ITick
	{
		readonly List<(int Due, Player Victim)> alarms = [];

		public CyberAttackPower(Actor self, CyberAttackPowerInfo info)
			: base(self, info) { }

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			base.Activate(self, order, manager);
			var info = (CyberAttackPowerInfo)Info;
			var attacker = self.Owner;
			var pos = order.Target.CenterPosition;

			// Victim: the enemy whose building is closest to where we clicked.
			var nearest = self.World.ActorsHavingTrait<Building>()
				.Where(a => !a.IsDead && a.IsInWorld && a.Owner.Playable && a.Owner != attacker
					&& attacker.RelationshipWith(a.Owner) != PlayerRelationship.Ally && a.Owner.WinState == WinState.Undefined)
				.MinByOrDefault(a => (a.CenterPosition - pos).LengthSquared);

			PlayLaunchSounds();
			if (nearest == null)
				return;

			var victim = nearest.Owner;
			var theirs = victim.PlayerActor.Trait<PlayerResources>();
			var amount = (theirs.Cash + theirs.Resources) * info.StealPercent / 100;
			if (amount > 0 && theirs.TakeCash(amount))
				attacker.PlayerActor.Trait<PlayerResources>().GiveCash(amount);

			// Knock out their radar for a minute...
			foreach (var a in self.World.Actors.Where(a => a.Owner == victim && !a.IsDead && a.IsInWorld).ToList())
				foreach (var ec in a.TraitsImplementing<ExternalCondition>())
					if (ec.Info.Condition == info.JamCondition && ec.CanGrantCondition(this))
						ec.GrantCondition(a, this, info.JamTicks);

			// ...and set off false 'base under attack' alarms while it's down.
			for (var i = 0; i < info.FakeAlarms; i++)
				alarms.Add((self.World.WorldTick + 5 + i * info.JamTicks / Math.Max(1, info.FakeAlarms), victim));

			var w = self.World;
			var at = nearest.CenterPosition;
			w.AddFrameEndTask(_ =>
			{
				if (w.LocalPlayer == attacker)
					TextNotificationsManager.AddSystemLine("Cyber Attack", $"Hacked {victim.ResolvedPlayerName}'s bank: you stole ${amount}!");
				else if (w.LocalPlayer == victim)
					TextNotificationsManager.AddSystemLine("Cyber Attack", $"{attacker.ResolvedPlayerName} hacked your bank and stole ${amount}!");

				if (amount > 0 && (w.LocalPlayer == attacker || w.LocalPlayer == victim))
					w.Add(new FloatingText(at, Color.Red, "-" + FloatingText.FormatCashTick(amount)[1..], 60));
			});
		}

		void ITick.Tick(Actor self)
		{
			for (var i = alarms.Count - 1; i >= 0; i--)
			{
				var (due, victim) = alarms[i];
				if (self.World.WorldTick < due)
					continue;

				alarms.RemoveAt(i);
				var buildings = self.World.ActorsHavingTrait<Building>().Where(a => a.Owner == victim && !a.IsDead && a.IsInWorld).ToList();
				if (buildings.Count == 0)
					continue;

				var fake = buildings[self.World.SharedRandom.Next(buildings.Count)];
				Game.Sound.PlayNotification(self.World.Map.Rules, victim, "Speech", "BaseAttack", victim.Faction.InternalName);
				TextNotificationsManager.AddTransientLine(victim, "notification-base-under-attack");
				self.World.WorldActor.TraitOrDefault<RadarPings>()?.Add(() => victim.IsAlliedWith(self.World.RenderPlayer), fake.CenterPosition, Color.Red, 50);
			}
		}
	}

	// ================================================================================================
	// Troop Badger: board infantry while it is parked at the airfield, then right-click anywhere on the
	// map - it flies there, everyone parachutes out over the spot, and it flies home.
	// ================================================================================================
	[Desc("With passengers aboard, a ground order makes the aircraft fly there and paradrop everyone.")]
	public class ParaDropOnOrderInfo : TraitInfo, Requires<ParaDropInfo>, Requires<CargoInfo>, Requires<AircraftInfo>
	{
		[CursorReference]
		public readonly string Cursor = "ability";

		[VoiceReference]
		public readonly string Voice = "Action";

		public override object Create(ActorInitializer init) { return new ParaDropOnOrder(init.Self); }
	}

	public class ParaDropOnOrder : IIssueOrder, IResolveOrder, IOrderVoice, ITick
	{
		const string OrderID = "ParaDropTroops";
		readonly Cargo cargo;
		readonly ParaDrop paraDrop;
		bool dropping;

		public ParaDropOnOrder(Actor self)
		{
			cargo = self.Trait<Cargo>();
			paraDrop = self.Trait<ParaDrop>();
		}

		IEnumerable<IOrderTargeter> IIssueOrder.Orders
		{
			get { yield return new DropTargeter(this); }
		}

		Order IIssueOrder.IssueOrder(Actor self, IOrderTargeter order, in Target target, bool queued)
		{
			return order.OrderID == OrderID ? new Order(OrderID, self, target, queued) : null;
		}

		string IOrderVoice.VoicePhraseForOrder(Actor self, Order order) => order.OrderString == OrderID ? "Action" : null;

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString != OrderID || cargo.IsEmpty())
				return;

			var map = self.World.Map;
			var cell = map.CellContaining(order.Target.CenterPosition);
			paraDrop.SetLZ(cell, false);
			dropping = true;

			// Fly over the drop zone, carry on a little past it so everyone gets out, then go home.
			var lz = map.CenterOfCell(cell);
			var dir = lz - self.CenterPosition;
			var len = Math.Max(1, dir.HorizontalLength);
			var beyond = lz + new WVec(dir.X * 6144 / len, dir.Y * 6144 / len, 0);
			self.QueueActivity(false, new Fly(self, Target.FromPos(lz), targetLineColor: Color.Orange));
			self.QueueActivity(true, new Fly(self, Target.FromPos(beyond)));
			self.QueueActivity(true, new ReturnToBase(self));
			self.ShowTargetLines();
		}

		void ITick.Tick(Actor self)
		{
			// Once everyone is out, forget the drop zone so the next trip doesn't drop by accident.
			if (dropping && cargo.IsEmpty())
			{
				dropping = false;
				paraDrop.SetLZ(new CPos(-10000, -10000), false);
			}
		}

		sealed class DropTargeter : IOrderTargeter
		{
			readonly ParaDropOnOrder owner;

			public DropTargeter(ParaDropOnOrder owner) { this.owner = owner; }

			public string OrderID => ParaDropOnOrder.OrderID;
			public int OrderPriority => 6;
			public bool IsQueued { get; private set; }

			public bool TargetOverridesSelection(Actor self, in Target target, List<Actor> actorsAt, CPos xy, TargetModifiers modifiers) => true;

			public bool CanTarget(Actor self, in Target target, ref TargetModifiers modifiers, ref string cursor)
			{
				if (owner.cargo.IsEmpty() || modifiers.HasModifier(TargetModifiers.ForceMove))
					return false;

				if (target.Type != TargetType.Terrain && target.Type != TargetType.Actor && target.Type != TargetType.FrozenActor)
					return false;

				// Clicking our own stuff keeps its normal meaning (e.g. go back to the airfield).
				if (target.Type == TargetType.Actor && target.Actor.Owner == self.Owner)
					return false;

				IsQueued = modifiers.HasModifier(TargetModifiers.ForceQueue);
				cursor = "ability";
				return self.World.Map.Contains(self.World.Map.CellContaining(target.CenterPosition));
			}
		}
	}

	// ================================================================================================
	// Hermit Nation AI diplomacy: offers truces all the time, moves its army into position while the
	// truce holds, then breaks it and attacks. When a battle goes badly it begs for a truce and pulls back.
	// ================================================================================================
	[TraitLocation(SystemActors.Player)]
	[Desc("AI: two-faced diplomacy (needs the TruceManager on the world).")]
	public class HermitDiplomacyBotModuleInfo : ConditionalTraitInfo
	{
		[Desc("Ticks between diplomacy checks.")]
		public readonly int Interval = 125;

		[Desc("Offer a truce to each enemy at most this often.")]
		public readonly int ProposeCooldown = 2250;

		[Desc("Keep a truce at least this long before turning on the partner.")]
		public readonly int MinTruceTicks = 2500;

		[Desc("Break the truce once our army is this percentage of the partner's army (and in position).")]
		public readonly int BreakArmyPercent = 120;

		[Desc("Need at least this many combat units in position before breaking a truce.")]
		public readonly int MinStrikeUnits = 15;

		[Desc("Percentage of the army that must be at the staging point before breaking the truce.")]
		public readonly int StagedPercent = 60;

		[Desc("Losing a battle = army value dropped by this percentage within LossWindow ticks.")]
		public readonly int LossPercent = 30;

		public readonly int LossWindow = 750;

		[Desc("Unit types that count as the army (empty = everything that can attack and move).")]
		public readonly HashSet<string> ExcludeTypes = [];

		public override object Create(ActorInitializer init) { return new HermitDiplomacyBotModule(init.Self, this); }
	}

	public class HermitDiplomacyBotModule : ConditionalTrait<HermitDiplomacyBotModuleInfo>, IBotTick
	{
		readonly World world;
		readonly Player player;
		readonly Dictionary<Player, int> lastProposal = [];
		readonly Dictionary<Player, int> truceSince = [];
		readonly Queue<(int Tick, int Value)> history = new();
		readonly HashSet<Player> breaking = [];
		int ticks, retreatUntil;
		TruceManager truce;

		public HermitDiplomacyBotModule(Actor self, HermitDiplomacyBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		protected override void Created(Actor self)
		{
			truce = world.WorldActor.TraitOrDefault<TruceManager>();
			base.Created(self);
		}

		List<Actor> Army() => world.Actors.Where(a => a.Owner == player && !a.IsDead && a.IsInWorld
			&& !Info.ExcludeTypes.Contains(a.Info.Name)
			&& a.Info.HasTraitInfo<MobileInfo>() && a.Info.HasTraitInfo<AttackBaseInfo>()).ToList();

		static int ArmyValue(Player p) => p.PlayerActor.TraitOrDefault<PlayerStatistics>()?.ArmyValue ?? 0;

		IEnumerable<Player> Others => world.Players.Where(p => p != player && p.Playable && !p.NonCombatant
			&& p.WinState == WinState.Undefined && !p.IsAlliedWith(player));

		CPos Home(Player p)
		{
			var yard = world.Actors.FirstOrDefault(a => a.Owner == p && !a.IsDead && a.Info.HasTraitInfo<BuildingInfo>()
				&& a.Info.Name == "fact");
			return yard?.Location ?? p.HomeLocation;
		}

		void Propose(IBot bot, Player other)
		{
			if (lastProposal.TryGetValue(other, out var last) && world.WorldTick - last < Info.ProposeCooldown)
				return;

			lastProposal[other] = world.WorldTick;
			bot.QueueOrder(new Order("ProposeTruce", player.PlayerActor, false) { ExtraData = (uint)world.Players.IndexOf(other) });
		}

		void IBotTick.BotTick(IBot bot)
		{
			// While the AI Commander has an order in force, it runs the diplomacy and the army.
			if (AICommanderBotModule.For(player) != null)
				return;

			if (truce == null || ++ticks < Info.Interval)
				return;

			ticks = 0;
			var now = world.WorldTick;
			var army = Army();
			var value = ArmyValue(player);

			// Track our army value to spot a battle going badly.
			history.Enqueue((now, value));
			while (history.Count > 0 && now - history.Peek().Tick > Info.LossWindow)
				history.Dequeue();

			var peak = history.Max(h => h.Value);
			var losing = peak > 0 && value * 100 < peak * (100 - Info.LossPercent) && now > retreatUntil;

			// Who are we fighting / at peace with?
			foreach (var p in Others)
			{
				if (truce.InTruce(player, p))
					truceSince.TryAdd(p, now);
				else
				{
					truceSince.Remove(p);
					breaking.Remove(p);
				}
			}

			if (losing)
			{
				// Beg everyone we're fighting for a truce and pull back home.
				foreach (var p in Others.Where(p => !truce.InTruce(player, p)))
				{
					lastProposal.Remove(p);
					Propose(bot, p);
				}

				var home = world.Map.CenterOfCell(Home(player));
				foreach (var u in army)
					bot.QueueOrder(new Order("Move", u, Target.FromCell(world, world.Map.CellContaining(home) + new CVec(world.LocalRandom.Next(-4, 5), world.LocalRandom.Next(-4, 5))), false));

				retreatUntil = now + Info.LossWindow * 2;
				history.Clear();
				return;
			}

			var enemies = Others.Where(p => !truce.InTruce(player, p)).ToList();
			var partners = Others.Where(p => truce.InTruce(player, p)).ToList();

			// Keep only one front open: offer truces to everyone except the weakest enemy.
			if (enemies.Count > 1)
			{
				var target = enemies.MinBy(ArmyValue);
				foreach (var p in enemies.Where(p => p != target))
					Propose(bot, p);
			}
			else if (enemies.Count == 0 || world.LocalRandom.Next(4) == 0)
			{
				foreach (var p in enemies)
					Propose(bot, p);
			}

			// Betrayal: once the truce is old enough and we're strong, march the army over and then break it.
			foreach (var p in partners)
			{
				if (breaking.Contains(p) || now - truceSince[p] < Info.MinTruceTicks)
					continue;

				if (army.Count < Info.MinStrikeUnits || value * 100 < Math.Max(1, ArmyValue(p)) * Info.BreakArmyPercent)
					continue;

				// Staging point: two thirds of the way from our base to theirs.
				var from = world.Map.CenterOfCell(Home(player));
				var to = world.Map.CenterOfCell(Home(p));
				var stage = world.Map.CellContaining(from + (to - from) * 2 / 3);
				var near = army.Count(u => (u.Location - stage).LengthSquared < 12 * 12);
				if (near * 100 < army.Count * Info.StagedPercent)
				{
					foreach (var u in army.Where(u => (u.Location - stage).LengthSquared >= 12 * 12))
						bot.QueueOrder(new Order("AttackMove", u, Target.FromCell(world, stage + new CVec(world.LocalRandom.Next(-5, 6), world.LocalRandom.Next(-5, 6))), false));

					continue;
				}

				breaking.Add(p);
				bot.QueueOrder(new Order("BreakTruce", player.PlayerActor, false) { ExtraData = (uint)world.Players.IndexOf(p) });
				break;
			}
		}
	}

	// ================================================================================================
	// AI: sends idle spies to sneak into enemy refineries and silos (stealing money).
	// ================================================================================================
	[TraitLocation(SystemActors.Player)]
	[Desc("AI: sends spies to infiltrate enemy buildings.")]
	public class SpyBotModuleInfo : ConditionalTraitInfo
	{
		public readonly HashSet<string> SpyTypes = ["spy.hermit"];
		public readonly HashSet<string> TargetTypes = ["proc", "silo"];
		public readonly int Interval = 300;

		public override object Create(ActorInitializer init) { return new SpyBotModule(init.Self, this); }
	}

	public class SpyBotModule : ConditionalTrait<SpyBotModuleInfo>, IBotTick
	{
		readonly World world;
		readonly Player player;
		int ticks;

		public SpyBotModule(Actor self, SpyBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (++ticks < Info.Interval)
				return;

			ticks = 0;
			foreach (var spy in world.Actors.Where(a => a.Owner == player && !a.IsDead && a.IsInWorld && a.IsIdle && Info.SpyTypes.Contains(a.Info.Name)).ToList())
			{
				var target = world.Actors.Where(a => !a.IsDead && a.IsInWorld && Info.TargetTypes.Contains(a.Info.Name)
						&& player.RelationshipWith(a.Owner) == PlayerRelationship.Enemy && BotHelpers.Known(player, a))
					.MinByOrDefault(a => (a.Location - spy.Location).LengthSquared);
				if (target != null)
					bot.QueueOrder(new Order("Infiltrate", spy, Target.FromActor(target), false));
			}
		}
	}

	// ================================================================================================
	// AI: the Hermit Nation's dirty war. This is the Hermit's main way of fighting:
	//  - spies sneak into enemy refineries/silos/radar (any non-allied player, truce or not),
	//  - Cyber Attacks hit the richest rival, Psy-Ops are dropped on the enemy base,
	//  - suicide-bomber packs and truck bombs go after whoever it is at war with (and swarm any enemy force
	//    that comes near its own base),
	//  - kamikaze Badgers dive into the enemy's most valuable buildings.
	// Units are taken away from the normal squads (list them in the SquadManager's ExcludeFromSquadsTypes).
	// ================================================================================================
	[TraitLocation(SystemActors.Player)]
	[Desc("AI: uses spies, suicide bombers, truck bombs, kamikaze planes, Cyber Attacks and Psy-Ops.")]
	public class HermitTacticsBotModuleInfo : ConditionalTraitInfo
	{
		public readonly HashSet<string> SpyTypes = ["spy.hermit"];
		public readonly HashSet<string> SpyTargets = ["proc", "silo", "dome", "powr", "apwr", "weap"];

		public readonly HashSet<string> BomberTypes = ["suicidebomber"];
		public readonly int BomberPack = 4;
		public readonly HashSet<string> BomberTargets = ["harv", "pbox", "hbox", "gun", "ftur", "tsla", "agun", "sam", "proc", "powr", "apwr", "barr", "tent"];

		public readonly HashSet<string> TruckTypes = ["truckbomb"];
		public readonly HashSet<string> TruckTargets = ["fact", "weap", "proc", "stek", "atek", "dome", "pbox", "hbox", "gun", "tsla", "ftur"];

		public readonly HashSet<string> KamikazeTypes = ["badr.kamikaze"];
		public readonly HashSet<string> KamikazeTargets = ["fact", "weap", "stek", "atek", "proc", "dome", "afld", "hpad", "apwr", "powr", "spen", "syrd"];

		[Desc("Kamikaze planes launched per round at most.")]
		public readonly int KamikazePerRound = 2;

		public readonly string CyberPower = "HermitCyberAttack";
		public readonly string PsyOpPower = "HermitPsyOp";

		[Desc("Don't launch another Psy-Op while we already own this many protesters.")]
		public readonly int MaxProtesters = 40;

		[Desc("Enemy forces this close (cells) to our base get swarmed by the bombers.")]
		public readonly int DefendRadius = 18;

		public readonly int Interval = 100;

		[Desc("When at peace with everyone, march the bombers and truck bombs up to the nearest truce partner and",
			"break the truce once this many explosive units are ready (0 = never).")]
		public readonly int BetrayalForce = 6;

		[Desc("After this long at peace with everyone, betray with whatever force we have (at least 3 units).")]
		public readonly int MaxPeaceTicks = 4000;

		[Desc("Give the staging march this long before breaking the truce anyway.")]
		public readonly int StagingTicks = 1500;

		public override object Create(ActorInitializer init) { return new HermitTacticsBotModule(init.Self, this); }
	}

	public class HermitTacticsBotModule : ConditionalTrait<HermitTacticsBotModuleInfo>, IBotTick
	{
		readonly World world;
		readonly Player player;
		readonly Dictionary<Actor, Actor> jobs = [];
		TruceManager truces;
		SupportPowerManager powers;
		int ticks;
		Player victim;
		int stagingStarted, peaceSince;

		public HermitTacticsBotModule(Actor self, HermitTacticsBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		protected override void Created(Actor self)
		{
			truces = world.WorldActor.TraitOrDefault<TruceManager>();
			powers = self.TraitOrDefault<SupportPowerManager>();
			ticks = world.LocalRandom.Next(Info.Interval);
			base.Created(self);
		}

		// An opponent: an enemy, or someone we're only in a truce with (a truce makes the two sides neutral).
		bool Rival(Player p) => p != null && p.Playable && !p.NonCombatant && p != player && p.WinState == WinState.Undefined
			&& (player.RelationshipWith(p) == PlayerRelationship.Enemy || (truces != null && truces.InTruce(player, p)));

		// Players we are openly at war with (no truce).
		bool AtWarAny(Player p) => Rival(p) && (truces == null || !truces.InTruce(player, p));

		// While the AI Commander has an order in force, the special units only go after the player it named.
		Player CommandTarget => AICommanderBotModule.For(player)?.ActiveTarget;

		bool AtWar(Player p) => AtWarAny(p) && (CommandTarget == null || p == CommandTarget);

		bool Alive(Actor a) => a != null && !a.IsDead && a.IsInWorld;

		IEnumerable<Actor> Mine(HashSet<string> types) => world.Actors.Where(a => a.Owner == player && Alive(a) && types.Contains(a.Info.Name));

		Actor Pick(WPos from, HashSet<string> types, Func<Player, bool> who, bool anyBuilding)
		{
			Actor best = null;
			long bestScore = long.MaxValue;
			foreach (var a in world.Actors)
			{
				if (!Alive(a) || !who(a.Owner) || !BotHelpers.Known(player, a) || a.Info.HasTraitInfo<HuskInfo>() || !a.Info.HasTraitInfo<ITargetableInfo>())
					continue;

				var wanted = types.Contains(a.Info.Name);
				if (!wanted && !(anyBuilding && a.Info.HasTraitInfo<BuildingInfo>()))
					continue;

				var score = (a.CenterPosition - from).Length / 1024 + (wanted ? 0 : 40);
				if (score < bestScore)
				{
					bestScore = score;
					best = a;
				}
			}

			return best;
		}

		// Go for it: a direct attack if we can see it, otherwise head there ready to fight.
		void Strike(IBot bot, Actor unit, Actor target, bool aircraft)
		{
			jobs[unit] = target;
			if (aircraft)
			{
				var t = target.CanBeViewedByPlayer(player) ? Target.FromActor(target) : Target.FromPos(target.CenterPosition);
				bot.QueueOrder(new Order("Attack", unit, t, false));
			}
			else if (target.CanBeViewedByPlayer(player))
				bot.QueueOrder(new Order("Attack", unit, Target.FromActor(target), false));
			else
				bot.QueueOrder(new Order("AttackMove", unit, Target.FromCell(world, target.Location), false));
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (++ticks < Info.Interval)
				return;

			ticks = 0;
			foreach (var dead in jobs.Keys.Where(a => !Alive(a) || a.Owner != player).ToList())
				jobs.Remove(dead);

			var squads = BotHelpers.Squads(player);
			var home = BotHelpers.BaseCenter(player, world.Map.CenterOfCell(player.HomeLocation));
			// The AI Commander's orders come first: "defend"/"economy" means no new strikes; its own diplomacy
			// replaces the Hermit's betrayal scheming.
			var commander = AICommanderBotModule.For(player);
			var anyWar = (commander == null || !commander.HoldOffensives) && world.Players.Any(AtWar);
			if (anyWar)
				peaceSince = world.WorldTick;

			if (world.WorldTick % 1500 < Info.Interval)
				Log.Write("debug", $"Hermit {player.ResolvedPlayerName} t={world.WorldTick}: war={anyWar} peaceSince={peaceSince} victim={victim?.ResolvedPlayerName} others=[{string.Join(", ", world.Players.Where(p => p.Playable && p != player).Select(p => $"{p.ResolvedPlayerName}:rival={Rival(p)},truce={truces?.InTruce(player, p)}"))}] jobs=[{string.Join(", ", jobs.Select(j => $"{j.Key.Info.Name}->{j.Value.Info.Name}@{j.Value.Location}{(j.Key.IsIdle ? "(idle)" : "")}"))}]"
					+ $" powers=[{string.Join(", ", powers?.Powers.Select(p => $"{p.Key}:{p.Value.RemainingTicks}{(p.Value.Active ? "" : "-off")}") ?? [])}]");

			UsePowers(bot, home);
			RunSpies(bot);

			// Enemy forces near our base: everything explosive goes at them.
			var intruder = world.Actors.Where(a => Alive(a) && AtWarAny(a.Owner) && a.Info.HasTraitInfo<MobileInfo>() && a.CanBeViewedByPlayer(player)
					&& (a.CenterPosition - home).LengthSquared < WDist.FromCells(Info.DefendRadius).LengthSquared)
				.MinByOrDefault(a => (a.CenterPosition - home).LengthSquared);

			var bombers = Mine(Info.BomberTypes).ToList();
			squads?.ClaimUnits(bombers);
			if (commander == null && !anyWar && intruder == null && Betrayal(bot, home, bombers.Concat(Mine(Info.TruckTypes)).ToList()))
				return;

			if (intruder != null)
			{
				foreach (var b in bombers.Where(b => !jobs.TryGetValue(b, out var j) || !Alive(j) || b.IsIdle))
					Strike(bot, b, intruder, false);
			}
			else if (anyWar)
			{
				// Busy ones carry on; re-aim the ones whose target is gone.
				foreach (var b in bombers.Where(b => jobs.ContainsKey(b)))
				{
					if (Alive(jobs[b]) && AtWar(jobs[b].Owner))
					{
						if (b.IsIdle)
							Strike(bot, b, jobs[b], false);
						continue;
					}

					var t = Pick(b.CenterPosition, Info.BomberTargets, AtWar, true);
					if (t != null)
						Strike(bot, b, t, false);
					else
						jobs.Remove(b);
				}

				var free = bombers.Where(b => !jobs.ContainsKey(b) && b.IsIdle).ToList();
				if (free.Count >= Info.BomberPack)
				{
					var t = Pick(free[0].CenterPosition, Info.BomberTargets, AtWar, true);
					if (t != null)
						foreach (var b in free)
							Strike(bot, b, t, false);
				}
			}

			// Truck bombs: one at a time into key buildings.
			var trucks = Mine(Info.TruckTypes).ToList();
			squads?.ClaimUnits(trucks);
			foreach (var tr in trucks)
			{
				if (jobs.TryGetValue(tr, out var j) && Alive(j) && (AtWar(j.Owner) || intruder != null))
				{
					if (tr.IsIdle)
						Strike(bot, tr, j, false);
					continue;
				}

				var t = intruder != null && !intruder.Info.HasTraitInfo<AircraftInfo>() ? intruder : (anyWar ? Pick(tr.CenterPosition, Info.TruckTargets, AtWar, true) : null);
				if (t != null)
					Strike(bot, tr, t, false);
				else
					jobs.Remove(tr);
			}

			// Kamikaze Badgers: a couple per round, each at a different valuable building.
			if (anyWar)
			{
				var launched = 0;
				var taken = new HashSet<Actor>(jobs.Values);
				foreach (var k in Mine(Info.KamikazeTypes).Where(k => k.IsIdle).ToList())
				{
					if (launched >= Info.KamikazePerRound)
						break;

					var ammo = k.TraitOrDefault<AmmoPool>();
					if (ammo != null && !ammo.HasAmmo)
						continue;

					var t = world.Actors.Where(a => Alive(a) && AtWar(a.Owner) && BotHelpers.Known(player, a) && a.Info.HasTraitInfo<BuildingInfo>()
							&& !a.Info.HasTraitInfo<HuskInfo>() && !taken.Contains(a))
						.OrderBy(a => Info.KamikazeTargets.Contains(a.Info.Name) ? 0 : 1)
						.ThenByDescending(a => BotHelpers.Cost(a.Info))
						.ThenBy(a => (a.CenterPosition - k.CenterPosition).LengthSquared)
						.FirstOrDefault();
					if (t == null)
						break;

					taken.Add(t);
					Strike(bot, k, t, true);
					launched++;
				}
			}
		}

		// At peace with everyone: walk the explosive units up to a truce partner's doorstep, then break the truce.
		bool Betrayal(IBot bot, WPos home, List<Actor> force)
		{
			if (Info.BetrayalForce <= 0 || truces == null)
				return false;

			if (victim == null || !Rival(victim) || !truces.InTruce(player, victim))
			{
				victim = null;
				var restless = world.WorldTick - peaceSince > Info.MaxPeaceTicks && force.Count >= 3;
				if (force.Count < Info.BetrayalForce && !restless)
					return false;

				victim = world.Players.Where(p => Rival(p) && truces.InTruce(player, p))
					.MinByOrDefault(p => (world.Map.CenterOfCell(p.HomeLocation) - home).LengthSquared);
				if (victim == null)
					return false;

				stagingStarted = world.WorldTick;
			}

			var target = world.Map.CenterOfCell(victim.HomeLocation);
			var stage = world.Map.CellContaining(home + (target - home) * 2 / 3);
			var ready = force.Count(a => (a.Location - stage).LengthSquared <= 64);
			if (ready * 10 >= force.Count * 7 || world.WorldTick - stagingStarted > Info.StagingTicks)
			{
				Log.Write("debug", $"Hermit {player.ResolvedPlayerName}: betrayal - breaking the truce with {victim.ResolvedPlayerName} ({ready}/{force.Count} in position)");
				bot.QueueOrder(new Order("BreakTruce", player.PlayerActor, false) { ExtraData = (uint)world.Players.IndexOf(victim) });
				victim = null;
				return false;
			}

			foreach (var a in force.Where(a => a.IsIdle && (a.Location - stage).LengthSquared > 64))
				bot.QueueOrder(new Order("Move", a, Target.FromCell(world, stage), false));
			return true;
		}

		void RunSpies(IBot bot)
		{
			var spies = Mine(Info.SpyTypes).ToList();
			BotHelpers.Squads(player)?.ClaimUnits(spies);
			foreach (var spy in spies)
			{
				// (Units can't act against a truce partner, so spies only go after players we're at war with.)
				if (!jobs.TryGetValue(spy, out var t) || !Alive(t) || !AtWar(t.Owner))
				{
					t = Pick(spy.CenterPosition, Info.SpyTargets, AtWar, false);
					if (t == null)
						continue;
					jobs[spy] = t;
				}
				else if (!spy.IsIdle)
					continue;

				if (t.CanBeViewedByPlayer(player))
					bot.QueueOrder(new Order("Infiltrate", spy, Target.FromActor(t), false));
				else
				{
					// Walk up to it first (can't sneak into what we can't see).
					var near = world.Map.FindTilesInAnnulus(t.Location, 2, 4).FirstOrDefault(c => world.Map.Contains(c));
					bot.QueueOrder(new Order("Move", spy, Target.FromCell(world, near), false));
				}
			}
		}

		void UsePowers(IBot bot, WPos home)
		{
			if (powers == null)
				return;

			// Cyber Attack: the richest rival (truce or not - it's deniable).
			if (powers.Powers.TryGetValue(Info.CyberPower, out var cyber) && cyber.Ready)
			{
				var victim = CommandTarget != null && Rival(CommandTarget) ? CommandTarget : world.Players.Where(Rival)
					.OrderByDescending(p => p.PlayerActor.Trait<PlayerResources>().Cash + p.PlayerActor.Trait<PlayerResources>().Resources).FirstOrDefault();
				var building = victim == null ? null : world.Actors.Where(a => Alive(a) && a.Owner == victim && a.Info.HasTraitInfo<BuildingInfo>())
					.MinByOrDefault(a => (a.Location - victim.HomeLocation).LengthSquared);
				if (building != null)
					bot.QueueOrder(new Order(Info.CyberPower, player.PlayerActor, Target.FromCell(world, building.Location), false) { SuppressVisualFeedback = true, ExtraData = uint.MaxValue });
			}

			// Psy-Op: drop the crowd in the middle of an enemy base (an enemy at war if there is one).
			// ... but not while the last crowds are still around: they only clog the army.
			var crowd = world.Actors.Count(a => a.Owner == player && Alive(a) && a.Info.Name.StartsWith("protester", System.StringComparison.Ordinal));
			if (crowd < Info.MaxProtesters && powers.Powers.TryGetValue(Info.PsyOpPower, out var psy) && psy.Ready)
			{
				var war = world.Players.Where(AtWar).ToList();
				var who = war.Count > 0 ? war : world.Players.Where(Rival).ToList();
				var victim = who.MinByOrDefault(p => (world.Map.CenterOfCell(p.HomeLocation) - home).LengthSquared);
				if (victim != null)
				{
					var buildings = world.Actors.Where(a => Alive(a) && a.Owner == victim && a.Info.HasTraitInfo<BuildingInfo>()).ToList();
					var spot = buildings.Count == 0 ? victim.HomeLocation
						: buildings.MinBy(b => buildings.Sum(o => (o.Location - b.Location).LengthSquared)).Location;
					bot.QueueOrder(new Order(Info.PsyOpPower, player.PlayerActor, Target.FromCell(world, spot), false) { SuppressVisualFeedback = true, ExtraData = uint.MaxValue });
				}
			}
		}
	}
}
