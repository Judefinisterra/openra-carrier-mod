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
using System.Reflection;
using OpenRA.Effects;
using OpenRA.GameRules;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.Projectiles;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

// Tribal Vibranium Dome: a shield generator. Incoming nukes aimed under it hit a big blue force field instead
// (everything under it is safe, whoever owns it), the dome soaks up the energy for a few seconds and then
// fires a beam back at whatever launched the nuke. V2 rockets, Tomahawks and Missile City rockets are
// stopped by a quick small dome that uses a separate set of charges, so they never drain the nuke charge.
namespace OpenRA.Mods.MyRA.Traits
{
	[Desc("Vibranium Dome shield generator. Needs VibraniumManager on the World.")]
	public class VibraniumDomeInfo : TraitInfo
	{
		public readonly WDist Radius = WDist.FromCells(15);

		[Desc("Dome height as a fraction of its radius (matches the art).")]
		public readonly int HeightPercent = 55;

		[Desc("Ticks to recharge the nuke shield after the beam fires.")]
		public readonly int NukeRecharge = 4500;

		[Desc("Ticks the dome soaks up the blast before firing back.")]
		public readonly int AbsorbTicks = 100;

		public readonly int SmallCharges = 6;
		public readonly int SmallRecharge = 250;

		[Desc("Width of the beam's kill zone.")]
		public readonly WDist BeamWidth = new(1536);

		[Desc("Radius of the blast where the beam lands.")]
		public readonly WDist BlastRadius = new(2560);

		public readonly string ChatLine = "Wakanda forever!!!!!!";
		public readonly int ChatRepeats = 10;
		public readonly string ChoirSound = "vibdome-choir.wav";

		public override object Create(ActorInitializer init) { return new VibraniumDome(init.Self, this); }
	}

	public class VibraniumDome : ITick, INotifyKilled, INotifyActorDisposing
	{
		public readonly VibraniumDomeInfo Info;
		readonly Actor self;

		public int NukeCooldown;          // 0 = ready
		public int Small;
		int smallTimer;
		bool busy;                         // between absorbing a nuke and firing back
		DomeEffect shown;

		public VibraniumDome(Actor self, VibraniumDomeInfo info)
		{
			this.self = self;
			Info = info;
			Small = info.SmallCharges;
		}

		public bool NukeReady => NukeCooldown <= 0 && !busy && self.IsInWorld && !self.IsDead;
		public WPos Center => self.CenterPosition - new WVec(0, 0, self.CenterPosition.Z - self.World.Map.CenterOfCell(self.Location).Z);
		public int Height => Info.Radius.Length * Info.HeightPercent / 100;
		public WPos Top => Center + new WVec(0, 0, Height);

		/// <summary>Is this point inside (or on) the dome?</summary>
		public bool Inside(WPos p)
		{
			var d = p - Center;
			var r = (long)Info.Radius.Length;
			var h = (long)Height;
			return d.Z < 0 ? (long)d.X * d.X + (long)d.Y * d.Y <= r * r
				: ((long)d.X * d.X + (long)d.Y * d.Y) * h * h + (long)d.Z * d.Z * r * r <= r * r * h * h;
		}

		public bool Covers(WPos ground) => (ground - Center).HorizontalLengthSquared <= (long)Info.Radius.Length * Info.Radius.Length;

		void ITick.Tick(Actor self)
		{
			if (NukeCooldown > 0)
				NukeCooldown--;
			if (Small < Info.SmallCharges && --smallTimer <= 0)
			{
				Small++;
				smallTimer = Info.SmallRecharge;
			}
		}

		int lastChoir = -10000;

		public void Raise()
		{
			if (shown != null && !shown.Done)
			{
				shown.Hold(40);
				return;
			}

			shown = new DomeEffect(self.World, this, false);
			shown.Hold(60);
			self.World.AddFrameEndTask(w => w.Add(shown));
			if (self.World.WorldTick - lastChoir > 400)
			{
				lastChoir = self.World.WorldTick;
				Game.Sound.Play(SoundType.World, Info.ChoirSound);
			}
		}

