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
using System.Numerics;
using OpenRA.Graphics;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

// El Presidente game mode: every player starts with El Presidente and four bodyguards riding in an armoured
// limousine next to their MCV. If El Presidente dies, his owner loses. Whatever he is hiding inside flies
// his nation's flag. Bodyguards follow him around when they have nothing else to do.
namespace OpenRA.Mods.MyRA.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("El Presidente game mode (lobby checkbox).")]
	public class PresidenteManagerInfo : TraitInfo, ILobbyOptions
	{
		[ActorReference]
		public readonly string Limo = "limo";

		[ActorReference]
		public readonly string President = "presidente";

		[ActorReference]
		public readonly string Guard = "bodyguard";

		public readonly int Guards = 4;

		IEnumerable<LobbyOption> ILobbyOptions.LobbyOptions(MapPreview map)
		{
			yield return new LobbyBooleanOption(map, "presidente", "checkbox-presidente.label", "checkbox-presidente.description", true, 63, false, false);
		}

		public override object Create(ActorInitializer init) { return new PresidenteManager(this); }
	}

	public class PresidenteManager : ITick, INotifyCreated, IRenderAnnotations
	{
		readonly PresidenteManagerInfo info;
		readonly Dictionary<Player, int> objectives = [];
		public readonly Dictionary<Player, Actor> Presidents = [];
		public bool Enabled { get; private set; }
		bool spawned;

		public PresidenteManager(PresidenteManagerInfo info) { this.info = info; }

		void INotifyCreated.Created(Actor self)
		{
			Enabled = self.World.LobbyInfo.GlobalSettings.OptionOrDefault("presidente", false);
		}

		void ITick.Tick(Actor self)
		{
			if (!Enabled || spawned || self.World.WorldTick < 2)
				return;

			spawned = true;
			var w = self.World;
			foreach (var p in w.Players.Where(p => p.Playable && !p.Spectating && !p.NonCombatant))
			{
				var mcv = w.Actors.Where(a => a.Owner == p && !a.IsDead && a.IsInWorld && (a.Info.Name.StartsWith("mcv") || a.Info.Name == "fact"))
					.MinByOrDefault(a => (a.Location - p.HomeLocation).LengthSquared);
				var near = mcv?.Location ?? p.HomeLocation;
				var mobile = w.Map.Rules.Actors[info.Limo].TraitInfo<MobileInfo>();
				var cells = w.Map.FindTilesInAnnulus(near, 2, 8)
					.Where(c => mobile.CanEnterCell(w, null, c) && !w.ActorMap.GetActorsAt(c).Any()).Take(1).ToList();
				var cell = cells.Count > 0 ? cells[0] : near;

				var td = new TypeDictionary { new LocationInit(cell), new OwnerInit(p), new FactionInit(p.Faction.InternalName), new FacingInit(new WAngle(384)) };
				var limo = w.CreateActor(info.Limo, td);
				var cargo = limo.Trait<Cargo>();

				var pres = w.CreateActor(false, info.President, new TypeDictionary { new OwnerInit(p), new FactionInit(p.Faction.InternalName) });
				cargo.Load(limo, pres);
				Presidents[p] = pres;
				for (var i = 0; i < info.Guards; i++)
				{
					var g = w.CreateActor(false, info.Guard, new TypeDictionary { new OwnerInit(p), new FactionInit(p.Faction.InternalName) });
					cargo.Load(limo, g);
				}

				var mo = p.PlayerActor.TraitOrDefault<MissionObjectives>();
				if (mo != null)
					objectives[p] = mo.Add(p, "Keep El Presidente alive", "Primary", inhibitAnnouncement: true);
			}

			TextNotificationsManager.AddSystemLine("El Presidente",
				"Every leader is in their limousine next to their MCV. Lose El Presidente and you lose the game!");
		}

		public void Died(Actor pres)
		{
			var p = pres.Owner;
			if (!Presidents.TryGetValue(p, out var known) || known != pres)
				return;

			Presidents.Remove(p);
			TextNotificationsManager.AddSystemLine("El Presidente", $"El Presidente of {p.ResolvedPlayerName} has fallen!");
			var mo = p.PlayerActor.TraitOrDefault<MissionObjectives>();
			if (mo != null && objectives.TryGetValue(p, out var id))
				p.World.AddFrameEndTask(_ => mo.MarkFailed(p, id));
		}

		/// <summary>The actor El Presidente is riding in, if any.</summary>
		public static Actor CarrierOf(Actor pres)
		{
			if (pres == null || pres.IsDead || pres.IsInWorld)
				return null;
			var t = pres.TraitOrDefault<Passenger>()?.Transport;
			return t != null && !t.IsDead && t.IsInWorld ? t : null;
		}

		IEnumerable<IRenderable> IRenderAnnotations.RenderAnnotations(Actor self, WorldRenderer wr)
		{
			if (!Enabled)
				yield break;

			var viewer = wr.World.RenderPlayer;
			foreach (var kv in Presidents)
			{
				var carrier = CarrierOf(kv.Value);
				if (carrier == null || (viewer != null && !carrier.CanBeViewedByPlayer(viewer)))
					continue;

				var flag = ChromeProvider.GetImage("flags", kv.Key.Faction.InternalName);
				if (flag == null)
					continue;

				var bounds = carrier.TraitsImplementing<Interactable>().Select(b => b.DecorationBounds(carrier, wr)).FirstOrDefault(b => !b.IsEmpty);
				var top = bounds.IsEmpty ? wr.Viewport.WorldToViewPx(wr.ScreenPxPosition(carrier.CenterPosition)) - new int2(0, 24)
					: wr.Viewport.WorldToViewPx(new int2(bounds.Left + bounds.Width / 2, bounds.Top));
				var sway = (float)System.Math.Sin(wr.World.WorldTick * 0.2) * 1.5f;
				var pos = new Vector2(top.X + 1, top.Y - flag.Size.Y - 10 + sway);
				yield return new UISpriteRenderable(flag, carrier.CenterPosition, pos, 0, wr.Palette("chrome"));
			}
		}

		bool IRenderAnnotations.SpatiallyPartitionable => false;
	}

	[Desc("El Presidente himself: his owner loses if he dies.")]
	public class PresidenteInfo : TraitInfo<Presidente> { }

	public class Presidente : INotifyKilled
	{
		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			self.World.WorldActor.TraitOrDefault<PresidenteManager>()?.Died(self);
		}
	}

	[Desc("Follows and guards the owner's El Presidente whenever it has nothing else to do.")]
	public class BodyguardInfo : TraitInfo
	{
		public readonly int Interval = 20;
		public readonly WDist FollowDistance = WDist.FromCells(3);

		public override object Create(ActorInitializer init) { return new Bodyguard(this); }
	}

	public class Bodyguard : ITick
	{
		readonly BodyguardInfo info;
		int timer;

		public Bodyguard(BodyguardInfo info) { this.info = info; }

		void ITick.Tick(Actor self)
		{
			if (--timer > 0 || !self.IsInWorld || self.IsDead)
				return;
			timer = info.Interval;
			if (!self.IsIdle)
				return;

			var mgr = self.World.WorldActor.TraitOrDefault<PresidenteManager>();
			if (mgr == null || !mgr.Presidents.TryGetValue(self.Owner, out var pres) || pres.IsDead)
				return;

			var carrier = PresidenteManager.CarrierOf(pres);
			if (carrier != null)
			{
				var cargo = carrier.TraitOrDefault<Cargo>();
				var pi = self.Info.TraitInfoOrDefault<PassengerInfo>();
				if (cargo != null && pi != null && cargo.HasSpace(pi.Weight) && cargo.Info.Types.Contains(pi.CargoType))
				{
					foreach (var ro in self.TraitsImplementing<Passenger>())
						((IResolveOrder)ro).ResolveOrder(self, new Order("EnterTransport", self, Target.FromActor(carrier), false));
					return;
				}

				Follow(self, carrier);
				return;
			}

			if (pres.IsInWorld)
				Follow(self, pres);
		}

		void Follow(Actor self, Actor leader)
		{
			if ((leader.CenterPosition - self.CenterPosition).HorizontalLengthSquared <= (long)info.FollowDistance.Length * info.FollowDistance.Length)
				return;
			self.QueueActivity(self.Trait<IMove>().MoveTo(leader.Location, 2));
		}
	}

	[Desc("AI: keeps its El Presidente, his limousine and bodyguards out of attack groups and safe at home.")]
	public class PresidenteBotModuleInfo : ConditionalTraitInfo
	{
		public readonly int Interval = 100;
		public readonly WDist HomeRadius = WDist.FromCells(10);

		public override object Create(ActorInitializer init) { return new PresidenteBotModule(init.Self, this); }
	}

	public class PresidenteBotModule : ConditionalTrait<PresidenteBotModuleInfo>, IBotTick
	{
		readonly Player player;
		int ticks;

		public PresidenteBotModule(Actor self, PresidenteBotModuleInfo info)
			: base(info)
		{
			player = self.Owner;
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (IsTraitDisabled || ++ticks % Info.Interval != 0)
				return;

			var w = player.World;
			var mgr = w.WorldActor.TraitOrDefault<PresidenteManager>();
			if (mgr == null || !mgr.Enabled || !mgr.Presidents.TryGetValue(player, out var pres) || pres.IsDead)
				return;

			var vip = new HashSet<string> { "limo", "presidente", "bodyguard" };
			foreach (var sm in player.PlayerActor.TraitsImplementing<SquadManagerBotModule>())
				foreach (var s in sm.Squads)
					s.Units.RemoveWhere(a => vip.Contains(a.Info.Name));

			// Keep the limo (with him in it) parked at home.
			var home = w.Actors.Where(a => a.Owner == player && !a.IsDead && a.IsInWorld && a.Info.HasTraitInfo<BuildingInfo>())
				.Select(a => a.Location).DefaultIfEmpty(player.HomeLocation)
				.MinBy(c => (c - player.HomeLocation).LengthSquared);
			var carrier = PresidenteManager.CarrierOf(pres);
			if (carrier != null && carrier.Info.Name == "limo" && carrier.IsIdle
				&& (carrier.CenterPosition - w.Map.CenterOfCell(home)).HorizontalLengthSquared > (long)Info.HomeRadius.Length * Info.HomeRadius.Length)
				bot.QueueOrder(new Order("Move", carrier, Target.FromCell(w, home), false));

			// If he got out, put him back in his limo.
			if (carrier == null && pres.IsInWorld && pres.IsIdle)
			{
				var limo = w.Actors.FirstOrDefault(a => a.Owner == player && a.Info.Name == "limo" && !a.IsDead && a.IsInWorld);
				if (limo != null && limo.Trait<Cargo>().HasSpace(1))
					bot.QueueOrder(new Order("EnterTransport", pres, Target.FromActor(limo), false));
			}
		}
	}
}
