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
using OpenRA.Graphics;
using OpenRA.Mods.Common.Commands;
using OpenRA.Mods.Common.Orders;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Widgets;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// Truces between players.
	//   Select any of an opponent's units or buildings and press Ctrl -> "Propose a truce?" pop-up.
	//   The opponent gets an Accept / Decline pop-up.
	//   Chat commands also work:
	//   /truce   -> click an opponent's unit to propose a truce to that player
	//   /accept  -> accept a truce someone proposed to you
	//   /decline -> turn it down
	// While a truce holds, both sides are neutral to each other, so nothing fires automatically.
	// Damaging the other side's units (after a short grace period) breaks the truce for both.
	[TraitLocation(SystemActors.World)]
	[Desc("Keeps track of truces between players. Attach this to the world actor.")]
	public class TruceManagerInfo : TraitInfo
	{
		[Desc("Ticks after a truce starts during which stray shots don't break it (25 ticks = 1 second).")]
		public readonly int GracePeriod = 125;

		[Desc("Chance (percent) that a computer player accepts a truce.")]
		public readonly int BotAcceptChance = 50;

		[Desc("Computer players answer after this many ticks, plus a random extra delay up to BotThinkTimeRandom.")]
		public readonly int BotThinkTime = 75;

		public readonly int BotThinkTimeRandom = 150;

		[Desc("Computer players may break a truce once it has lasted this long (ticks) and they have become much stronger.")]
		public readonly int BotBreakMinTicks = 6000;

		[Desc("How much stronger (percent of the other side) a computer player must be before it thinks about breaking a truce.")]
		public readonly int BotBreakStrengthPercent = 200;

		[Desc("Chance (percent) per check that a strong computer player breaks the truce.")]
		public readonly int BotBreakChance = 25;

		[Desc("Warning time (ticks) between announcing the end of a truce and breaking it.")]
		public readonly int BotBreakWarning = 750;

		[Desc("Players of these factions start the game in a truce with everyone (until someone breaks it).")]
		public readonly HashSet<string> AutoTruceFactions = [];

		[Desc("Computer players of these factions never break a truce themselves.")]
		public readonly HashSet<string> PeacefulFactions = [];

		public override object Create(ActorInitializer init) { return new TruceManager(init.World, this); }
	}

	public class TruceManager : ITick
	{
		const string Label = "Diplomacy";
		readonly World world;
		readonly TruceManagerInfo info;

		// proposer index -> target index
		readonly HashSet<(int From, int To)> proposals = [];

		// unordered pair -> tick the truce started
		readonly Dictionary<(int A, int B), int> truces = [];

		// computer players thinking about an offer: (proposer, bot, tick to answer)
		readonly List<(int From, int To, int Due)> botAnswers = [];

		// truces a computer player has announced it will break: (bot, other, tick)
		readonly List<(int Bot, int Other, int Due)> botBreaks = [];

		public TruceManager(World world, TruceManagerInfo info)
		{
			this.world = world;
			this.info = info;
		}

		static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);
		int Index(Player p) => world.Players.IndexOf(p);
		Player At(uint i) => i < world.Players.Length ? world.Players[i] : null;

		public bool InTruce(Player a, Player b) => truces.ContainsKey(Key(Index(a), Index(b)));

		static bool CanTruce(Player a, Player b)
		{
			return a != null && b != null && a != b && a.Playable && b.Playable && !a.NonCombatant && !b.NonCombatant
				&& a.WinState == WinState.Undefined && b.WinState == WinState.Undefined;
		}

		static void Say(string text)
		{
			TextNotificationsManager.AddSystemLine(Label, text);
		}

		bool IsLocal(Player p) => world.LocalPlayer == p;

		public void Propose(Player from, uint targetIndex)
		{
			var to = At(targetIndex);
			if (!CanTruce(from, to) || InTruce(from, to) || from.RelationshipWith(to) != PlayerRelationship.Enemy)
				return;

			proposals.Add((Index(from), Index(to)));
			if (IsLocal(from))
				Say($"You proposed a truce to {to.ResolvedPlayerName}. Waiting for an answer...");

			// Computer players think it over, then randomly accept or decline (synced random).
			if (to.IsBot)
				botAnswers.Add((Index(from), Index(to), world.WorldTick + info.BotThinkTime + world.SharedRandom.Next(info.BotThinkTimeRandom)));

			if (IsLocal(to))
			{
				Say($"{from.ResolvedPlayerName} proposes a truce.");
				Game.Sound.PlayNotification(world.Map.Rules, to, "Sounds", "ChatLine", to.Faction.InternalName);
				var name = from.ResolvedPlayerName;
				Game.RunAfterTick(() => ConfirmationDialogs.ButtonPrompt(Game.ModData,
					title: TruceCommands.OfferTitle,
					text: TruceCommands.OfferPrompt,
					textArguments: ["player", name],
					onConfirm: () => world.IssueOrder(new Order("AcceptTruce", to.PlayerActor, false)),
					confirmText: TruceCommands.AcceptButton,
					onCancel: () => world.IssueOrder(new Order("DeclineTruce", to.PlayerActor, false)),
					cancelText: TruceCommands.DeclineButton));
			}
		}

		public void Answer(Player answering, bool accept)
		{
			// Answer the most recent proposal made to this player.
			var me = Index(answering);
			var offer = proposals.LastOrDefault(p => p.To == me);
			if (offer == default || !proposals.Remove(offer))
			{
				if (IsLocal(answering))
					Say("Nobody has proposed a truce to you.");

				return;
			}

			var from = world.Players[offer.From];
			if (!accept)
			{
				if (IsLocal(from) || IsLocal(answering))
					Say($"{answering.ResolvedPlayerName} declined the truce with {from.ResolvedPlayerName}.");

				return;
			}

			if (!CanTruce(from, answering) || from.RelationshipWith(answering) != PlayerRelationship.Enemy)
				return;

			proposals.Remove((offer.To, offer.From));
			truces[Key(offer.From, me)] = world.WorldTick;
			SetEnemies(from, answering, false);
			CeaseFire(from, answering);
			CeaseFire(answering, from);
			Say($"{from.ResolvedPlayerName} and {answering.ResolvedPlayerName} have agreed to a truce. Attacking the other side will break it.");
		}

		bool startingTrucesDone;

		void ITick.Tick(Actor self)
		{
			// Peaceful factions start the game at peace with everyone.
			if (!startingTrucesDone)
			{
				startingTrucesDone = true;
				foreach (var p in world.Players.Where(p => p.Playable && info.AutoTruceFactions.Contains(p.Faction.InternalName)))
				{
					foreach (var o in world.Players)
					{
						if (!CanTruce(p, o) || p.RelationshipWith(o) != PlayerRelationship.Enemy || InTruce(p, o))
							continue;

						truces[Key(Index(p), Index(o))] = world.WorldTick;
						SetEnemies(p, o, false);
					}

					if (world.Players.Any(o => o != p && InTruce(p, o)))
						Say($"{p.ResolvedPlayerName} is a peaceful nation and starts in a truce with everyone. Attacking them will break it.");
				}
			}

			for (var i = botAnswers.Count - 1; i >= 0; i--)
			{
				var (from, to, due) = botAnswers[i];
				if (world.WorldTick < due)
					continue;

				botAnswers.RemoveAt(i);
				if (!proposals.Contains((from, to)))
					continue;

				var bot = world.Players[to];

				// A computer player that is losing badly grabs a truce; one that is winning usually refuses.
				var mine = Strength(bot);
				var theirs = Math.Max(1, Strength(world.Players[from]));
				var ratio = mine * 100 / theirs;
				var chance = info.BotAcceptChance;
				if (ratio < 100)
					chance += (100 - ratio) * 45 / 100;
				else if (ratio > 150)
					chance -= Math.Min(40, (ratio - 150) / 5);
				chance = Math.Clamp(chance, 5, 95);
				var accept = world.SharedRandom.Next(100) < chance;
				if (!accept)
				{
					proposals.Remove((from, to));
					Say($"{bot.ResolvedPlayerName} rejected the truce offered by {world.Players[from].ResolvedPlayerName}.");
					continue;
				}

				Answer(bot, true);
			}

			// Strong computer players may turn on a weaker truce partner (with a warning first).
			if (world.WorldTick % 750 == 0)
			{
				foreach (var kv in truces.ToList())
				{
					if (world.WorldTick - kv.Value < info.BotBreakMinTicks)
						continue;

					foreach (var (b, o) in new[] { (kv.Key.A, kv.Key.B), (kv.Key.B, kv.Key.A) })
					{
						var bot = world.Players[b];
						var other = world.Players[o];
						if (!bot.IsBot || botBreaks.Any(x => x.Bot == b && x.Other == o))
							continue;

						if (Strength(bot) * 100 < Strength(other) * info.BotBreakStrengthPercent || world.SharedRandom.Next(100) >= info.BotBreakChance)
							continue;

						botBreaks.Add((b, o, world.WorldTick + info.BotBreakWarning));
						Say($"{bot.ResolvedPlayerName} announces that its truce with {other.ResolvedPlayerName} ends in 30 seconds!");
						if (IsLocal(other))
							Game.Sound.PlayNotification(world.Map.Rules, other, "Speech", "BaseAttack", other.Faction.InternalName);
					}
				}
			}

			for (var i = botBreaks.Count - 1; i >= 0; i--)
			{
				var (b, o, due) = botBreaks[i];
				if (world.WorldTick < due)
					continue;

				botBreaks.RemoveAt(i);
				var key = Key(b, o);
				if (!truces.Remove(key))
					continue;

				SetEnemies(world.Players[b], world.Players[o], true);
				Say($"{world.Players[b].ResolvedPlayerName} has broken the truce with {world.Players[o].ResolvedPlayerName}!");
			}
		}

		// Rough military + economic strength, from the synced player statistics.
		static int Strength(Player p)
		{
			var st = p.PlayerActor.TraitOrDefault<PlayerStatistics>();
			return st == null ? 1 : Math.Max(1, st.ArmyValue + st.AssetsValue / 2);
		}

		// A player announces that it is ending a truce (computer players use this; 30 seconds' warning).
		public void AnnounceBreak(Player bot, uint otherIndex)
		{
			var other = At(otherIndex);
			if (other == null || !InTruce(bot, other))
				return;

			var b = Index(bot);
			var o = Index(other);
			if (botBreaks.Any(x => x.Bot == b && x.Other == o))
				return;

			botBreaks.Add((b, o, world.WorldTick + info.BotBreakWarning));
			Say($"{bot.ResolvedPlayerName} announces that its truce with {other.ResolvedPlayerName} ends in 30 seconds!");
			if (IsLocal(other))
				Game.Sound.PlayNotification(world.Map.Rules, other, "Speech", "BaseAttack", other.Faction.InternalName);
		}

		public void NotifyDamage(Player attacker, Player victim)
		{
			if (attacker == null || victim == null || attacker == victim)
				return;

			var key = Key(Index(attacker), Index(victim));
			if (!truces.TryGetValue(key, out var started) || world.WorldTick - started < info.GracePeriod)
				return;

			truces.Remove(key);
			botBreaks.RemoveAll(x => Key(x.Bot, x.Other) == key);
			SetEnemies(attacker, victim, true);
			Say($"{attacker.ResolvedPlayerName} attacked {victim.ResolvedPlayerName}. The truce is broken!");
			if (IsLocal(victim) || IsLocal(attacker))
				Game.Sound.PlayNotification(world.Map.Rules, world.LocalPlayer, "Speech", "BaseAttack", world.LocalPlayer.Faction.InternalName);
		}

		static void SetEnemies(Player a, Player b, bool enemies)
		{
			if (enemies)
			{
				a.EnemyPlayersMask = a.EnemyPlayersMask.Union(b.PlayerMask);
				b.EnemyPlayersMask = b.EnemyPlayersMask.Union(a.PlayerMask);
			}
			else
			{
				a.EnemyPlayersMask = a.EnemyPlayersMask.Except(b.PlayerMask);
				b.EnemyPlayersMask = b.EnemyPlayersMask.Except(a.PlayerMask);
			}
		}

		// Call off attacks that units of 'side' are making on 'other'.
		void CeaseFire(Player side, Player other)
		{
			foreach (var a in world.Actors.Where(a => a.Owner == side && !a.IsDead && a.IsInWorld).ToList())
			{
				var attacks = a.TraitsImplementing<AttackBase>().ToArray();
				if (attacks.Length == 0)
					continue;

				var targeting = false;
				var act = a.CurrentActivity;
				if (act != null)
					targeting = act.TargetLineNodes(a).Any(n => TargetOwner(n.Target) == other);

				foreach (var follow in attacks.OfType<AttackFollow>())
					if (TargetOwner(follow.RequestedTarget) == other || TargetOwner(follow.OpportunityTarget) == other)
						targeting = true;

				if (targeting)
					foreach (var ab in attacks)
						ab.OnStopOrder(a);
			}
		}

		static Player TargetOwner(in Target t)
		{
			if (t.Type == TargetType.Actor)
				return t.Actor.Owner;

			if (t.Type == TargetType.FrozenActor)
				return t.FrozenActor.Owner;

			return null;
		}
	}

	[TraitLocation(SystemActors.Player)]
	[Desc("Handles truce orders from this player. Attach this to the player actor.")]
	public class TruceOrdersInfo : TraitInfo<TruceOrders> { }

	public class TruceOrders : IResolveOrder
	{
		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			var manager = self.World.WorldActor.TraitOrDefault<TruceManager>();
			if (manager == null)
				return;

			switch (order.OrderString)
			{
				case "ProposeTruce":
					manager.Propose(self.Owner, order.ExtraData);
					break;
				case "AcceptTruce":
					manager.Answer(self.Owner, true);
					break;
				case "DeclineTruce":
					manager.Answer(self.Owner, false);
					break;
				case "BreakTruce":
					manager.AnnounceBreak(self.Owner, order.ExtraData);
					break;
			}
		}
	}

	[Desc("Breaks a truce when this actor is damaged by the other side.")]
	public class TruceWatcherInfo : TraitInfo<TruceWatcher> { }

	public class TruceWatcher : INotifyDamage
	{
		void INotifyDamage.Damaged(Actor self, AttackInfo e)
		{
			if (e.Attacker == null || e.Damage.Value <= 0)
				return;

			self.World.WorldActor.TraitOrDefault<TruceManager>()?.NotifyDamage(e.Attacker.Owner, self.Owner);
		}
	}

	[TraitLocation(SystemActors.World)]
	[IncludeStaticFluentReferences(typeof(TruceCommands))]
	[Desc("Adds the /truce, /accept and /decline chat commands. Attach this to the world actor.")]
	public class TruceCommandsInfo : TraitInfo<TruceCommands> { }

	public class TruceCommands : IChatCommand, IWorldLoaded, ITickRender
	{
		[FluentReference]
		public const string ProposeTitle = "dialog-truce-propose.title";

		[FluentReference("player")]
		public const string ProposePrompt = "dialog-truce-propose.prompt";

		[FluentReference]
		public const string ProposeButton = "dialog-truce-propose.confirm";

		[FluentReference]
		public const string CancelButton = "dialog-truce-propose.cancel";

		[FluentReference]
		public const string OfferTitle = "dialog-truce-offer.title";

		[FluentReference("player")]
		public const string OfferPrompt = "dialog-truce-offer.prompt";

		[FluentReference]
		public const string AcceptButton = "dialog-truce-offer.accept";

		[FluentReference]
		public const string DeclineButton = "dialog-truce-offer.decline";

		bool ctrlWasDown;
		bool promptOpen;

		void ITickRender.TickRender(WorldRenderer wr, Actor self)
		{
			var ctrl = Game.GetModifierKeys().HasModifier(Modifiers.Ctrl);
			var pressed = ctrl && !ctrlWasDown;
			ctrlWasDown = ctrl;
			if (!pressed || promptOpen || world == null)
				return;

			var me = world.LocalPlayer;
			if (me == null || me.WinState != WinState.Undefined)
				return;

			// Any selection made up only of one opponent's units or buildings.
			var selected = world.Selection.Actors.Where(a => !a.IsDead).ToList();
			if (selected.Count == 0)
				return;

			var them = selected[0].Owner;
			if (selected.Any(a => a.Owner != them) || !them.Playable || them.NonCombatant
				|| me.RelationshipWith(them) != PlayerRelationship.Enemy)
				return;

			promptOpen = true;
			ConfirmationDialogs.ButtonPrompt(Game.ModData,
				title: ProposeTitle,
				text: ProposePrompt,
				textArguments: ["player", them.ResolvedPlayerName],
				onConfirm: () =>
				{
					promptOpen = false;
					world.IssueOrder(new Order("ProposeTruce", me.PlayerActor, false) { ExtraData = (uint)world.Players.IndexOf(them) });
				},
				confirmText: ProposeButton,
				onCancel: () => promptOpen = false,
				cancelText: CancelButton);
		}

		[FluentReference]
		const string TruceDescription = "description-truce";

		[FluentReference]
		const string AcceptDescription = "description-accept-truce";

		[FluentReference]
		const string DeclineDescription = "description-decline-truce";

		World world;

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			world = w;
			var console = w.WorldActor.TraitOrDefault<ChatCommands>();
			var help = w.WorldActor.TraitOrDefault<HelpCommand>();
			if (console == null || help == null)
				return;

			console.RegisterCommand("truce", this);
			help.RegisterHelp("truce", TruceDescription);
			console.RegisterCommand("accept", this);
			help.RegisterHelp("accept", AcceptDescription);
			console.RegisterCommand("decline", this);
			help.RegisterHelp("decline", DeclineDescription);
		}

		public void InvokeCommand(string name, string arg)
		{
			var me = world.LocalPlayer;
			if (me == null || me.WinState != WinState.Undefined)
				return;

			switch (name)
			{
				case "truce":
					var named = string.IsNullOrWhiteSpace(arg) ? null
						: world.Players.FirstOrDefault(p => p.Playable && p != me && p.ResolvedPlayerName.Equals(arg.Trim(), System.StringComparison.OrdinalIgnoreCase));

					if (named != null)
						world.IssueOrder(new Order("ProposeTruce", me.PlayerActor, false) { ExtraData = (uint)world.Players.IndexOf(named) });
					else
					{
						TextNotificationsManager.AddSystemLine("Diplomacy", "Click one of an opponent's units or buildings to propose a truce (right-click to cancel).");
						world.OrderGenerator = new SelectTruceTarget(world);
					}

					break;
				case "accept":
					world.IssueOrder(new Order("AcceptTruce", me.PlayerActor, false));
					break;
				case "decline":
					world.IssueOrder(new Order("DeclineTruce", me.PlayerActor, false));
					break;
			}
		}
	}

	// Cursor mode: click an opponent's unit to propose a truce to its owner.
	public class SelectTruceTarget : OrderGenerator
	{
		protected override MouseActionType ActionType => MouseActionType.SupportPower;

		public SelectTruceTarget(World world)
			: base(world) { }

		static Player OpponentUnder(World world, MouseInput mi)
		{
			var me = world.LocalPlayer;
			if (me == null)
				return null;

			return world.ScreenMap.ActorsAtMouse(mi)
				.Select(p => p.Actor)
				.Where(a => !a.IsDead && a.Owner != me && a.Owner.Playable && !a.Owner.NonCombatant
					&& me.RelationshipWith(a.Owner) == PlayerRelationship.Enemy && a.CanBeViewedByPlayer(me))
				.Select(a => a.Owner)
				.FirstOrDefault();
		}

		protected override IEnumerable<Order> OrderInner(World world, CPos cell, int2 worldPixel, MouseInput mi)
		{
			var target = OpponentUnder(world, mi);
			if (target == null)
				yield break;

			world.CancelInputMode();
			yield return new Order("ProposeTruce", world.LocalPlayer.PlayerActor, false) { ExtraData = (uint)world.Players.IndexOf(target) };
		}

		protected override string GetCursor(World world, CPos cell, int2 worldPixel, MouseInput mi)
		{
			return OpponentUnder(world, mi) != null ? "ability" : "generic-blocked";
		}

		protected override IEnumerable<IRenderable> Render(WorldRenderer wr, World world) { yield break; }
		protected override IEnumerable<IRenderable> RenderAboveShroud(WorldRenderer wr, World world) { yield break; }
		protected override IEnumerable<IRenderable> RenderAnnotations(WorldRenderer wr, World world) { yield break; }
	}
}