		public void Absorb(WPos at, WPos source, Player attacker)
		{
			busy = true;
			Raise();
			shown.Charge(Info.AbsorbTicks);
			var w = self.World;

			// The blast spreads over the outside of the dome.
			w.AddFrameEndTask(ww =>
			{
				ww.Add(new SpriteEffect(at, ww, "explosion", "nuke", "effect"));
				for (var i = 0; i < 8; i++)
				{
					var a = WAngle.FromDegrees(i * 45);
					var p = at + new WVec(0, -3072, 0).Rotate(WRot.FromYaw(a)) - new WVec(0, 0, 1024);
					ww.Add(new SpriteEffect(p, ww, "explosion", "building", "effect"));
				}
			});
			Game.Sound.Play(SoundType.World, "kaboom1.aud", at);
			w.WorldActor.TraitsImplementing<FlashPostProcessEffect>().FirstOrDefault(f => f.Info.Type == null)?.Enable(-1);

			for (var i = 0; i < Info.ChatRepeats; i++)
				TextNotificationsManager.AddMissionLine(self.Owner.ResolvedPlayerName, Info.ChatLine, self.Owner.Color);

			w.AddFrameEndTask(ww => ww.Add(new OpenRA.Effects.DelayedAction(Info.AbsorbTicks, () => Fire(source, attacker))));
		}

		void Fire(WPos target, Player attacker)
		{
			busy = false;
			NukeCooldown = Info.NukeRecharge;
			if (self.IsDead || !self.IsInWorld)
				return;

			var w = self.World;
			var from = Top;
			var ground = new WPos(target.X, target.Y, w.Map.CenterOfCell(w.Map.CellContaining(target)).Z);
			w.Add(new VibraniumBeam(w, from, ground + new WVec(0, 0, 256)));
			Game.Sound.Play(SoundType.World, "kaboom25.aud", from);

			// Everything not on our side along the beam's path and around where it lands is destroyed.
			var path = ground - from;
			var len2 = (long)path.X * path.X + (long)path.Y * path.Y;
			var victims = new List<Actor>();
			foreach (var a in w.Actors)
			{
				if (a.IsDead || !a.IsInWorld || a.Owner == null || a.OccupiesSpace == null || a.Info.HasTraitInfo<VibraniumDomeInfo>())
					continue;
				if (a.Owner == self.Owner || a.Owner.RelationshipWith(self.Owner) == PlayerRelationship.Ally)
					continue;
				if (a.TraitOrDefault<IHealth>() == null)
					continue;

				var p = a.CenterPosition;
				if ((p - ground).HorizontalLengthSquared <= (long)Info.BlastRadius.Length * Info.BlastRadius.Length)
				{
					victims.Add(a);
					continue;
				}

				if (len2 == 0 || Covers(p))
					continue;
				var rel = p - from;
				var t = ((long)rel.X * path.X + (long)rel.Y * path.Y) * 1024 / len2;
				if (t < 0 || t > 1024)
					continue;
				var closest = from + new WVec((int)(path.X * t / 1024), (int)(path.Y * t / 1024), 0);
				if ((p - closest).HorizontalLengthSquared <= (long)Info.BeamWidth.Length * Info.BeamWidth.Length)
					victims.Add(a);
			}

			w.AddFrameEndTask(ww =>
			{
				foreach (var a in victims)
					if (!a.IsDead)
						a.Kill(self);

				ww.Add(new SpriteEffect(ground, ww, "explosion", "nuke", "effect"));
				for (var i = 0; i < 6; i++)
				{
					var p = ground + new WVec(0, -1536, 0).Rotate(WRot.FromYaw(WAngle.FromDegrees(i * 60)));
					ww.Add(new SpriteEffect(p, ww, "explosion", "building", "effect"));
				}
			});

			var who = attacker != null ? attacker.ResolvedPlayerName + "'s" : "the enemy's";
			TextNotificationsManager.AddMissionLine(self.Owner.ResolvedPlayerName,
				$"The Vibranium Dome struck back at {who} launch site! ({victims.Count} destroyed)", self.Owner.Color);
		}

