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
using OpenRA.GameRules;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Effects;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Projectiles
{
	[Desc("A small attack drone: climbs off the launcher, chases its target at low altitude",
		"while darting from side to side, then dives onto it.")]
	public class FPVDroneInfo : IProjectileInfo
	{
		[Desc("Image containing the drone sequence.")]
		public readonly string Image = null;

		[SequenceReference(nameof(Image), allowNullImage: true)]
		[Desc("Looping sequence to play.")]
		public readonly string Sequence = "idle";

		[PaletteReference]
		public readonly string Palette = "effect";

		[Desc("Draw a shadow on the ground under the drone.")]
		public readonly bool Shadow = true;

		public readonly Color ShadowColor = Color.FromArgb(140, 0, 0, 0);

		[Desc("Top speed per tick.")]
		public readonly WDist Speed = new(300);

		[Desc("Speed per tick when launched.")]
		public readonly WDist LaunchSpeed = new(80);

		[Desc("Speed gained per tick until top speed.")]
		public readonly WDist Acceleration = new(30);

		[Desc("How hard the drone steers toward where it wants to go each tick, as a percentage.",
			"Higher turns tighter.")]
		public readonly int Agility = 35;

		[Desc("Largest sideways dart per tick.")]
		public readonly WDist Jitter = new(170);

		[Desc("Shortest and longest number of ticks between direction changes while darting.")]
		public readonly int2 JitterInterval = new(3, 8);

		[Desc("Height above the ground the drone flies at while chasing.")]
		public readonly WDist CruiseAltitude = new(640);

		[Desc("Horizontal distance from the target at which the drone stops darting and dives.")]
		public readonly WDist DiveDistance = new(1536);

		[Desc("Explode within this distance of the target.")]
		public readonly WDist CloseEnough = new(192);

		[Desc("Distance the drone can fly before its battery dies and it falls. Zero means twice the weapon range.")]
		public readonly WDist RangeLimit = WDist.Zero;

		[Desc("Ticks before the warhead is armed.")]
		public readonly int Arm = 3;

		[Desc("Length of the thin trail behind the drone, in ticks. Zero for none.")]
		public readonly int ContrailLength = 8;

		public readonly WDist ContrailWidth = new(12);

		public readonly Color ContrailColor = Color.FromArgb(110, 220, 220, 220);

		[Desc("Swarm mode: instead of following the target it was fired at, the drone flies off in that general",
			"direction and attacks the first enemy it sees within this radius. Zero turns swarm mode off.")]
		public readonly WDist SeekRadius = WDist.Zero;

		[Desc("Ticks between looks around for something to attack, in swarm mode.")]
		public readonly int SeekInterval = 4;

		[Desc("Swarm mode: largest angle each drone strays from the chosen direction, so a swarm fans out.")]
		public readonly WAngle LaunchSpread = WAngle.Zero;

		public IProjectile Create(ProjectileArgs args) { return new FPVDrone(this, args); }
	}

	public class FPVDrone : IProjectile, ISync
	{
		readonly FPVDroneInfo info;
		readonly ProjectileArgs args;
		readonly Animation anim;
		readonly ContrailRenderable contrail;
		readonly Vector3 shadowColor;
		readonly float shadowAlpha;
		readonly int rangeLimit;
		readonly Player owner;

		[VerifySync]
		WPos pos;

		WVec velocity;
		WVec dart;
		WPos targetPosition;
		int speed;
		int ticks;
		int nextDart;
		int distanceCovered;
		int nextSeek;
		WAngle facing;
		Actor prey;

		public FPVDrone(FPVDroneInfo info, ProjectileArgs args)
		{
			this.info = info;
			this.args = args;

			var world = args.SourceActor.World;
			pos = args.Source;
			targetPosition = args.PassiveTarget;
			facing = args.Facing;
			speed = info.LaunchSpeed.Length;

			var limit = info.RangeLimit != WDist.Zero ? info.RangeLimit.Length : args.Weapon.Range.Length * 2;
			rangeLimit = Common.Util.ApplyPercentageModifiers(limit, args.RangeModifiers);
			owner = args.SourceActor.Owner;

			if (info.SeekRadius.Length > 0)
			{
				// Head off in roughly the chosen direction, as far as the battery allows.
				var direction = args.PassiveTarget - args.Source;
				direction = new WVec(direction.X, direction.Y, 0);
				if (direction == WVec.Zero)
					direction = new WVec(0, -1024, 0).Rotate(WRot.FromYaw(facing));

				var spread = info.LaunchSpread.Angle;
				var stray = new WAngle(world.SharedRandom.Next(-spread, spread + 1));
				direction = Scale(direction, rangeLimit).Rotate(WRot.FromYaw(stray));
				targetPosition = args.Source + direction;
				targetPosition = new WPos(targetPosition.X, targetPosition.Y, 0);
				nextSeek = world.SharedRandom.Next(info.SeekInterval);
			}

			// Pop up and slightly forward off the operator's hands.
			velocity = new WVec(0, -speed / 2, speed).Rotate(WRot.FromYaw(facing));

			if (!string.IsNullOrEmpty(info.Image))
			{
				anim = new Animation(world, info.Image, () => facing);
				anim.PlayRepeating(info.Sequence);
			}

			if (info.ContrailLength > 0)
			{
				var end = Color.FromArgb(0, info.ContrailColor);
				contrail = new ContrailRenderable(world, args.SourceActor, info.ContrailColor, false, end, false,
					info.ContrailWidth, info.ContrailWidth, info.ContrailLength, 1, 2047);
			}

			var sColor = info.ShadowColor.ToVector4();
			shadowColor = sColor.AsVector3();
			shadowAlpha = sColor.W;
		}

		static WVec Scale(WVec v, int length)
		{
			var l = v.Length;
			return l == 0 ? WVec.Zero : v * length / l;
		}

		public void Tick(World world)
		{
			ticks++;
			anim?.Tick();

			if (info.SeekRadius.Length > 0)
			{
				if (prey != null && (prey.IsDead || !prey.IsInWorld))
					prey = null;

				if (prey == null && --nextSeek <= 0)
				{
					prey = FindPrey(world);
					nextSeek = info.SeekInterval;
				}

				if (prey != null)
					targetPosition = prey.CenterPosition;
			}
			else if (args.GuidedTarget.IsValidFor(args.SourceActor))
				targetPosition = args.GuidedTarget.CenterPosition;

			speed = Math.Min(speed + info.Acceleration.Length, info.Speed.Length);

			var toTarget = targetPosition - pos;
			var horizontal = new WVec(toTarget.X, toTarget.Y, 0);
			var horizontalDistance = horizontal.Length;
			var groundHeight = world.Map.DistanceAboveTerrain(pos).Length;

			WVec desired;
			if (horizontalDistance > info.DiveDistance.Length)
			{
				// Chase at cruise height, darting sideways at random intervals.
				if (--nextDart <= 0)
				{
					var side = new WVec(-horizontal.Y, horizontal.X, 0);
					var amount = world.SharedRandom.Next(-info.Jitter.Length, info.Jitter.Length + 1);
					var bob = world.SharedRandom.Next(-info.Jitter.Length / 4, info.Jitter.Length / 4 + 1);
					dart = Scale(side, Math.Abs(amount)) * Math.Sign(amount) + new WVec(0, 0, bob);
					nextDart = world.SharedRandom.Next(info.JitterInterval.X, info.JitterInterval.Y + 1);
				}

				var climb = info.CruiseAltitude.Length - groundHeight;
				desired = Scale(horizontal, speed) + new WVec(0, 0, Math.Clamp(climb, -speed / 2, speed / 2)) + dart;
			}
			else
			{
				// Close in: straight dive at the target.
				desired = Scale(toTarget, speed);
			}

			velocity += (desired - velocity) * info.Agility / 100;
			if (velocity.Length > speed * 3 / 2)
				velocity = Scale(velocity, speed * 3 / 2);

			pos += velocity;
			distanceCovered += velocity.Length;
			if (velocity.HorizontalLengthSquared > 0)
				facing = velocity.Yaw;

			contrail?.Update(pos);

			var cell = world.Map.CellContaining(pos);
			if ((targetPosition - pos).Length < info.CloseEnough.Length
				|| (ticks > info.Arm && world.Map.DistanceAboveTerrain(pos).Length < 0)
				|| distanceCovered > rangeLimit
				|| !world.Map.Contains(cell))
				Explode(world);
		}

		// The nearest enemy the warhead can hurt, skipping anything cloaked or disguised from the owner.
		Actor FindPrey(World world)
		{
			Actor best = null;
			var bestDistance = long.MaxValue;
			foreach (var a in world.FindActorsInCircle(pos, info.SeekRadius))
			{
				if (a.IsDead || !a.IsInWorld || a.Owner == null || owner.RelationshipWith(a.Owner) != PlayerRelationship.Enemy)
					continue;

				if (!args.Weapon.IsValidAgainst(a, args.SourceActor))
					continue;

				var hidden = false;
				foreach (var m in a.TraitsImplementing<IVisibilityModifier>())
				{
					if (!m.IsVisible(a, owner))
					{
						hidden = true;
						break;
					}
				}

				if (hidden)
					continue;

				var d = (a.CenterPosition - pos).HorizontalLengthSquared;
				if (d < bestDistance)
				{
					best = a;
					bestDistance = d;
				}
			}

			return best;
		}

		void Explode(World world)
		{
			if (contrail != null)
				world.AddFrameEndTask(w => w.Add(new ContrailFader(pos, contrail)));

			world.AddFrameEndTask(w => w.Remove(this));

			if (ticks <= info.Arm)
				return;

			var warheadArgs = new WarheadArgs(args)
			{
				ImpactOrientation = new WRot(WAngle.Zero, WAngle.Zero, facing),
				ImpactPosition = pos,
			};

			args.Weapon.Impact(Target.FromPos(pos), warheadArgs);
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr)
		{
			if (contrail != null)
				yield return contrail;

			var world = args.SourceActor.World;
			if (anim == null || world.FogObscures(pos))
				yield break;

			var palette = wr.Palette(info.Palette);
			if (info.Shadow)
			{
				var shadowPos = pos - new WVec(0, 0, world.Map.DistanceAboveTerrain(pos).Length);
				foreach (var r in anim.Render(shadowPos, palette))
					yield return ((IModifyableRenderable)r)
						.WithTint(shadowColor, ((IModifyableRenderable)r).TintModifiers | TintModifiers.ReplaceColor)
						.WithAlpha(shadowAlpha);
			}

			foreach (var r in anim.Render(pos, palette))
				yield return r;
		}
	}
}
