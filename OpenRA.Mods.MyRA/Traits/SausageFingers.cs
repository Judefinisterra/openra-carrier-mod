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
using OpenRA.Effects;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// ================================================================================================
	// Sausage Fingers (Israel's superweapon, from the Slap Shrine): click a base. A hand bigger than the whole base
	// swoops in from the west and SLAPS it. Every building under the palm and fingers is flattened; every unit
	// nearby goes flying off to the east and lands hurt. The screen shakes, there's an almighty slap, and a voice
	// yells "SAUSAGE FINGERS!!!!" while the words burst onto the screen comic-book style.
	// ================================================================================================
	[Desc("Support power: a giant hand slaps the target area, destroying buildings and flinging units sideways.")]
	public class SausageFingersPowerInfo : SupportPowerInfo
	{
		[Desc("Buildings whose centre is within this ellipse of the target are destroyed (half width, half height).")]
		public readonly WVec KillRadius = new(13312, 10240, 0);

		[Desc("Units within this distance of the target are flung.")]
		public readonly WDist FlingRadius = WDist.FromCells(16);

		[Desc("How far units fly (min, max), in cells.")]
		public readonly int MinFling = 10;
		public readonly int MaxFling = 20;

		[Desc("Percentage of their full health the flung units lose when they land.")]
		public readonly int LandingDamagePercent = 40;

		public readonly string Image = "slaphand";

		[PaletteReference]
		public readonly string HandPalette = "effect";

		public readonly string SlapSound = "slap.wav";

		public readonly string VoiceSound = "sausagefingers.wav";

		public override object Create(ActorInitializer init) { return new SausageFingersPower(init.Self, this); }
	}

	public class SausageFingersPower : SupportPower
	{
		readonly SausageFingersPowerInfo info;

		public SausageFingersPower(Actor self, SausageFingersPowerInfo info)
			: base(self, info)
		{
			this.info = info;
		}

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			// Needs somewhere to aim (an order sent without a target would crash the game).
			if (order.Target.Type == TargetType.Invalid)
				return;

			base.Activate(self, order, manager);
			PlayLaunchSounds();

			var w = self.World;
			var target = order.Target.CenterPosition;
			w.AddFrameEndTask(ww => ww.Add(new SlapEffect(self, info, target)));
			var who = self.Owner.ResolvedPlayerName;
			w.AddFrameEndTask(ww =>
			{
				if (ww.LocalPlayer != null)
					TextNotificationsManager.AddSystemLine("Sausage Fingers", $"{who} has unleashed the SAUSAGE FINGERS!");
			});
		}
	}

	// The hand: swoops in, slaps, lingers, lifts away. Also flies the flung units along their arcs.
	public class SlapEffect : IEffect, IEffectAboveShroud
	{
		const int SwoopTicks = 40;
		const int HoldTicks = 40;
		const int LiftTicks = 35;
		const int FlightTicks = 32;
		const int TextTicks = 95;

		readonly Actor attacker;
		readonly SausageFingersPowerInfo info;
		readonly WPos target;
		readonly World world;
		readonly List<Flight> flights = [];
		int ticks;

		sealed class Flight
		{
			public Actor Actor;
			public Mobile Mobile;
			public WPos From;
			public WPos To;
			public CPos Cell;
			public int Height;
		}

		public SlapEffect(Actor attacker, SausageFingersPowerInfo info, WPos target)
		{
			this.attacker = attacker;
			this.info = info;
			this.target = target;
			world = attacker.World;
		}

		public void Tick(World w)
		{
			ticks++;
			if (ticks == SwoopTicks)
				Slap(w);

			foreach (var f in flights)
				Fly(f, ticks - SwoopTicks);

			if (ticks > SwoopTicks + Math.Max(Math.Max(HoldTicks + LiftTicks, FlightTicks), TextTicks) + 1)
				w.AddFrameEndTask(ww => ww.Remove(this));
		}

		void Slap(World w)
		{
			Game.Sound.Play(SoundType.UI, info.SlapSound);
			Game.Sound.Play(SoundType.UI, info.VoiceSound);
			w.WorldActor.TraitOrDefault<ScreenShaker>()?.AddEffect(60, target, 40, new Vector2(1.6f, 0.6f));

			var map = w.Map;
			var taken = new HashSet<CPos>();
			var flattened = 0;
			var flung = 0;
			foreach (var a in w.Actors.ToList())
			{
				if (a.IsDead || !a.IsInWorld || a.Owner.NonCombatant || a.OccupiesSpace == null)
					continue;

				var d = a.CenterPosition - target;
				if (a.Info.HasTraitInfo<BuildingInfo>())
				{
					var ex = (long)d.X * d.X * info.KillRadius.Y * info.KillRadius.Y
						+ (long)d.Y * d.Y * info.KillRadius.X * info.KillRadius.X;
					if (ex <= (long)info.KillRadius.X * info.KillRadius.X * info.KillRadius.Y * info.KillRadius.Y)
					{
						a.Kill(attacker);
						flattened++;
					}

					continue;
				}

				if (d.HorizontalLengthSquared > info.FlingRadius.LengthSquared || a.Info.HasTraitInfo<AircraftInfo>())
					continue;

				var mobile = a.TraitOrDefault<Mobile>();
				if (mobile == null)
					continue;

				// Swatted east: further for things near the palm, with some scatter north-south.
				var dist = w.SharedRandom.Next(info.MinFling, info.MaxFling + 1);
				var drift = w.SharedRandom.Next(-3, 4);
				var aim = map.CellContaining(a.CenterPosition) + new CVec(dist, drift);
				var land = map.FindTilesInCircle(aim, 5)
					.Where(c => map.Contains(c) && !taken.Contains(c) && mobile.CanEnterCell(c, a))
					.Cast<CPos?>().FirstOrDefault();

				a.CancelActivity();
				if (land == null)
				{
					Hurt(a);
					continue;
				}

				taken.Add(land.Value);
				var from = a.CenterPosition;
				mobile.SetPosition(a, land.Value);
				var to = a.CenterPosition;
				mobile.SetCenterPosition(a, from);
				flights.Add(new Flight
				{
					Actor = a,
					Mobile = mobile,
					From = from,
					To = to,
					Cell = land.Value,
					Height = 1024 * (2 + dist / 3),
				});
				flung++;
			}

			var by = attacker.Owner.ResolvedPlayerName;
			w.AddFrameEndTask(ww =>
			{
				if (ww.LocalPlayer != null)
					TextNotificationsManager.AddSystemLine("Sausage Fingers",
						$"SLAP! {by}'s hand flattened {flattened} buildings and sent {flung} units flying.");
			});
		}

		void Fly(Flight f, int t)
		{
			if (t < 0 || t > FlightTicks || f.Actor.IsDead || !f.Actor.IsInWorld)
				return;

			if (t == FlightTicks)
			{
				f.Mobile.SetPosition(f.Actor, f.Cell);
				Hurt(f.Actor);
				return;
			}

			var p = WPos.Lerp(f.From, f.To, t, FlightTicks);
			var z = (int)(4L * f.Height * t * (FlightTicks - t) / (FlightTicks * FlightTicks));
			f.Mobile.SetCenterPosition(f.Actor, p + new WVec(0, 0, z));
		}

		void Hurt(Actor a)
		{
			var health = a.TraitOrDefault<IHealth>();
			if (health == null || a.IsDead)
				return;

			a.InflictDamage(attacker, new Damage(health.MaxHP * info.LandingDamagePercent / 100));
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr) => [];

		// Drawn above everything, shroud included: it's a hand coming down from the sky.
		public IEnumerable<IRenderable> RenderAboveShroud(WorldRenderer wr)
		{
			var seqs = world.Map.Sequences;
			var palette = wr.Palette(info.HandPalette);
			var lift = ticks - SwoopTicks - HoldTicks;

			// Swoop: comes in from the upper west, high and big, and drops onto the target. Lift: rises and fades.
			float along, height, alpha;
			if (ticks < SwoopTicks)
			{
				var s = ticks / (float)SwoopTicks;
				var e = s * s;
				along = 1 - e;
				height = 1 - e;
				alpha = Math.Min(1f, ticks / 8f);
			}
			else if (lift <= 0)
			{
				along = 0;
				height = 0;
				alpha = 1;
			}
			else
			{
				var s = Math.Min(1f, lift / (float)LiftTicks);
				along = -0.3f * s;
				height = s;
				alpha = 1 - s;
			}

			// Comic-book lettering bursts out on impact: pops up big, bounces, wobbles, fades.
			var text = ticks - SwoopTicks;
			IRenderable lettering = null;
			if (text >= 0 && text < TextTicks)
			{
				var pop = text < 5 ? 1.45f * text / 5f
					: text < 12 ? 1.45f - 0.5f * (text - 5) / 7f
					: 0.95f + 0.05f * MathF.Sin(text * 0.35f);
				var wobble = new WAngle((int)(14 * MathF.Sin(text * 0.5f)) & 1023);
				var fade = Math.Min(1f, (TextTicks - text) / 15f);
				lettering = new SpriteRenderable(seqs.GetSequence(info.Image, "text").GetSprite(0), target + new WVec(0, -2048, 0),
					WVec.Zero, 0, palette, 1.6f * pop, fade, Vector3.One, TintModifiers.IgnoreWorldTint, true, wobble);
			}

			if (alpha <= 0.01f)
			{
				if (lettering != null)
					yield return lettering;

				yield break;
			}

			var offset = new WVec((int)(-30 * 1024 * along), (int)(-5 * 1024 * along), (int)(24 * 1024 * height));
			var scale = 1f + 0.5f * height;
			var ground = new WPos(target.X + offset.X, target.Y + offset.Y, 0);

			var shadow = seqs.GetSequence(info.Image, "shadow").GetSprite(0);
			yield return new SpriteRenderable(shadow, ground, WVec.Zero, 0, palette, 1f + 0.2f * height, alpha * (1 - 0.6f * height),
				Vector3.One, TintModifiers.IgnoreWorldTint, true);

			var hand = seqs.GetSequence(info.Image, "idle").GetSprite(0);
			yield return new SpriteRenderable(hand, ground + new WVec(0, 0, offset.Z), WVec.Zero, 0, palette, scale, alpha,
				Vector3.One, TintModifiers.IgnoreWorldTint, true);

			if (lettering != null)
				yield return lettering;
		}
	}
}