		public void Flash(WPos at)
		{
			Small--;
			if (smallTimer <= 0)
				smallTimer = Info.SmallRecharge;
			var w = self.World;
			var ground = new WPos(at.X, at.Y, w.Map.CenterOfCell(w.Map.CellContaining(at)).Z);
			w.AddFrameEndTask(ww =>
			{
				ww.Add(new DomeEffect(ww, ground, true));
				ww.Add(new SpriteEffect(at, ww, "explosion", "med_explosion_air", "effect"));
			});
			Game.Sound.Play(SoundType.World, "kaboom12.aud", at);
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e) { shown?.Drop(); }
		void INotifyActorDisposing.Disposing(Actor self) { shown?.Drop(); }
	}

	// The force field itself (big dome over the generator, or a quick small flash dome).
	sealed class DomeEffect : IEffect, ISpatiallyPartitionable
	{
		const float BigScale = 360f / 128f;
		const float SmallScale = 60f / 40f;

		readonly World world;
		readonly VibraniumDome dome;
		readonly WPos at;
		readonly bool small;
		readonly Sprite shield, glow;
		int ticks, hold = 60, charge;
		bool dropping;

		public bool Done { get; private set; }

		public DomeEffect(World world, VibraniumDome dome, bool small)
			: this(world, dome.Center, small)
		{
			this.dome = dome;
		}

		public DomeEffect(World world, WPos at, bool small)
		{
			this.world = world;
			this.at = at;
			this.small = small;
			var seqs = world.Map.Sequences;
			shield = seqs.GetSequence("vibdomefx", small ? "flash" : "shield").GetSprite(0);
			glow = small ? null : seqs.GetSequence("vibdomefx", "glow").GetSprite(0);
			if (small)
				hold = 12;
			world.ScreenMap.Add(this, at, new Size((int)(shield.Size.X * Scale) + 64, (int)(shield.Size.Y * Scale) + 128));
		}

		float Scale => small ? SmallScale : BigScale;

		public void Charge(int ticks)
		{
			charge = ticks;
			Hold(ticks + 50);
		}

		public void Drop() { dropping = true; }

		/// <summary>Stay up for at least this many more ticks.</summary>
		public void Hold(int more)
		{
			var rise = small ? 8 : 12;
			hold = Math.Max(hold, ticks - rise + more);
		}

		public void Tick(World w)
		{
			ticks++;
			if (charge > 0)
				charge--;
			if (dropping && ticks < 1000)
				ticks = Math.Max(ticks, 1000);
			var end = (small ? 8 : 12) + hold + (small ? 10 : 25);
			if (ticks >= end && ticks < 1000 || ticks >= 1025)
			{
				Done = true;
				w.AddFrameEndTask(ww => { ww.Remove(this); ww.ScreenMap.Remove(this); });
			}
		}

		float Alpha()
		{
			var rise = small ? 8 : 12;
			if (ticks >= 1000)
				return Math.Max(0, 1 - (ticks - 1000) / 25f);
			if (ticks < rise)
				return ticks / (float)rise;
			var fadeStart = rise + hold;
			if (ticks > fadeStart)
				return Math.Max(0, 1 - (ticks - fadeStart) / (small ? 10f : 25f));
			return 1;
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr)
		{
			var a = Alpha();
			if (a <= 0)
				yield break;

			var pal = wr.Palette("effect");
			var anchor = small ? 5 : 13.5f;
			var rise = small ? 0 : (int)((1 - Math.Min(1, ticks / 12f)) * 2048);
			var offset = new WVec(0, 0, (int)(anchor * Scale * 1024 / 24) - rise);
			yield return new SpriteRenderable(shield, at, offset, 2048, pal, Scale, a, Vector3.One, TintModifiers.IgnoreWorldTint, false);

			if (glow != null && (charge > 0 || ticks < 40))
			{
				var prog = charge > 0 ? 1 - charge / (float)(dome?.Info.AbsorbTicks ?? 100) : 0.2f;
				var pulse = 0.5f + 0.5f * (float)Math.Sin(ticks * (0.3 + 0.5 * prog));
				var ga = a * (0.25f + 0.6f * prog) * (0.5f + 0.5f * pulse);
				yield return new SpriteRenderable(glow, at, offset, 2049, pal, Scale, ga, Vector3.One, TintModifiers.IgnoreWorldTint, false);
			}
		}
	}

