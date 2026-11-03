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
using System.Reflection;
using System.Runtime.CompilerServices;
using OpenRA.Effects;
using OpenRA.GameRules;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.Projectiles;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// ================================================================================================
	// Iron Dome: a battery that watches the sky over your base. Every enemy missile, artillery shell, bomb or nuke
	// that comes into range gets an interceptor: a very fast missile that leaps straight up, then weaves and jinks
	// its way onto the target and blows it out of the sky. It always hits ordinary rounds; a nuke has a 50% chance
	// of getting through anyway.
	// ================================================================================================
	[Desc("Shoots down enemy projectiles (missiles, arcing shells, bombs and nukes) that come within range.")]
	public class IronDomeInfo : ConditionalTraitInfo
	{
		[Desc("Projectiles within this horizontal distance of the battery are engaged.")]
		public readonly WDist Range = WDist.FromCells(20);

		[Desc("Nukes are engaged once they have come down to this height (so the interception is on screen).")]
		public readonly WDist NukeEngageAltitude = WDist.FromCells(24);

		[Desc("Chance (percent) that the interceptor destroys a nuke. Other projectiles are always destroyed.")]
		public readonly int NukeKillChance = 50;

		[Desc("Interceptors in the air at once.")]
		public readonly int MaxInFlight = 10;

		[Desc("Ticks between launches.")]
		public readonly int LaunchInterval = 3;

		[Desc("Interceptor speed when it leaves the tube and its top speed, per tick.")]
		public readonly WDist LaunchSpeed = new(380);

		public readonly WDist MaxSpeed = new(900);

		[Desc("Interceptors give up after this many ticks.")]
		public readonly int MaxFlightTicks = 150;

		[Desc("Where the interceptors leave the launcher, relative to the building centre.")]
		public readonly WVec LaunchOffset = new(-300, 0, 500);

		public readonly string LaunchSound = "interceptor.wav";

		public readonly string HitSound = "kaboom25.aud";

		public readonly string ExplosionImage = "explosion";

		[SequenceReference(nameof(ExplosionImage))]
		public readonly string HitSequence = "med_explosion_air";

		[SequenceReference(nameof(ExplosionImage))]
		public readonly string FizzleSequence = "small_explosion_air";

		[SequenceReference(nameof(ExplosionImage))]
		public readonly string NukeKillSequence = "artillery_explosion";

		[PaletteReference]
		public readonly string ExplosionPalette = "effect";

		public override object Create(ActorInitializer init) { return new IronDome(this); }
	}

	public class IronDome : ConditionalTrait<IronDomeInfo>, ITick
	{
		// Projectiles some battery has already fired at, so two batteries don't waste interceptors on one round.
		static readonly ConditionalWeakTable<World, HashSet<IEffect>> Claimed = [];

		readonly List<IronDomeInterceptor> inFlight = [];
		int cooldown;

		public IronDome(IronDomeInfo info)
			: base(info) { }

		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled || --cooldown > 0)
				return;

			inFlight.RemoveAll(i => i.Finished);
			if (inFlight.Count >= Info.MaxInFlight)
				return;

			var claimed = Claimed.GetOrCreateValue(self.World);
			claimed.RemoveWhere(e => !self.World.Effects.Contains(e));

			var home = self.CenterPosition;
			Threat best = null;
			foreach (var e in self.World.Effects)
			{
				if (claimed.Contains(e) || !Threat.TryRead(e, out var t) || t.Owner == null
					|| self.Owner.RelationshipWith(t.Owner) != PlayerRelationship.Enemy)
					continue;

				var pos = t.Position;
				if ((pos - home).HorizontalLengthSquared > Info.Range.LengthSquared)
					continue;

				if (t.IsNuke && (!t.Descending || self.World.Map.DistanceAboveTerrain(pos) > Info.NukeEngageAltitude))
					continue;

				// Nukes first, then whatever is closest.
				if (best == null || (t.IsNuke && !best.IsNuke)
					|| (t.IsNuke == best.IsNuke && (pos - home).LengthSquared < (best.Position - home).LengthSquared))
					best = t;
			}

			if (best == null)
				return;

			claimed.Add(best.Effect);
			var kill = !best.IsNuke || self.World.SharedRandom.Next(100) < Info.NukeKillChance;
			var interceptor = new IronDomeInterceptor(self.World, self.Owner, Info, home + Info.LaunchOffset, best, kill, self.World.SharedRandom.Next(1024));
			inFlight.Add(interceptor);
			self.World.AddFrameEndTask(w => w.Add(interceptor));
			Game.Sound.Play(SoundType.World, Info.LaunchSound, home);
			cooldown = Info.LaunchInterval;
		}
	}

	// Reads the position and owner of the engine's projectile types, which keep them private.
	public class Threat
	{
		const BindingFlags Field = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
		static readonly FieldInfo MissileArgs = typeof(Missile).GetField("args", Field);
		static readonly FieldInfo MissilePos = typeof(Missile).GetField("pos", Field);
		static readonly FieldInfo BulletArgs = typeof(Bullet).GetField("Args", Field);
		static readonly FieldInfo BulletPos = typeof(Bullet).GetField("pos", Field);
		static readonly FieldInfo BulletAngle = typeof(Bullet).GetField("angle", Field);
		static readonly FieldInfo BombArgs = typeof(GravityBomb).GetField("args", Field);
		static readonly FieldInfo BombPos = typeof(GravityBomb).GetField("pos", Field);
		static readonly FieldInfo NukeOwner = typeof(NukeLaunch).GetField("firedBy", Field);
		static readonly FieldInfo NukePos = typeof(NukeLaunch).GetField("pos", Field);
		static readonly FieldInfo NukeTicks = typeof(NukeLaunch).GetField("ticks", Field);
		static readonly FieldInfo NukeTurn = typeof(NukeLaunch).GetField("turn", Field);

		public IEffect Effect { get; private init; }
		public Player Owner { get; private init; }
		public bool IsNuke { get; private init; }

		public WPos Position => Read(Effect) ?? WPos.Zero;

		public bool Descending => Effect is NukeLaunch n && (int)NukeTicks.GetValue(n) >= (int)NukeTurn.GetValue(n);

		public static bool TryRead(IEffect e, out Threat threat)
		{
			threat = e switch
			{
				Missile m => new Threat { Effect = e, Owner = (MissileArgs.GetValue(m) as ProjectileArgs)?.SourceActor?.Owner },
				Bullet b when ((WAngle)BulletAngle.GetValue(b)).Angle > 0
					=> new Threat { Effect = e, Owner = (BulletArgs.GetValue(b) as ProjectileArgs)?.SourceActor?.Owner },
				GravityBomb g => new Threat { Effect = e, Owner = (BombArgs.GetValue(g) as ProjectileArgs)?.SourceActor?.Owner },
				NukeLaunch n => new Threat { Effect = e, Owner = NukeOwner.GetValue(n) as Player, IsNuke = true },
				_ => null,
			};

			return threat != null;
		}

		public static WPos? Read(IEffect e)
		{
			return e switch
			{
				Missile m => (WPos)MissilePos.GetValue(m),
				Bullet b => (WPos)BulletPos.GetValue(b),
				GravityBomb g => (WPos)BombPos.GetValue(g),
				NukeLaunch n => (WPos)NukePos.GetValue(n),
				_ => null,
			};
		}
	}

	// One interceptor missile. Its flight is worked out in the simulation (straight up out of the tube, then
	// homing at increasing speed); the weaving you see is drawn on top of that path and doesn't affect the hit.
	public class IronDomeInterceptor : IEffect
	{
		const int BoostTicks = 5;
		const int TrailLength = 28;

		readonly Player owner;
		readonly IronDomeInfo info;
		readonly Threat target;
		readonly bool kill;
		readonly int phase;
		readonly ContrailRenderable smoke;
		readonly ContrailRenderable flame;

		WPos pos;
		WPos drawn;
		int ticks;
		int speed;
		int linger = -1;

		public IronDomeInterceptor(World world, Player owner, IronDomeInfo info, WPos launch, Threat target, bool kill, int phase)
		{
			this.owner = owner;
			this.info = info;
			this.target = target;
			this.kill = kill;
			this.phase = phase;
			pos = drawn = launch;
			speed = info.LaunchSpeed.Length;
			smoke = new ContrailRenderable(world, null, Color.FromArgb(235, 245, 245, 245), false, Color.FromArgb(0, 170, 170, 170), false,
				new WDist(40), new WDist(150), TrailLength, 1, 0);
			flame = new ContrailRenderable(world, null, Color.FromArgb(255, 255, 250, 200), false, Color.FromArgb(160, 255, 140, 30), false,
				new WDist(70), new WDist(30), 3, 0, 0);
		}

		public bool Finished { get; private set; }

		public void Tick(World w)
		{
			if (Finished)
			{
				// Let the smoke trail drift away before removing the effect.
				smoke.Update(drawn);
				if (--linger <= 0)
					w.AddFrameEndTask(ww => ww.Remove(this));

				return;
			}

			ticks++;
			var targetPos = w.Effects.Contains(target.Effect) ? Threat.Read(target.Effect) : null;
			if (targetPos == null || ticks > info.MaxFlightTicks)
			{
				// Target already landed or gone: self-destruct in the air.
				Burst(w, info.FizzleSequence, 1, 0);
				Finish();
				return;
			}

			var to = targetPos.Value - pos;
			var dist = to.Length;
			if (ticks <= BoostTicks)
				pos += new WVec(0, 0, speed);
			else if (dist <= speed + 384)
			{
				Hit(w, targetPos.Value);
				return;
			}
			else
				pos += to * speed / dist;

			speed = Math.Min(info.MaxSpeed.Length, speed + 45);

			// Cosmetic jinking: a corkscrew around the true path that dies away as it closes in.
			var t = ticks * 0.55f + phase;
			var amp = Math.Min(1f, dist / 6144f) * Math.Min(1f, ticks / 6f) * 700f;
			var across = new WVec(-to.Y, to.X, 0);
			var al = Math.Max(1, across.Length);
			drawn = pos + across * (int)(amp * MathF.Sin(t)) / al + new WVec(0, 0, (int)(amp * 0.6f * MathF.Cos(t * 1.3f)));
			smoke.Update(drawn);
			flame.Update(drawn);
		}

		void Hit(World w, WPos at)
		{
			pos = drawn = at;
			if (kill)
			{
				var victim = target.Effect;
				w.AddFrameEndTask(ww =>
				{
					ww.Remove(victim);
					ww.ScreenMap.Remove(victim);
				});

				Game.Sound.Play(SoundType.World, info.HitSound, at);
				if (target.IsNuke)
				{
					Burst(w, info.NukeKillSequence, 7, 1100);
					Burst(w, info.HitSequence, 6, 1800);
				}
				else
					Burst(w, info.HitSequence, 1, 0);
			}
			else
				Burst(w, info.HitSequence, 2, 900);

			if (target.IsNuke)
			{
				var text = kill ? "IRON DOME: nuke intercepted!" : "IRON DOME: the interceptor missed the nuke!";
				w.AddFrameEndTask(ww =>
				{
					if (ww.LocalPlayer != null && ww.LocalPlayer.IsAlliedWith(owner))
						TextNotificationsManager.AddSystemLine("Iron Dome", text);
					else if (ww.LocalPlayer != null && kill)
						TextNotificationsManager.AddSystemLine("Iron Dome", $"{owner.ResolvedPlayerName}'s Iron Dome shot down a nuke!");
				});
			}

			Finish();
		}

		void Burst(World w, string sequence, int count, int spread)
		{
			var centre = pos;
			for (var i = 0; i < count; i++)
			{
				var a = new WAngle(i * 1024 / Math.Max(1, count) + phase);
				var r = i == 0 ? 0 : spread;
				var at = centre + new WVec(r * a.Cos() / 1024, r * a.Sin() / 1024, r * a.Sin() / 2048);
				w.AddFrameEndTask(ww => ww.Add(new SpriteEffect(at, ww, info.ExplosionImage, sequence, info.ExplosionPalette)));
			}
		}

		void Finish()
		{
			Finished = true;
			linger = TrailLength;
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr)
		{
			yield return smoke;
			if (!Finished)
				yield return flame;
		}
	}
}
