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
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Activities;
using OpenRA.Effects;
using OpenRA.GameRules;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// ================================================================================================
	// Black Hawk fast-rope: unload while it is in the air and the commandos (Tanya, SEALs) slide down two ropes hung
	// from the helicopter's belly, one each side, taking turns, while it hovers. At the bottom each one steps off to
	// a free spot nearby. Anyone else on board still needs it to land.
	// ================================================================================================
	[Desc("Commandos can be unloaded while the aircraft is airborne: they slide down two ropes from its belly.")]
	public class FastRopeInfo : TraitInfo, Requires<CargoInfo>, Requires<AircraftInfo>
	{
		[Desc("Passenger actor types that can fast-rope.")]
		public readonly ImmutableArray<string> Types = ["e7", "seal"];

		[Desc("Ticks to slide down.")]
		public readonly int SlideTicks = 28;

		[Desc("Ticks between commandos leaving the helicopter (they alternate ropes).")]
		public readonly int Interval = 9;

		[Desc("Ropes hang this far either side of the centre line.")]
		public readonly WDist RopeSpacing = new(190);

		[Desc("Height of the rope anchors below the helicopter's centre.")]
		public readonly WDist BellyDrop = new(60);

		[Desc("Ticks the ropes keep hanging after the last commando is down, before they are cut loose.")]
		public readonly int RopeLinger = 25;

		public override object Create(ActorInitializer init) { return new FastRope(init.Self, this); }
	}

	public class FastRope : IResolveOrder, ITick, INotifyRemovedFromWorld
	{
		static readonly System.Reflection.FieldInfo CurrentActivityField =
			typeof(Actor).GetField("currentActivity", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

		internal readonly FastRopeInfo Info;
		readonly Actor self;
		readonly Cargo cargo;
		readonly IFacing facing;

		// Roping: who is still to go, where the helicopter is holding, and when the next one goes.
		readonly Queue<Actor> waiting = new();
		WPos hold;
		int nextDrop, lastLanded, quiet, turn;
		internal int Sliding;
		RopeEffect ropes;

		public FastRope(Actor self, FastRopeInfo info)
		{
			this.self = self;
			Info = info;
			cargo = self.Trait<Cargo>();
			facing = self.TraitOrDefault<IFacing>();
		}

		bool Active => ropes != null;

		// The top of rope 0 (left) or 1 (right), at the helicopter's belly.
		internal WPos Anchor(int rope)
		{
			var side = new WVec(rope == 0 ? -Info.RopeSpacing.Length : Info.RopeSpacing.Length, 0, -Info.BellyDrop.Length);
			var yaw = facing != null ? WRot.FromYaw(facing.Facing) : WRot.None;
			return self.CenterPosition + side.Rotate(yaw);
		}

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString != "Unload")
			{
				// Flying off or landing mid-drop: whoever is still aboard stays aboard.
				if (Active && order.OrderString is "Move" or "AttackMove" or "Attack" or "ReturnToBase" or "Land" or "Enter")
					waiting.Clear();

				return;
			}

			if (self.World.Map.DistanceAboveTerrain(self.CenterPosition).Length <= 0)
				return;

			var ropers = cargo.Passengers.Where(p => Info.Types.Contains(p.Info.Name) && !waiting.Contains(p)).ToList();
			if (ropers.Count == 0)
				return;

			foreach (var p in ropers)
				waiting.Enqueue(p);

			hold = self.CenterPosition;
			nextDrop = 0;
			quiet = 0;
			if (ropes == null)
			{
				ropes = new RopeEffect(self, this);
				self.World.AddFrameEndTask(w => w.Add(ropes));
			}

			// Only commandos aboard: hover here instead of landing to unload.
			if (cargo.Passengers.All(p => Info.Types.Contains(p.Info.Name)))
				self.World.AddFrameEndTask(_ => self.CancelActivity());
		}

		void ITick.Tick(Actor self)
		{
			if (!Active)
				return;

			// Moved off the spot or put down: stop sending people down.
			if (waiting.Count > 0 && ((self.CenterPosition - hold).HorizontalLengthSquared > 256 * 256
				|| self.World.Map.DistanceAboveTerrain(self.CenterPosition).Length <= 0))
				waiting.Clear();

			if (waiting.Count > 0 && --nextDrop <= 0)
			{
				var p = waiting.Dequeue();
				if (!p.IsDead && cargo.Passengers.Contains(p) && Drop(p, turn))
				{
					turn ^= 1;
					nextDrop = Info.Interval;
				}
			}

			// Everyone down (or, failing that, long enough for them to be): cut the ropes loose.
			quiet = waiting.Count > 0 ? 0 : quiet + 1;
			lastLanded = waiting.Count > 0 || Sliding > 0 ? 0 : lastLanded + 1;
			if (lastLanded > Info.RopeLinger || quiet > Info.SlideTicks + Info.RopeLinger + 50)
			{
				ropes.Release();
				ropes = null;
				lastLanded = quiet = 0;
				Sliding = 0;
			}
		}

		bool Drop(Actor p, int rope)
		{
			var map = self.World.Map;
			var top = Anchor(rope);
			var foot = map.CellContaining(top);
			var mobile = p.TraitOrDefault<Mobile>();
			var cell = map.FindTilesInCircle(foot, 4)
				.Where(c => map.Contains(c) && self.World.ActorMap.FreeSubCell(c) != SubCell.Invalid && (mobile == null || mobile.CanEnterCell(c)))
				.Cast<CPos?>().FirstOrDefault();
			if (cell == null || !map.Contains(foot))
				return false;

			cargo.Unload(self, p);
			Sliding++;
			var subCell = self.World.ActorMap.FreeSubCell(cell.Value);
			self.World.AddFrameEndTask(w =>
			{
				if (p.Disposed)
				{
					Sliding--;
					return;
				}

				// Stand in the landing cell (so nobody else takes it) but hang on the rope above the rope's foot.
				var pos = p.Trait<IPositionable>();
				pos.SetPosition(p, cell.Value, subCell);
				var spot = p.CenterPosition;
				var bottom = new WPos(top.X, top.Y, map.CenterOfCell(foot).Z);
				pos.SetCenterPosition(p, top);
				p.Trait<Passenger>().OnBeforeAddedToWorld(p);

				// Drop whatever the passenger had queued: the engine's "back into the cell grid" step sees them in
				// the air and opens a parachute. The rope brings them down instead.
				CurrentActivityField.SetValue(p, null);
				p.QueueActivity(new RopeDown(this, pos, top, bottom, Info.SlideTicks));
				if (mobile != null)
					p.QueueActivity(mobile.LocalMove(p, bottom, spot));

				w.Add(p);
			});

			return true;
		}

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self)
		{
			waiting.Clear();
			ropes?.Release();
			ropes = null;
		}
	}

	// A commando sliding down a rope: slow off the skid, fast in the middle, braking at the bottom.
	sealed class RopeDown : Activity
	{
		readonly FastRope rope;
		readonly IPositionable pos;
		readonly WPos top, bottom;
		readonly int total;
		int ticks;
		bool counted = true;

		public RopeDown(FastRope rope, IPositionable pos, WPos top, WPos bottom, int total)
		{
			this.rope = rope;
			this.pos = pos;
			this.top = top;
			this.bottom = bottom;
			this.total = Math.Max(1, total);
			IsInterruptible = false;
		}

		public override bool Tick(Actor self)
		{
			ticks++;
			var t = Math.Min(1f, (float)ticks / total);
			var eased = t * t * (3 - 2 * t);
			pos.SetCenterPosition(self, top + (bottom - top) * (int)(eased * 1024) / 1024);
			return ticks >= total;
		}

		protected override void OnLastRun(Actor self) { Down(); }

		protected override void OnActorDispose(Actor self) { Down(); }

		void Down()
		{
			if (!counted)
				return;

			counted = false;
			rope.Sliding--;
		}
	}

	// The two ropes hanging from the helicopter's belly to the ground while it is roping. When it is done the ropes
	// are cut loose and drop away.
	sealed class RopeEffect : IEffect
	{
		static readonly Color RopeColor = Color.FromArgb(255, 196, 176, 128);

		readonly Actor heli;
		readonly FastRope owner;
		WPos[] cutTops;
		WPos[] cutBottoms;
		int falling = -1;

		public RopeEffect(Actor heli, FastRope owner)
		{
			this.heli = heli;
			this.owner = owner;
		}

		// Cut loose: the ropes stay where they were and drop to the ground over a few ticks.
		public void Release()
		{
			if (falling >= 0)
				return;

			cutTops = new WPos[2];
			cutBottoms = new WPos[2];
			for (var i = 0; i < 2; i++)
			{
				cutTops[i] = heli.IsInWorld ? owner.Anchor(i) : heli.CenterPosition;
				cutBottoms[i] = Ground(cutTops[i]);
			}

			falling = 0;
		}

		WPos Ground(WPos p) => new(p.X, p.Y, heli.World.Map.CenterOfCell(heli.World.Map.CellContaining(p)).Z);

		public void Tick(World world)
		{
			if (falling < 0)
				return;

			if (++falling > 12)
				world.AddFrameEndTask(w => w.Remove(this));
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr)
		{
			for (var i = 0; i < 2; i++)
			{
				WPos top, bottom;
				if (falling < 0)
				{
					if (!heli.IsInWorld)
						yield break;

					top = owner.Anchor(i);
					bottom = Ground(top);
				}
				else
				{
					// The cut end falls the rest of the way: the rope shortens into a heap at its foot.
					bottom = cutBottoms[i];
					var drop = (cutTops[i].Z - bottom.Z) * falling / 12;
					top = cutTops[i] - new WVec(0, 0, drop);
				}

				if (top.Z - bottom.Z < 8)
					continue;

				yield return new BeamRenderable(top, 0, bottom - top, BeamRenderableShape.Flat, new WDist(40), RopeColor);
			}
		}
	}

	// ================================================================================================
	// B-52 carpet bombing: an attack order sends it on a straight bombing run through the target, half the map long,
	// dropping a bomb every BombSpacing as it goes. Then it is free again; another run can start after Cooldown.
	// ================================================================================================
	[Desc("Attack orders become a long straight bombing run through the target.")]
	public class CarpetBomberInfo : TraitInfo, Requires<AircraftInfo>, IRulesetLoaded
	{
		[WeaponReference]
		[FieldLoader.Require]
		public readonly string Weapon = null;

		[Desc("Length of the run as a fraction (percent) of the map's width.")]
		public readonly int RunPercent = 50;

		[Desc("Distance between bombs.")]
		public readonly WDist BombSpacing = new(640);

		[Desc("Ticks after a run before the next one can start.")]
		public readonly int Cooldown = 750;

		public WeaponInfo WeaponInfo { get; private set; }

		public void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			WeaponInfo = rules.Weapons[Weapon.ToLowerInvariant()];
		}

		public override object Create(ActorInitializer init) { return new CarpetBomber(this); }
	}

	public class CarpetBomber : IIssueOrder, IResolveOrder, IOrderVoice, ITick
	{
		public const string OrderID = "CarpetBomb";
		readonly CarpetBomberInfo info;
		Activity approach;
		Activity run;
		WPos runStart;
		WPos lastDrop;
		int readyAt;

		public CarpetBomber(CarpetBomberInfo info) { this.info = info; }

		IEnumerable<IOrderTargeter> IIssueOrder.Orders
		{
			get { yield return new BombTargeter(); }
		}

		Order IIssueOrder.IssueOrder(Actor self, IOrderTargeter order, in Target target, bool queued)
		{
			return order.OrderID == OrderID ? new Order(OrderID, self, target, queued) : null;
		}

		string IOrderVoice.VoicePhraseForOrder(Actor self, Order order) => null;

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString is not (OrderID or "Attack" or "ForceAttack") || order.Target.Type == TargetType.Invalid)
				return;

			var w = self.World;
			if (w.WorldTick < readyAt)
			{
				var secs = (readyAt - w.WorldTick) / 25;
				if (self.Owner == w.LocalPlayer)
					w.AddFrameEndTask(_ => TextNotificationsManager.AddSystemLine("B-52", $"Bomb bay reloading: next run in {secs} s."));

				return;
			}

			// A straight line through the target, along the way the bomber is heading to it.
			var target = order.Target.CenterPosition;
			var dir = target - self.CenterPosition;
			dir = new WVec(dir.X, dir.Y, 0);
			if (dir.HorizontalLengthSquared == 0)
				dir = new WVec(1024, 0, 0);

			var half = w.Map.MapSize.Width * 1024 * info.RunPercent / 200;
			var len = dir.HorizontalLength;
			var along = new WVec(dir.X * half / len, dir.Y * half / len, 0);
			runStart = Inside(w.Map, new WPos(target.X - along.X, target.Y - along.Y, 0));
			var runEnd = Inside(w.Map, new WPos(target.X + along.X, target.Y + along.Y, 0));

			// Line up, then the run.
			if (!order.Queued)
				self.CancelActivity();

			approach = new Fly(self, Target.FromPos(runStart), WDist.FromCells(5));
			run = new Fly(self, Target.FromPos(runEnd), WDist.FromCells(1));
			self.QueueActivity(approach);
			self.QueueActivity(run);
			lastDrop = runStart;
			self.ShowTargetLines();
		}

		// Keep the run over the playable map (it would otherwise fly off the edge).
		static WPos Inside(Map map, WPos p)
		{
			var c = map.Clamp(map.CellContaining(p));
			var centre = map.CenterOfCell(c);
			return new WPos(centre.X, centre.Y, 0);
		}

		// Right-click anywhere (ground or an enemy) to bomb it.
		sealed class BombTargeter : IOrderTargeter
		{
			public string OrderID => CarpetBomber.OrderID;
			public int OrderPriority => 6;
			public bool IsQueued { get; private set; }

			public bool TargetOverridesSelection(Actor self, in Target target, List<Actor> actorsAt, CPos xy, TargetModifiers modifiers) => true;

			public bool CanTarget(Actor self, in Target target, ref TargetModifiers modifiers, ref string cursor)
			{
				if (target.Type == TargetType.Terrain)
				{
					if (!modifiers.HasModifier(TargetModifiers.ForceAttack))
						return false;
				}
				else if (target.Type == TargetType.Actor || target.Type == TargetType.FrozenActor)
				{
					var owner = target.Type == TargetType.Actor ? target.Actor.Owner : target.FrozenActor.Owner;
					if (!modifiers.HasModifier(TargetModifiers.ForceAttack) && self.Owner.RelationshipWith(owner) != PlayerRelationship.Enemy)
						return false;
				}
				else
					return false;

				IsQueued = modifiers.HasModifier(TargetModifiers.ForceQueue);
				cursor = "attack";
				return true;
			}
		}

		void ITick.Tick(Actor self)
		{
			if (run == null || !self.IsInWorld)
				return;

			if (self.CurrentActivity != run)
			{
				// Run over (or interrupted): start the cooldown once it has actually started bombing.
				if (self.CurrentActivity != approach && approach != null)
				{
					approach = null;
					run = null;
					readyAt = self.World.WorldTick + info.Cooldown;
				}

				return;
			}

			var here = self.CenterPosition;
			var ground = new WPos(here.X, here.Y, 0);
			if ((ground - lastDrop).HorizontalLengthSquared < info.BombSpacing.LengthSquared)
				return;

			lastDrop = ground;
			var weapon = info.WeaponInfo;
			var args = new ProjectileArgs
			{
				Weapon = weapon,
				Facing = self.Orientation.Yaw,
				CurrentMuzzleFacing = () => self.Orientation.Yaw,
				DamageModifiers = [],
				InaccuracyModifiers = [],
				RangeModifiers = [],
				Source = here,
				CurrentSource = () => here,
				SourceActor = self,
				PassiveTarget = ground,
				GuidedTarget = Target.FromPos(ground),
			};

			var projectile = weapon.Projectile.Create(args);
			if (projectile != null)
				self.World.AddFrameEndTask(w => w.Add(projectile));

			if (weapon.Report != null && weapon.Report.Length > 0)
				Game.Sound.Play(SoundType.World, weapon.Report, self.World, here);
		}
	}
}
