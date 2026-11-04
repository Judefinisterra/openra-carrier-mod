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
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// ================================================================================================
	// Coups (Spy Center). The Spy Center trains spies. Inside an enemy base they pass for that side's own soldiers,
	// so nobody shoots them unless told to. Once three or more of your spies have spent five minutes (in total)
	// inside the same enemy base (the clock is the Spy Center's Stage Coup power, which only charges while enough
	// spies are inside), the Spy Center can stage a coup there:
	//   - 20% of that player's armed units and defences (3 spies) up to 50% (10 spies) go rogue, with one goal:
	//     capture El Presidente (PJ's president; if the game mode is off he and his bodyguards are brought out for
	//     the coup). He can't be hurt while the coup is on. Rogue units close in on him; if he's in his limousine they
	//     shoot it up to force him out. One rogue reaching him takes him prisoner.
	//   - Then a usurper takes over. The old side has five minutes to kill the usurper. If they can't, they lose,
	//     and everything they have left joins the side that staged the coup. If they do, the coup is crushed: their
	//     president is freed and the rogue units come back.
	// ================================================================================================
	[Desc("Spy for coups: disguises itself as one of the enemy's soldiers when it gets near them.")]
	public class CoupSpyInfo : TraitInfo
	{
		[Desc("Looks for an enemy soldier to copy within this range.")]
		public readonly WDist DisguiseRange = WDist.FromCells(10);

		public override object Create(ActorInitializer init) { return new CoupSpy(this); }
	}

	public class CoupSpy : ITick
	{
		readonly CoupSpyInfo info;
		int wait;

		public CoupSpy(CoupSpyInfo info) { this.info = info; }

		void ITick.Tick(Actor self)
		{
			if (--wait > 0 || !self.IsInWorld || self.IsDead)
				return;

			wait = 25;
			var disguise = self.TraitOrDefault<IEffectiveOwner>();
			if (disguise == null || disguise.Disguised)
				return;

			// Copy the nearest enemy soldier, so we look like one of theirs.
			var range = info.DisguiseRange.LengthSquared;
			var model = self.World.Actors
				.Where(a => !a.IsDead && a.IsInWorld
					&& a.Info.HasTraitInfo<WithInfantryBodyInfo>() && a.Info.HasTraitInfo<AttackBaseInfo>()
					&& self.Owner.RelationshipWith(a.Owner) == PlayerRelationship.Enemy && !a.Owner.NonCombatant
					&& (a.CenterPosition - self.CenterPosition).HorizontalLengthSquared <= range)
				.MinByOrDefault(a => (a.CenterPosition - self.CenterPosition).HorizontalLengthSquared);

			if (model != null)
				self.ResolveOrder(new Order("Disguise", self, Target.FromActor(model), false));
		}
	}

	[Desc("Tracks coup plots and coups in progress. Attach to the world actor.")]
	public class CoupManagerInfo : TraitInfo
	{
		[Desc("Spies inside an enemy base needed for a plot.")]
		public readonly int MinSpies = 3;

		[Desc("Spies for the strongest coup.")]
		public readonly int MaxSpies = 10;

		[Desc("Percentage of armed units and defences that go rogue with MinSpies and MaxSpies.")]
		public readonly int MinPercent = 20;
		public readonly int MaxPercent = 50;

		[Desc("A spy within this distance of one of the enemy's buildings is inside their base.")]
		public readonly WDist BaseRange = WDist.FromCells(12);

		[Desc("Ticks the old side has to kill the usurper.")]
		public readonly int UsurperTicks = 7500;

		[ActorReference]
		public readonly string HeadOfState = "presidente";

		[Desc("A rogue unit this close to El Presidente takes him prisoner.")]
		public readonly WDist CaptureRange = new(1536);

		[Desc("External condition granted to El Presidente while a coup hunts him (makes him unhurtable).")]
		public readonly string ProtectCondition = "coup-target";

		[ActorReference]
		public readonly string Guard = "bodyguard";

		public readonly int Guards = 3;

		[ActorReference]
		[Desc("Invisible actor created when a player's first plot starts: gives the coup-plot prerequisite, which shows",
			"the Spy Center's coup power. That power's charge is the plot's clock.")]
		public readonly string ReadyMarker = "coupready";

		[Desc("External condition granted to the plotter's Spy Center while a plot has enough spies inside;",
			"the coup power only charges while it has it.")]
		public readonly string PlottingCondition = "coup-plotting";

		public override object Create(ActorInitializer init) { return new CoupManager(this); }
	}

	public class CoupManager : ITick, IRenderAnnotations
	{
		sealed class Plot
		{
			public Player Attacker;
			public Player Victim;
			public int Spies;
			public bool Active;
		}

		sealed class Coup
		{
			public Player Attacker;
			public Player Victim;
			public readonly List<Actor> Rogues = [];
			public bool Started;
			public Actor Head;
			public int HeadToken = Actor.InvalidConditionToken;
			public bool Captured;
			public Actor Usurper;
			public int Deadline;
		}

		readonly CoupManagerInfo info;
		readonly List<Plot> plots = [];
		readonly List<Coup> coups = [];
		readonly Dictionary<Actor, int> plotting = [];
		int wait;

		public CoupManager(CoupManagerInfo info) { this.info = info; }

		Plot ReadyPlot(Player attacker, Player victim) =>
			plots.FirstOrDefault(p => p.Attacker == attacker && p.Victim == victim && p.Spies >= info.MinSpies);

		void ITick.Tick(Actor self)
		{
			var w = self.World;
			TickCoups(w);

			if (--wait > 0)
				return;

			wait = 25;
			UpdatePlots(w);
		}

		// Count every player's spies inside each enemy base; plots advance while there are enough of them.
		void UpdatePlots(World w)
		{
			var spies = w.ActorsHavingTrait<CoupSpy>().Where(a => !a.IsDead && a.IsInWorld).ToList();
			var buildings = w.ActorsHavingTrait<Building>().Where(a => !a.IsDead && a.IsInWorld && !a.Owner.NonCombatant).ToList();
			var range = info.BaseRange.LengthSquared;
			var counts = new Dictionary<(Player, Player), int>();
			foreach (var spy in spies)
			{
				var home = buildings.Where(b => spy.Owner.RelationshipWith(b.Owner) == PlayerRelationship.Enemy
						&& (b.CenterPosition - spy.CenterPosition).HorizontalLengthSquared <= range)
					.MinByOrDefault(b => (b.CenterPosition - spy.CenterPosition).HorizontalLengthSquared);
				if (home == null)
					continue;

				var key = (spy.Owner, home.Owner);
				counts[key] = counts.GetValueOrDefault(key) + 1;
			}

			foreach (var ((attacker, victim), n) in counts)
			{
				var plot = plots.FirstOrDefault(p => p.Attacker == attacker && p.Victim == victim);
				if (plot == null)
					plots.Add(plot = new Plot { Attacker = attacker, Victim = victim });

				plot.Spies = n;
			}

			foreach (var plot in plots.ToList())
			{
				if (!counts.ContainsKey((plot.Attacker, plot.Victim)))
					plot.Spies = 0;

				if (plot.Victim.WinState != WinState.Undefined || plot.Attacker.WinState != WinState.Undefined)
				{
					plots.Remove(plot);
					continue;
				}

				// A plot starts (or resumes) with enough spies inside, and pauses without them.
				var active = plot.Spies >= info.MinSpies;
				if (active == plot.Active)
					continue;

				plot.Active = active;
				var (attacker, victimName, n) = (plot.Attacker, plot.Victim.ResolvedPlayerName, plot.Spies);
				var marker = info.ReadyMarker;
				w.AddFrameEndTask(ww =>
				{
					if (active && !ww.Actors.Any(a => a.Owner == attacker && a.Info.Name == marker && !a.IsDead))
						ww.CreateActor(marker, [new OwnerInit(attacker)]);

					if (ww.LocalPlayer == attacker)
						TextNotificationsManager.AddSystemLine("Spy Center", active
							? $"{n} spies are inside {victimName}'s base. The coup plot clock is running (the Spy Center's Stage Coup power)."
							: $"Too few spies left inside {victimName}'s base: the coup plot clock has stopped.");
				});
			}

			// The Spy Center's coup power charges only while one of its owner's plots is running.
			foreach (var spyCenter in w.ActorsHavingTrait<StageCoupPower>().Where(a => !a.IsDead && a.IsInWorld).ToList())
			{
				var on = plots.Any(p => p.Attacker == spyCenter.Owner && p.Active);
				var ec = spyCenter.TraitsImplementing<ExternalCondition>().FirstOrDefault(c => c.Info.Condition == info.PlottingCondition);
				if (ec == null)
					continue;

				var has = plotting.TryGetValue(spyCenter, out var token);
				if (on && !has)
					plotting[spyCenter] = ec.GrantCondition(spyCenter, this);
				else if (!on && has)
				{
					ec.TryRevokeCondition(spyCenter, this, token);
					plotting.Remove(spyCenter);
				}
			}

			foreach (var gone in plotting.Keys.Where(a => a.IsDead || !a.IsInWorld).ToList())
				plotting.Remove(gone);
		}

		// Stage the coup against the player whose base is nearest the click. Returns false if no plot is ready there.
		public bool Stage(World w, Player attacker, WPos near)
		{
			var victim = w.ActorsHavingTrait<Building>()
				.Where(a => !a.IsDead && a.IsInWorld && attacker.RelationshipWith(a.Owner) == PlayerRelationship.Enemy && ReadyPlot(attacker, a.Owner) != null)
				.MinByOrDefault(a => (a.CenterPosition - near).LengthSquared)?.Owner;
			if (victim == null)
			{
				if (w.LocalPlayer == attacker)
					w.AddFrameEndTask(_ => TextNotificationsManager.AddSystemLine("Spy Center", "No coup plot there: you need 3 or more spies inside that base."));

				return false;
			}

			var plot = ReadyPlot(attacker, victim);

			// How strong: MinPercent with MinSpies inside, up to MaxPercent with MaxSpies.
			var spies = Math.Clamp(plot.Spies, info.MinSpies, info.MaxSpies);
			var percent = info.MinPercent + (info.MaxPercent - info.MinPercent) * (spies - info.MinSpies) / Math.Max(1, info.MaxSpies - info.MinSpies);

			var armed = w.Actors
				.Where(a => a.Owner == victim && !a.IsDead && a.IsInWorld && a.OccupiesSpace != null
					&& a.Info.HasTraitInfo<AttackBaseInfo>() && !a.Info.HasTraitInfo<IcbmSiloInfo>() && !a.Info.HasTraitInfo<NukePowerInfo>())
				.ToList();
			for (var i = armed.Count - 1; i > 0; i--)
			{
				var j = w.SharedRandom.Next(i + 1);
				(armed[i], armed[j]) = (armed[j], armed[i]);
			}

			var coup = new Coup { Attacker = attacker, Victim = victim };
			coup.Rogues.AddRange(armed.Take((armed.Count * percent + 99) / 100));
			coups.Add(coup);

			// The head of state: El Presidente if the game has one, otherwise one appears in his palace.
			var manager = w.WorldActor.TraitOrDefault<PresidenteManager>();
			if (manager != null && manager.Presidents.TryGetValue(victim, out var pres) && !pres.IsDead)
				coup.Head = pres;

			var rogues = coup.Rogues.ToList();
			w.AddFrameEndTask(ww =>
			{
				foreach (var a in rogues)
				{
					if (a.IsDead || !a.IsInWorld)
						continue;

					a.ChangeOwnerSync(attacker);
					a.CancelActivity();
					ww.Add(new FloatingText(a.CenterPosition, Color.OrangeRed, "ROGUE!", 50));
				}

				coup.Head ??= SpawnLeader(ww, victim);
				Protect(coup, true);
				coup.Started = true;

				if (ww.LocalPlayer == attacker)
					TextNotificationsManager.AddSystemLine("Coup", $"Coup in {victim.ResolvedPlayerName}! {rogues.Count} units ({percent}%) have gone rogue. Kill their head of state!");
				else if (ww.LocalPlayer == victim)
					TextNotificationsManager.AddSystemLine("Coup", $"COUP! {rogues.Count} of your units have gone rogue and are hunting your head of state. Protect him!");
				else if (ww.LocalPlayer != null)
					TextNotificationsManager.AddSystemLine("Coup", $"A coup has broken out in {victim.ResolvedPlayerName}'s army!");
			});

			return true;
		}

		// A leader (and his bodyguards) appears by the player's most valuable building.
		Actor SpawnLeader(World w, Player owner)
		{
			var seat = w.ActorsHavingTrait<Building>().Where(a => a.Owner == owner && !a.IsDead && a.IsInWorld)
				.MaxByOrDefault(a => a.Info.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0);
			var near = seat?.Location ?? owner.HomeLocation;
			var map = w.Map;
			var mobile = map.Rules.Actors[info.HeadOfState].TraitInfoOrDefault<MobileInfo>();
			var cells = map.FindTilesInAnnulus(near, 2, 10)
				.Where(c => map.Contains(c) && (mobile == null || mobile.CanEnterCell(w, null, c)) && w.ActorMap.FreeSubCell(c) != SubCell.Invalid)
				.Take(info.Guards + 1).ToList();
			if (cells.Count == 0)
				return null;

			var head = w.CreateActor(info.HeadOfState, [new LocationInit(cells[0]), new OwnerInit(owner), new FactionInit(owner.Faction.InternalName)]);
			foreach (var c in cells.Skip(1))
				w.CreateActor(info.Guard, [new LocationInit(c), new OwnerInit(owner), new FactionInit(owner.Faction.InternalName)]);

			return head;
		}

		void TickCoups(World w)
		{
			foreach (var coup in coups.ToList())
			{
				// The rogues switch sides at the end of the tick the coup is staged; wait for that.
				if (!coup.Started)
					continue;

				coup.Rogues.RemoveAll(a => a.IsDead || !a.IsInWorld || a.Owner != coup.Attacker);

				if (coup.Usurper == null)
				{
					// Phase one: the rogues hunt El Presidente, to capture him.
					if (coup.Head == null)
					{
						if (w.WorldTick % 25 == 0)
						{
							coup.Head = SpawnLeader(w, coup.Victim);
							Protect(coup, true);
						}

						continue;
					}

					// Taken prisoner (or, if something killed him anyway, gone): the usurper takes over.
					var head = coup.Head;
					// Rogue ground units and the plotters' own spies in the base can take him.
					var spies = w.ActorsHavingTrait<CoupSpy>().Where(a => a.Owner == coup.Attacker && !a.IsDead && a.IsInWorld
						&& (a.CenterPosition - head.CenterPosition).HorizontalLengthSquared <= info.BaseRange.LengthSquared * 4).ToList();
					var catchers = coup.Rogues.Where(a => a.Info.HasTraitInfo<MobileInfo>()).Concat(spies).ToList();
					var caught = head.IsDead || (head.IsInWorld && catchers.Any(a =>
						(a.CenterPosition - head.CenterPosition).HorizontalLengthSquared <= info.CaptureRange.LengthSquared));
					if (caught)
					{
						var at = head.IsInWorld || head.IsDead ? head.Location : coup.Victim.HomeLocation;
						coup.Deadline = w.WorldTick + info.UsurperTicks;
						coup.Captured = !head.IsDead;
						var c = coup;
						w.AddFrameEndTask(ww =>
						{
							if (c.Captured && !head.IsDead)
							{
								head.ChangeOwnerSync(c.Attacker);
								head.CancelActivity();
								ww.Add(new FloatingText(head.CenterPosition, Color.OrangeRed, "CAPTURED!", 60));
							}

							c.Usurper = SpawnUsurper(ww, c.Attacker, at);
							var mins = info.UsurperTicks / 1500;
							if (ww.LocalPlayer == c.Victim)
								TextNotificationsManager.AddSystemLine("Coup", $"El Presidente has been captured and a usurper has seized power! Kill the usurper within {mins} minutes or lose everything!");
							else if (ww.LocalPlayer == c.Attacker)
								TextNotificationsManager.AddSystemLine("Coup", $"{c.Victim.ResolvedPlayerName}'s El Presidente is our prisoner. Keep the new president alive for {mins} minutes and their whole army is yours!");
							else if (ww.LocalPlayer != null)
								TextNotificationsManager.AddSystemLine("Coup", $"{c.Victim.ResolvedPlayerName}'s El Presidente has been captured in a coup!");
						});

						continue;
					}

					if (w.WorldTick % 40 == 0)
					{
						// Close in on him; if he's riding in his limousine, shoot it up to get him out.
						var carrier = PresidenteManager.CarrierOf(head);
						foreach (var a in coup.Rogues.Concat(spies).Where(a => a.IsIdle || w.WorldTick % 200 == 0))
						{
							if (carrier != null && a.Info.HasTraitInfo<AttackBaseInfo>())
								a.TraitOrDefault<AttackBase>()?.AttackTarget(Target.FromActor(carrier), AttackSource.Default, false, true);
							else if (head.IsInWorld && a.TraitOrDefault<IMove>() is IMove move)
							{
								a.CancelActivity();
								a.QueueActivity(move.MoveWithinRange(Target.FromActor(head), new WDist(1024)));
							}
						}
					}

					continue;
				}

				// Phase two: the old side must kill the usurper before the deadline.
				if (coup.Usurper.IsDead)
				{
					coups.Remove(coup);
					var rogues = coup.Rogues.ToList();
					w.AddFrameEndTask(ww =>
					{
						foreach (var a in rogues.Where(a => !a.IsDead && a.IsInWorld && a.Owner == coup.Attacker))
						{
							a.ChangeOwnerSync(coup.Victim);
							a.CancelActivity();
						}

						if (coup.Head != null && !coup.Head.IsDead && coup.Head.Owner == coup.Attacker)
							coup.Head.ChangeOwnerSync(coup.Victim);

						Protect(coup, false);

						if (ww.LocalPlayer != null)
							TextNotificationsManager.AddSystemLine("Coup", $"The usurper is dead: the coup in {coup.Victim.ResolvedPlayerName}'s army has been crushed.");
					});
					continue;
				}

				if (w.WorldTick >= coup.Deadline)
				{
					// Too late: everything the old side has left goes over to the new regime.
					coups.Remove(coup);
					var all = w.Actors.Where(a => a.Owner == coup.Victim && !a.IsDead && a.IsInWorld && a.OccupiesSpace != null).ToList();
					w.AddFrameEndTask(ww =>
					{
						foreach (var a in all.Where(a => !a.IsDead && a.IsInWorld))
							a.ChangeOwnerSync(coup.Attacker);

						if (ww.LocalPlayer != null)
							TextNotificationsManager.AddSystemLine("Coup", $"The coup succeeded: {coup.Victim.ResolvedPlayerName}'s whole army now serves {coup.Attacker.ResolvedPlayerName}.");
					});
				}
			}
		}

		// A countdown over the usurper's head, for everyone who can see him.
		IEnumerable<IRenderable> IRenderAnnotations.RenderAnnotations(Actor self, WorldRenderer wr)
		{
			var font = Game.Renderer.Fonts["Bold"];
			foreach (var c in coups)
			{
				var u = c.Usurper;
				if (u == null || u.IsDead || !u.IsInWorld || (wr.World.RenderPlayer != null && !u.CanBeViewedByPlayer(wr.World.RenderPlayer)))
					continue;

				var left = Math.Max(0, c.Deadline - wr.World.WorldTick) / 25;
				yield return new TextAnnotationRenderable(font, u.CenterPosition + new WVec(0, -1536, 0), 0, Color.OrangeRed,
					$"USURPER {left / 60}:{left % 60:00}");
			}
		}

		bool IRenderAnnotations.SpatiallyPartitionable => false;

		// While the coup hunts him, El Presidente can't be hurt (PRESIDENTE's ExternalCondition@coup).
		void Protect(Coup coup, bool on)
		{
			var head = coup.Head;
			if (head == null || head.IsDead)
				return;

			var ec = head.TraitsImplementing<ExternalCondition>().FirstOrDefault(c => c.Info.Condition == info.ProtectCondition);
			if (ec == null)
				return;

			if (on && coup.HeadToken == Actor.InvalidConditionToken)
				coup.HeadToken = ec.GrantCondition(head, this);
			else if (!on && coup.HeadToken != Actor.InvalidConditionToken)
			{
				ec.TryRevokeCondition(head, this, coup.HeadToken);
				coup.HeadToken = Actor.InvalidConditionToken;
			}
		}

		Actor SpawnUsurper(World w, Player owner, CPos at)
		{
			var map = w.Map;
			var cells = map.FindTilesInAnnulus(at, 0, 8)
				.Where(c => map.Contains(c) && w.ActorMap.FreeSubCell(c) != SubCell.Invalid && map.GetTerrainInfo(c).Type is "Clear" or "Road" or "Rough")
				.Take(info.Guards + 1).ToList();
			if (cells.Count == 0)
				return null;

			var head = w.CreateActor(info.HeadOfState, [new LocationInit(cells[0]), new OwnerInit(owner), new FactionInit(owner.Faction.InternalName)]);
			foreach (var c in cells.Skip(1))
				w.CreateActor(info.Guard, [new LocationInit(c), new OwnerInit(owner), new FactionInit(owner.Faction.InternalName)]);

			return head;
		}
	}

	[Desc("Spy Center support power: stages a coup where a plot is ready (see CoupManager).")]
	public class StageCoupPowerInfo : SupportPowerInfo
	{
		public override object Create(ActorInitializer init) { return new StageCoupPower(init.Self, this); }
	}

	public class StageCoupPower : SupportPower
	{
		public StageCoupPower(Actor self, StageCoupPowerInfo info)
			: base(self, info) { }

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			// Needs somewhere to aim (an order sent without a target would crash the game).
			if (order.Target.Type == TargetType.Invalid)
				return;

			var coups = self.World.WorldActor.TraitOrDefault<CoupManager>();
			if (coups == null || !coups.Stage(self.World, self.Owner, order.Target.CenterPosition))
				return;

			base.Activate(self, order, manager);
			PlayLaunchSounds();
		}
	}
}
