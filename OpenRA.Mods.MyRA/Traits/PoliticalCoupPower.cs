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
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// ================================================================================================
	// Political Coup: click near an enemy's base. A third of that player's armed units and half of their defences
	// (turrets, Tesla coils, SAM sites...) defect to you for five minutes. The units go after their old side's most
	// valuable buildings and units, picking new targets as each one falls; the defences fire on whatever comes near.
	// When the five minutes are up, everyone still standing goes back to their old side.
	// ================================================================================================
	[Desc("Support power: part of the target player's armed units and defences defect for a while and attack that player.")]
	public class PoliticalCoupPowerInfo : SupportPowerInfo
	{
		[Desc("Percentage of the victim's armed units that defect (rounded up).")]
		public readonly int DefectPercent = 33;

		[Desc("Percentage of the victim's armed buildings (defences) that defect (rounded up).")]
		public readonly int DefencePercent = 50;

		[Desc("Ticks before the coup collapses and every defector goes back.")]
		public readonly int Duration = 7500;

		[Desc("Ticks between new target orders for defectors that have run out of things to do.")]
		public readonly int Interval = 40;

		[Desc("Each defector picks the nearest of this many of the victim's most valuable targets.")]
		public readonly int TopTargets = 6;

		public override object Create(ActorInitializer init) { return new PoliticalCoupPower(init.Self, this); }
	}

	public class PoliticalCoupPower : SupportPower, ITick
	{
		readonly PoliticalCoupPowerInfo info;
		readonly List<(Actor Defector, Player Victim)> defectors = [];
		readonly List<(int Due, Actor Defector, Player Attacker, Player Victim)> returning = [];
		int wait;

		public PoliticalCoupPower(Actor self, PoliticalCoupPowerInfo info)
			: base(self, info)
		{
			this.info = info;
		}

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			// Needs somewhere to aim (an order sent without a target would crash the game).
			if (order.Target.Type == TargetType.Invalid)
				return;

			base.Activate(self, order, manager);
			PlayLaunchSounds();

			var w = self.World;
			var attacker = self.Owner;
			var pos = order.Target.CenterPosition;

			// Victim: the enemy whose building is closest to where we clicked.
			var nearest = w.ActorsHavingTrait<Building>()
				.Where(a => !a.IsDead && a.IsInWorld && !a.Owner.NonCombatant && a.Owner != attacker
					&& attacker.RelationshipWith(a.Owner) != PlayerRelationship.Ally && a.Owner.WinState == WinState.Undefined)
				.MinByOrDefault(a => (a.CenterPosition - pos).LengthSquared);
			if (nearest == null)
				return;

			var victim = nearest.Owner;
			var armed = w.Actors
				.Where(a => a.Owner == victim && !a.IsDead && a.IsInWorld && a.Info.HasTraitInfo<AttackBaseInfo>())
				.ToList();
			var army = Shuffled(w, armed.Where(a => a.Info.HasTraitInfo<IMoveInfo>()));
			var defences = Shuffled(w, armed.Where(a => a.Info.HasTraitInfo<BuildingInfo>()));

			var turned = army.Take((army.Count * info.DefectPercent + 99) / 100).ToList();
			var turnedDefences = defences.Take((defences.Count * info.DefencePercent + 99) / 100).ToList();
			var due = w.WorldTick + info.Duration;
			foreach (var a in turned)
				defectors.Add((a, victim));

			foreach (var a in turned.Concat(turnedDefences))
			{
				returning.Add((due, a, attacker, victim));
				w.AddFrameEndTask(ww =>
				{
					if (a.IsDead || !a.IsInWorld)
						return;

					a.ChangeOwnerSync(attacker);
					a.CancelActivity();
					if (ww.LocalPlayer == attacker || ww.LocalPlayer == victim)
						ww.Add(new FloatingText(a.CenterPosition, Color.OrangeRed, "COUP!", 50));
				});
			}

			wait = 1;
			w.AddFrameEndTask(ww =>
			{
				if (ww.LocalPlayer == attacker)
					TextNotificationsManager.AddSystemLine("Political Coup",
						$"Coup in {victim.ResolvedPlayerName}'s army: {turned.Count} of their {army.Count} units and {turnedDefences.Count} of their {defences.Count} defences have joined you for five minutes!");
				else if (ww.LocalPlayer == victim)
					TextNotificationsManager.AddSystemLine("Political Coup",
						$"COUP! {turned.Count} of your units and {turnedDefences.Count} defences have defected to {attacker.ResolvedPlayerName} for five minutes!");
				else if (ww.LocalPlayer != null)
					TextNotificationsManager.AddSystemLine("Political Coup", $"{attacker.ResolvedPlayerName} has staged a coup in {victim.ResolvedPlayerName}'s army!");
			});
		}

		static List<Actor> Shuffled(World w, IEnumerable<Actor> actors)
		{
			// Shuffle with the shared random so every client picks the same traitors.
			var list = actors.ToList();
			for (var i = list.Count - 1; i > 0; i--)
			{
				var j = w.SharedRandom.Next(i + 1);
				(list[i], list[j]) = (list[j], list[i]);
			}

			return list;
		}

		void ITick.Tick(Actor self)
		{
			// The coup collapses: everyone still alive goes back, if their old side is still in the game.
			var restored = 0;
			Player restoredTo = null;
			for (var i = returning.Count - 1; i >= 0; i--)
			{
				var (due, a, attacker, victim) = returning[i];
				if (self.World.WorldTick < due)
					continue;

				returning.RemoveAt(i);
				defectors.RemoveAll(d => d.Defector == a);
				if (a.IsDead || !a.IsInWorld || a.Owner != attacker || victim.WinState != WinState.Undefined)
					continue;

				restored++;
				restoredTo = victim;
				self.World.AddFrameEndTask(_ =>
				{
					if (a.IsDead || !a.IsInWorld)
						return;

					a.ChangeOwnerSync(victim);
					a.CancelActivity();
				});
			}

			if (restored > 0 && self.World.LocalPlayer != null)
			{
				var text = $"The coup in {restoredTo.ResolvedPlayerName}'s army has collapsed: {restored} defectors have gone back.";
				self.World.AddFrameEndTask(_ => TextNotificationsManager.AddSystemLine("Political Coup", text));
			}

			if (defectors.Count == 0 || --wait > 0)
				return;

			wait = info.Interval;
			defectors.RemoveAll(d => d.Defector.IsDead || !d.Defector.IsInWorld || d.Defector.Owner != self.Owner
				|| d.Victim.WinState != WinState.Undefined);

			foreach (var (a, victim) in defectors)
			{
				if (!a.IsIdle)
					continue;

				var attack = a.TraitOrDefault<AttackBase>();
				if (attack == null)
					continue;

				var prize = self.World.Actors
					.Where(t => t.Owner == victim && !t.IsDead && t.IsInWorld && t.IsTargetableBy(a)
						&& attack.HasAnyValidWeapons(Target.FromActor(t)))
					.Select(t => (Actor: t, Cost: t.Info.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0))
					.OrderByDescending(t => t.Cost)
					.Take(info.TopTargets)
					.MinByOrDefault(t => (t.Actor.CenterPosition - a.CenterPosition).LengthSquared);

				if (prize.Actor != null)
					attack.AttackTarget(Target.FromActor(prize.Actor), AttackSource.Default, false, true);
			}
		}
	}
}
