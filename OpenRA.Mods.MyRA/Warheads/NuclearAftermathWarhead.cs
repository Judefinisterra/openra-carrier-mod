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
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenRA.Effects;
using OpenRA.GameRules;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Warheads;
using OpenRA.Mods.MyRA.Traits;
using OpenRA.Primitives;
using OpenRA.Support;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Warheads
{
	// ================================================================================================
	// A nuke, played out over time (the flash and screen shake stay in the weapon):
	//
	//   1. a shockwave races out to BlastRadius. Everything it passes takes PeakDamage, halved for every
	//      HalfDamageDistance from ground zero, so the centre is wiped out and the edge barely scratched;
	//   2. structures it reaches catch fire and burn, faster the closer they stood;
	//   3. a huge see-through mushroom cloud rises: a fireball, a thick column boiling up into a rolling cap,
	//      and a dust ring along the shockwave. It hangs over the site and drifts apart;
	//   4. radiation lingers, killing infantry near ground zero;
	//   5. a crater is left at ground zero for good.
	//
	// Burning and radiation stop when the NuclearGrade colour scheme ends (RadiationDuration without one).
	// All damage uses integer maths and the shared random so every client in a multiplayer game agrees.
	// The cloud is cosmetic and uses the local random.
	// ================================================================================================
	[Desc("Shockwave with exponential falloff, burning structures, a mushroom cloud, radiation and a crater.")]
	public class NuclearAftermathWarhead : Warhead
	{
		[Desc("Radius the shockwave travels to.")]
		public readonly WDist BlastRadius = WDist.FromCells(54);

		[Desc("Ticks the shockwave takes to reach BlastRadius.")]
		public readonly int ShockwaveDuration = 72;

		[Desc("Damage at ground zero.")]
		public readonly int PeakDamage = 300000;

		[Desc("Damage halves for every this much distance from ground zero.")]
		public readonly WDist HalfDamageDistance = WDist.FromCells(4);

		[Desc("Damage percentage by armor type.")]
		public readonly FrozenDictionary<string, int> Versus = FrozenDictionary<string, int>.Empty;

		public readonly BitSet<DamageType> DamageTypes = new("Prone50Percent", "TriggerProne", "FireDeath", "Incendiary");

		[Desc("Target types the shockwave hurts.")]
		public readonly BitSet<TargetableType> DamageTargets = new("GroundActor", "WaterActor", "Underwater", "AirborneActor", "Trees");

		[Desc("Target types that burn.")]
		public readonly BitSet<TargetableType> BurnTargets = new("Structure");

		[Desc("Each burn tick at ground zero takes this many thousandths of a structure's full health.")]
		public readonly int BurnPerMille = 50;

		[Desc("Burn speed halves for every this much distance from ground zero.")]
		public readonly WDist HalfBurnDistance = WDist.FromCells(8);

		[Desc("Structures that would burn slower than this many thousandths a burn tick don't catch fire.")]
		public readonly int MinBurnPerMille = 5;

		[Desc("Ticks between burn damage on a burning structure.")]
		public readonly int BurnInterval = 25;

		[Desc("Burning structures show this many flames.")]
		public readonly int FlamesPerStructure = 3;

		public readonly BitSet<DamageType> BurnDamageTypes = new("Incendiary");

		[Desc("Infantry inside this radius take radiation damage.")]
		public readonly WDist RadiationRadius = WDist.FromCells(16);

		[Desc("Ticks burning and radiation last when the world has no NuclearGrade.")]
		public readonly int RadiationDuration = 1500;

		[Desc("Ticks between radiation damage.")]
		public readonly int RadiationInterval = 10;

		[Desc("Radiation damage at ground zero when it starts. Halves every HalfRadiationDistance and fades with time.")]
		public readonly int RadiationDamage = 400;

		public readonly WDist HalfRadiationDistance = WDist.FromCells(5);

		public readonly BitSet<DamageType> RadiationDamageTypes = new("FireDeath");

		[Desc("Target types hurt by radiation.")]
		public readonly BitSet<TargetableType> RadiationTargets = new("Infantry");

		[ActorReference]
		[Desc("Actor left at ground zero.")]
		public readonly string Crater = "nukecrater";

		[Desc("Height the mushroom cap rises to.")]
		public readonly WDist CloudHeight = WDist.FromCells(14);

		[Desc("Ticks the mushroom cloud hangs over the site before it drifts apart.")]
		public readonly int CloudDuration = 1100;

		[Desc("Ticks the cloud takes to fade out.")]
		public readonly int CloudFade = 300;

		[Desc("Cloud opacity when it forms, 0 to 1. It thins to CloudAlpha, then fades out after CloudDuration.")]
		public readonly float CloudStartAlpha = 0.92f;

		[Desc("Cloud opacity once it has thinned, 0 to 1.")]
		public readonly float CloudAlpha = 0.5f;

		public readonly string FireImage = "fire";

		[SequenceReference(nameof(FireImage))]
		public readonly string[] FireSequences = ["1", "2", "3", "4"];

		public readonly string ExplosionImage = "explosion";

		[SequenceReference(nameof(ExplosionImage))]
		public readonly string FireballSequence = "building";

		[Desc("Soft white puffs, tinted in game.")]
		public readonly string PuffImage = "nukepuff";

		[SequenceReference(nameof(PuffImage))]
		public readonly string[] PuffSequences = ["a", "b", "c", "d"];

		[PaletteReference]
		public readonly string Palette = "effect";

		public readonly Color ShockwaveColor = Color.FromArgb(255, 255, 220, 170);

		public override void DoImpact(in Target target, WarheadArgs args)
		{
			var source = args.SourceActor;
			if (source == null)
				return;

			var pos = target.CenterPosition;
			var world = source.World;
			world.AddFrameEndTask(w =>
			{
				w.Add(new NuclearAftermath(this, w, pos, source));

				var cell = w.Map.CellContaining(pos);
				var neutral = w.Players.FirstOrDefault(p => p.InternalName == "Neutral") ?? source.Owner;
				if (Crater != null && w.Map.Contains(cell))
					w.CreateActor(Crater, [new LocationInit(cell), new OwnerInit(neutral)]);
			});
		}

		// value / 2^(distance / half), linear between the halvings. Integer only, so it is the same on every client.
		public static int Halve(int value, int distance, int half)
		{
			var steps = distance / half;
			if (steps >= 30)
				return 0;

			var a = value >> steps;
			var b = a >> 1;
			return a - (int)((long)(a - b) * (distance % half) / half);
		}
	}

	public class NuclearAftermath : IEffect, IEffectAboveShroud, IEffectAnnotation
	{
		sealed class Burning
		{
			public Actor Actor;
			public int Damage;
			public int NextBurn;
			public Animation[] Flames;
			public WVec[] Offsets;
		}

		enum PuffKind { Plain, Column, Cap, Base }

		// One puff of the mushroom cloud.
		sealed class Puff
		{
			public Animation Anim;
			public PuffKind Kind;
			public WVec Offset;
			public WVec Velocity;
			public int Age;
			public int Life;
			public float Scale0, Scale1;
			public Vector3 Tint0, Tint1;
			public int TintTicks;
			public float Alpha;

			// Cap puffs roll around the ring of the cap: angle around the vertical axis and around the ring tube.
			public float Around, Roll, RollSpeed, Ring;
		}

		static readonly Vector3 White = new(1f, 0.97f, 0.85f);
		static readonly Vector3 Glow = new(1f, 0.72f, 0.38f);
		static readonly Vector3 Ember = new(0.85f, 0.42f, 0.22f);
		static readonly Vector3 DarkSmoke = new(0.42f, 0.36f, 0.34f);
		static readonly Vector3 Dust = new(0.78f, 0.64f, 0.48f);

		readonly NuclearAftermathWarhead info;
		readonly World world;
		readonly WPos pos;
		readonly Actor source;
		readonly Player sourceOwner;
		readonly List<Burning> burning = [];
		readonly HashSet<Actor> hit = [];
		readonly List<Puff> puffs = [];
		readonly MersenneTwister random;
		readonly int end;

		int tick;
		int lastDustRadius;

		int CloudEnd => info.CloudDuration + info.CloudFade;

		public NuclearAftermath(NuclearAftermathWarhead info, World world, WPos pos, Actor source)
		{
			this.info = info;
			this.world = world;
			this.pos = pos;
			this.source = source;
			sourceOwner = source.Owner;
			random = world.LocalRandom;

			// Burning and radiation last exactly as long as the colour scheme.
			var grade = world.WorldActor.TraitOrDefault<NuclearGrade>();
			end = grade?.Info.Total ?? info.ShockwaveDuration + info.RadiationDuration;
			grade?.Start();

			// Fireball: a blinding core of puffs plus a cluster of blasts at ground zero.
			for (var i = 0; i < 14; i++)
			{
				var a = random.NextFloat() * MathF.Tau;
				var r = i < 4 ? random.Next(400) : 600 + random.Next(1800);
				AddPuff(PuffKind.Plain, info.PuffImage, Pick(info.PuffSequences),
					new WVec((int)(MathF.Cos(a) * r), (int)(MathF.Sin(a) * r), random.Next(1200)),
					new WVec(0, 0, 20 + random.Next(20)), 80, 1.6f, 2.8f, White, Ember, 70, 0.9f);
			}

			for (var i = 0; i < 10; i++)
			{
				var a = random.NextFloat() * MathF.Tau;
				var r = random.Next(2400);
				AddPuff(PuffKind.Plain, info.ExplosionImage, info.FireballSequence,
					new WVec((int)(MathF.Cos(a) * r), (int)(MathF.Sin(a) * r), random.Next(800)),
					new WVec(0, 0, 6), 50, 2f, 2.5f, White, White, 1, 1f);
			}
		}

		Actor Attacker => source.IsInWorld && !source.IsDead ? source : sourceOwner.PlayerActor;

		WDist ShockwaveRadius => new(info.BlastRadius.Length * Math.Min(tick, info.ShockwaveDuration) / info.ShockwaveDuration);

		string Pick(string[] options) => options[random.Next(options.Length)];

		void IEffect.Tick(World world)
		{
			tick++;

			if (tick <= info.ShockwaveDuration)
				Shockwave(ShockwaveRadius);

			if (tick <= end)
				BurnStructures();
			else
				burning.Clear();

			if (tick > info.ShockwaveDuration && tick <= end && tick % info.RadiationInterval == 0)
				Irradiate();

			TickCloud();

			if (tick > Math.Max(end, CloudEnd) && puffs.Count == 0)
				world.AddFrameEndTask(w => w.Remove(this));
		}

		void Shockwave(WDist reach)
		{
			foreach (var a in world.FindActorsInCircle(pos, reach))
			{
				if (a.IsDead || !a.IsInWorld || !hit.Add(a))
					continue;

				var types = a.GetEnabledTargetTypes();
				var distance = (a.CenterPosition - pos).HorizontalLength;

				if (types.Overlaps(info.DamageTargets))
				{
					var damage = NuclearAftermathWarhead.Halve(info.PeakDamage, distance, info.HalfDamageDistance.Length);
					damage = damage * Versus(a) / 100;
					if (damage > 0)
						a.InflictDamage(Attacker, new Damage(damage, info.DamageTypes));
				}

				if (!a.IsDead && types.Overlaps(info.BurnTargets))
					Ignite(a, distance);
			}
		}

		int Versus(Actor a)
		{
			var armor = a.TraitsImplementing<Armor>().Where(x => !x.IsTraitDisabled && x.Info.Type != null)
				.Select(x => info.Versus.TryGetValue(x.Info.Type, out var v) ? v : 100);
			return armor.DefaultIfEmpty(100).Min();
		}

		void Ignite(Actor a, int distance)
		{
			var health = a.TraitOrDefault<IHealth>();
			if (health == null)
				return;

			var perMille = NuclearAftermathWarhead.Halve(info.BurnPerMille, distance, info.HalfBurnDistance.Length);
			if (perMille < info.MinBurnPerMille)
				return;

			// Flames are cosmetic, so they use the local random.
			var building = a.Info.TraitInfoOrDefault<BuildingInfo>();
			var cells = building?.OccupiedTiles(CPos.Zero).ToArray() ?? [CPos.Zero];
			var center = cells.Aggregate(WVec.Zero, (s, c) => s + new WVec(c.X * 1024, c.Y * 1024, 0)) / cells.Length;
			var flames = new Animation[info.FlamesPerStructure];
			var offsets = new WVec[info.FlamesPerStructure];
			for (var i = 0; i < flames.Length; i++)
			{
				var c = cells[random.Next(cells.Length)];
				offsets[i] = new WVec(c.X * 1024, c.Y * 1024, 0) - center
					+ new WVec(random.Next(-300, 300), random.Next(-300, 300), 0);
				flames[i] = new Animation(world, info.FireImage);
				flames[i].PlayRepeating(Pick(info.FireSequences));
				for (var t = random.Next(20); t > 0; t--)
					flames[i].Tick();
			}

			burning.Add(new Burning
			{
				Actor = a,
				Damage = Math.Max(1, (int)((long)health.MaxHP * perMille / 1000)),
				NextBurn = tick + info.BurnInterval + world.SharedRandom.Next(info.BurnInterval),
				Flames = flames,
				Offsets = offsets,
			});
		}

		void BurnStructures()
		{
			for (var i = burning.Count - 1; i >= 0; i--)
			{
				var b = burning[i];
				if (b.Actor.IsDead || !b.Actor.IsInWorld)
				{
					burning.RemoveAt(i);
					continue;
				}

				foreach (var f in b.Flames)
					f.Tick();

				if (tick >= b.NextBurn)
				{
					b.NextBurn = tick + info.BurnInterval;
					b.Actor.InflictDamage(Attacker, new Damage(b.Damage, info.BurnDamageTypes));
				}
			}
		}

		void Irradiate()
		{
			var remaining = end - tick;
			var duration = Math.Max(1, end - info.ShockwaveDuration);
			foreach (var a in world.FindActorsInCircle(pos, info.RadiationRadius))
			{
				if (a.IsDead || !a.IsInWorld || !a.GetEnabledTargetTypes().Overlaps(info.RadiationTargets))
					continue;

				var distance = (a.CenterPosition - pos).HorizontalLength;
				var damage = NuclearAftermathWarhead.Halve(info.RadiationDamage, distance, info.HalfRadiationDistance.Length);
				damage = damage * Math.Max(1, remaining) / duration + 1;
				a.InflictDamage(Attacker, new Damage(damage, info.RadiationDamageTypes));
			}
		}

		// Mushroom cloud
		Puff AddPuff(PuffKind kind, string image, string sequence, WVec offset, WVec velocity, int life,
			float scale0, float scale1, Vector3 tint0, Vector3 tint1, int tintTicks, float alpha)
		{
			var anim = new Animation(world, image);
			anim.Play(sequence);
			var p = new Puff
			{
				Anim = anim,
				Kind = kind,
				Offset = offset,
				Velocity = velocity,
				Life = life,
				Scale0 = scale0,
				Scale1 = scale1,
				Tint0 = tint0,
				Tint1 = tint1,
				TintTicks = Math.Max(1, tintTicks),
				Alpha = alpha,
			};

			puffs.Add(p);
			return p;
		}

		int CapHeight
		{
			get
			{
				// Rises fast at first, then slows as it reaches CloudHeight.
				var cap = info.CloudHeight.Length;
				const int Rise = 200;
				if (tick >= Rise)
					return cap;

				var left = Rise - tick;
				return Math.Max(cap / 5, cap - (int)((long)cap * left * left / (Rise * Rise)));
			}
		}

		void TickCloud()
		{
			var capHeight = CapHeight;
			var forming = Math.Min(1f, tick / 200f);

			// The column: puffs boil up from ground zero and are swallowed by the cap.
			if (tick < info.CloudDuration && tick % 2 == 0)
			{
				for (var k = 0; k < 2; k++)
				{
					var a = random.NextFloat() * MathF.Tau;
					var r = random.Next(700 + (int)(500 * forming));
					var hot = tick < 160;
					AddPuff(PuffKind.Column, info.PuffImage, Pick(info.PuffSequences),
						new WVec((int)(MathF.Cos(a) * r), (int)(MathF.Sin(a) * r * 0.6f), random.Next(400)),
						new WVec(random.Next(-8, 9), random.Next(-5, 6), 90 + random.Next(50)),
						400, 1.4f + random.NextFloat() * 0.6f, 2.3f, hot ? Glow : Ember, DarkSmoke, 150, 0.9f);
				}
			}

			// Base surge: a thick skirt of smoke over ground zero that hides the crater, then thins with the cloud.
			if (tick < info.CloudDuration + info.CloudFade / 2 && tick % 3 == 0)
			{
				var a = random.NextFloat() * MathF.Tau;
				var r = random.Next(500 + (int)(5500 * forming));
				AddPuff(PuffKind.Base, info.PuffImage, Pick(info.PuffSequences),
					new WVec((int)(MathF.Cos(a) * r), (int)(MathF.Sin(a) * r * 0.75f), random.Next(300)),
					new WVec((int)(MathF.Cos(a) * 6), (int)(MathF.Sin(a) * 4), 2),
					600, 1.8f + random.NextFloat() * 0.8f, 2.8f, Ember, Dust, 120, 0.85f);
			}

			// The cap: a ring of huge puffs that rolls outward over the top and back underneath.
			if (tick >= 20 && tick < 200 && tick % 2 == 0)
			{
				var p = AddPuff(PuffKind.Cap, info.PuffImage, Pick(info.PuffSequences), WVec.Zero, WVec.Zero, int.MaxValue,
					2.2f + random.NextFloat() * 0.8f, 3.2f, tick < 90 ? White : Glow, DarkSmoke, 260, 1f);
				p.Around = random.NextFloat() * MathF.Tau;
				p.Roll = random.NextFloat() * MathF.Tau;
				p.RollSpeed = 0.012f + random.NextFloat() * 0.01f;
				p.Ring = 0.6f + random.NextFloat() * 0.5f;
			}

			// Glowing heart of the cap while it forms.
			if (tick >= 20 && tick < 240 && tick % 4 == 0)
			{
				AddPuff(PuffKind.Plain, info.PuffImage, Pick(info.PuffSequences),
					new WVec(random.Next(-2500, 2500), random.Next(-1000, 1000), capHeight + random.Next(-1500, 600)),
					new WVec(0, 0, 8), 70, 2.4f, 3.2f, White, Ember, 70, 0.6f);
			}

			// Dust kicked up along the shockwave.
			var reach = ShockwaveRadius.Length;
			if (tick <= info.ShockwaveDuration && reach - lastDustRadius > 1400)
			{
				lastDustRadius = reach;
				var count = Math.Min(32, 8 + reach / 2000);
				for (var i = 0; i < count; i++)
				{
					var a = MathF.Tau * i / count + random.NextFloat() * 0.2f;
					AddPuff(PuffKind.Plain, info.PuffImage, Pick(info.PuffSequences),
						new WVec((int)(MathF.Cos(a) * reach), (int)(MathF.Sin(a) * reach), 0),
						new WVec((int)(MathF.Cos(a) * 8), (int)(MathF.Sin(a) * 8), 1),
						500, 1.5f, 3f, Dust, Dust, 1, 0.5f);
				}
			}

			var wind = tick > info.CloudDuration / 2 ? (tick - info.CloudDuration / 2) * 4 : 0;
			for (var i = puffs.Count - 1; i >= 0; i--)
			{
				var p = puffs[i];
				p.Age++;
				p.Anim.Tick();

				switch (p.Kind)
				{
					case PuffKind.Cap:
					{
						// Torus: the ring widens as the cap grows; each puff turns slowly around the ring tube.
						p.Roll += p.RollSpeed;
						p.Around += 0.002f;
						var grow = Math.Min(1f, p.Age / 200f);
						var ringRadius = (2500 + 4500 * grow) * p.Ring;
						var tube = 1400 + 1200 * grow;
						var radial = ringRadius + MathF.Cos(p.Roll) * tube;
						p.Offset = new WVec(
							(int)(MathF.Cos(p.Around) * radial) + wind,
							(int)(MathF.Sin(p.Around) * radial * 0.55f),
							capHeight + (int)(MathF.Sin(p.Roll) * tube * 0.7f));
						break;
					}

					case PuffKind.Column:
					{
						// Rise, widen towards the top, and fade into the cap.
						p.Offset += p.Velocity;
						var lean = (long)wind * p.Offset.Z / Math.Max(1, info.CloudHeight.Length);
						p.Offset = new WVec(p.Offset.X + (int)(lean / 40), p.Offset.Y, p.Offset.Z);
						if (p.Offset.Z > capHeight - 1200)
							p.Life = Math.Min(p.Life, p.Age + 25);

						break;
					}

					default:
						p.Offset += p.Velocity;
						break;
				}

				if (p.Age >= p.Life || (p.Kind == PuffKind.Cap && tick >= CloudEnd))
					puffs.RemoveAt(i);
			}
		}

		// Dense at first, thinning steadily until CloudDuration.
		float Density => info.CloudStartAlpha + (info.CloudAlpha - info.CloudStartAlpha) * Math.Min(1f, (float)tick / info.CloudDuration);

		float PuffAlpha(Puff p)
		{
			if (p.Kind == PuffKind.Cap)
				return Density * (tick <= info.CloudDuration ? 1f : Math.Max(0f, 1f - (float)(tick - info.CloudDuration) / info.CloudFade));

			// Others fade in quickly and out over the last third of their life.
			var fadeIn = Math.Min(1f, p.Age / 8f);
			var t = (float)p.Age / p.Life;
			var fadeOut = t < 0.66f ? 1f : Math.Max(0f, 3f - 3f * t);

			// The column goes with the cap; smoke and dust on the ground linger and only fade with their own age.
			var cloudFade = p.Kind != PuffKind.Column || tick <= info.CloudDuration ? 1f
				: Math.Max(0f, 1f - (float)(tick - info.CloudDuration) / info.CloudFade);
			var alpha = p.Kind == PuffKind.Plain ? p.Alpha : Density * p.Alpha;
			return alpha * fadeIn * fadeOut * cloudFade;
		}

		IEnumerable<IRenderable> IEffect.Render(WorldRenderer wr)
		{
			var palette = wr.Palette(info.Palette);
			foreach (var b in burning)
			{
				if (world.FogObscures(b.Actor))
					continue;

				for (var i = 0; i < b.Flames.Length; i++)
					foreach (var r in b.Flames[i].Render(b.Actor.CenterPosition, b.Offsets[i], 0, palette))
						yield return r;
			}
		}

		// The cloud towers over everything, so it is drawn above the fog and shroud.
		IEnumerable<IRenderable> IEffectAboveShroud.RenderAboveShroud(WorldRenderer wr)
		{
			if (world.ShroudObscures(pos))
				yield break;

			var palette = wr.Palette(info.Palette);
			foreach (var p in puffs.OrderBy(p => p.Offset.Z))
			{
				var alpha = PuffAlpha(p);
				if (alpha <= 0.01f)
					continue;

				var seq = p.Anim.CurrentSequence;
				var frame = Math.Min(p.Anim.CurrentFrame, seq.Length - 1);
				var k = Math.Min(1f, (float)p.Age / p.TintTicks);
				var tint = Vector3.Lerp(p.Tint0, p.Tint1, k);

				// The cap's top catches the light; its underside is in shadow.
				if (p.Kind == PuffKind.Cap)
					tint *= 0.8f + 0.3f * MathF.Sin(p.Roll);
				var scale = p.Scale0 + (p.Scale1 - p.Scale0) * Math.Min(1f, p.Age / 200f);

				// Higher puffs draw on top, so the cap covers the column.
				yield return new SpriteRenderable(seq.GetSprite(frame), pos + new WVec(0, 0, 1), p.Offset, 4096 + p.Offset.Z / 4, palette,
					seq.Scale * scale, alpha * seq.GetAlpha(frame), tint, TintModifiers.IgnoreWorldTint, false);
			}
		}

		IEnumerable<IRenderable> IEffectAnnotation.RenderAnnotation(WorldRenderer wr)
		{
			if (world.ShroudObscures(pos) || tick > info.ShockwaveDuration)
				yield break;

			// Thin bright pressure front, fading as it spreads.
			var fade = 1f - (float)tick / info.ShockwaveDuration;
			var c = info.ShockwaveColor;
			var radius = ShockwaveRadius;
			yield return new CircleAnnotationRenderable(pos, radius, 3, Color.FromArgb((int)(c.A * fade * fade), c));
			yield return new CircleAnnotationRenderable(pos, new WDist(Math.Max(0, radius.Length - 700)), 2,
				Color.FromArgb((int)(140 * fade * fade), 255, 140, 60));
		}
	}
}
