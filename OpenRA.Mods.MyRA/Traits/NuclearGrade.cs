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
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("After a nuke the whole game turns a hazy burnt orange (glsl/postprocess_nucleargrade.frag), then slowly returns to normal.",
		"Started by NuclearAftermathWarhead. Attach to the world actor.")]
	public class NuclearGradeInfo : TraitInfo
	{
		[Desc("Ticks to fade in.")]
		public readonly int FadeIn = 20;

		[Desc("Ticks at full strength.")]
		public readonly int Hold = 1000;

		[Desc("Ticks to fade back to normal colours.")]
		public readonly int FadeOut = 750;

		[Desc("Strength at full, 0 to 1.")]
		public readonly float Strength = 0.85f;

		[Desc("Music played while the grade lasts.")]
		public readonly string Music = null;

		public int Total => FadeIn + Hold + FadeOut;

		public override object Create(ActorInitializer init) { return new NuclearGrade(this); }
	}

	public class NuclearGrade : RenderPostProcessPassBase, ITick
	{
		public readonly NuclearGradeInfo Info;
		readonly NuclearGradeInfo info;
		int age = -1;
		ISound music;

		public NuclearGrade(NuclearGradeInfo info)
			: base("nucleargrade", PostProcessPassType.AfterWorld)
		{
			Info = this.info = info;
		}

		public void Start()
		{
			// A second nuke while the first is still showing restarts the hold without flickering or doubling the music.
			if (age < 0 && info.Music != null)
			{
				StopMusic();
				music = Game.Sound.Play(SoundType.UI, info.Music);
			}

			age = age >= 0 && age < info.FadeIn + info.Hold ? Math.Min(age, info.FadeIn) : 0;
		}

		void StopMusic()
		{
			if (music != null)
				Game.Sound.StopSound(music);

			music = null;
		}

		float CurrentStrength
		{
			get
			{
				if (age < 0)
					return 0;
				if (age < info.FadeIn)
					return info.Strength * age / info.FadeIn;
				if (age < info.FadeIn + info.Hold)
					return info.Strength;
				return info.Strength * Math.Max(0f, 1f - (float)(age - info.FadeIn - info.Hold) / info.FadeOut);
			}
		}

		void ITick.Tick(Actor self)
		{
			if (age < 0)
				return;

			if (++age > info.Total)
			{
				age = -1;
				StopMusic();
			}
		}

		protected override void Disposing(Actor self)
		{
			StopMusic();
			base.Disposing(self);
		}

		protected override bool Enabled => age >= 0;

		protected override void PrepareRender(WorldRenderer wr, IShader shader)
		{
			shader.SetVec("Strength", CurrentStrength);
			shader.SetVec("Pulse", 0.5f + 0.5f * MathF.Sin(age * 0.05f));
		}
	}
}
