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
using OpenRA.Graphics;
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// ================================================================================================
	// Enriched uranium (EU). Once a player has Nuclear Tech, each Nuclear Lab enriches uranium on its own, slowly, in
	// plain sight: anyone who can see the lab sees it working and how much it holds. Nukes are built from it: 1 EU for
	// a test nuke, 3 tactical, 10 medium, 20 hydrogen. If the lab is lost, its uranium is left on the ground where any
	// side's unit can grab it; it goes into the next lab its owner has.
	// ================================================================================================
	[Desc("Enriches uranium while its owner has Nuclear Tech. Holds the stock; drops it as a cache if destroyed.")]
	public class UraniumEnricherInfo : PausableConditionalTraitInfo
	{
		[Desc("Ticks to enrich one unit.")]
		public readonly int TicksPerUnit = 1500;

		[Desc("Prerequisite needed before enrichment starts.")]
		public readonly string RequiresTech = "nuctech";

		[ActorReference]
		[Desc("Actor dropped holding the stock when the lab is destroyed or sold.")]
		public readonly string Cache = "uraniumcache";

		[Desc("Whole units of the owner's total stock at which conditions are granted (named ConditionPrefix + units).",
			"The nuke prerequisites hang off these.")]
		public readonly ImmutableArray<int> Thresholds = [1, 3, 10, 20];

		public readonly string ConditionPrefix = "eu-";

		[GrantedConditionReference]
		public IEnumerable<string> LinterConditions => Thresholds.Select(t => ConditionPrefix + t);

		public override object Create(ActorInitializer init) { return new UraniumEnricher(this); }
	}

	public class UraniumEnricher : PausableConditionalTrait<UraniumEnricherInfo>, ITick, INotifyKilled, INotifySold, IRenderAnnotations
	{
		readonly Dictionary<int, int> tokens = [];
		int partial;
		int wait;

		public UraniumEnricher(UraniumEnricherInfo info)
			: base(info) { }

		// Thousandths of a unit.
		public int Milli { get; private set; }
		public bool Enriching { get; private set; }

		public static int Total(Player p) =>
			p.World.ActorsWithTrait<UraniumEnricher>().Where(e => e.Actor.Owner == p && !e.Actor.IsDead).Sum(e => e.Trait.Milli);

		// Takes uranium from the player's labs, fullest first. False (and nothing taken) if there isn't enough.
		public static bool Consume(Player p, int milli)
		{
			var labs = p.World.ActorsWithTrait<UraniumEnricher>().Where(e => e.Actor.Owner == p && !e.Actor.IsDead)
				.OrderByDescending(e => e.Trait.Milli).ToList();
			if (labs.Sum(e => e.Trait.Milli) < milli)
				return false;

			foreach (var lab in labs)
			{
				var take = Math.Min(milli, lab.Trait.Milli);
				lab.Trait.Milli -= take;
				milli -= take;
				if (milli == 0)
					break;
			}

			return true;
		}

		public void Add(int milli) { Milli += milli; }

		public static string Format(int milli) => $"{milli / 1000}.{milli % 1000 / 100}";

		void ITick.Tick(Actor self)
		{
			Enriching = !IsTraitDisabled && !IsTraitPaused
				&& self.Owner.PlayerActor.Trait<TechTree>().HasPrerequisites([Info.RequiresTech]);
			if (Enriching)
			{
				partial += 1000;
				while (partial >= Info.TicksPerUnit)
				{
					partial -= Info.TicksPerUnit;
					Milli++;
				}
			}

			if (--wait > 0)
				return;

			wait = 25;

			// Uranium caches its owner has (dropped by a lost lab, or captured) come in here.
			foreach (var cache in self.World.ActorsWithTrait<UraniumCache>().Where(c => c.Actor.Owner == self.Owner && !c.Actor.IsDead).ToList())
			{
				Milli += cache.Trait.Milli;
				cache.Trait.Milli = 0;
				self.World.AddFrameEndTask(_ => cache.Actor.Dispose());
				if (self.World.LocalPlayer == self.Owner)
					TextNotificationsManager.AddSystemLine("Nuclear Lab", "Recovered uranium taken into the Nuclear Lab.");
			}

			// Conditions for the nuke prerequisites follow the owner's whole stock.
			var total = Total(self.Owner);
			foreach (var units in Info.Thresholds)
			{
				var have = tokens.TryGetValue(units, out var token);
				var want = total >= units * 1000;
				if (want && !have)
					tokens[units] = self.GrantCondition(Info.ConditionPrefix + units);
				else if (!want && have)
				{
					self.RevokeCondition(token);
					tokens.Remove(units);
				}
			}
		}

		void Drop(Actor self)
		{
			if (Milli <= 0)
				return;

			var amount = Milli;
			Milli = 0;
			var owner = self.Owner;
			var cell = self.Location;
			self.World.AddFrameEndTask(w =>
			{
				var cache = w.CreateActor(Info.Cache, [new LocationInit(cell), new OwnerInit(owner)]);
				cache.Trait<UraniumCache>().Milli = amount;
				TextNotificationsManager.AddSystemLine("Nuclear Lab", $"A Nuclear Lab is down: {Format(amount)} units of enriched uranium are lying in the rubble.");
			});
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e) => Drop(self);
		void INotifySold.Selling(Actor self) { }
		void INotifySold.Sold(Actor self) => Drop(self);

		// Anyone who can see the lab sees it at work and how much it holds.
		IEnumerable<IRenderable> IRenderAnnotations.RenderAnnotations(Actor self, WorldRenderer wr)
		{
			var viewer = wr.World.RenderPlayer;
			if (!self.IsInWorld || (viewer != null && !self.CanBeViewedByPlayer(viewer)))
				yield break;

			var font = Game.Renderer.Fonts["Bold"];
			var pos = self.CenterPosition + new WVec(0, -1536, 0);

			// Not enriching: its owner is told why.
			if (!Enriching && (viewer == null || viewer == self.Owner))
			{
				var why = !self.Owner.PlayerActor.Trait<TechTree>().HasPrerequisites([Info.RequiresTech])
					? "NEEDS NUCLEAR TECH (VEHICLE TAB)" : "ENRICHMENT PAUSED: LOW POWER";
				yield return new TextAnnotationRenderable(font, pos + new WVec(0, -512, 0), 0, Color.Orange, why);
			}

			if (!Enriching && Milli == 0)
				yield break;
			if (Enriching)
			{
				var pulse = (float)(0.5 + 0.5 * Math.Sin(self.World.WorldTick * 0.2));
				yield return new CircleAnnotationRenderable(self.CenterPosition, new WDist(1200 + (int)(300 * pulse)), 2,
					Color.FromArgb((int)(120 + 120 * pulse), 90, 255, 60));
			}

			yield return new TextAnnotationRenderable(font, pos, 0, Enriching ? Color.LimeGreen : Color.Yellow,
				(Enriching ? "ENRICHING URANIUM " : "URANIUM ") + Format(Milli) + " EU");
		}

		bool IRenderAnnotations.SpatiallyPartitionable => true;
	}

	[Desc("Enriched uranium on the ground. Any unit can pick it up for its side; its owner's Nuclear Lab takes it in.")]
	public class UraniumCacheInfo : TraitInfo
	{
		public readonly WDist PickupRange = new(1536);

		public override object Create(ActorInitializer init) { return new UraniumCache(this); }
	}

	public class UraniumCache : ITick, IRenderAnnotations
	{
		readonly UraniumCacheInfo info;
		int wait;

		public UraniumCache(UraniumCacheInfo info) { this.info = info; }

		public int Milli { get; set; }

		void ITick.Tick(Actor self)
		{
			if (--wait > 0 || !self.IsInWorld)
				return;

			wait = 10;
			var grabber = self.World.FindActorsInCircle(self.CenterPosition, info.PickupRange)
				.FirstOrDefault(a => a.Owner != self.Owner && !a.IsDead && !a.Owner.NonCombatant
					&& a.Info.HasTraitInfo<MobileInfo>());
			if (grabber == null)
				return;

			var by = grabber.Owner;
			self.ChangeOwnerSync(by);
			self.World.Add(new FloatingText(self.CenterPosition, by.Color, "URANIUM SEIZED", 50));
			if (self.World.LocalPlayer != null)
				TextNotificationsManager.AddSystemLine("Nuclear", $"{by.ResolvedPlayerName} has seized {UraniumEnricher.Format(Milli)} units of enriched uranium!");
		}

		IEnumerable<IRenderable> IRenderAnnotations.RenderAnnotations(Actor self, WorldRenderer wr)
		{
			var viewer = wr.World.RenderPlayer;
			if (!self.IsInWorld || (viewer != null && !self.CanBeViewedByPlayer(viewer)))
				yield break;

			yield return new TextAnnotationRenderable(Game.Renderer.Fonts["Bold"], self.CenterPosition + new WVec(0, -768, 0), 0,
				Color.Yellow, $"URANIUM {UraniumEnricher.Format(Milli)} EU");
		}

		bool IRenderAnnotations.SpatiallyPartitionable => true;
	}

	// ================================================================================================
	// Stealing nuclear tech: a spy who gets into a Nuclear Lab or Tech Center of a player who has Nuclear Tech comes
	// out carrying the secrets. Bring him home (near one of your buildings) and your side has the tech too.
	// ================================================================================================
	[Desc("A spy getting in here steals Nuclear Tech, if the owner has it.")]
	public class NuclearSecretsInfo : TraitInfo
	{
		public readonly string Tech = "nuctech";

		public override object Create(ActorInitializer init) { return new NuclearSecrets(this); }
	}

	public class NuclearSecrets : INotifyInfiltrated
	{
		readonly NuclearSecretsInfo info;

		public NuclearSecrets(NuclearSecretsInfo info) { this.info = info; }

		void INotifyInfiltrated.Infiltrated(Actor self, Actor infiltrator, BitSet<TargetableType> types)
		{
			var carrier = infiltrator.TraitOrDefault<SecretCarrier>();
			if (carrier == null || !self.Owner.PlayerActor.Trait<TechTree>().HasPrerequisites([info.Tech])
				|| infiltrator.Owner.PlayerActor.Trait<TechTree>().HasPrerequisites([info.Tech]))
				return;

			carrier.Carry(infiltrator);
		}
	}

	[Desc("Carries stolen Nuclear Tech home: reaching its own base gives its side the tech.")]
	public class SecretCarrierInfo : TraitInfo
	{
		[ActorReference]
		[Desc("Marker actor that gives the tech.")]
		public readonly string TechMarker = "nuctech";

		[Desc("Within this distance of one of its own buildings it is home.")]
		public readonly WDist HomeRange = WDist.FromCells(8);

		public override object Create(ActorInitializer init) { return new SecretCarrier(this); }
	}

	public class SecretCarrier : ITick, IRenderAnnotations
	{
		readonly SecretCarrierInfo info;
		bool carrying;
		int wait;

		public SecretCarrier(SecretCarrierInfo info) { this.info = info; }

		public void Carry(Actor self)
		{
			carrying = true;
			if (self.World.LocalPlayer == self.Owner)
				TextNotificationsManager.AddSystemLine("Spy", "Our spy has the nuclear secrets! Get him home.");
		}

		void ITick.Tick(Actor self)
		{
			if (!carrying || --wait > 0 || !self.IsInWorld)
				return;

			wait = 15;
			var range = info.HomeRange.LengthSquared;
			var home = self.World.ActorsHavingTrait<Building>().Any(b => b.Owner == self.Owner && !b.IsDead && b.IsInWorld
				&& (b.CenterPosition - self.CenterPosition).HorizontalLengthSquared <= range);
			if (!home)
				return;

			carrying = false;
			var owner = self.Owner;
			var marker = info.TechMarker;
			self.World.AddFrameEndTask(w =>
			{
				if (!w.Actors.Any(a => a.Owner == owner && a.Info.Name == marker && !a.IsDead))
					w.CreateActor(marker, [new OwnerInit(owner)]);

				if (w.LocalPlayer != null)
					TextNotificationsManager.AddSystemLine("Spy", $"{owner.ResolvedPlayerName}'s spy has brought stolen nuclear secrets home: they now have Nuclear Tech.");
			});
		}

		IEnumerable<IRenderable> IRenderAnnotations.RenderAnnotations(Actor self, WorldRenderer wr)
		{
			if (!carrying || !self.IsInWorld || wr.World.RenderPlayer != null && wr.World.RenderPlayer != self.Owner)
				yield break;

			yield return new TextAnnotationRenderable(Game.Renderer.Fonts["Bold"], self.CenterPosition + new WVec(0, -768, 0), 0,
				Color.Cyan, "SECRETS");
		}

		bool IRenderAnnotations.SpatiallyPartitionable => true;
	}
}
