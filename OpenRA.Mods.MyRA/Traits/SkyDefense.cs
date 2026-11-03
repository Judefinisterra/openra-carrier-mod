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
using System.Reflection;
using OpenRA.Activities;
using OpenRA.Effects;
using OpenRA.GameRules;
using OpenRA.Graphics;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Projectiles;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

// Skyview satellites and the Nike missile defense site.
namespace OpenRA.Mods.MyRA.Traits
{
	// ================================================================================================
	// Skyview satellites: every Skyview Tower puts a handful of satellites in the sky. They fan out in
	// different directions and ping-pong around the map, bouncing off the edges at angles, and their
	// owner's team sees everything under them. Nobody can select them or shoot them. They fall out of
	// the sky when their tower is lost and change sides with it.
	// ================================================================================================
	[Desc("Launches orbiting satellites (actors with SatelliteOrbit) when this building appears.")]
	public class SatelliteLauncherInfo : TraitInfo
	{
		[ActorReference]
		public readonly string Actor = "satellite";

		[Desc("How many satellites each tower launches.")]
		public readonly int Count = 5;

		public readonly WDist LaunchAltitude = new(2048);

		public override object Create(ActorInitializer init) { return new SatelliteLauncher(this); }
	}

	public class SatelliteLauncher : INotifyAddedToWorld, INotifyKilled, INotifyActorDisposing, INotifyOwnerChanged
	{
		readonly SatelliteLauncherInfo info;
		readonly List<Actor> sats = [];
		bool launched;

		public SatelliteLauncher(SatelliteLauncherInfo info) { this.info = info; }

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			// Ownership changes briefly remove and re-add the tower; only the first time counts.
			if (launched || self.World.Type != WorldType.Regular)
				return;

			launched = true;
			self.World.AddFrameEndTask(w =>
			{
				if (self.IsDead || self.Disposed)
					return;

				// Launch effect: a flash on the station and a smoke column climbing to where the satellites appear.
				w.Add(new SpriteEffect(self.CenterPosition, w, "explosion", "small_napalm", "effect"));
				for (var z = 0; z <= info.LaunchAltitude.Length; z += 256)
					w.Add(new SpriteEffect(self.CenterPosition + new WVec(0, 0, z), w, "smokey", "idle", "effect"));

				// Spread the launch headings evenly round the compass from a random start.
				var start = w.SharedRandom.Next(1024);
				for (var i = 0; i < info.Count; i++)
				{
					var heading = new WAngle(start + i * 1024 / info.Count);
					var sat = w.CreateActor(info.Actor, new TypeDictionary
					{
						new LocationInit(self.Location),
						new OwnerInit(self.Owner),
						new CenterPositionInit(self.CenterPosition + new WVec(0, 0, info.LaunchAltitude.Length)),
						new FacingInit(heading),
					});
					sat.TraitOrDefault<SatelliteOrbit>()?.Launch(heading);
					sats.Add(sat);
				}
			});
		}