	// The counter-strike beam: layered glowing beams that flicker, then fade.
	sealed class VibraniumBeam : IEffect
	{
		readonly WPos from, to;
		int ticks;

		public VibraniumBeam(World w, WPos from, WPos to)
		{
			this.from = from;
			this.to = to;
		}

		public void Tick(World w)
		{
			if (++ticks > 40)
				w.AddFrameEndTask(ww => ww.Remove(this));
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr)
		{
			var grow = Math.Min(1f, ticks / 6f);
			var fade = ticks < 28 ? 1f : Math.Max(0, 1 - (ticks - 28) / 12f);
			var len = to - from;
			var shown = new WVec((int)(len.X * grow), (int)(len.Y * grow), (int)(len.Z * grow));
			var flicker = 1 + 0.15f * (float)Math.Sin(ticks * 1.7);
			yield return new BeamRenderable(from, 4096, shown, BeamRenderableShape.Cylindrical, new WDist((int)(1100 * flicker)), Color.FromArgb((int)(70 * fade), 60, 140, 255));
			yield return new BeamRenderable(from, 4097, shown, BeamRenderableShape.Cylindrical, new WDist((int)(560 * flicker)), Color.FromArgb((int)(170 * fade), 110, 200, 255));
			yield return new BeamRenderable(from, 4098, shown, BeamRenderableShape.Cylindrical, new WDist(200), Color.FromArgb((int)(255 * fade), 235, 250, 255));
		}
	}

	[TraitLocation(SystemActors.World)]
	[Desc("Lets Vibranium Domes catch nukes and rockets.")]
	public class VibraniumManagerInfo : TraitInfo
	{
		[Desc("Bullet weapons that count as nuclear (full dome + counter-strike).")]
		public readonly HashSet<string> NuclearBullets = ["subnuke"];

		[Desc("Rocket weapons a quick small dome stops.")]
		public readonly HashSet<string> SmallWeapons = ["scud", "f14v2", "b2v2", "tomahawk", "missilecityrocket"];

		public override object Create(ActorInitializer init) { return new VibraniumManager(this); }
	}

	public class VibraniumManager : ITick, IWorldLoaded
	{
		const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
		static readonly FieldInfo NukeTicks = typeof(NukeLaunch).GetField("ticks", NP);
		static readonly FieldInfo NukeTurn = typeof(NukeLaunch).GetField("turn", NP);
		static readonly FieldInfo NukePos = typeof(NukeLaunch).GetField("pos", NP);
		static readonly FieldInfo NukeTarget = typeof(NukeLaunch).GetField("descendTarget", NP);
		static readonly FieldInfo NukeSource = typeof(NukeLaunch).GetField("ascendSource", NP);
		static readonly FieldInfo NukeOwner = typeof(NukeLaunch).GetField("firedBy", NP);
		static readonly FieldInfo BulletArgs = typeof(Bullet).GetField("Args", NP);
		static readonly FieldInfo BulletPos = typeof(Bullet).GetField("pos", NP);
		static readonly FieldInfo BulletTarget = typeof(Bullet).GetField("target", NP);
		static readonly FieldInfo MissileArgs = typeof(Missile).GetField("args", NP);
		static readonly FieldInfo MissilePos = typeof(Missile).GetField("pos", NP);
		static readonly FieldInfo MissileTarget = typeof(Missile).GetField("targetPosition", NP);

