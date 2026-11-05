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
using System.Numerics;
using OpenRA.Graphics;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.BotModules.Squads;
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
		internal readonly PresidenteManagerInfo info;
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
			// He always belongs to the side he started with, even if something (a coup, a capture) changed his owner.
			var p = Presidents.FirstOrDefault(kv => kv.Value == pres).Key;
			if (p == null)
				return;

			Presidents.Remove(p);
			TextNotificationsManager.AddSystemLine("El Presidente", $"El Presidente of {p.ResolvedPlayerName} has fallen!");
			var mo = p.PlayerActor.TraitOrDefault<MissionObjectives>();
			if (mo != null && objectives.TryGetValue(p, out var id))
				p.World.AddFrameEndTask(_ => mo.MarkFailed(p, id));
		}

		/// <summary>El Presidente, a bodyguard, his limo, or anything carrying one of them: never defects or changes side.</summary>
		public static bool IsProtected(Actor a)
		{
			if (a == null)
				return false;
			if (a.Info.HasTraitInfo<PresidenteInfo>() || a.Info.HasTraitInfo<BodyguardInfo>())
				return true;
			var mgr = a.World.WorldActor.TraitOrDefault<PresidenteManager>();
			if (mgr != null && mgr.Enabled && a.Info.Name == mgr.info.Limo)
				return true;
			var cargo = a.TraitOrDefault<Cargo>();
			return cargo != null && cargo.Passengers.Any(IsProtected);
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

	[Desc("AI in El Presidente games: keep our president safe at home, retreat him when enemies come near, ",
		"rally the army to defend him, and send attack groups after enemy presidents.")]
	public class PresidenteBotModuleInfo : ConditionalTraitInfo
	{
		public readonly int Interval = 25;

		[Desc("Enemy fighters this close to our president count as a threat.")]
		public readonly WDist ThreatRadius = WDist.FromCells(10);

		[Desc("Our units this close to the president are pulled in to defend him.")]
		public readonly WDist DefendRadius = WDist.FromCells(25);

		[Desc("How far from the safe spot the limo may sit before it's sent back.")]
		public readonly WDist HomeRadius = WDist.FromCells(4);

		[Desc("Ticks between re-aiming attack groups at an enemy president.")]
		public readonly int HuntInterval = 250;

		public override object Create(ActorInitializer init) { return new PresidenteBotModule(init.Self, this); }
	}

	public class PresidenteBotModule : ConditionalTrait<PresidenteBotModuleInfo>, IBotTick
	{
		static readonly HashSet<string> Vip = ["limo", "presidente", "bodyguard"];
		static readonly HashSet<string> NotFighters = ["harv", "mcv", "mcv.tribal", "e6", "spy", "medi", "mech", "truk", "oiltruck", "supplytruck", "oilhauler", "dog"];

		readonly Player player;
		int ticks, lastHp = -1, lastAlarm = -10000, lastHunt = -10000, lastThreat = -100000;
		Actor lastHolder;

		public PresidenteBotModule(Actor self, PresidenteBotModuleInfo info)
			: base(info)
		{
			player = self.Owner;
		}

		static bool Fighter(Actor a) => a.Info.HasTraitInfo<AttackBaseInfo>() && a.Info.HasTraitInfo<IMoveInfo>();

		void IBotTick.BotTick(IBot bot)
		{
			if (IsTraitDisabled || ++ticks % Info.Interval != 0)
				return;

			var w = player.World;
			var mgr = w.WorldActor.TraitOrDefault<PresidenteManager>();
			if (mgr == null || !mgr.Enabled || !mgr.Presidents.TryGetValue(player, out var pres) || pres.IsDead)
				return;

			// He and his people never join attack groups.
			foreach (var sm in player.PlayerActor.TraitsImplementing<SquadManagerBotModule>())
				foreach (var s in sm.Squads)
					s.Units.RemoveWhere(a => Vip.Contains(a.Info.Name));

			var carrier = PresidenteManager.CarrierOf(pres);
			var vip = carrier ?? (pres.IsInWorld ? pres : null);
			if (vip == null)
				return;

			var enemies = w.Players.Where(p => p.Playable && !p.NonCombatant && p.RelationshipWith(player) == PlayerRelationship.Enemy).ToList();
			var safe = SafeSpot(w, enemies);

			// Threats: enemy fighters we can see near him, or him getting hurt.
			// (Only compare against the same vehicle/building: getting out of a big one isn't "getting hurt".)
			var holder = carrier ?? pres;
			var hp = holder.TraitOrDefault<IHealth>()?.HP ?? 0;
			var hurt = lastHp >= 0 && holder == lastHolder && hp < lastHp;
			lastHp = hp;
			lastHolder = holder;
			var threats = w.FindActorsInCircle(vip.CenterPosition, Info.ThreatRadius)
				.Where(a => !a.IsDead && a.IsInWorld && enemies.Contains(a.Owner) && Fighter(a) && a.CanBeViewedByPlayer(player))
				.ToList();

			// The VIP Bunker: under attack, he shelters there (production freezes); once it's quiet for a minute he comes out.
			var bunker = w.Actors.FirstOrDefault(a => a.Owner == player && !a.IsDead && a.IsInWorld && a.Info.HasTraitInfo<VipBunkerInfo>());
			var inBunker = carrier != null && carrier.Info.HasTraitInfo<VipBunkerInfo>();
			if (threats.Count > 0 || hurt)
				lastThreat = w.WorldTick;
			if (inBunker)
			{
				if (w.WorldTick - lastThreat > 1500)
					bot.QueueOrder(new Order("Unload", carrier, false));
				return;
			}

			if ((threats.Count > 0 || hurt) && bunker != null)
			{
				if (carrier != null && carrier.Info.HasTraitInfo<IMoveInfo>())
				{
					// Drive to the bunker, then get out next to it.
					if ((carrier.Location - bunker.Location).LengthSquared <= 9)
						bot.QueueOrder(new Order("Unload", carrier, false));
					else
						bot.QueueOrder(new Order("Move", carrier, Target.FromActor(bunker), false));
				}
				else if (carrier == null)
				{
					bot.QueueOrder(new Order("EnterTransport", pres, Target.FromActor(bunker), false));
					foreach (var g in w.Actors.Where(a => a.Owner == player && a.IsInWorld && !a.IsDead && a.Info.HasTraitInfo<BodyguardInfo>()
						&& (a.Location - pres.Location).LengthSquared <= 15 * 15))
						bot.QueueOrder(new Order("EnterTransport", g, Target.FromActor(bunker), true));
				}

				return;
			}

			if (threats.Count > 0 || hurt)
			{
				var center = threats.Count > 0
					? new WPos((int)threats.Average(a => (long)a.CenterPosition.X), (int)threats.Average(a => (long)a.CenterPosition.Y), 0)
					: vip.CenterPosition;

				// Run away from the fight: to whichever of our buildings is farthest from it.
				var retreat = w.Actors.Where(a => a.Owner == player && !a.IsDead && a.IsInWorld && a.Info.HasTraitInfo<BuildingInfo>())
					.Select(a => a.Location)
					.DefaultIfEmpty(safe)
					.MaxBy(c => (w.Map.CenterOfCell(c) - center).HorizontalLengthSquared);
				if ((w.Map.CenterOfCell(retreat) - center).HorizontalLengthSquared < (vip.CenterPosition - center).HorizontalLengthSquared)
					retreat = safe;
				MoveVip(bot, w, pres, carrier, retreat);

				// Everyone nearby comes to his defence.
				if (threats.Count > 0 && w.WorldTick - lastAlarm > 100)
				{
					lastAlarm = w.WorldTick;
					var target = w.Map.CellContaining(center);
					foreach (var a in w.FindActorsInCircle(vip.CenterPosition, Info.DefendRadius))
						if (a.Owner == player && !a.IsDead && a.IsInWorld && Fighter(a) && !Vip.Contains(a.Info.Name) && !NotFighters.Contains(a.Info.Name)
							&& !a.Info.HasTraitInfo<BuildingInfo>())
							bot.QueueOrder(new Order("AttackMove", a, Target.FromCell(w, target), false));
				}
			}
			else if ((vip.CenterPosition - w.Map.CenterOfCell(safe)).HorizontalLengthSquared > (long)Info.HomeRadius.Length * Info.HomeRadius.Length
				&& (carrier == null || carrier.IsIdle) && (carrier != null || pres.IsIdle))
				MoveVip(bot, w, pres, carrier, safe);

			// Before anything goes out to attack, the General makes sure he isn't left undefended.
			var general = AIGeneral.For(player);
			var guarded = general == null || general.AllowNewAttacks;

			// Our goal: kill the other presidents. Point the attack groups at the nearest one we know about,
			// but only while home is properly guarded.
			if (guarded && w.WorldTick - lastHunt >= Info.HuntInterval)
			{
				lastHunt = w.WorldTick;
				// Only presidents (or the vehicle/building hiding one) we can see right now - no peeking through fog.
				var prey = mgr.Presidents.Where(kv => enemies.Contains(kv.Key) && !kv.Value.IsDead)
					.Select(kv => PresidenteManager.CarrierOf(kv.Value) ?? (kv.Value.IsInWorld ? kv.Value : null))
					.Where(a => a != null && a.CanBeViewedByPlayer(player))
					.MinByOrDefault(a => (a.Location - player.HomeLocation).LengthSquared);
				if (prey != null)
					foreach (var sm in player.PlayerActor.TraitsImplementing<SquadManagerBotModule>())
						foreach (var s in sm.Squads)
							if (s.Type is SquadType.Assault or SquadType.Rush or SquadType.Air && s.Units.Count > 0)
								s.SetActorToTarget((prey, WVec.Zero));
			}
		}

		// Somewhere inside our base, on the side away from the enemy.
		CPos SafeSpot(World w, List<Player> enemies)
		{
			var home = player.HomeLocation;
			var buildings = w.Actors.Where(a => a.Owner == player && !a.IsDead && a.IsInWorld && a.Info.HasTraitInfo<BuildingInfo>()
				&& (a.Location - home).LengthSquared <= 20 * 20).Select(a => a.Location).ToList();
			if (buildings.Count == 0 || enemies.Count == 0)
				return home;
			return buildings.MaxBy(c => enemies.Min(e => (c - e.HomeLocation).LengthSquared));
		}

		void MoveVip(IBot bot, World w, Actor pres, Actor carrier, CPos to)
		{
			if (carrier != null)
			{
				// Only the limo moves; if he's holed up in a building, stay put.
				if (carrier.Info.HasTraitInfo<IMoveInfo>())
					bot.QueueOrder(new Order("Move", carrier, Target.FromCell(w, to), false));
				return;
			}

			// On foot: get into the limo if it's close, otherwise run for safety.
			var limo = w.Actors.FirstOrDefault(a => a.Owner == player && a.Info.Name == "limo" && !a.IsDead && a.IsInWorld);
			if (limo != null && limo.Trait<Cargo>().HasSpace(1) && (limo.Location - pres.Location).LengthSquared <= 12 * 12)
				bot.QueueOrder(new Order("EnterTransport", pres, Target.FromActor(limo), false));
			else
				bot.QueueOrder(new Order("Move", pres, Target.FromCell(w, to), false));
		}
	}
}
