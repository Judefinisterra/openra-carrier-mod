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
using OpenRA.Activities;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Mods.Common.Widgets;
using OpenRA.Primitives;
using OpenRA.Traits;
using OpenRA.Widgets;

namespace OpenRA.Mods.MyRA.Traits
{
	// ================================================================================================
	// SR-71: everywhere it flies stays revealed to its side for the rest of the game.
	// ================================================================================================
	[Desc("Permanently reveals the area under the aircraft's flight path to its owner (and allies).")]
	public class PermanentRevealTrailInfo : TraitInfo
	{
		public readonly WDist Radius = WDist.FromCells(7);

		[Desc("One reveal per grid square of this many cells.")]
		public readonly int GridSize = 5;

		public override object Create(ActorInitializer init) { return new PermanentRevealTrail(this); }
	}

	public class PermanentRevealTrail : ITick
	{
		static readonly Dictionary<Player, HashSet<int>> Revealed = [];
		readonly PermanentRevealTrailInfo info;
		int timer;

		public PermanentRevealTrail(PermanentRevealTrailInfo info) { this.info = info; }

		void ITick.Tick(Actor self)
		{
			if (!self.IsInWorld || --timer > 0)
				return;

			timer = 5;
			var map = self.World.Map;
			var cell = map.CellContaining(self.CenterPosition);
			if (!map.Contains(cell))
				return;

			var key = (cell.X / info.GridSize) * 10000 + cell.Y / info.GridSize;
			if (!Revealed.TryGetValue(self.Owner, out var done))
				Revealed[self.Owner] = done = [];

			if (!done.Add(key))
				return;

			var centre = map.CenterOfCell(cell);
			var cells = Shroud.ProjectedCellsInRange(map, centre, WDist.Zero, info.Radius).ToArray();
			var source = new object();
			foreach (var p in self.World.Players)
				if (p == self.Owner || (p.IsAlliedWith(self.Owner) && !p.NonCombatant))
					p.Shroud.AddSource(source, Shroud.SourceType.Visibility, cells);
		}
	}

	// ================================================================================================
	// Aerial tanker: friendly aircraft flying close by are rearmed in the air.
	// ================================================================================================
	[Desc("Rearms friendly airborne aircraft near this one, one round per pool every Interval ticks.")]
	public class AerialRefuelerInfo : TraitInfo
	{
		public readonly WDist Range = WDist.FromCells(5);
		public readonly int Interval = 25;

		public override object Create(ActorInitializer init) { return new AerialRefueler(this); }
	}

	public class AerialRefueler : ITick
	{
		readonly AerialRefuelerInfo info;
		int timer;

		public AerialRefueler(AerialRefuelerInfo info) { this.info = info; }

		void ITick.Tick(Actor self)
		{
			if (!self.IsInWorld || --timer > 0)
				return;

			timer = info.Interval;
			foreach (var a in self.World.FindActorsInCircle(self.CenterPosition, info.Range))
			{
				if (a == self || a.IsDead || !a.Owner.IsAlliedWith(self.Owner) || !a.Info.HasTraitInfo<AircraftInfo>())
					continue;

				foreach (var pool in a.TraitsImplementing<AmmoPool>())
					if (pool.CurrentAmmoCount < pool.Info.Ammo)
						pool.GiveAmmo(a, 1);
			}
		}
	}

	// ================================================================================================
	// AC-130: on an attack order it circles the target while its side guns fire.
	// ================================================================================================
	[Desc("Gunship: circles the attack target, firing (needs a turreted attack trait).")]
	public class GunshipOrbitInfo : TraitInfo, Requires<AircraftInfo>
	{
		public readonly WDist Radius = WDist.FromCells(5);

		public override object Create(ActorInitializer init) { return new GunshipOrbit(this); }
	}

	public class GunshipOrbit : ITick, IResolveOrder, INotifyCreated
	{
		readonly GunshipOrbitInfo info;
		Target target = Target.Invalid;
		int leg, timer;
		Activity legActivity;
		AttackFollow[] attacks = [];

		public GunshipOrbit(GunshipOrbitInfo info) { this.info = info; }

