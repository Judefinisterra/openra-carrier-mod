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
using OpenRA.GameRules;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// ================================================================================================
	// Tunnel Network (Iran): units that enter one tunnel entrance pop out of another one a moment later.
	// The building has a Cargo trait; this trait takes each passenger straight back out at the next entrance.
	// ================================================================================================
	[Desc("Units entering this building travel underground and come out at another of the owner's entrances.")]
	public class TunnelNetworkInfo : TraitInfo, Requires<CargoInfo>
	{
		[Desc("Ticks spent underground.")]
		public readonly int TravelTicks = 50;

		public override object Create(ActorInitializer init) { return new TunnelNetwork(init.Self, this); }
	}

	public class TunnelNetwork : INotifyPassengerEntered, ITick
	{
		readonly TunnelNetworkInfo info;
		readonly Cargo cargo;
		readonly List<(Actor Passenger, int Ticks)> travelling = [];

		public TunnelNetwork(Actor self, TunnelNetworkInfo info)
		{
			this.info = info;
			cargo = self.Trait<Cargo>();
		}

		void INotifyPassengerEntered.OnPassengerEntered(Actor self, Actor passenger)
		{
			travelling.Add((passenger, info.TravelTicks));
		}

		// The other entrance this one leads to: the next one by build order, wrapping around.
		Actor Destination(Actor self)
		{
			var all = self.World.ActorsHavingTrait<TunnelNetwork>()
				.Where(a => a.Owner == self.Owner && !a.IsDead && a.IsInWorld)
				.OrderBy(a => a.ActorID).ToList();
			if (all.Count < 2)
				return self;

			var next = all.FirstOrDefault(a => a.ActorID > self.ActorID);
			return next ?? all[0];
		}

		void ITick.Tick(Actor self)
		{
			for (var i = travelling.Count - 1; i >= 0; i--)
			{
				var (p, t) = travelling[i];
				if (p.IsDead || p.Disposed || !cargo.Passengers.Contains(p))
				{
					travelling.RemoveAt(i);
					continue;
				}

				if (--t > 0)
				{
					travelling[i] = (p, t);
					continue;
				}

				var dest = Destination(self);
				var exit = FindExit(dest, p);
				if (exit == null)
				{
					travelling[i] = (p, 25);
					continue;
				}

				travelling.RemoveAt(i);
				cargo.Unload(self, p);
				var cell = exit.Value;
				var lonely = dest == self;
				self.World.AddFrameEndTask(w =>
				{
					if (p.Disposed)
						return;

					var pos = p.Trait<IPositionable>();
					var sub = w.ActorMap.FreeSubCell(cell);
					pos.SetPosition(p, cell, sub == SubCell.Invalid ? SubCell.Any : sub);
					p.Trait<Passenger>().OnBeforeAddedToWorld(p);
					w.Add(p);
					if (lonely && p.Owner == w.LocalPlayer)
						TextNotificationsManager.AddSystemLine("Tunnels", "Build a second tunnel entrance to travel underground.");
				});
			}
		}

		static CPos? FindExit(Actor entrance, Actor passenger)
		{
			var w = entrance.World;
			var mobile = passenger.TraitOrDefault<Mobile>();
			var bi = entrance.Info.TraitInfoOrDefault<BuildingInfo>();
			var centre = entrance.Location + (bi != null ? new CVec(bi.Dimensions.X / 2, bi.Dimensions.Y) : CVec.Zero);
			foreach (var c in w.Map.FindTilesInAnnulus(centre, 0, 4))
			{
				if (!w.Map.Contains(c) || w.ActorMap.GetActorsAt(c).Any(a => a == entrance))
					continue;

				if (mobile != null && !mobile.CanEnterCell(c, null, BlockedByActor.All))
					continue;

				return c;
			}

			return null;
		}
	}

	// ================================================================================================
	// Shahed Launch Truck (Iran): when it "fires" (an invisible long-range weapon), it releases a swarm of
	// Shahed drones that fly at the target. The weapon's reload time is the time to reload the rack.
	// ================================================================================================
	[Desc("Launches drones at the target whenever the named armament fires.")]
	public class DroneLauncherInfo : TraitInfo
	{
		[ActorReference]
		public readonly string Drone = "shahed";

		public readonly int Count = 5;

		[Desc("Armament that triggers a launch.")]
		public readonly string Armament = "primary";

		[GrantedConditionReference]
		[Desc("Condition granted while the rack is empty (reloading).")]
		public readonly string EmptyCondition = "rack-empty";

		[Desc("How long the rack shows as empty.")]
		public readonly int ReloadTicks = 750;

		public override object Create(ActorInitializer init) { return new DroneLauncher(this); }
	}

	public class DroneLauncher : INotifyAttack, ITick
	{
		readonly DroneLauncherInfo info;
		int emptyTicks;
		int token = Actor.InvalidConditionToken;

		public DroneLauncher(DroneLauncherInfo info) { this.info = info; }

		void INotifyAttack.PreparingAttack(Actor self, in Target target, Armament a, Barrel barrel) { }

		void INotifyAttack.Attacking(Actor self, in Target target, Armament a, Barrel barrel)
		{
			if (a.Info.Name != info.Armament || emptyTicks > 0)
				return;

			emptyTicks = info.ReloadTicks;
			if (token == Actor.InvalidConditionToken)
				token = self.GrantCondition(info.EmptyCondition);

			var t = target;
			var owner = self.Owner;
			var start = self.CenterPosition;
			var facing = self.TraitOrDefault<IFacing>()?.Facing ?? WAngle.Zero;
			self.World.AddFrameEndTask(w =>
			{
				for (var i = 0; i < info.Count; i++)
				{
					var offset = new WVec(-256 * (i - info.Count / 2), 0, 0).Rotate(WRot.FromYaw(facing));
					var drone = w.CreateActor(info.Drone, new TypeDictionary
					{
						new CenterPositionInit(start + offset + new WVec(0, 0, 512)),
						new OwnerInit(owner),
						new FacingInit(facing),
					});

					foreach (var attack in drone.TraitsImplementing<AttackBase>())
						attack.AttackTarget(t, AttackSource.Default, false, true, true);
				}
			});
		}

		void ITick.Tick(Actor self)
		{
			if (emptyTicks > 0 && --emptyTicks == 0 && token != Actor.InvalidConditionToken)
				token = self.RevokeCondition(token);
		}
	}

	// ================================================================================================
	// Missile City (Iran superweapon): fires a volley of ballistic missiles at an area.
	// ================================================================================================
	[Desc("Support power that launches a volley of missiles from this building at the target area.")]
	public class MissileVolleyPowerInfo : SupportPowerInfo
	{
		[WeaponReference]
		[FieldLoader.Require]
		public readonly string Weapon = null;

		public readonly int Count = 8;

		[Desc("Ticks between missiles.")]
		public readonly int Interval = 6;

		[Desc("Missiles land at random within this distance of the target.")]
		public readonly WDist Scatter = WDist.FromCells(4);

		[Desc("Launch point, relative to the building's centre.")]
		public readonly WVec LaunchOffset = new(0, -256, 512);

		[SequenceReference]
		public readonly string LaunchSequence = "active";

		public WeaponInfo WeaponInfo { get; private set; }

		public override void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			base.RulesetLoaded(rules, ai);
			WeaponInfo = rules.Weapons[Weapon.ToLowerInvariant()];
		}

		public override object Create(ActorInitializer init) { return new MissileVolleyPower(init.Self, this); }
	}

	public class MissileVolleyPower : SupportPower, ITick
	{
		readonly List<(int Delay, WPos Target)> pending = [];

		public MissileVolleyPower(Actor self, MissileVolleyPowerInfo info)
			: base(self, info) { }

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			base.Activate(self, order, manager);
			var info = (MissileVolleyPowerInfo)Info;
			PlayLaunchSounds();
			var body = self.TraitOrDefault<WithSpriteBody>();
			if (body != null && body.IsTraitEnabled() && !string.IsNullOrEmpty(info.LaunchSequence))
				body.PlayCustomAnimation(self, info.LaunchSequence);

			var centre = order.Target.CenterPosition;
			for (var i = 0; i < info.Count; i++)
			{
				var r = self.World.SharedRandom.Next(info.Scatter.Length);
				var a = WAngle.FromFacing(self.World.SharedRandom.Next(256));
				var off = new WVec(r, 0, 0).Rotate(WRot.FromYaw(a));
				pending.Add((20 + i * info.Interval, centre + off));
			}
		}

		void ITick.Tick(Actor self)
		{
			var info = (MissileVolleyPowerInfo)Info;
			for (var i = pending.Count - 1; i >= 0; i--)
			{
				var (d, tgt) = pending[i];
				if (--d > 0)
				{
					pending[i] = (d, tgt);
					continue;
				}

				pending.RemoveAt(i);
				if (self.IsDead || !self.IsInWorld)
					continue;

				var src = self.CenterPosition + info.LaunchOffset + new WVec(self.World.SharedRandom.Next(-512, 512), 0, 0);
				var target = new WPos(tgt.X, tgt.Y, self.World.Map.CenterOfCell(self.World.Map.CellContaining(tgt)).Z);
				var weapon = info.WeaponInfo;
				var args = new ProjectileArgs
				{
					Weapon = weapon,
					Facing = (target - src).Yaw,
					CurrentMuzzleFacing = () => (target - src).Yaw,
					DamageModifiers = [],
					InaccuracyModifiers = [],
					RangeModifiers = [],
					Source = src,
					CurrentSource = () => src,
					SourceActor = self,
					PassiveTarget = target,
					GuidedTarget = Target.FromPos(target),
				};

				if (weapon.Report != null && weapon.Report.Length > 0)
					Game.Sound.Play(SoundType.World, weapon.Report, self.World, src);

				self.World.AddFrameEndTask(w =>
				{
					var projectile = weapon.Projectile.Create(args);
					if (projectile != null)
						w.Add(projectile);
				});
			}
		}
	}

	// ================================================================================================
	// Burning oil: an actor that keeps damaging everything standing in or next to it.
	// ================================================================================================
	[Desc("Fires a weapon at its own position every few ticks (burning oil, gas clouds...).")]
	public class PeriodicWeaponInfo : ConditionalTraitInfo
	{
		[WeaponReference]
		[FieldLoader.Require]
		public readonly string Weapon = null;

		public readonly int Interval = 15;

		public WeaponInfo WeaponInfo { get; private set; }

		public override void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			base.RulesetLoaded(rules, ai);
			WeaponInfo = rules.Weapons[Weapon.ToLowerInvariant()];
		}

		public override object Create(ActorInitializer init) { return new PeriodicWeapon(this); }
	}

	public class PeriodicWeapon : ConditionalTrait<PeriodicWeaponInfo>, ITick
	{
		int ticks;

		public PeriodicWeapon(PeriodicWeaponInfo info)
			: base(info) { }

		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled || ++ticks < Info.Interval)
				return;

			ticks = 0;
			Info.WeaponInfo.Impact(Target.FromPos(self.CenterPosition), self);
		}
	}

	// ================================================================================================
	// Militia Uprising: Psy-Op protesters that are left alone for a while may arm themselves and join
	// the side that launched the Psy-Op.
	// ================================================================================================
	[Desc("After a while without being hurt, this unit may turn into another unit owned by its instigator.")]
	public class MilitiaUprisingInfo : TraitInfo
	{
		[ActorReference]
		public readonly string Actor = "militia";

		[Desc("Ticks without taking damage before it may rise up.")]
		public readonly int Delay = 1500;

		[Desc("Percent chance that it rises up (otherwise it stays a protester for good).")]
		public readonly int Chance = 25;

		public override object Create(ActorInitializer init) { return new MilitiaUprising(this); }
	}

	public class MilitiaUprising : ITick, INotifyDamage
	{
		readonly MilitiaUprisingInfo info;
		int ticks;

		/// <summary>The player who launched the Psy-Op (set when the crowd is spawned).</summary>
		public Player Instigator;

		public MilitiaUprising(MilitiaUprisingInfo info) { this.info = info; }

		void INotifyDamage.Damaged(Actor self, AttackInfo e)
		{
			if (e.Damage.Value > 0)
				ticks = 0;
		}

		void ITick.Tick(Actor self)
		{
			if (Instigator == null || ++ticks < info.Delay)
				return;

			var instigator = Instigator;
			Instigator = null;
			if (self.World.SharedRandom.Next(100) >= info.Chance || instigator.WinState != WinState.Undefined)
				return;

			var cell = self.Location;
			var facing = self.TraitOrDefault<IFacing>()?.Facing ?? WAngle.Zero;
			var sub = self.TraitOrDefault<Mobile>()?.FromSubCell ?? SubCell.Any;
			var hp = self.TraitOrDefault<IHealth>();
			var ratio = hp != null ? hp.HP * 100 / Math.Max(1, hp.MaxHP) : 100;
			self.World.AddFrameEndTask(w =>
			{
				if (self.IsDead || self.Disposed)
					return;

				self.Dispose();
				var militia = w.CreateActor(info.Actor, new TypeDictionary
				{
					new LocationInit(cell),
					new SubCellInit(sub),
					new OwnerInit(instigator),
					new FacingInit(facing),
					new HealthInit(Math.Max(1, ratio)),
				});

				if (w.LocalPlayer == instigator && militia.IsInWorld)
					TextNotificationsManager.AddSystemLine("Psy-Op", "Protesters have taken up arms for you!");
			});
		}
	}

	// ================================================================================================
	// Mercenary Camp: placed at game start (lobby option) near the middle of the map, away from everyone.
	// ================================================================================================
	[TraitLocation(SystemActors.World)]
	[Desc("Places a neutral, capturable building near the middle of the map at the start of the game.")]
	public class NeutralCampSpawnerInfo : TraitInfo, ILobbyOptions
	{
		[ActorReference]
		public readonly string Actor = "merccamp";

		[FluentReference]
		public readonly string CheckboxLabel = "checkbox-merccamp.label";

		[FluentReference]
		public readonly string CheckboxDescription = "checkbox-merccamp.description";

		public readonly bool CheckboxEnabled = true;
		public readonly int CheckboxDisplayOrder = 60;

		[Desc("Don't place it closer than this to any player's start.")]
		public readonly int MinDistanceFromStart = 14;

		[Desc("Don't place it if the map already has one.")]
		public readonly bool SkipIfMapHasOne = true;

		IEnumerable<LobbyOption> ILobbyOptions.LobbyOptions(MapPreview map)
		{
			yield return new LobbyBooleanOption(map, "merccamp", CheckboxLabel, CheckboxDescription, true, CheckboxDisplayOrder, CheckboxEnabled, false);
		}

		public override object Create(ActorInitializer init) { return new NeutralCampSpawner(this); }
	}

	public class NeutralCampSpawner : IWorldLoaded
	{
		readonly NeutralCampSpawnerInfo info;

		public NeutralCampSpawner(NeutralCampSpawnerInfo info) { this.info = info; }

		void IWorldLoaded.WorldLoaded(World w, OpenRA.Graphics.WorldRenderer wr)
		{
			if (!w.LobbyInfo.GlobalSettings.OptionOrDefault("merccamp", info.CheckboxEnabled))
				return;

			if (info.SkipIfMapHasOne && w.Actors.Any(a => a.Info.Name == info.Actor))
				return;

			var ai = w.Map.Rules.Actors[info.Actor];
			var bi = ai.TraitInfo<BuildingInfo>();
			var neutral = w.Players.First(p => p.InternalName == "Neutral");
			var homes = w.Players.Where(p => p.Playable && p.HomeLocation != CPos.Zero).Select(p => p.HomeLocation).ToList();
			var b = w.Map.Bounds;
			var centre = new MPos(b.Left + b.Width / 2, b.Top + b.Height / 2).ToCPos(w.Map);

			CPos? best = null;
			var bestScore = long.MinValue;
			for (var v = b.Top + 2; v < b.Bottom - 3; v += 2)
			{
				for (var u = b.Left + 2; u < b.Right - 3; u += 2)
				{
					var cell = new MPos(u, v).ToCPos(w.Map);
					if (!w.CanPlaceBuilding(cell, ai, bi, null))
						continue;

					// Every footprint cell (and the cells around it) must be open ground.
					if (bi.Tiles(cell).Any(c => !w.Map.Contains(c + new CVec(0, 1))))
						continue;

					var minHome = homes.Count == 0 ? 1000 : homes.Min(h => (h - cell).Length);
					if (minHome < info.MinDistanceFromStart)
						continue;

					// Fair: as equally far from everyone as possible, and close to the middle.
					var maxHome = homes.Count == 0 ? 0 : homes.Max(h => (h - cell).Length);
					long score = -(maxHome - minHome) * 3 - (cell - centre).Length * 2;
					if (score > bestScore)
					{
						bestScore = score;
						best = cell;
					}
				}
			}

			if (best == null)
				return;

			w.CreateActor(info.Actor, new TypeDictionary
			{
				new LocationInit(best.Value),
				new OwnerInit(neutral),
			});
		}
	}
}
