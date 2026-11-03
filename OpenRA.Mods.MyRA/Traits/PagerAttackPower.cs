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
using OpenRA.GameRules;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// ================================================================================================
	// Pager Attack (Israel): no target needed. Every enemy infantryman's pager beeps, and over the next couple of
	// seconds each one blows up where he stands, damaging buildings and vehicles close by.
	// ================================================================================================
	[Desc("Instant support power: every enemy infantry unit on the map explodes.")]
	public class PagerAttackPowerInfo : SupportPowerInfo
	{
		[Desc("Target types that carry a pager.")]
		public readonly BitSet<TargetableType> Targets = new("Infantry");

		[Desc("Ticks after the beep before the first blast.")]
		public readonly int Delay = 25;

		[Desc("The blasts are spread over this many ticks.")]
		public readonly int Spread = 50;

		public readonly BitSet<DamageType> DamageTypes = new("ExplosionDeath");

		[WeaponReference]
		[Desc("The blast at each victim: its damage to whatever is nearby, explosion and sound.")]
		public readonly string Weapon = "PagerBlast";

		public override object Create(ActorInitializer init) { return new PagerAttackPower(init.Self, this); }
	}

	public class PagerAttackPower : SupportPower, ITick
	{
		readonly PagerAttackPowerInfo info;
		readonly List<(int Due, Actor Victim)> pending = [];

		public PagerAttackPower(Actor self, PagerAttackPowerInfo info)
			: base(self, info)
		{
			this.info = info;
		}

		// Nothing to aim: clicking the icon fires it.
		public override void SelectTarget(Actor self, string order, SupportPowerManager manager)
		{
			self.World.IssueOrder(new Order(order, manager.Self, false));
		}

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			base.Activate(self, order, manager);
			PlayLaunchSounds();

			var attacker = self.Owner;
			var now = self.World.WorldTick;
			var victims = self.World.Actors
				.Where(a => !a.IsDead && a.IsInWorld && attacker.RelationshipWith(a.Owner) == PlayerRelationship.Enemy
					&& a.GetEnabledTargetTypes().Overlaps(info.Targets))
				.ToList();

			foreach (var v in victims)
				pending.Add((now + info.Delay + self.World.SharedRandom.Next(info.Spread + 1), v));

			var w = self.World;
			var count = victims.Count;
			w.AddFrameEndTask(_ =>
			{
				if (w.LocalPlayer == attacker)
					TextNotificationsManager.AddSystemLine("Pager Attack", $"{count} enemy pagers are beeping...");
				else if (w.LocalPlayer != null && attacker.RelationshipWith(w.LocalPlayer) == PlayerRelationship.Enemy)
					TextNotificationsManager.AddSystemLine("Pager Attack", "Your soldiers' pagers are all beeping at once...");
			});
		}

		void ITick.Tick(Actor self)
		{
			var now = self.World.WorldTick;
			for (var i = pending.Count - 1; i >= 0; i--)
			{
				var (due, victim) = pending[i];
				if (now < due)
					continue;

				pending.RemoveAt(i);
				if (victim.IsDead || !victim.IsInWorld)
					continue;

				var pos = victim.CenterPosition;
				victim.Kill(self, info.DamageTypes);
				var weapon = self.World.Map.Rules.Weapons[info.Weapon.ToLowerInvariant()];
				var target = Target.FromPos(pos);
				weapon.Impact(target, new WarheadArgs { Weapon = weapon, Source = pos, SourceActor = self, WeaponTarget = target });
			}
		}
	}
}