		void INotifyCreated.Created(Actor self) { attacks = self.TraitsImplementing<AttackFollow>().ToArray(); }

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString is "Attack" or "ForceAttack")
			{
				target = order.Target;
				legActivity = null;
				timer = 0;
			}
			else if (!order.Queued && order.OrderString is "Move" or "Stop" or "ReturnToBase" or "AttackMove" or "Guard")
				target = Target.Invalid;
		}

		void ITick.Tick(Actor self)
		{
			if (target.Type == TargetType.Invalid || !self.IsInWorld || --timer > 0)
				return;

			timer = 5;
			if (target.Type == TargetType.Actor && (target.Actor.IsDead || !target.Actor.IsInWorld))
			{
				target = Target.Invalid;
				return;
			}

			foreach (var a in attacks)
				a.SetRequestedTarget(target, true);

			var act = self.CurrentActivity;
			if (act == null || act != legActivity || act.State == ActivityState.Done)
			{
				if (act != null && act != legActivity)
					self.CancelActivity();

				leg = (leg + 1) % 8;
				var t = leg * Math.PI * 2 / 8;
				var c = target.CenterPosition;
				var p = new WPos(c.X + (int)(info.Radius.Length * Math.Cos(t)), c.Y + (int)(info.Radius.Length * Math.Sin(t)), 0);
				legActivity = new Fly(self, Target.FromPos(p), WDist.FromCells(1));
				self.QueueActivity(act != null, legActivity);
			}
		}
	}

	// ================================================================================================
	// C-130: lands wherever it is sent, so troops and vehicles can board or get off anywhere.
	// ================================================================================================
	[Desc("A move order makes the aircraft land at (or as close as possible to) the destination.")]
	public class LandOnMoveInfo : TraitInfo, Requires<AircraftInfo>
	{
		[Desc("Land within this distance of the spot if the spot itself is blocked.")]
		public readonly WDist LandRange = WDist.FromCells(5);

		public override object Create(ActorInitializer init) { return new LandOnMove(this); }
	}

	public class LandOnMove : IResolveOrder
	{
		readonly LandOnMoveInfo info;

		public LandOnMove(LandOnMoveInfo info) { this.info = info; }

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString != "Move" || order.Target.Type == TargetType.Invalid)
				return;

			var cell = self.World.Map.Clamp(self.World.Map.CellContaining(order.Target.CenterPosition));
			var queued = order.Queued;
			self.World.AddFrameEndTask(w =>
			{
				if (self.IsDead || !self.IsInWorld)
					return;

				// Replace the plain fly-to with a landing there.
				self.QueueActivity(queued, new Land(self, Target.FromCell(w, cell), info.LandRange));
			});
		}
	}

	// ================================================================================================
	// News helicopter: a neutral chopper that wanders the map; what it sees, everyone sees.
	// ================================================================================================
	[Desc("Aircraft that wanders to random spots on the map.")]
	public class WandersByAirInfo : TraitInfo, Requires<AircraftInfo>
	{
		public readonly int Interval = 400;

		public override object Create(ActorInitializer init) { return new WandersByAir(this); }
	}

	public class WandersByAir : ITick
	{
		readonly WandersByAirInfo info;
		int timer;

		public WandersByAir(WandersByAirInfo info) { this.info = info; }

		void ITick.Tick(Actor self)
		{
			if (!self.IsInWorld || --timer > 0)
				return;

			timer = info.Interval;
			var map = self.World.Map;
			var b = map.Bounds;
			var r = self.World.SharedRandom;
			var cell = new MPos(b.Left + r.Next(b.Width), b.Top + r.Next(b.Height)).ToCPos(map);
			self.QueueActivity(false, new Fly(self, Target.FromCell(self.World, cell)));
		}
	}

	[TraitLocation(SystemActors.World)]
	[Desc("Keeps a neutral actor (the News Helicopter) flying over the map; replaces it a while after it is lost.")]
	public class NeutralAirSpawnerInfo : TraitInfo
	{
		[ActorReference]
		public readonly string Actor = "newsheli";

		public readonly int FirstDelay = 750;
		public readonly int RespawnDelay = 4500;

		[Desc("One per this many map cells (at least one).")]
		public readonly int CellsPer = 30000;

		public override object Create(ActorInitializer init) { return new NeutralAirSpawner(this); }
	}

	public class NeutralAirSpawner : ITick
	{
		readonly NeutralAirSpawnerInfo info;
		readonly List<Actor> live = [];
		int timer;

		public NeutralAirSpawner(NeutralAirSpawnerInfo info)
		{
			this.info = info;
			timer = info.FirstDelay;
		}

		void ITick.Tick(Actor self)
		{
			if (--timer > 0)
				return;

			timer = info.RespawnDelay;
			var w = self.World;
			live.RemoveAll(a => a.IsDead || !a.IsInWorld);
			var b = w.Map.Bounds;
			var want = Math.Max(1, b.Width * b.Height / info.CellsPer);
			var neutral = NycHelpers.Neutral(w);
			for (var i = live.Count; i < want; i++)
			{
				var edge = new MPos(b.Left + w.SharedRandom.Next(b.Width), b.Top + 1).ToCPos(w.Map);
				w.AddFrameEndTask(ww => live.Add(ww.CreateActor(info.Actor, new TypeDictionary
				{
					new LocationInit(edge),
					new OwnerInit(neutral),
					new CenterPositionInit(ww.Map.CenterOfCell(edge) + new WVec(0, 0, 1536)),
					new FacingInit(new WAngle(512)),
				})));
			}
		}
	}

	// ================================================================================================
	// Plague: infantry packed into one spot for too long catch a disease that slowly kills them and
	// spreads to the troops around them. Spreading out (or a medic/hospital) cures it.
	// ================================================================================================
	[TraitLocation(SystemActors.World)]
	[Desc("Starts plague outbreaks among crowded infantry. Needs Plagueable on infantry.")]
	public class PlagueManagerInfo : TraitInfo, ILobbyOptions
	{
		public readonly int Interval = 50;

		[Desc("Infantry of one player in one area that counts as a crowd.")]
		public readonly int CrowdSize = 20;

		[Desc("How close together counts as one crowd.")]
		public readonly WDist CrowdRange = WDist.FromCells(3);

		[Desc("Ticks a crowd must stay together before an outbreak.")]
		public readonly int CrowdTicks = 1125;

		[Desc("Chance (percent) per check that an infected soldier infects each one next to it.")]
		public readonly int SpreadChance = 25;

		public readonly WDist SpreadRange = new(1280);

		[Desc("Ticks a sick soldier must stay away from other infantry to recover.")]
		public readonly int RecoverTicks = 1500;

		public readonly WDist LonelyRange = WDist.FromCells(2);

		[ActorReference]
		[Desc("Actors that cure the sick around them.")]
		public readonly HashSet<string> Healers = ["medi", "doctor", "fieldhosp", "hosp", "witchdoctor"];

		public readonly WDist HealRange = WDist.FromCells(4);

		IEnumerable<LobbyOption> ILobbyOptions.LobbyOptions(MapPreview map)
		{
			yield return new LobbyBooleanOption(map, "plague", "checkbox-plague.label", "checkbox-plague.description", true, 62, true, false);
		}

		public override object Create(ActorInitializer init) { return new PlagueManager(this); }
	}

	public class PlagueManager : ITick, INotifyCreated
	{
		readonly PlagueManagerInfo info;
		readonly HashSet<Player> warned = [];
		bool enabled;
		int timer;

		public PlagueManager(PlagueManagerInfo info) { this.info = info; }

		void INotifyCreated.Created(Actor self)
		{
			enabled = self.World.LobbyInfo.GlobalSettings.OptionOrDefault("plague", true);
		}

		void ITick.Tick(Actor self)
		{
			if (!enabled || --timer > 0)
				return;

			timer = info.Interval;
			var w = self.World;
			var troops = w.ActorsWithTrait<Plagueable>()
				.Where(p => p.Actor.IsInWorld && !p.Actor.IsDead && p.Actor.Owner.Playable).ToList();

			// Outbreaks in crowds: a soldier with CrowdSize or more of his own side's infantry within
			// CrowdRange for CrowdTicks may fall sick.
			var crowdRangeSq = info.CrowdRange.LengthSquared;
			foreach (var p in troops)
			{
				var a = p.Actor;
				var crowd = w.FindActorsInCircle(a.CenterPosition, info.CrowdRange)
					.Count(o => o.Owner == a.Owner && !o.IsDead && o.Info.HasTraitInfo<PlagueableInfo>());

				if (!p.Trait.Crowded(crowd >= info.CrowdSize, info.Interval, info.CrowdTicks) || p.Trait.Infected)
					continue;

				var owner = a.Owner;
				if (warned.Add(owner))
				{
					if (w.LocalPlayer == owner)
						TextNotificationsManager.AddSystemLine("Plague", "Plague outbreak in your camp! Spread out your infantry or bring a medic.");

					GameRecorder.Note(owner, "plague outbreak");
				}

				if (w.SharedRandom.Next(100) < 15)
					p.Trait.Infect(a);
			}

			foreach (var owner in warned.ToList())
				if (!troops.Any(p => p.Actor.Owner == owner && p.Trait.Infected))
					warned.Remove(owner);

			// Spread, recovery and cures.
			foreach (var p in troops.Where(p => p.Trait.Infected).ToList())
			{
				var a = p.Actor;
				var near = w.FindActorsInCircle(a.CenterPosition, info.LonelyRange)
					.Where(o => o != a && !o.IsDead && o.Info.HasTraitInfo<PlagueableInfo>()).ToList();

				foreach (var o in near)
					if ((o.CenterPosition - a.CenterPosition).HorizontalLengthSquared <= info.SpreadRange.LengthSquared
						&& w.SharedRandom.Next(100) < info.SpreadChance)
						o.Trait<Plagueable>().Infect(o);

				var healer = w.FindActorsInCircle(a.CenterPosition, info.HealRange)
					.Any(o => !o.IsDead && info.Healers.Contains(o.Info.Name) && o.Owner.IsAlliedWith(a.Owner));

				if (healer)
					p.Trait.Cure(a);
				else if (near.Count == 0)
					p.Trait.Alone(a, info.Interval, info.RecoverTicks);
				else
					p.Trait.Alone(a, -1, info.RecoverTicks);
			}
		}
	}

	[Desc("This unit can catch the plague (see PlagueManager).")]
	public class PlagueableInfo : TraitInfo
	{
		[GrantedConditionReference]
		public readonly string Condition = "plague";

		public override object Create(ActorInitializer init) { return new Plagueable(this); }
	}

	public class Plagueable
	{
		readonly PlagueableInfo info;
		int token = Actor.InvalidConditionToken;
		int alone, crowded;

		/// <summary>Track time spent in a crowd; true once it has lasted long enough.</summary>
		public bool Crowded(bool inCrowd, int ticks, int needed)
		{
			crowded = inCrowd ? crowded + ticks : Math.Max(0, crowded - ticks);
			return crowded >= needed;
		}

		public Plagueable(PlagueableInfo info) { this.info = info; }

		public bool Infected => token != Actor.InvalidConditionToken;

		public void Infect(Actor self)
		{
			if (!Infected && !self.IsDead)
			{
				token = self.GrantCondition(info.Condition);
				alone = 0;
			}
		}

		public void Cure(Actor self)
		{
			if (Infected)
				token = self.RevokeCondition(token);
		}

		public void Alone(Actor self, int ticks, int needed)
		{
			if (ticks < 0)
			{
				alone = 0;
				return;
			}

			alone += ticks;
			if (alone >= needed)
				Cure(self);
		}
	}

	// ================================================================================================
	// On-screen controls: recall buttons (air / ground / navy) and simple unit totals.
	// ================================================================================================
	[TraitLocation(SystemActors.World)]
	[Desc("Adds recall buttons and unit totals to the player's screen.")]
	public class RecallPanelInfo : TraitInfo
	{
		[Desc("Units never recalled (harvesters, MCVs...).")]
		public readonly HashSet<string> Ignore = ["harv", "mcv", "oiltruck", "oiltanker", "containership", "newsheli", "supplytruck"];

		public readonly HashSet<string> GroundHomes = ["fact"];
		public readonly HashSet<string> NavalHomes = ["syrd", "spen", "seaport", "tribaldock"];

		public override object Create(ActorInitializer init) { return new RecallPanel(this); }
	}

	public class RecallPanel : ITick
	{
		readonly RecallPanelInfo info;
		bool added;
		string totals = "";
		int timer;

		public RecallPanel(RecallPanelInfo info) { this.info = info; }

		static bool IsNaval(Actor a)
		{
			var m = a.Info.TraitInfoOrDefault<MobileInfo>();
			return m != null && (m.Locomotor.Contains("naval") || m.Locomotor == "lcraft" || m.Locomotor.Contains("sub"));
		}

		IEnumerable<Actor> Mine(World w) => w.Actors.Where(a => a.Owner == w.LocalPlayer && a.IsInWorld && !a.IsDead
			&& !info.Ignore.Contains(a.Info.Name) && a.Info.HasTraitInfo<SelectableInfo>());

		void ITick.Tick(Actor self)
		{
			var w = self.World;
			if (w.LocalPlayer == null || w.Type != WorldType.Regular)
				return;

			if (!added)
				AddWidgets(w);

			if (--timer > 0)
				return;

			timer = 25;
			int air = 0, ground = 0, navy = 0, inf = 0, bld = 0;
			foreach (var a in w.Actors.Where(a => a.Owner == w.LocalPlayer && a.IsInWorld && !a.IsDead && a.Info.HasTraitInfo<SelectableInfo>()))
			{
				if (a.Info.HasTraitInfo<BuildingInfo>()) bld++;
				else if (a.Info.HasTraitInfo<AircraftInfo>()) air++;
				else if (a.Info.HasTraitInfo<WithInfantryBodyInfo>()) inf++;
				else if (IsNaval(a)) navy++;
				else if (a.Info.HasTraitInfo<MobileInfo>()) ground++;
			}

			totals = $"Infantry {inf}   Vehicles {ground}   Air {air}   Navy {navy}   Buildings {bld}";
		}

		void AddWidgets(World w)
		{
			var parent = Ui.Root.GetOrNull("PLAYER_WIDGETS");
			if (parent == null)
				return;

			added = true;
			var modData = Game.ModData;
			var screenW = Game.Renderer.Resolution.Width;
			var x = (screenW - 3 * 116) / 2;
			foreach (var (label, act) in new (string, Action)[] { ("Recall Air", () => RecallAir(w)), ("Recall Ground", () => RecallGround(w)), ("Recall Navy", () => RecallNavy(w)) })
			{
				var b = new ButtonWidget(modData)
				{
					Bounds = new WidgetBounds(x, 10, 110, 24),
					GetText = () => label,
					OnClick = act,
					GetTooltipText = null,
				};

				parent.AddChild(b);
				x += 116;
			}

			parent.AddChild(new LabelWidget(modData)
			{
				Bounds = new WidgetBounds((screenW - 500) / 2, 36, 500, 20),
				Align = TextAlign.Center,
				GetText = () => totals,
				Contrast = true,
			});
		}

		CPos? Nearest(World w, Actor a, HashSet<string> types)
		{
			var home = w.Actors.Where(h => h.Owner == w.LocalPlayer && !h.IsDead && h.IsInWorld && types.Contains(h.Info.Name))
				.MinByOrDefault(h => (h.CenterPosition - a.CenterPosition).HorizontalLengthSquared);
			return home?.Location;
		}

		CPos Spread(World w, CPos c, int r) => c + new CVec(w.LocalRandom.Next(-r, r + 1), w.LocalRandom.Next(-r, r + 1));

		void RecallAir(World w)
		{
			foreach (var a in Mine(w).Where(a => a.Info.HasTraitInfo<AircraftInfo>()))
			{
				if (a.Info.HasTraitInfo<RearmableInfo>())
					w.IssueOrder(new Order("ReturnToBase", a, false));
				else
				{
					var home = Nearest(w, a, info.GroundHomes) ?? w.LocalPlayer.HomeLocation;
					w.IssueOrder(new Order("Move", a, Target.FromCell(w, Spread(w, home, 3)), false));
				}
			}
		}

		void RecallGround(World w)
		{
			foreach (var a in Mine(w).Where(a => a.Info.HasTraitInfo<MobileInfo>() && !IsNaval(a)))
			{
				var home = Nearest(w, a, info.GroundHomes) ?? w.LocalPlayer.HomeLocation;
				w.IssueOrder(new Order("Move", a, Target.FromCell(w, Spread(w, home, 6)), false));
			}
		}

		void RecallNavy(World w)
		{
			foreach (var a in Mine(w).Where(IsNaval))
			{
				var home = Nearest(w, a, info.NavalHomes);
				if (home != null)
					w.IssueOrder(new Order("Move", a, Target.FromCell(w, Spread(w, home.Value, 4)), false));
			}
		}
	}
}