		readonly VibraniumManagerInfo info;
		readonly HashSet<IEffect> handled = [];
		HashSet<WeaponInfo> nuclear = [], small = [];

		public int Absorbed { get; private set; }
		public int Blocked { get; private set; }

		public VibraniumManager(VibraniumManagerInfo info) { this.info = info; }

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			var weapons = w.Map.Rules.Weapons;
			nuclear = info.NuclearBullets.Where(weapons.ContainsKey).Select(n => weapons[n]).ToHashSet();
			small = info.SmallWeapons.Where(weapons.ContainsKey).Select(n => weapons[n]).ToHashSet();
		}

		static bool Hostile(VibraniumDome d, Actor dome, Player firer) =>
			firer == null || (firer != dome.Owner && firer.RelationshipWith(dome.Owner) != PlayerRelationship.Ally);

		void ITick.Tick(Actor self)
		{
			var w = self.World;
			handled.RemoveWhere(e => !w.Effects.Contains(e));
			List<(Actor A, VibraniumDome D)> domes = null;

			foreach (var e in w.Effects.ToList())
			{
				if (handled.Contains(e))
					continue;

				bool nuke;
				WPos pos, target, source;
				Player firer;
				if (e is NukeLaunch n)
				{
					if ((int)NukeTicks.GetValue(n) < (int)NukeTurn.GetValue(n))
						continue;
					nuke = true;
					pos = (WPos)NukePos.GetValue(n);
					target = (WPos)NukeTarget.GetValue(n);
					source = (WPos)NukeSource.GetValue(n);
					firer = (Player)NukeOwner.GetValue(n);
				}
				else if (e is Bullet b && BulletArgs.GetValue(b) is ProjectileArgs ba && (nuclear.Contains(ba.Weapon) || small.Contains(ba.Weapon)))
				{
					nuke = nuclear.Contains(ba.Weapon);
					pos = (WPos)BulletPos.GetValue(b);
					target = (WPos)BulletTarget.GetValue(b);
					source = ba.Source;
					firer = ba.SourceActor?.Owner;
				}
				else if (e is Missile m && MissileArgs.GetValue(m) is ProjectileArgs ma && small.Contains(ma.Weapon))
				{
					nuke = false;
					pos = (WPos)MissilePos.GetValue(m);
					target = (WPos)MissileTarget.GetValue(m);
					source = ma.Source;
					firer = ma.SourceActor?.Owner;
				}
				else
					continue;

				domes ??= w.ActorsWithTrait<VibraniumDome>().Where(x => x.Actor.IsInWorld && !x.Actor.IsDead).Select(x => (x.Actor, x.Trait)).ToList();
				if (domes.Count == 0)
					return;

				foreach (var (a, d) in domes)
				{
					if (!Hostile(d, a, firer) || !d.Covers(target))
						continue;

					if (nuke)
					{
						if (d.Covers(source))
							continue;
						if (!d.NukeReady)
							continue;

						// Put the shield up as soon as the warhead is on its way down, catch it on the dome.
						d.Raise();
						if (!d.Inside(pos))
							break;

						handled.Add(e);
						Absorbed++;
						var gone = e;
						w.AddFrameEndTask(ww => { ww.Remove(gone); ww.ScreenMap.Remove(gone); });
						d.Absorb(pos, source, firer);
						break;
					}

					// Rockets are caught just before they land, wherever they were fired from.
					if (d.Small > 0 && (pos - target).HorizontalLengthSquared <= 2048L * 2048)
					{
						handled.Add(e);
						Blocked++;
						var gone = e;
						w.AddFrameEndTask(ww => { ww.Remove(gone); ww.ScreenMap.Remove(gone); });
						d.Flash(pos);
						break;
					}
				}
			}
		}
	}
}
