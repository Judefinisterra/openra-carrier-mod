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
using System.Numerics;
using OpenRA.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	[Desc("Something a nuke sets alight (trees): it burns, chars black for good and smokes for a long while after.",
		"Lit by NuclearAftermathWarhead. Cosmetic only: the actor is not hurt.")]
	public class ScorchableInfo : TraitInfo
	{
		[Desc("Ticks it burns, darkening as it goes.")]
		public readonly int BurnTicks = 250;

		[Desc("Ticks it keeps smoking after it catches fire.")]
		public readonly int SmokeTicks = 4500;

		public readonly string FireImage = "fire";

		[SequenceReference(nameof(FireImage))]
		public readonly string[] FireSequences = ["1", "2", "3", "4"];

		public readonly string SmokeImage = "smoke_m";

		[SequenceReference(nameof(SmokeImage))]
		public readonly string SmokeSequence = "idle";

		[PaletteReference]
		public readonly string Palette = "effect";

		public override object Create(ActorInitializer init) => new Scorchable(init.Self, this);
	}

	public class Scorchable : IRenderModifier, ITick
	{
		sealed class Smoke
		{
			public Animation Anim;
			public WVec Offset;
			public bool Done;
		}

		static readonly Vector3 Charred = new(0.34f, 0.3f, 0.26f);

		readonly ScorchableInfo info;
		readonly Actor self;
		readonly List<Smoke> smoke = [];
		Animation[] flames = [];
		WVec[] flameOffsets = [];

		// Ticks until it catches fire (-1 = not lit), then ticks since it did (-1 = not burning yet).
		int delay = -1;
		int age = -1;

		public Scorchable(Actor self, ScorchableInfo info)
		{
			this.self = self;
			this.info = info;
		}

		public bool Lit => delay >= 0 || age >= 0;

		public void Ignite(int delayTicks)
		{
			if (!Lit)
				delay = Math.Max(0, delayTicks);
		}

		void ITick.Tick(Actor self)
		{
			if (delay >= 0 && --delay < 0)
			{
				age = 0;
				var r = Game.CosmeticRandom;
				flames = new Animation[2];
				flameOffsets = new WVec[2];
				for (var i = 0; i < flames.Length; i++)
				{
					flames[i] = new Animation(self.World, info.FireImage);
					flames[i].PlayRepeating(info.FireSequences[r.Next(info.FireSequences.Length)]);
					flameOffsets[i] = new WVec(r.Next(-260, 260), r.Next(-200, 120), 0);
				}
			}

			if (age < 0)
				return;

			age++;
			if (age < info.BurnTicks)
				foreach (var f in flames)
					f.Tick();

			// Thick smoke while it burns, a thin wisp for a long while after.
			var every = age < info.BurnTicks ? 9 : 40;
			if (age < info.SmokeTicks && age % every == 0)
			{
				var s = new Smoke
				{
					Anim = new Animation(self.World, info.SmokeImage),
					Offset = new WVec(Game.CosmeticRandom.Next(-200, 200), Game.CosmeticRandom.Next(-150, 50), 300),
				};
				s.Anim.PlayThen(info.SmokeSequence, () => s.Done = true);
				smoke.Add(s);
			}

			foreach (var s in smoke)
			{
				s.Anim.Tick();
				s.Offset += new WVec(1, 0, 8);
			}

			smoke.RemoveAll(s => s.Done);
		}

		IEnumerable<Rectangle> IRenderModifier.ModifyScreenBounds(Actor self, WorldRenderer wr, IEnumerable<Rectangle> bounds)
		{
			return bounds;
		}

		IEnumerable<IRenderable> IRenderModifier.ModifyRender(Actor self, WorldRenderer wr, IEnumerable<IRenderable> r)
		{
			if (age < 0)
			{
				foreach (var a in r)
					yield return a;

				yield break;
			}

			// Goes black as it burns, and stays that way.
			var tint = Vector3.Lerp(Vector3.One, Charred, Math.Min(1f, (float)age / info.BurnTicks));
			foreach (var a in r)
				yield return a is IModifyableRenderable m ? m.WithTint(m.Tint * tint, m.TintModifiers) : a;

			var palette = wr.Palette(info.Palette);
			if (age < info.BurnTicks)
				for (var i = 0; i < flames.Length; i++)
					foreach (var f in flames[i].Render(self.CenterPosition, flameOffsets[i], 0, palette))
						yield return f;

			foreach (var s in smoke)
				foreach (var f in s.Anim.Render(self.CenterPosition, s.Offset, 0, palette))
					yield return f;
		}
	}
}
