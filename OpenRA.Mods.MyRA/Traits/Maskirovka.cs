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

// ================================================================================================
// Maskirovka (Russia only): the counter to Israel's Sausage Fingers.
//   - The power only appears while an enemy has Sausage Fingers charged and waiting.
//   - Click it and every Russian unit vanishes from that enemy's view: not drawn, not on radar, can't be targeted.
//   - They stay hidden for as long as Sausage Fingers stays charged, and reappear the moment it is used
//     (or the Slap Shrine is lost). Buildings stay visible: you can hide an army, not a base.
// Lives on the Player actor, so it doesn't need any particular building.
// ================================================================================================
namespace OpenRA.Mods.MyRA.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("Support power (Russia): hide all units from enemies whose Sausage Fingers is charged, until they use it.")]
	public class MaskirovkaPowerInfo : SupportPowerInfo
	{
		[Desc("Only players of these factions get the power.")]
		public readonly HashSet<string> Factions = ["russia"];

		[GrantedConditionReference]
		[Desc("Granted to the player actor while the power can be used (an enemy has Sausage Fingers charged).")]
		public readonly string AvailableCondition = "maskirovka-available";

		public override object Create(ActorInitializer init) { return new MaskirovkaPower(init.Self, this); }
	}

	public class MaskirovkaPower : SupportPower, ITick
	{
		readonly MaskirovkaPowerInfo info;
		int token = Actor.InvalidConditionToken;

		public MaskirovkaPower(Actor self, MaskirovkaPowerInfo info)
			: base(self, info)
		{
			this.info = info;
		}

		/// <summary>True while this player's Sausage Fingers is charged and hasn't been fired.</summary>
		public static bool SlapCharged(Player p)
		{
			var spm = p.PlayerActor.TraitOrDefault<SupportPowerManager>();
			if (spm == null)
				return false;
			foreach (var spi in spm.Powers.Values)
				if (spi.Info is SausageFingersPowerInfo && !spi.Disabled && spi.Instances.Count > 0 && spi.RemainingTicks == 0)
					return true;
			return false;
		}

		/// <summary>Enemies with Sausage Fingers charged that we aren't already hidden from.</summary>
		IEnumerable<Player> Targets(Actor self, MaskirovkaManager mm) => self.World.Players.Where(p => p.Playable && !p.NonCombatant
			&& p.WinState == WinState.Undefined && !p.IsAlliedWith(self.Owner) && SlapCharged(p) && (mm == null || !mm.IsHidden(self.Owner, p)));

		void ITick.Tick(Actor self)
		{
			if (self.World.WorldTick % 5 != 0)
				return;

			var mm = self.World.WorldActor.TraitOrDefault<MaskirovkaManager>();
			var available = info.Factions.Contains(self.Owner.Faction.InternalName) && self.Owner.WinState == WinState.Undefined
				&& Targets(self, mm).Any();
			if (available && token == Actor.InvalidConditionToken)
				token = self.GrantCondition(info.AvailableCondition);
			else if (!available && token != Actor.InvalidConditionToken)
				token = self.RevokeCondition(token);
		}

		public override void SelectTarget(Actor self, string order, SupportPowerManager manager)
		{
			self.World.IssueOrder(new Order(order, manager.Self, false));
		}

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			var mm = self.World.WorldActor.TraitOrDefault<MaskirovkaManager>();
			if (mm == null)
				return;

			var victims = Targets(self, mm).ToList();
			if (victims.Count == 0)
				return;

			base.Activate(self, order, manager);
			PlayLaunchSounds();
			foreach (var v in victims)
				mm.Hide(self.Owner, v);
		}
	}

	[TraitLocation(SystemActors.World)]
	[Desc("Tracks which players' units are hidden from whom by Maskirovka.")]
	public class MaskirovkaManagerInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new MaskirovkaManager(); }
	}

	public class MaskirovkaManager : ITick
	{
		// hider -> players who can't see the hider's units
		readonly Dictionary<Player, HashSet<Player>> hidden = [];
		public int Count { get; private set; }

		public bool IsHidden(Player hider, Player viewer) => Count > 0 && viewer != null
			&& hidden.TryGetValue(hider, out var set) && set.Contains(viewer);

		public void Hide(Player hider, Player viewer)
		{
			if (!hidden.TryGetValue(hider, out var set))
				hidden[hider] = set = [];
			if (!set.Add(viewer))
				return;
			Count++;

			var lp = hider.World.LocalPlayer;
			if (lp == hider)
				TextNotificationsManager.AddSystemLine("Maskirovka",
					$"Our forces are hidden from {viewer.ResolvedPlayerName} until they use Sausage Fingers.");
			else if (lp == viewer)
				TextNotificationsManager.AddSystemLine("Maskirovka", $"Contact lost: {hider.ResolvedPlayerName}'s forces have vanished from our sensors!");
			GameRecorder.Note(hider, $"Maskirovka: units hidden from {viewer.ResolvedPlayerName}");
		}

		void ITick.Tick(Actor self)
		{
			if (Count == 0 || self.World.WorldTick % 5 != 0)
				return;

			List<(Player Hider, Player Viewer)> lift = null;
			foreach (var kv in hidden)
				foreach (var v in kv.Value)
					if (!MaskirovkaPower.SlapCharged(v) || v.WinState != WinState.Undefined)
						(lift ??= []).Add((kv.Key, v));

			if (lift == null)
				return;

			var lp = self.World.LocalPlayer;
			foreach (var (h, v) in lift)
			{
				hidden[h].Remove(v);
				Count--;
				if (lp == h || lp == v)
					TextNotificationsManager.AddSystemLine("Maskirovka",
						$"Sausage Fingers is spent: {h.ResolvedPlayerName}'s forces are visible to {v.ResolvedPlayerName} again.");
				GameRecorder.Note(h, $"Maskirovka lifted: units visible to {v.ResolvedPlayerName} again");
			}
		}
	}

	[Desc("This unit can be hidden by its owner's Maskirovka. Buildings and immobile actors are never hidden.")]
	public class MaskirovkaHiddenInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new MaskirovkaHidden(init.Self); }
	}

	public class MaskirovkaHidden : IVisibilityModifier
	{
		readonly bool isUnit;
		readonly MaskirovkaManager manager;

		public MaskirovkaHidden(Actor self)
		{
			isUnit = self.Info.HasTraitInfo<MobileInfo>() || self.Info.HasTraitInfo<AircraftInfo>();
			manager = self.World.WorldActor.TraitOrDefault<MaskirovkaManager>();
		}

		bool IVisibilityModifier.IsVisible(Actor self, Player byPlayer) => !isUnit || manager == null || !manager.IsHidden(self.Owner, byPlayer);
	}

	// Computer players (and SkyNet) use it as soon as it's offered.
	[TraitLocation(SystemActors.Player)]
	[Desc("The AI uses Maskirovka whenever it's available.")]
	public class MaskirovkaBotModuleInfo : ConditionalTraitInfo
	{
		public override object Create(ActorInitializer init) { return new MaskirovkaBotModule(init.Self, this); }
	}

	public class MaskirovkaBotModule : ConditionalTrait<MaskirovkaBotModuleInfo>, IBotTick
	{
		readonly Player player;

		public MaskirovkaBotModule(Actor self, MaskirovkaBotModuleInfo info)
			: base(info)
		{
			player = self.Owner;
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (IsTraitDisabled || player.World.WorldTick % 25 != 0)
				return;
			var spm = player.PlayerActor.TraitOrDefault<SupportPowerManager>();
			var spi = spm?.Powers.Values.FirstOrDefault(p => p.Info is MaskirovkaPowerInfo && p.Ready);
			if (spi != null)
				bot.QueueOrder(new Order(spi.Key, player.PlayerActor, false) { SuppressVisualFeedback = true });
		}
	}
}