		void Drop()
		{
			foreach (var sat in sats)
				if (!sat.Disposed)
					sat.TraitOrDefault<SatelliteOrbit>()?.Fall(sat);
			sats.Clear();
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e) { Drop(); }
		void INotifyActorDisposing.Disposing(Actor self) { Drop(); }

		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			// Called from inside a frame-end task, so the satellites can switch sides right away.
			foreach (var sat in sats)
				if (!sat.Disposed && sat.Owner != newOwner)
					sat.ChangeOwnerSync(newOwner);
		}
	}

	[Desc("Ping-pongs around the map in straight lines, bouncing off the map edges at angles. Ignores orders.")]
	public class SatelliteOrbitInfo : TraitInfo, Requires<AircraftInfo>
	{
		[Desc("Random change to the bounce angle (in 1024ths of a turn) so the paths don't repeat.")]
		public readonly int BounceJitter = 48;

		[Desc("Keep headings at least this far (1024ths of a turn) from straight up/down/left/right.")]
		public readonly int MinAxisAngle = 48;

		public readonly int ClimbSpeed = 24;

		[WeaponReference]
		public readonly string CrashWeapon = "UnitExplodePlane";

		public readonly int FallSpeed = 48;

		public override object Create(ActorInitializer init) { return new SatelliteOrbit(this); }
	}

	public class SatelliteOrbit : ITick
	{
		readonly SatelliteOrbitInfo info;
		Aircraft aircraft;
		WAngle heading;
		WPos pos;
		bool started, falling;

		public SatelliteOrbit(SatelliteOrbitInfo info) { this.info = info; }

		public void Launch(WAngle heading) { this.heading = OffAxis(heading); }

		WAngle OffAxis(WAngle a)
		{
			var m = a.Angle % 256;
			if (m < info.MinAxisAngle)
				return new WAngle(a.Angle + info.MinAxisAngle - m);
			if (m > 256 - info.MinAxisAngle)
				return new WAngle(a.Angle - (m - (256 - info.MinAxisAngle)));
			return a;
		}

		public void Fall(Actor self)
		{
			if (falling)
				return;

			falling = true;
			aircraft ??= self.Trait<Aircraft>();
			self.CancelActivity();
			self.QueueActivity(false, new SatelliteFall(aircraft, info));
		}

		static WVec Dir(WAngle a) => new WVec(0, -1024, 0).Rotate(WRot.FromYaw(a));

		void ITick.Tick(Actor self)
		{
			if (!self.IsInWorld || self.IsDead)
				return;

			var w = self.World;
			var map = w.Map;
			aircraft ??= self.Trait<Aircraft>();

			if (falling)
			{
				// Keep the fall going even if something else tries to give orders.
				if (self.CurrentActivity is not SatelliteFall)
				{
					self.CancelActivity();
					self.QueueActivity(false, new SatelliteFall(aircraft, info));
				}

				return;
			}

			if (!started)
			{
				started = true;
				pos = self.CenterPosition;
				if (heading == WAngle.Zero)
					heading = OffAxis(new WAngle(w.SharedRandom.Next(1024)));
			}

			// Nothing else gets to steer it (AI squads, idle circling).
			if (self.CurrentActivity != null)
				self.CancelActivity();

			var b = map.Bounds;
			var minX = b.Left * 1024 + 512;
			var maxX = b.Right * 1024 - 512;
			var minY = b.Top * 1024 + 512;
			var maxY = b.Bottom * 1024 - 512;

			var speed = aircraft.Info.Speed;
			var v = Dir(heading) * speed / 1024;
			var x = pos.X + v.X;
			var y = pos.Y + v.Y;
			var z = System.Math.Min(pos.Z + info.ClimbSpeed, map.CenterOfCell(map.CellContaining(pos)).Z + aircraft.Info.CruiseAltitude.Length);
			z = System.Math.Max(z, pos.Z);

			var bounceX = (x < minX && v.X < 0) || (x > maxX && v.X > 0);
			var bounceY = (y < minY && v.Y < 0) || (y > maxY && v.Y > 0);
			if (bounceX || bounceY)
			{
				// Mirror off the edge, then nudge the angle a little so it doesn't retrace the same lines.
				var d = Dir(heading);
				var r = new WVec(bounceX ? -d.X : d.X, bounceY ? -d.Y : d.Y, 0);
				var a = OffAxis(new WAngle(r.Yaw.Angle + w.SharedRandom.Next(-info.BounceJitter, info.BounceJitter + 1)));

				// Make sure the nudge didn't point it back off the map.
				var n = Dir(a);
				if ((bounceX && System.Math.Sign(n.X) != System.Math.Sign(r.X)) || (bounceY && System.Math.Sign(n.Y) != System.Math.Sign(r.Y)))
					a = OffAxis(r.Yaw);

				heading = a;
				x = System.Math.Clamp(x, minX, maxX);
				y = System.Math.Clamp(y, minY, maxY);
			}

			pos = new WPos(x, y, z);
			aircraft.SetPosition(self, pos);
			aircraft.Facing = heading;
		}
	}

	// A satellite whose tower is gone: tumbles straight down and explodes on the ground.
	sealed class SatelliteFall : Activity
	{
		readonly Aircraft aircraft;
		readonly SatelliteOrbitInfo info;

		public SatelliteFall(Aircraft aircraft, SatelliteOrbitInfo info)
		{
			this.aircraft = aircraft;
			this.info = info;
			IsInterruptible = false;
		}

		public override bool Tick(Actor self)
		{
			var pos = self.CenterPosition - new WVec(0, 0, info.FallSpeed);
			aircraft.SetPosition(self, pos);
			aircraft.Facing += new WAngle(24);
			if (self.World.Map.DistanceAboveTerrain(pos).Length > 0)
				return false;

			self.World.Map.Rules.Weapons[info.CrashWeapon.ToLowerInvariant()].Impact(Target.FromPos(pos), self);
			self.Dispose();
			return true;
		}
	}

	// ================================================================================================
	// Nike missile defense: shoots down nuclear missiles (silo, mega nuke, sub-launched) and Missile
	// City rockets aimed near it. A few interceptors are ready at once; each has a chance to hit.
	// ================================================================================================
	[Desc("Missile defense site. Needs an AmmoPool (one round per interceptor) and the MissileDefenseManager on the World.")]
	public class NikeSiteInfo : PausableConditionalTraitInfo, Requires<AmmoPoolInfo>
	{
		[Desc("Defends against missiles aimed within this distance.")]
		public readonly WDist Range = WDist.FromCells(15);

		[Desc("Chance (percent) that one interceptor destroys its missile.")]
		public readonly int HitChance = 75;

		public readonly WVec LaunchOffset = new(0, 0, 512);

		public override object Create(ActorInitializer init) { return new NikeSite(this); }
	}

	public class NikeSite : PausableConditionalTrait<NikeSiteInfo>
	{
		AmmoPool pool;

		public NikeSite(NikeSiteInfo info)
			: base(info) { }

		protected override void Created(Actor self)
		{
			pool = self.TraitsImplementing<AmmoPool>().First();
			base.Created(self);
		}

		public bool Ready(Actor self) => !IsTraitDisabled && !IsTraitPaused && pool.HasAmmo && self.IsInWorld && !self.IsDead;

		// Where the interceptor that fires next sits on the pad (front rail while both are loaded, then the back one).
		public WPos LaunchPoint(Actor self) => self.CenterPosition + (pool.CurrentAmmoCount >= 2 ? new WVec(-367, -196, 0) : new WVec(128, -341, 0));

		public void Spend(Actor self) { pool.TakeAmmo(self, 1); }
	}

	[TraitLocation(SystemActors.World)]
	[Desc("Lets Nike sites see incoming nuclear missiles and ballistic rockets.")]
	public class MissileDefenseManagerInfo : TraitInfo
	{
		[Desc("Bullet weapons that Nike sites will shoot down.")]
		public readonly HashSet<string> BulletWeapons = ["missilecityrocket", "subnuke"];

		[Desc("Bullet weapons that count as nuclear (announced when shot down).")]
		public readonly HashSet<string> NuclearBullets = ["subnuke"];

		public readonly int InterceptorSpeed = 640;

		public override object Create(ActorInitializer init) { return new MissileDefenseManager(this); }
	}

	public class MissileDefenseManager : ITick, IWorldLoaded
	{
		static readonly FieldInfo NukeTicks = typeof(NukeLaunch).GetField("ticks", BindingFlags.NonPublic | BindingFlags.Instance);
		static readonly FieldInfo NukeTurn = typeof(NukeLaunch).GetField("turn", BindingFlags.NonPublic | BindingFlags.Instance);
		static readonly FieldInfo NukePos = typeof(NukeLaunch).GetField("pos", BindingFlags.NonPublic | BindingFlags.Instance);
		static readonly FieldInfo NukeTarget = typeof(NukeLaunch).GetField("descendTarget", BindingFlags.NonPublic | BindingFlags.Instance);
		static readonly FieldInfo NukeOwner = typeof(NukeLaunch).GetField("firedBy", BindingFlags.NonPublic | BindingFlags.Instance);
		static readonly FieldInfo BulletArgs = typeof(Bullet).GetField("Args", BindingFlags.NonPublic | BindingFlags.Instance);
		static readonly FieldInfo BulletPos = typeof(Bullet).GetField("pos", BindingFlags.NonPublic | BindingFlags.Instance);
		static readonly FieldInfo BulletTarget = typeof(Bullet).GetField("target", BindingFlags.NonPublic | BindingFlags.Instance);

		readonly MissileDefenseManagerInfo info;
		readonly HashSet<IEffect> engaged = [];
		HashSet<WeaponInfo> bulletWeapons = [], nuclearBullets = [];

		public int Intercepted { get; private set; }
		public int Missed { get; private set; }

		public MissileDefenseManager(MissileDefenseManagerInfo info) { this.info = info; }

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			var weapons = w.Map.Rules.Weapons;
			bulletWeapons = info.BulletWeapons.Where(weapons.ContainsKey).Select(n => weapons[n]).ToHashSet();
			nuclearBullets = info.NuclearBullets.Where(weapons.ContainsKey).Select(n => weapons[n]).ToHashSet();
		}

		sealed class Threat
		{
			public IEffect Effect;
			public Player Owner;
			public WPos Target;
			public bool Nuclear;
		}

		Threat Read(IEffect e)
		{
			if (e is NukeLaunch n)
			{
				// Only once it has turned over and is coming down.
				if ((int)NukeTicks.GetValue(n) < (int)NukeTurn.GetValue(n))
					return null;
				return new Threat { Effect = e, Owner = (Player)NukeOwner.GetValue(n), Target = (WPos)NukeTarget.GetValue(n), Nuclear = true };
			}

			if (e is Bullet b && BulletArgs.GetValue(b) is ProjectileArgs args && bulletWeapons.Contains(args.Weapon))
				return new Threat { Effect = e, Owner = args.SourceActor?.Owner, Target = (WPos)BulletTarget.GetValue(b), Nuclear = nuclearBullets.Contains(args.Weapon) };

			return null;
		}

		public static WPos? PositionOf(World w, IEffect e)
		{
			if (!w.Effects.Contains(e))
				return null;
			if (e is NukeLaunch n)
				return (WPos)NukePos.GetValue(n);
			if (e is Bullet b)
				return (WPos)BulletPos.GetValue(b);
			return null;
		}

		void ITick.Tick(Actor self)
		{
			var w = self.World;
			engaged.RemoveWhere(e => !w.Effects.Contains(e));

			List<Threat> threats = null;
			foreach (var e in w.Effects)
			{
				if (engaged.Contains(e) || (e is not NukeLaunch && e is not Bullet))
					continue;
				var t = Read(e);
				if (t != null)
					(threats ??= []).Add(t);
			}

			if (threats == null)
				return;

			var sites = w.ActorsWithTrait<NikeSite>().ToList();
			foreach (var t in threats)
			{
				var site = sites.FirstOrDefault(s => s.Trait.Ready(s.Actor)
					&& (t.Owner == null || s.Actor.Owner.RelationshipWith(t.Owner) != PlayerRelationship.Ally)
					&& (s.Actor.CenterPosition - t.Target).HorizontalLengthSquared <= s.Trait.Info.Range.LengthSquared);
				if (site.Actor == null)
					continue;

				site.Trait.Spend(site.Actor);
				engaged.Add(t.Effect);
				var from = site.Trait.LaunchPoint(site.Actor);
				var defender = site.Actor.Owner;
				var chance = site.Trait.Info.HitChance;
				Game.Sound.Play(SoundType.World, "missile1.aud", from);
				w.AddFrameEndTask(ww => ww.Add(new Interceptor(ww, from, t.Effect, info.InterceptorSpeed, ok => Resolve(ww, t, defender, chance, ok))));
			}
		}

		// Called when the interceptor reaches its missile (ok = the missile was still in the air).
		bool Resolve(World w, Threat t, Player defender, int chance, bool stillFlying)
		{
			engaged.Remove(t.Effect);
			if (!stillFlying)
				return false;

			var hit = w.SharedRandom.Next(100) < chance;
			if (hit)
			{
				Intercepted++;
				var gone = t.Effect;
				w.AddFrameEndTask(ww => { ww.Remove(gone); ww.ScreenMap.Remove(gone); });
				if (t.Nuclear)
					TextNotificationsManager.AddSystemLine("Nike", $"{defender.ResolvedPlayerName}'s Nike site shot down a nuclear missile!");
			}
			else
			{
				Missed++;
				if (t.Nuclear)
					TextNotificationsManager.AddSystemLine("Nike", $"A Nike interceptor from {defender.ResolvedPlayerName} missed the nuclear missile!");
			}

			return hit;
		}
	}

	// A Nike interceptor in flight: chases its missile and decides the hit when it gets there.
	sealed class Interceptor : IEffect, ISpatiallyPartitionable
	{
		readonly World world;
		readonly IEffect threat;
		readonly int speed;
		readonly System.Func<bool, bool> resolve;
		readonly Animation anim;
		WPos pos;
		WAngle facing;
		int ticks;

		public Interceptor(World world, WPos from, IEffect threat, int speed, System.Func<bool, bool> resolve)
		{
			this.world = world;
			this.threat = threat;
			this.speed = speed;
			this.resolve = resolve;
			pos = from;
			anim = new Animation(world, "dragon", () => facing);
			anim.PlayRepeating("idle");
			world.ScreenMap.Add(this, pos, anim.Image);
		}

		void Done()
		{
			world.AddFrameEndTask(w => { w.Remove(this); w.ScreenMap.Remove(this); });
		}

		// Launch sequence: ignition flash and a billowing cloud on the pad, a straight vertical climb on the booster,
		// booster separation (it tumbles away), then the sustainer pitches over and homes on the missile.
		const int BoostTicks = 16;

		void Launch(World w)
		{
			var p = pos;
			w.AddFrameEndTask(ww =>
			{
				ww.Add(new SpriteEffect(p, ww, "explosion", "small_napalm", "effect"));
				for (var i = 0; i < 6; i++)
				{
					var a = new WAngle(i * 170 + 40);
					var puff = p + new WVec(a.Sin() * 300 / 1024, a.Cos() * 200 / 1024, 0);
					ww.Add(new SpriteEffect(puff, ww, "smokey", "idle", "effect"));
				}
			});
		}

		public void Tick(World w)
		{
			anim.Tick();
			if (ticks == 0)
				Launch(w);

			if (ticks < BoostTicks)
			{
				// Booster burn: straight up, accelerating, a thick trail and a flame under the tail.
				ticks++;
				facing = WAngle.Zero;
				pos += new WVec(0, 0, 40 + ticks * 14);
				var p = pos;
				w.AddFrameEndTask(ww =>
				{
					ww.Add(new SpriteEffect(p - new WVec(0, 0, 120), ww, "smokey", "idle", "effect"));
					if (ticks % 3 == 0)
						ww.Add(new SpriteEffect(p - new WVec(0, 0, 60), ww, "explosion", "piffs", "effect"));
				});
				if (ticks == BoostTicks)
				{
					w.AddFrameEndTask(ww => ww.Add(new SpriteEffect(p, ww, "explosion", "piff", "effect")));
					w.AddFrameEndTask(ww => ww.Add(new FallingBooster(ww, p)));
				}

				w.ScreenMap.Update(this, pos, anim.Image);
				return;
			}

			var dest = MissileDefenseManager.PositionOf(w, threat);
			if (dest == null || ++ticks > 250)
			{
				// The missile already went off (or got away): burst harmlessly.
				resolve(false);
				w.AddFrameEndTask(ww => ww.Add(new SpriteEffect(pos, ww, "explosion", "med_explosion_air", "effect")));
				Done();
				return;
			}

			var delta = dest.Value - pos;
			if (delta.Length <= speed)
			{
				pos = dest.Value;
				var hit = resolve(true);
				w.AddFrameEndTask(ww => ww.Add(new SpriteEffect(pos, ww, "explosion", hit ? "building" : "med_explosion_air", "effect")));
				Game.Sound.Play(SoundType.World, hit ? "kaboom25.aud" : "kaboom12.aud", pos);
				Done();
				return;
			}

			facing = delta.Yaw;
			pos += delta * speed / delta.Length;
			if (ticks % 2 == 0)
			{
				var p = pos;
				w.AddFrameEndTask(ww => ww.Add(new SpriteEffect(p, ww, "smokey", "idle", "effect")));
			}

			w.ScreenMap.Update(this, pos, anim.Image);
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr) => anim.Render(pos, wr.Palette("effect"));
	}

	// The spent Nike booster: tumbles back down trailing smoke and puffs on the ground.
	sealed class FallingBooster : IEffect
	{
		readonly Animation anim;
		WPos pos;
		WAngle facing;
		int fall, ticks;

		public FallingBooster(World world, WPos from)
		{
			pos = from;
			anim = new Animation(world, "dragon", () => facing);
			anim.PlayRepeating("idle");
		}

		public void Tick(World w)
		{
			ticks++;
			facing = new WAngle(ticks * 96);
			fall += 6;
			pos += new WVec(18, 10, -fall);
			if (ticks % 3 == 0)
			{
				var p = pos;
				w.AddFrameEndTask(ww => ww.Add(new SpriteEffect(p, ww, "smokey", "idle", "effect")));
			}

			if (pos.Z <= 0 || ticks > 120)
			{
				var p = new WPos(pos.X, pos.Y, 0);
				w.AddFrameEndTask(ww => { ww.Remove(this); ww.Add(new SpriteEffect(p, ww, "explosion", "piff", "effect")); });
			}
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr) => anim.Render(pos, wr.Palette("effect"));
	}
}
