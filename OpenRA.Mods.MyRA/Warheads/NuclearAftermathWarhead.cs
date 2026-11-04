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
using System.Runtime.CompilerServices;
using OpenRA.Effects;
using OpenRA.GameRules;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.Render;
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
	//   5. a small crater is left at ground zero for good;
	//   6. everything close in (VaporizeRadius, and infantry out to InfantryVaporizeRadius) is vaporized on the
	//      spot. The ground around ground zero is scorched black for good, except the nuclear shadows: the
	//      hollow outlines where things stood, stretching outward away from the blast;
	//   7. trees and ore out to FireRadius catch fire, char black and smoke for minutes. All ore in the blast
	//      zone is ruined.
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
		public readonly BitSet<TargetableType> DamageTargets = new("GroundActor", "WaterActor", "Underwater", "AirborneActor");

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

		[Desc("Structures, vehicles, ships and infantry on the ground inside this radius are vaporized, leaving a nuclear shadow.")]
		public readonly WDist VaporizeRadius = WDist.FromCells(12);

		[Desc("Infantry are vaporized out to this radius.")]
		public readonly WDist InfantryVaporizeRadius = WDist.FromCells(20);

		[Desc("Damage types the vaporized die with (none have a death animation: they are just gone).")]
		public readonly BitSet<DamageType> VaporizeDamageTypes = new("Vaporized");

		[Desc("Radius of the sooty ground. 0 = InfantryVaporizeRadius.")]
		public readonly WDist ScorchRadius = WDist.Zero;

		[Desc("Opacity of the scorched ground, 0 to 1.")]
		public readonly float ScorchAlpha = 0.72f;

		[Desc("Trees and ore inside this radius catch fire, char black and smoke.")]
		public readonly WDist FireRadius = WDist.FromCells(24);

		[Desc("Ticks ore burns before it is gone, leaving scorched ground.")]
		public readonly int OreBurnTicks = 200;

		[Desc("Ticks the burnt ore keeps smoking.")]
		public readonly int SmokeTicks = 3000;

		public readonly string SmokeImage = "smoke_m";

		[SequenceReference(nameof(SmokeImage))]
		public readonly string SmokeSequence = "idle";

		[Desc("Smudge left where ore burnt away (none by default: the soot stain covers it).")]
		public readonly string OreSmudge = null;

		[ActorReference]
		[Desc("Actor left at ground zero.")]
		public readonly string Crater = "nukecrater";

		[Desc("Size of the fireball and mushroom cloud, in percent (100 = the full-size bomb). Smaller nukes use less.")]
		public readonly int CloudScale = 100;

		[Desc("A blinding flash at ground zero this many ticks long (seen only by those looking at it; the big bomb",
			"uses the screen-wide FlashEffect warhead instead). 0 for none.")]
		public readonly int LocalFlash = 0;

		[Desc("Radius of the local flash.")]
		public readonly WDist LocalFlashRadius = WDist.FromCells(4);

		[Desc("Start the world's NuclearGrade (the orange sky and doom music). Small nukes don't.")]
		public readonly bool StartsGrade = true;

		[Desc("A bomb landing this close to another nuke's cloud that is still up joins it: one bigger cloud.")]
		public readonly WDist MergeRadius = WDist.FromCells(5);

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
				if (!string.IsNullOrEmpty(Crater) && !ScorchedEarth.IsWater(w.Map, cell))
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

		// Caught by the flash: drawn once into a shadow, then gone a couple of ticks later.
		sealed class Vaporizing
		{
			public Actor Actor;
			public int KillTick;
		}

		// An ore cell on fire, then smoking.
		sealed class OreFire
		{
			public CPos Cell;
			public WPos Pos;
			public int Distance;
			public int IgniteTick = -1;
			public int BurnOut;
			public bool Burns;
			public bool Gone;
			public Animation Flame;
		}

		sealed class Wisp
		{
			public Animation Anim;
			public WPos Pos;
			public WVec Offset;
			public bool Done;
		}

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

			// Drawn at its true place and size even when the cloud is scaled down (the shockwave's dust).
			public bool FullSize;
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
		readonly List<Vaporizing> vaporizing = [];
		readonly List<OreFire> ore = [];
		readonly List<Wisp> wisps = [];
		readonly ScorchedEarth scorched;
		readonly IResourceLayer resources;
		readonly SmudgeLayer oreSmudges;
		readonly MersenneTwister random;
		int nextOre;
		readonly int end;

		int tick;
		int lastDustRadius;

		// Every nuke's cloud lingers on its own; bombs landing in a cloud still up grow it instead of making another.
		static readonly ConditionalWeakTable<World, List<NuclearAftermath>> Active = [];
		int cloudDuration;
		float cloudScale;
		readonly bool cloudless;

		int CloudEnd => cloudDuration + info.CloudFade;

		public NuclearAftermath(NuclearAftermathWarhead info, World world, WPos pos, Actor source)
		{
			this.info = info;
			this.world = world;
			this.pos = pos;
			this.source = source;
			sourceOwner = source.Owner;
			random = world.LocalRandom;
			cloudDuration = info.CloudDuration;
			cloudScale = info.CloudScale;

			var active = Active.GetOrCreateValue(world);
			active.RemoveAll(a => a.tick >= a.cloudDuration);
			var near = active.Where(a => (a.pos - pos).HorizontalLengthSquared <= info.MergeRadius.LengthSquared)
				.OrderBy(a => (a.pos - pos).HorizontalLengthSquared).FirstOrDefault();
			if (near != null)
			{
				near.Join(info);
				cloudless = true;
			}
			else
				active.Add(this);

			// Burning and radiation last exactly as long as the colour scheme.
			var grade = info.StartsGrade ? world.WorldActor.TraitOrDefault<NuclearGrade>() : null;
			end = grade?.Info.Total ?? info.ShockwaveDuration + info.RadiationDuration;
			grade?.Start();

			var scorchRadius = info.ScorchRadius.Length > 0 ? info.ScorchRadius : info.InfantryVaporizeRadius;
			scorched = new ScorchedEarth(world, pos, scorchRadius.Length, info.ScorchAlpha);
			world.AddFrameEndTask(w => w.Add(scorched));

			// Ore in the blast zone, nearest first, so the shockwave ruins it as it passes (and sets fire to the near ore).
			resources = world.WorldActor.TraitOrDefault<IResourceLayer>();
			oreSmudges = world.WorldActor.TraitsImplementing<SmudgeLayer>().FirstOrDefault(s => s.Info.Type == info.OreSmudge);
			if (resources != null)
			{
				var center = world.Map.CellContaining(pos);
				var reach = Math.Max(info.FireRadius.Length, info.BlastRadius.Length);
				var cells = (reach + 1023) / 1024;
				for (var y = center.Y - cells; y <= center.Y + cells; y++)
				{
					for (var x = center.X - cells; x <= center.X + cells; x++)
					{
						var c = new CPos(x, y);
						if (!world.Map.Contains(c) || resources.GetResource(c).Type == null)
							continue;

						var p = world.Map.CenterOfCell(c);
						var d = (p - pos).HorizontalLength;
						if (d <= reach)
							ore.Add(new OreFire { Cell = c, Pos = p, Distance = d, Burns = d <= info.FireRadius.Length });
					}
				}

				ore.Sort((a, b) => a.Distance.CompareTo(b.Distance));
			}

			// Fireball: a blinding core of puffs plus a cluster of blasts at ground zero.
			if (!cloudless)
				Fireball();
		}

		// Another bomb went off inside this cloud: one bigger, longer-lasting cloud with a fresh fireball in it.
		void Join(NuclearAftermathWarhead other)
		{
			cloudScale = Math.Min(170f, MathF.Sqrt(cloudScale * cloudScale + other.CloudScale * other.CloudScale) * 1.1f);
			cloudDuration = Math.Max(cloudDuration, tick + other.CloudDuration);
			Fireball();
		}

		void Fireball()
		{
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

			Vaporize();
			BurnOre();

			if (tick <= end)
				BurnStructures();
			else
				burning.Clear();

			if (tick > info.ShockwaveDuration && tick <= end && tick % info.RadiationInterval == 0)
				Irradiate();

			if (!cloudless)
				TickCloud();

			if (tick > Math.Max(end, CloudEnd) && puffs.Count == 0 && vaporizing.Count == 0
				&& tick > info.ShockwaveDuration + info.OreBurnTicks + info.SmokeTicks + 100 && wisps.Count == 0)
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

				// Trees burn black but stay standing.
				var scorch = a.TraitOrDefault<Scorchable>();
				if (scorch != null)
				{
					if (distance <= info.FireRadius.Length)
						scorch.Ignite(world.SharedRandom.Next(20));

					scorched.Block(a, 300);
					continue;
				}

				if (CanVaporize(a, distance))
				{
					scorched.Block(a, a.Info.HasTraitInfo<WithInfantryBodyInfo>() ? 170 : 400);
					vaporizing.Add(new Vaporizing { Actor = a, KillTick = tick + 1 });
					continue;
				}

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

		bool CanVaporize(Actor a, int distance)
		{
			if (a.TraitOrDefault<IHealth>() == null || world.Map.DistanceAboveTerrain(a.CenterPosition).Length > 256)
				return false;

			return distance <= info.VaporizeRadius.Length
				|| (distance <= info.InfantryVaporizeRadius.Length && a.Info.HasTraitInfo<WithInfantryBodyInfo>());
		}

		void Vaporize()
		{
			for (var i = vaporizing.Count - 1; i >= 0; i--)
			{
				var v = vaporizing[i];
				if (tick < v.KillTick)
					continue;

				vaporizing.RemoveAt(i);
				var a = v.Actor;
				if (a.IsDead || !a.IsInWorld)
					continue;

				// Wrecks left by what was just vaporized go without a trace.
				if (a.Info.HasTraitInfo<HuskInfo>())
					a.Dispose();
				else
					a.Kill(Attacker, info.VaporizeDamageTypes);
			}
		}

		void BurnOre()
		{
			// Light ore as the shockwave reaches it.
			var reach = tick <= info.ShockwaveDuration ? ShockwaveRadius.Length : int.MaxValue;
			while (nextOre < ore.Count && ore[nextOre].Distance <= reach)
			{
				var o = ore[nextOre++];
				var h = (o.Cell.X * 73856093) ^ (o.Cell.Y * 19349663);
				o.IgniteTick = tick + (h & 15);
				o.BurnOut = o.Burns ? o.IgniteTick + info.OreBurnTicks + ((h >> 4) & 63) : o.IgniteTick;
			}

			for (var i = 0; i < nextOre; i++)
			{
				var o = ore[i];
				if (tick == o.IgniteTick && o.Burns)
				{
					o.Flame = new Animation(world, info.FireImage);
					o.Flame.PlayRepeating(Pick(info.FireSequences));
				}

				if (tick >= o.IgniteTick && !o.Gone)
					o.Flame?.Tick();

				// Burnt away: scorched ground, still smoking.
				if (!o.Gone && tick >= o.BurnOut)
				{
					o.Gone = true;
					resources.ClearResources(o.Cell);
					oreSmudges?.AddSmudge(o.Cell);
				}

				// Smoke is cosmetic, so it uses the local random.
				if (o.Burns && tick >= o.IgniteTick && tick < o.IgniteTick + info.SmokeTicks && wisps.Count < 600
					&& random.Next(o.Gone ? 70 : 14) == 0)
				{
					var w = new Wisp
					{
						Anim = new Animation(world, info.SmokeImage),
						Pos = o.Pos,
						Offset = new WVec(random.Next(-350, 350), random.Next(-350, 350), 100),
					};
					w.Anim.PlayThen(info.SmokeSequence, () => w.Done = true);
					wisps.Add(w);
				}
			}

			foreach (var w in wisps)
			{
				w.Anim.Tick();
				w.Offset += new WVec(1, 0, 8);
			}

			wisps.RemoveAll(w => w.Done);
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
			if (tick < cloudDuration && tick % 2 == 0)
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
			if (tick < cloudDuration + info.CloudFade / 2 && tick % 3 == 0)
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
			if (tick <= info.ShockwaveDuration && reach - lastDustRadius > Math.Min(1400, info.BlastRadius.Length / 5))
			{
				lastDustRadius = reach;
				var count = Math.Min(32, 8 + reach / 2000);
				for (var i = 0; i < count; i++)
				{
					var a = MathF.Tau * i / count + random.NextFloat() * 0.2f;
					var dust = AddPuff(PuffKind.Plain, info.PuffImage, Pick(info.PuffSequences),
						new WVec((int)(MathF.Cos(a) * reach), (int)(MathF.Sin(a) * reach), 0),
						new WVec((int)(MathF.Cos(a) * 8), (int)(MathF.Sin(a) * 8), 1),
						500, 1.5f, 3f, Dust, Dust, 1, 0.5f);
					dust.FullSize = true;
				}
			}

			var wind = tick > cloudDuration / 2 ? (tick - cloudDuration / 2) * 4 : 0;
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
		float Density => info.CloudStartAlpha + (info.CloudAlpha - info.CloudStartAlpha) * Math.Min(1f, (float)tick / cloudDuration);

		float PuffAlpha(Puff p)
		{
			if (p.Kind == PuffKind.Cap)
				return Density * (tick <= cloudDuration ? 1f : Math.Max(0f, 1f - (float)(tick - cloudDuration) / info.CloudFade));

			// Others fade in quickly and out over the last third of their life.
			var fadeIn = Math.Min(1f, p.Age / 8f);
			var t = (float)p.Age / p.Life;
			var fadeOut = t < 0.66f ? 1f : Math.Max(0f, 3f - 3f * t);

			// The column goes with the cap; smoke and dust on the ground linger and only fade with their own age.
			var cloudFade = p.Kind != PuffKind.Column || tick <= cloudDuration ? 1f
				: Math.Max(0f, 1f - (float)(tick - cloudDuration) / info.CloudFade);
			var alpha = p.Kind == PuffKind.Plain ? p.Alpha : Density * p.Alpha;
			return alpha * fadeIn * fadeOut * cloudFade;
		}

		IEnumerable<IRenderable> IEffect.Render(WorldRenderer wr)
		{
			var palette = wr.Palette(info.Palette);

			foreach (var o in ore)
			{
				if (o.Flame == null || o.Gone || world.ShroudObscures(o.Pos))
					continue;

				foreach (var r in o.Flame.Render(o.Pos, WVec.Zero, 0, palette))
					yield return r;
			}

			foreach (var w in wisps)
			{
				if (world.ShroudObscures(w.Pos))
					continue;

				foreach (var r in w.Anim.Render(w.Pos, w.Offset, 0, palette))
					yield return r;
			}

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

			if (tick < info.LocalFlash)
			{
				var f = 1f - (float)tick / info.LocalFlash;
				var flashSeq = wr.World.Map.Sequences.GetSequence(info.PuffImage, info.PuffSequences[0]);
				var r = info.LocalFlashRadius.Length;
				var flashScale = r / 512f;
				for (var i = 0; i < 3; i++)
					yield return new SpriteRenderable(flashSeq.GetSprite(0), pos, new WVec(0, 0, 256), 8192, palette,
						flashScale * (0.6f + 0.3f * i) * (1.2f - 0.4f * f), f * f, new Vector3(3f, 3f, 2.6f),
						TintModifiers.IgnoreWorldTint, false);
			}

			// Smaller nukes: the same cloud, shrunk about ground zero.
			var size = cloudScale / 100f;
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
				var s = p.FullSize ? 1f : size;
				var offset = s == 1f ? p.Offset : new WVec((int)(p.Offset.X * s), (int)(p.Offset.Y * s), (int)(p.Offset.Z * s));
				yield return new SpriteRenderable(seq.GetSprite(frame), pos + new WVec(0, 0, 1), offset, 4096 + offset.Z / 4, palette,
					seq.Scale * scale * s, alpha * seq.GetAlpha(frame), tint, TintModifiers.IgnoreWorldTint, false);
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
			yield return new CircleAnnotationRenderable(pos, radius, 5, Color.FromArgb((int)(c.A * fade * fade), c));
			yield return new CircleAnnotationRenderable(pos, new WDist(Math.Max(0, radius.Length - 250)), 3,
				Color.FromArgb((int)(200 * fade * fade), 255, 255, 255));
			yield return new CircleAnnotationRenderable(pos, new WDist(Math.Max(0, radius.Length - 700)), 2,
				Color.FromArgb((int)(140 * fade * fade), 255, 140, 60));
		}
	}

	// Soot on the ground around ground zero: darkest at the centre and fading out unevenly, never on water. Whatever
	// stood in the flash shielded the ground behind it, which stays lighter: the nuclear shadows. The outline of each
	// vaporized soldier, vehicle and building stays burnt into the ground where it stood.
	public class ScorchedEarth : IEffect
	{
		// Quarter-cell grid: fine enough for a soft stain.
		const int Step = 256;
		static readonly Color Soot = Color.FromArgb(255, 42, 34, 27);
		static readonly Color Outline = Color.FromArgb(255, 20, 16, 13);

		// A standing figure, feet at the origin's south, as its shadow falls on the ground (x right, y down).
		static readonly (int X, int Y)[] Figure =
		[
			(-14, -120), (-34, -112), (-70, -96), (-78, -30), (-58, -28), (-50, -78), (-44, 10), (-46, 120),
			(-16, 120), (0, 30), (16, 120), (46, 120), (44, 10), (50, -78), (58, -28), (78, -30), (70, -96),
			(34, -112), (14, -120),
		];

		readonly World world;
		readonly WPos center;
		readonly int radius;
		readonly float alpha;
		readonly List<(int X0, int Y0, int X1, int Y1)> blockers = [];
		readonly List<WPos[]> outlines = [];
		readonly int made;
		readonly int seed;
		List<Quad> quads;

		public ScorchedEarth(World world, WPos center, int radius, float alpha)
		{
			this.world = world;
			this.center = center;
			this.radius = radius;
			this.alpha = alpha;
			made = world.WorldTick;
			seed = center.X * 31 + center.Y;
		}

		// Something stood here when the flash went off: buildings cast their footprint, units a box this half-size.
		public void Block(Actor a, int halfSize)
		{
			if ((a.CenterPosition - center).HorizontalLengthSquared > (long)radius * radius)
				return;

			var building = a.Info.TraitInfoOrDefault<BuildingInfo>();
			if (building != null && halfSize > 300)
			{
				var cells = building.OccupiedTiles(a.Location).ToHashSet();
				foreach (var c in cells)
				{
					var p = world.Map.CenterOfCell(c);
					blockers.Add((p.X - 512, p.Y - 512, p.X + 512, p.Y + 512));
				}

				OutlineFootprint(cells);
			}
			else
			{
				var p = a.CenterPosition;
				blockers.Add((p.X - halfSize, p.Y - halfSize, p.X + halfSize, p.Y + halfSize));

				// Trees are only charred and still stand; everything else is gone and leaves its outline.
				if (!a.Info.HasTraitInfo<ScorchableInfo>() && !IsWater(world.Map, world.Map.CellContaining(p)))
				{
					var ground = new WPos(p.X, p.Y, center.Z);
					if (a.Info.HasTraitInfo<WithInfantryBodyInfo>())
						outlines.Add(Figure.Select(f => ground + new WVec(f.X * 3 / 2, f.Y * 3 / 2 - 40, 0)).ToArray());
					else
					{
						var facing = a.TraitOrDefault<IFacing>()?.Facing ?? WAngle.Zero;
						var rot = WRot.FromYaw(facing);
						var len = halfSize * 6 / 5;
						var wid = halfSize * 3 / 4;
						outlines.Add(new[] { new WVec(-wid, -len, 0), new WVec(wid, -len, 0), new WVec(wid, len, 0), new WVec(-wid, len, 0) }
							.Select(v => ground + v.Rotate(rot)).ToArray());
					}
				}
			}

			quads = null;
		}

		// The building's outer edge: every cell side that does not face another footprint cell.
		void OutlineFootprint(HashSet<CPos> cells)
		{
			foreach (var c in cells)
			{
				var p = world.Map.CenterOfCell(c);
				var tl = new WPos(p.X - 512, p.Y - 512, center.Z);
				var tr = new WPos(p.X + 512, p.Y - 512, center.Z);
				var br = new WPos(p.X + 512, p.Y + 512, center.Z);
				var bl = new WPos(p.X - 512, p.Y + 512, center.Z);
				if (!cells.Contains(c + new CVec(0, -1)))
					outlines.Add([tl, tr]);
				if (!cells.Contains(c + new CVec(1, 0)))
					outlines.Add([tr, br]);
				if (!cells.Contains(c + new CVec(0, 1)))
					outlines.Add([br, bl]);
				if (!cells.Contains(c + new CVec(-1, 0)))
					outlines.Add([bl, tl]);
			}
		}

		public static bool IsWater(Map map, CPos cell)
		{
			if (!map.Contains(cell))
				return true;

			var type = map.GetTerrainInfo(cell).Type;
			return type == "Water" || type == "River";
		}

		// Smooth random field in 0..1, on a two-cell lattice.
		float Noise(int x, int y)
		{
			const int Lattice = 2048;
			var gx = (int)Math.Floor(x / (float)Lattice);
			var gy = (int)Math.Floor(y / (float)Lattice);
			var fx = (x - gx * Lattice) / (float)Lattice;
			var fy = (y - gy * Lattice) / (float)Lattice;
			fx = fx * fx * (3 - 2 * fx);
			fy = fy * fy * (3 - 2 * fy);
			var a = Hash(gx, gy);
			var b = Hash(gx + 1, gy);
			var c = Hash(gx, gy + 1);
			var d = Hash(gx + 1, gy + 1);
			return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
		}

		float Hash(int x, int y)
		{
			var h = unchecked((uint)(x * 374761393 + y * 668265263 + seed * 1442695041));
			h = unchecked((h ^ (h >> 13)) * 1274126177);
			return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
		}

		float Darkness(int dx, int dy)
		{
			var dist = MathF.Sqrt((float)dx * dx + (float)dy * dy);

			// A ragged edge: the reach varies by up to a fifth around the circle.
			var reach = radius * (0.85f + 0.2f * Noise(center.X + dx / 3, center.Y + dy / 3));
			if (dist >= reach)
				return 0f;

			var d = dist / reach;
			var falloff = (1 - d * d) * (1 - d * d);

			// Patchy: some ground burns darker than the rest.
			var v = falloff * (0.65f + 0.5f * Noise(center.X + dx, center.Y + dy));

			// In the shadow of something that stood closer in: shielded from the flash, so much lighter.
			if (dist > 0f)
			{
				var ux = dx / dist;
				var uy = dy / dist;
				foreach (var b in blockers)
				{
					if (Enter(ux, uy, b.X0 - center.X, b.Y0 - center.Y, b.X1 - center.X, b.Y1 - center.Y) < dist)
					{
						v *= 0.35f;
						break;
					}
				}
			}

			return Math.Clamp(v, 0f, 1f);
		}

		void Build()
		{
			quads = [];
			var n = radius / Step + 2;
			var size = 2 * n + 1;
			var dark = new float[size, size];
			for (var i = 0; i < size; i++)
				for (var j = 0; j < size; j++)
					dark[i, j] = Darkness((i - n) * Step, (j - n) * Step);

			for (var i = 0; i < size - 1; i++)
			{
				for (var j = 0; j < size - 1; j++)
				{
					var da = dark[i, j];
					var db = dark[i + 1, j];
					var dc = dark[i + 1, j + 1];
					var dd = dark[i, j + 1];
					if (da + db + dc + dd <= 0f)
						continue;

					var a = center + new WVec((i - n) * Step, (j - n) * Step, 0);
					if (IsWater(world.Map, world.Map.CellContaining(a + new WVec(Step / 2, Step / 2, 0))))
						continue;

					quads.Add(new Quad(a, a + new WVec(Step, 0, 0), a + new WVec(Step, Step, 0), a + new WVec(0, Step, 0), da, db, dc, dd));
				}
			}
		}

		// Distance along the ray from the origin at which it enters the box (infinity if it misses).
		static float Enter(float dx, float dy, float x0, float y0, float x1, float y1)
		{
			var tmin = 0f;
			var tmax = float.MaxValue;
			if (!Slab(dx, x0, x1, ref tmin, ref tmax) || !Slab(dy, y0, y1, ref tmin, ref tmax))
				return float.MaxValue;

			return tmin;
		}

		static bool Slab(float d, float lo, float hi, ref float tmin, ref float tmax)
		{
			if (MathF.Abs(d) < 1e-6f)
				return lo <= 0 && hi >= 0;

			var t0 = lo / d;
			var t1 = hi / d;
			if (t0 > t1)
				(t0, t1) = (t1, t0);

			tmin = Math.Max(tmin, t0);
			tmax = Math.Min(tmax, t1);
			return tmin <= tmax;
		}

		void IEffect.Tick(World world) { }

		IEnumerable<IRenderable> IEffect.Render(WorldRenderer wr)
		{
			if (world.ShroudObscures(center))
				yield break;

			// Shows through slowly as the fireball fades.
			var a = alpha * Math.Clamp((world.WorldTick - made - 20) / 120f, 0f, 1f);
			if (a <= 0f)
				yield break;

			if (quads == null)
				Build();

			yield return new ScorchRenderable(center, quads, outlines, a);
		}

		sealed record Quad(WPos A, WPos B, WPos C, WPos D, float Da, float Db, float Dc, float Dd);

		sealed class ScorchRenderable : IRenderable, IFinalizedRenderable
		{
			readonly List<Quad> quads;
			readonly List<WPos[]> outlines;
			readonly float alpha;

			public ScorchRenderable(WPos pos, List<Quad> quads, List<WPos[]> outlines, float alpha)
			{
				Pos = pos;
				this.quads = quads;
				this.outlines = outlines;
				this.alpha = alpha;
			}

			public WPos Pos { get; }

			// Under everything: it is part of the ground.
			public int ZOffset => -1000000;
			public bool IsDecoration => false;

			public IRenderable WithZOffset(int newOffset) { return this; }
			public IRenderable OffsetBy(in WVec vec) { return this; }
			public IRenderable AsDecoration() { return this; }

			public IFinalizedRenderable PrepareRender(WorldRenderer wr) { return this; }

			Color Shade(float d) => Color.FromArgb((int)(255 * alpha * d), Soot);

			public void Render(WorldRenderer wr)
			{
				var cr = Game.Renderer.WorldRgbaColorRenderer;
				foreach (var q in quads)
					cr.FillRect(wr.Screen3DPosition(q.A), wr.Screen3DPosition(q.B), wr.Screen3DPosition(q.C), wr.Screen3DPosition(q.D),
						Shade(q.Da), Shade(q.Db), Shade(q.Dc), Shade(q.Dd));

				// Burnt-in outlines: closed shapes for units, single edges for building footprints.
				var line = Color.FromArgb((int)(230 * Math.Min(1f, alpha / 0.72f)), Outline);
				foreach (var o in outlines)
				{
					var pts = o.Select(wr.Screen3DPosition).ToArray();
					if (pts.Length == 2)
						cr.DrawLine(pts[0], pts[1], 1.5f, line);
					else
						cr.DrawPolygon(pts, 1.5f, line);
				}
			}

			public void RenderDebugGeometry(WorldRenderer wr) { }
			public Rectangle ScreenBounds(WorldRenderer wr) { return Rectangle.Empty; }
		}
	}
}
