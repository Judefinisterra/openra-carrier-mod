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
	// ================================================================================================
	// Units defend the base by themselves. Red Alert's default stance only fires at things already inside a unit's own
	// weapon range and never moves, so an enemy shelling the base from just outside it is ignored. Every so often this
	// looks for enemies near the player's buildings and units; idle armed units close by go and fight them, then walk
	// back to where they were once it's over. Units busy with an order, or set to Hold Fire, are left alone.
	// Anything of ours that gets hit also calls for help (CallForHelp): idle units near it go after whoever fired,
	// even artillery shelling from out of sight. Idle aircraft join in too.
	// Human players only (the AI has its own defence logic).
	// ================================================================================================
	[Desc("Idle armed units go after enemies that come near the base or near them, and return afterwards. Attach to the player actor.")]
	public class GuardBaseInfo : TraitInfo
	{
		[Desc("Ticks between looks around.")]
		public readonly int Interval = 15;

		[Desc("An enemy this close to one of our buildings is a threat.")]
		public readonly WDist BuildingThreatRange = WDist.FromCells(10);

		[Desc("An enemy this close to one of our units is a threat.")]
		public readonly WDist UnitThreatRange = WDist.FromCells(7);

		[Desc("Idle units this close to a threat respond.")]
		public readonly WDist ResponseRange = WDist.FromCells(14);

		[Desc("Idle units this close to something of ours that was hit go after the attacker.")]
		public readonly WDist HelpRange = WDist.FromCells(16);

		[Desc("Ticks an attacker stays wanted after its last hit.")]
		public readonly int RememberAttackerTicks = 250;

		[Desc("Responders walk back home when they are this far from where they started.")]
		public readonly WDist ReturnDistance = WDist.FromCells(3);

		public override object Create(ActorInitializer init) { return new GuardBase(init.Self, this); }
	}

	public class GuardBase : ITick
	{
		readonly GuardBaseInfo info;
		readonly Player player;
		readonly Dictionary<Actor, CPos> home = [];

		// Who has been shooting at us lately, where they hit and until when they stay wanted.
		readonly Dictionary<Actor, (WPos Victim, int Until)> attackers = [];
		int wait;

		public void Attacked(Actor victim, Actor attacker)
		{
			attackers[attacker] = (victim.CenterPosition, victim.World.WorldTick + info.RememberAttackerTicks);
		}

		public GuardBase(Actor self, GuardBaseInfo info)
		{
			this.info = info;
			player = self.Owner;
		}

		void ITick.Tick(Actor self)
		{
			if (--wait > 0 || !player.Playable || player.IsBot || player.WinState != WinState.Undefined)
				return;

			wait = info.Interval;
			var world = self.World;

			// Ours: buildings (things to protect) and idle armed units (possible responders).
			var buildings = new List<Actor>();
			var units = new List<Actor>();
			var responders = new List<(Actor Actor, AttackBase Attack)>();
			foreach (var a in world.Actors)
			{
				if (a.Owner != player || a.IsDead || !a.IsInWorld || a.OccupiesSpace == null)
					continue;

				if (a.Info.HasTraitInfo<BuildingInfo>())
				{
					buildings.Add(a);
					continue;
				}

				// Idle aircraft help with attacks on us, but don't count as things to protect.
				if (!a.Info.HasTraitInfo<MobileInfo>())
				{
					var air = a.TraitOrDefault<AttackBase>();
					if (air != null && a.IsIdle && a.Info.HasTraitInfo<AircraftInfo>() && a.TraitOrDefault<AutoTarget>()?.Stance != UnitStance.HoldFire)
						responders.Add((a, air));

					continue;
				}

				units.Add(a);
				var attack = a.TraitOrDefault<AttackBase>();
				var stance = a.TraitOrDefault<AutoTarget>()?.Stance;
				if (attack != null && a.IsIdle && stance != UnitStance.HoldFire)
					responders.Add((a, attack));
			}

			// Threats: visible enemy units and defences near any of those.
			var bt = info.BuildingThreatRange.LengthSquared;
			var ut = info.UnitThreatRange.LengthSquared;
			var threats = world.Actors
				.Where(e => !e.IsDead && e.IsInWorld && e.OccupiesSpace != null && player.RelationshipWith(e.Owner) == PlayerRelationship.Enemy
					&& !e.Owner.NonCombatant && e.Info.HasTraitInfo<IHealthInfo>()
					&& (e.Info.HasTraitInfo<IMoveInfo>() || e.Info.HasTraitInfo<AttackBaseInfo>())
					&& e.CanBeViewedByPlayer(player)
					&& (buildings.Any(b => (b.CenterPosition - e.CenterPosition).HorizontalLengthSquared <= bt)
						|| units.Any(u => (u.CenterPosition - e.CenterPosition).HorizontalLengthSquared <= ut)))
				.ToList();

			// Whoever hit us recently, seen or not, is wanted by the units near what they hit.
			var now = world.WorldTick;
			foreach (var e in attackers.Keys.ToList())
				if (e.IsDead || !e.IsInWorld || attackers[e].Until < now || player.RelationshipWith(e.Owner) != PlayerRelationship.Enemy)
					attackers.Remove(e);

			var rr = info.ResponseRange.LengthSquared;
			var hr = info.HelpRange.LengthSquared;
			foreach (var (a, attack) in responders)
			{
				var target = threats
					.Where(e => (e.CenterPosition - a.CenterPosition).HorizontalLengthSquared <= rr
						&& attack.HasAnyValidWeapons(Target.FromActor(e)))
					.MinByOrDefault(e => (e.CenterPosition - a.CenterPosition).HorizontalLengthSquared);

				target ??= attackers
					.Where(kv => (kv.Value.Victim - a.CenterPosition).HorizontalLengthSquared <= hr
						&& attack.HasAnyValidWeapons(Target.FromActor(kv.Key)))
					.Select(kv => kv.Key)
					.MinByOrDefault(e => (e.CenterPosition - a.CenterPosition).HorizontalLengthSquared);

				if (target != null)
				{
					if (!home.ContainsKey(a))
						home[a] = a.Location;

					attack.AttackTarget(Target.FromActor(target), AttackSource.AutoTarget, false, true);
					continue;
				}

				// Nothing left to fight: go back to where we were.
				if (home.Remove(a, out var origin) && a.TraitOrDefault<Mobile>() != null
					&& (a.CenterPosition - world.Map.CenterOfCell(origin)).HorizontalLengthSquared > info.ReturnDistance.LengthSquared)
				{
					var mobile = a.TraitOrDefault<Mobile>();
					if (mobile != null)
						a.QueueActivity(mobile.MoveTo(origin, 2));
				}
			}

			// Forget units that died or were given other orders.
			foreach (var a in home.Keys.Where(a => a.IsDead || !a.IsInWorld || a.Owner != player).ToList())
				home.Remove(a);
		}
	}

	[Desc("Tells the owner's GuardBase when this is hit, so idle units nearby go after the attacker.")]
	public class CallForHelpInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new CallForHelp(); }
	}

	public class CallForHelp : INotifyDamage
	{
		void INotifyDamage.Damaged(Actor self, AttackInfo e)
		{
			var attacker = e.Attacker;
			if (e.Damage.Value <= 0 || attacker == null || attacker.IsDead || !attacker.IsInWorld
				|| self.Owner.RelationshipWith(attacker.Owner) != PlayerRelationship.Enemy)
				return;

			self.Owner.PlayerActor.TraitOrDefault<GuardBase>()?.Attacked(self, attacker);
		}
	}
}
