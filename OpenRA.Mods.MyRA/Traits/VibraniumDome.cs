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

		/// <summary>Who should see the big on-screen banners: the dome's side and whoever attacked it.</summary>
		bool ConcernsViewer(Player attacker)
		{
			var lp = self.World.LocalPlayer;
			if (lp == null)
				return true;
			return lp == self.Owner || lp.RelationshipWith(self.Owner) == PlayerRelationship.Ally
				|| (attacker != null && (lp == attacker || lp.RelationshipWith(attacker) == PlayerRelationship.Ally));
		}

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
			self.World.WorldActor.TraitOrDefault<ScreenShaker>()?.AddEffect(15, Center, 3);
			Game.Sound.Play(SoundType.World, "ironcur9.aud", Center);
			if (ConcernsViewer(null))
				VibraniumBanner.Show("SHIELD ONLINE", Color.FromArgb(150, 220, 255), 50);
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
			shown.Absorb(Info.AbsorbTicks, at);
			var w = self.World;

			// The warhead goes off on top of the dome: fireball squashed flat, fire pouring down the sides.
			var top = Top;
			w.AddFrameEndTask(ww =>
			{
				ww.Add(new SpriteEffect(top, ww, "explosion", "nuke", "effect"));
				for (var ring = 1; ring <= 4; ring++)
				{
					var rr = Info.Radius.Length * ring / 5;
					var z = Height * (int)Math.Sqrt(Math.Max(0, 25 - ring * ring)) / 5;
					var n = 4 + ring * 3;
					for (var i = 0; i < n; i++)
					{
						var p = Center + new WVec(0, -rr, 0).Rotate(WRot.FromYaw(WAngle.FromDegrees(i * 360 / n + ring * 17))) + new WVec(0, 0, z);
						var seq = ring < 3 ? "building" : "napalm";
						ww.Add(new OpenRA.Effects.DelayedAction(ring * 6, () => ww.Add(new SpriteEffect(p, ww, "explosion", seq, "effect"))));
					}
				}
			});
			Game.Sound.Play(SoundType.World, "kaboom1.aud", at);
			w.WorldActor.TraitsImplementing<FlashPostProcessEffect>().FirstOrDefault(f => f.Info.Type == null)?.Enable(-1);
			w.WorldActor.TraitOrDefault<ScreenShaker>()?.AddEffect(30, Center, 10);

			// A ring of scorched, burning ground right outside the dome: everything inside was spared.
			var smudges = w.WorldActor.TraitsImplementing<SmudgeLayer>().FirstOrDefault(l => l.Info.Type == "Scorch");
			var map = w.Map;
			var edge = Info.Radius.Length + 512;
			for (var i = 0; i < 72; i++)
			{
				var p = Center + new WVec(0, -edge - (i % 3) * 384, 0).Rotate(WRot.FromYaw(WAngle.FromDegrees(i * 5)));
				var c = map.CellContaining(p);
				if (map.Contains(c))
					smudges?.AddSmudge(c);
			}

			w.AddFrameEndTask(ww => ww.Add(new FireRing(ww, Center, edge, 250)));

			for (var i = 0; i < Info.ChatRepeats; i++)
				TextNotificationsManager.AddMissionLine(self.Owner.ResolvedPlayerName, Info.ChatLine, self.Owner.Color);

			// Countdown, then the counterstrike.
			if (ConcernsViewer(attacker))
			{
				var lp = w.LocalPlayer;
				var targeted = lp != null && attacker != null && (lp == attacker || lp.RelationshipWith(attacker) == PlayerRelationship.Ally);
				var secs = Math.Max(1, Info.AbsorbTicks / 25);
				for (var k = 0; k < secs; k++)
				{
					var left = secs - k;
					w.AddFrameEndTask(ww => ww.Add(new OpenRA.Effects.DelayedAction(k * 25 + 1, () =>
						VibraniumBanner.Show(targeted ? $"INCOMING VIBRANIUM STRIKE IN {left}..." : $"VIBRANIUM COUNTERSTRIKE IN {left}...",
							targeted ? Color.FromArgb(255, 80, 70) : Color.FromArgb(225, 195, 255), 24))));
				}
			}

			w.AddFrameEndTask(ww => ww.Add(new OpenRA.Effects.DelayedAction(Info.AbsorbTicks, () => Fire(source, attacker))));
		}

		void Fire(WPos target, Player attacker)
		{
			busy = false;
			NukeCooldown = Info.NukeRecharge;
			if (self.IsDead || !self.IsInWorld)
				return;

			var w = self.World;
			var ground = new WPos(target.X, target.Y, w.Map.CenterOfCell(w.Map.CellContaining(target)).Z);

			// 1. The beam blasts straight up out of the dome into the sky.
			w.Add(new SkyBeam(w, Top, true));
			Game.Sound.Play(SoundType.World, "ironcur9.aud", Top);
			w.WorldActor.TraitOrDefault<ScreenShaker>()?.AddEffect(20, Center, 4);

			// 2. The attacker's camera is taken to their launch site to watch it come down.
			var lp = w.LocalPlayer;
			if (lp != null && attacker != null && (lp == attacker || lp.RelationshipWith(attacker) == PlayerRelationship.Ally))
				w.AddFrameEndTask(ww => ww.Add(new OpenRA.Effects.DelayedAction(12, () => SniperScopeUI.WorldRenderer?.Viewport.Center(ground))));

			// 3. A column of light slams down from the sky onto the launch site, then one big blast.
			w.AddFrameEndTask(ww => ww.Add(new OpenRA.Effects.DelayedAction(25, () =>
			{
				ww.Add(new SkyBeam(ww, ground, false));
				Game.Sound.Play(SoundType.World, "ironcur9.aud", ground);
			})));
			w.AddFrameEndTask(ww => ww.Add(new OpenRA.Effects.DelayedAction(37, () => Strike(ground, attacker))));
		}

		void Strike(WPos ground, Player attacker)
		{
			var w = self.World;
			var victims = w.FindActorsInCircle(ground, Info.BlastRadius)
				.Where(a => !a.IsDead && a.IsInWorld && a.Owner != null && a.Owner != self.Owner
					&& a.Owner.RelationshipWith(self.Owner) != PlayerRelationship.Ally && a.TraitOrDefault<IHealth>() != null
					&& !a.Info.HasTraitInfo<VibraniumDomeInfo>())
				.ToList();
			foreach (var a in victims)
				a.Kill(self);

			w.Add(new SpriteEffect(ground, w, "explosion", "nuke", "effect"));
			Game.Sound.Play(SoundType.World, "kaboom1.aud", ground);
			w.WorldActor.TraitsImplementing<FlashPostProcessEffect>().FirstOrDefault(f => f.Info.Type == null)?.Enable(-1);
			w.WorldActor.TraitOrDefault<ScreenShaker>()?.AddEffect(35, ground, 12);
			w.Add(new Shockwave(w, ground, Info.BlastRadius.Length * 3));

			var craters = w.WorldActor.TraitsImplementing<SmudgeLayer>().FirstOrDefault(l => l.Info.Type == "Crater");
			var cells = w.Map.FindTilesInCircle(w.Map.CellContaining(ground), Math.Max(1, Info.BlastRadius.Length / 1024 - 1));
			foreach (var c in cells)
				craters?.AddSmudge(c);

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

	// The force field (big dome over the generator, or a quick small flash dome).
	sealed class DomeEffect : IEffect, ISpatiallyPartitionable, IEffectAnnotation
	{
		const float BigScale = 360f / 128f;
		const float SmallScale = 60f / 40f;
		const int Grow = 36;

		readonly World world;
		readonly VibraniumDome dome;
		readonly WPos at;
		readonly bool small;
		readonly Sprite shield, glow;
		readonly OpenRA.Support.MersenneTwister rng = new();
		int ticks, hold = 60, charge, chargeLength = 1, impactAt = -1000;
		WPos impact;
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
			world.ScreenMap.Add(this, at, new Size((int)(shield.Size.X * MaxScale) + 64, (int)(shield.Size.Y * MaxScale) + 128));
		}

		float MaxScale => small ? SmallScale : BigScale;

		// Inflates out of the generator.
		float Growth => small ? 1 : Math.Min(1, EaseInOut(ticks / (float)Grow));
		static float EaseInOut(float t) => t * t * (3 - 2 * t);

		public void Absorb(int ticks, WPos where)
		{
			charge = chargeLength = ticks;
			impact = where;
			impactAt = this.ticks;
			Hold(ticks + 50);
		}

		public void Drop() { dropping = true; }

		public void Hold(int more)
		{
			var rise = small ? 8 : Grow;
			hold = Math.Max(hold, ticks - rise + more);
		}

		public void Tick(World w)
		{
			ticks++;
			if (charge > 0)
				charge--;
			if (dropping && ticks < 1000)
				ticks = 1000;
			var rise = small ? 8 : Grow;
			var end = rise + hold + (small ? 10 : 25);
			if ((ticks >= end && ticks < 1000) || ticks >= 1025)
			{
				Done = true;
				w.AddFrameEndTask(ww => { ww.Remove(this); ww.ScreenMap.Remove(this); });
			}
		}

		float Alpha()
		{
			var rise = small ? 8 : Grow;
			if (ticks >= 1000)
				return Math.Max(0, 1 - (ticks - 1000) / 25f);
			if (small && ticks < rise)
				return ticks / (float)rise;
			var fadeStart = rise + hold;
			if (ticks > fadeStart)
				return Math.Max(0, 1 - (ticks - fadeStart) / (small ? 10f : 25f));
			return 1;
		}

		WVec Offset(float scale) => new(0, 0, (int)((small ? 5 : 13.5f) * scale * 1024 / 24));

		public IEnumerable<IRenderable> Render(WorldRenderer wr)
		{
			var a = Alpha();
			if (a <= 0)
				yield break;

			var pal = wr.Palette("effect");
			var scale = MaxScale * Math.Max(0.02f, Growth);
			var offset = Offset(scale);
			yield return new SpriteRenderable(shield, at, offset, 2048, pal, scale, a, Vector3.One, TintModifiers.IgnoreWorldTint, false);
			if (glow == null)
				yield break;

			// Charging up: blue, then violet, then white-hot, pulsing faster and faster.
			var prog = charge > 0 ? 1 - charge / (float)chargeLength : 0;
			var sinceImpact = ticks - impactAt;
			if (charge > 0 || ticks < Grow + 10 || sinceImpact < 30)
			{
				var speed = 0.25f + 1.1f * prog;
				var pulse = 0.5f + 0.5f * (float)Math.Sin(ticks * speed);
				var ga = a * (0.3f + 0.7f * prog) * (0.55f + 0.45f * pulse);
				if (sinceImpact >= 0 && sinceImpact < 30)
					ga = Math.Max(ga, a * (1 - sinceImpact / 30f));
				var tint = prog < 0.6f ? Vector3.Lerp(Vector3.One, new Vector3(1.25f, 0.62f, 1.1f), prog / 0.6f)
					: Vector3.Lerp(new Vector3(1.25f, 0.62f, 1.1f), new Vector3(1.6f, 1.4f, 1.3f), (prog - 0.6f) / 0.4f);
				yield return new SpriteRenderable(glow, at, offset, 2049, pal, scale, Math.Min(1, ga), tint, TintModifiers.IgnoreWorldTint, false);
				if (prog > 0.65f)
					yield return new SpriteRenderable(glow, at, offset, 2050, pal, scale, Math.Min(1, ga * (prog - 0.65f) * 2.5f), Vector3.One, TintModifiers.IgnoreWorldTint, false);
			}

			if (dome == null)
				yield break;

			var r = dome.Info.Radius.Length * Math.Max(0.02f, Growth);
			var h = dome.Height * Math.Max(0.02f, Growth);
			var crystal = at + new WVec(0, 0, 1800);

			// Lightning from the crystal to the dome while it forms.
			if (ticks < Grow + 6)
				for (var b = 0; b < 3; b++)
				{
					var ang = WAngle.FromDegrees(rng.Next(360));
					var dist = r * (0.5f + 0.4f * rng.NextFloat());
					var end = at + new WVec(0, -(int)dist, 0).Rotate(WRot.FromYaw(ang)) + new WVec(0, 0, (int)(h * Math.Sqrt(Math.Max(0, 1 - dist * dist / (r * r)))));
					foreach (var seg in Bolt(crystal, end, 6))
						yield return seg;
				}

			// Energy spiralling in to the crystal while it charges.
			if (charge > 0)
			{
				var n = (int)(10 + 24 * prog);
				for (var i = 0; i < n; i++)
				{
					var ph = (ticks * (0.02f + 0.03f * prog) + i / 17f) % 1f;
					var ang = i * 2.4 + ph * 6;
					var rr = r * 0.9f * (1 - ph);
					var p = at + new WVec((int)(Math.Cos(ang) * rr), (int)(Math.Sin(ang) * rr), (int)(h * 0.6f * (1 - ph) + 1800 * ph));
					var dir = crystal - p;
					var len = dir.Length;
					var step = len > 0 ? dir * Math.Min(len, 700) / len : WVec.Zero;
					yield return new BeamRenderable(p, 2060, step, BeamRenderableShape.Cylindrical, new WDist(90), Color.FromArgb((int)(230 * ph), 235, 240, 255));
				}
			}
		}

		IEnumerable<IRenderable> Bolt(WPos from, WPos to, int segments)
		{
			var prev = from;
			for (var i = 1; i <= segments; i++)
			{
				var t = i / (float)segments;
				var next = i == segments ? to : from + (to - from) * i / segments + new WVec(rng.Next(-700, 700), rng.Next(-700, 700), rng.Next(-400, 400));
				yield return new BeamRenderable(prev, 2070, next - prev, BeamRenderableShape.Flat, new WDist(70), Color.FromArgb(230, 215, 240, 255));
				prev = next;
			}
		}

		public IEnumerable<IRenderable> RenderAnnotation(WorldRenderer wr)
		{
			// Blue shockwave ring rolling out along the ground as the dome goes up.
			if (small || dome == null || ticks > 18)
				yield break;
			var u = ticks / 18f;
			var rr = new WDist((int)(dome.Info.Radius.Length * (0.15f + 1.05f * u)));
			yield return new RangeCircleAnnotationRenderable(at, rr, 0, Color.FromArgb((int)(230 * (1 - u)), 160, 220, 255), 4, Color.FromArgb((int)(120 * (1 - u)), 255, 255, 255), 2);
		}
	}

	// A column of light: shooting up out of the dome, or slamming down from the sky.
	sealed class SkyBeam : IEffect
	{
		readonly WPos at;
		readonly bool up;
		readonly OpenRA.Support.MersenneTwister rng = new();
		int ticks;
		const int Height = 60 * 1024;

		public SkyBeam(World w, WPos at, bool up)
		{
			this.at = at;
			this.up = up;
		}

		public void Tick(World w)
		{
			if (++ticks > (up ? 22 : 40))
				w.AddFrameEndTask(ww => ww.Remove(this));
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr)
		{
			var grow = Math.Min(1f, ticks / (up ? 8f : 6f));
			var life = up ? 22f : 40f;
			var fade = ticks < life - 12 ? 1f : Math.Max(0, (life - ticks) / 12f);
			var flicker = 1 + 0.15f * (float)Math.Sin(ticks * 1.9);
			WPos from;
			WVec len;
			if (up)
			{
				from = at;
				len = new WVec(0, 0, (int)(Height * grow));
			}
			else
			{
				from = at + new WVec(0, 0, Height);
				len = new WVec(0, 0, -(int)(Height * grow));
			}

			var w0 = up ? 1.0f : 1.8f;
			yield return new BeamRenderable(from, 4096, len, BeamRenderableShape.Cylindrical, new WDist((int)(1500 * w0 * flicker)), Color.FromArgb((int)(70 * fade), 110, 80, 255));
			yield return new BeamRenderable(from, 4097, len, BeamRenderableShape.Cylindrical, new WDist((int)(850 * w0 * flicker)), Color.FromArgb((int)(150 * fade), 150, 200, 255));
			yield return new BeamRenderable(from, 4098, len, BeamRenderableShape.Cylindrical, new WDist((int)(400 * w0)), Color.FromArgb((int)(230 * fade), 230, 245, 255));
			yield return new BeamRenderable(from, 4099, len, BeamRenderableShape.Cylindrical, new WDist((int)(160 * w0)), Color.FromArgb((int)(255 * fade), 255, 255, 255));

			// Lightning crackling around the column.
			if (!up)
				for (var b = 0; b < 3; b++)
				{
					var x0 = rng.Next(-1800, 1800);
					var prev = at + new WVec(x0, 0, Height / 3);
					for (var i = 1; i <= 6; i++)
					{
						var next = at + new WVec(x0 * (6 - i) / 6 + rng.Next(-900, 900), rng.Next(-500, 500), Height / 3 * (6 - i) / 6);
						yield return new BeamRenderable(prev, 4100, next - prev, BeamRenderableShape.Flat, new WDist(70), Color.FromArgb((int)(220 * fade), 215, 240, 255));
						prev = next;
					}
				}
		}
	}

	// A shockwave ring racing out along the ground.
	sealed class Shockwave : IEffect, IEffectAnnotation
	{
		readonly WPos at;
		readonly int radius;
		int ticks;

		public Shockwave(World w, WPos at, int radius)
		{
			this.at = at;
			this.radius = radius;
		}

		public void Tick(World w)
		{
			if (++ticks > 20)
				w.AddFrameEndTask(ww => ww.Remove(this));
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr) { yield break; }

		public IEnumerable<IRenderable> RenderAnnotation(WorldRenderer wr)
		{
			var u = ticks / 20f;
			yield return new RangeCircleAnnotationRenderable(at, new WDist((int)(radius * u) + 256), 0,
				Color.FromArgb((int)(220 * (1 - u)), 255, 235, 200), 5, Color.FromArgb((int)(120 * (1 - u)), 255, 160, 60), 3);
		}
	}

	// Flames burning in a ring just outside the dome for a while after the hit.
	sealed class FireRing : IEffect
	{
		readonly WPos center;
		readonly int radius;
		readonly OpenRA.Support.MersenneTwister rng = new();
		int ticks;
		readonly int life;

		public FireRing(World w, WPos center, int radius, int life)
		{
			this.center = center;
			this.radius = radius;
			this.life = life;
		}

		public void Tick(World w)
		{
			if (++ticks > life)
			{
				w.AddFrameEndTask(ww => ww.Remove(this));
				return;
			}

			if (ticks % 3 != 0)
				return;
			for (var i = 0; i < 3; i++)
			{
				var p = center + new WVec(0, -(radius + rng.Next(0, 900)), 0).Rotate(WRot.FromYaw(new WAngle(rng.Next(1024))));
				w.AddFrameEndTask(ww => ww.Add(new SpriteEffect(p, ww, "fire", (1 + rng.Next(4)).ToString(), "effect")));
			}
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr) { yield break; }
	}

	// Big text across the middle of the screen for a moment.
	public sealed class VibraniumBanner : OpenRA.Widgets.Widget
	{
		static VibraniumBanner current;
		readonly string text;
		readonly Color color;
		readonly long until;

		VibraniumBanner(string text, Color color, int ticks)
		{
			this.text = text;
			this.color = color;
			until = Game.RunTime + ticks * 40;
			var res = Game.Renderer.Resolution;
			Bounds = new OpenRA.Widgets.WidgetBounds(0, res.Height / 5, res.Width, 60);
		}

		public static void Show(string text, Color color, int ticks)
		{
			var b = new VibraniumBanner(text, color, ticks);
			Game.RunAfterTick(() =>
			{
				if (current != null)
					OpenRA.Widgets.Ui.Root.RemoveChild(current);
				current = b;
				OpenRA.Widgets.Ui.Root.AddChild(b);
			});
		}

		public override void Tick()
		{
			if (Game.RunTime > until)
				Game.RunAfterTick(() =>
				{
					OpenRA.Widgets.Ui.Root.RemoveChild(this);
					if (current == this)
						current = null;
				});
		}

		public override bool HandleMouseInput(OpenRA.MouseInput mi) => false;
		public override bool EventBoundsContains(int2 location) => false;

		public override void Draw()
		{
			var font = Game.Renderer.Fonts["Title"];
			var size = font.Measure(text);
			var res = Game.Renderer.Resolution;
			var pos = new Vector2((res.Width - size.X) / 2, res.Height / 5);
			font.DrawTextWithShadow(text, pos, color, Color.Black, 2);
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
