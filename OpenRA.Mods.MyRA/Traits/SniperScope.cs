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
using OpenRA.Graphics;
using OpenRA.Mods.Common.Orders;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Widgets;
using OpenRA.Primitives;
using OpenRA.Traits;
using OpenRA.Widgets;

// Sniper scope mode: deploy a sniper (like an MCV) and it goes prone and the screen turns into a rifle scope.
// Aim with the mouse, hold Shift to steady your breath, click to fire, wheel to zoom, Esc/right-click to exit.
// Shots go through normal orders, so it works online; the scope itself is only drawn for the sniper's owner.
namespace OpenRA.Mods.MyRA.Traits
{
	[Desc("Deploy to go prone and open the sniper scope.")]
	public class SniperScopeInfo : TraitInfo, Requires<IHealthInfo>
	{
		[GrantedConditionReference]
		[Desc("Condition while the sniper is planted (prone, can't walk).")]
		public readonly string PlantedCondition = "planted";

		[WeaponReference]
		public readonly string Weapon = "SniperRifle";

		[Desc("Ticks between scoped shots.")]
		public readonly int ReloadTicks = 50;

		[Desc("How long the sniper shows up to the enemy after a shot.")]
		public readonly int RevealTicks = 75;

		[Desc("Hit chance at point blank and at full range (percent).")]
		public readonly int CloseHitChance = 97;
		public readonly int FarHitChance = 80;

		[Desc("Enemy units this close spot the sniper.")]
		public readonly WDist SpottedRange = WDist.FromCells(2);

		[Desc("How close to the crosshair a target must be (world units).")]
		public readonly int AimTolerance = 420;

		public override object Create(ActorInitializer init) { return new SniperScope(init.Self, this); }
	}

	public class SniperScope : IIssueOrder, IResolveOrder, IIssueDeployOrder, IOrderVoice, ITick, INotifyKilled
	{
		public readonly SniperScopeInfo Info;
		readonly Actor self;
		int token = Actor.InvalidConditionToken;
		public int Cooldown { get; private set; }
		public bool Planted => token != Actor.InvalidConditionToken;
		public WDist Range => self.World.Map.Rules.Weapons[Info.Weapon.ToLowerInvariant()].Range;

		public SniperScope(Actor self, SniperScopeInfo info)
		{
			this.self = self;
			Info = info;
		}

		IEnumerable<IOrderTargeter> IIssueOrder.Orders
		{
			get { yield return new DeployOrderTargeter("SniperDeploy", 5, () => Planted ? "deploy" : "deploy"); }
		}

		Order IIssueOrder.IssueOrder(Actor self, IOrderTargeter order, in Target target, bool queued)
		{
			return order.OrderID == "SniperDeploy" ? new Order(order.OrderID, self, queued) : null;
		}

		Order IIssueDeployOrder.IssueDeployOrder(Actor self, bool queued) { return new Order("SniperDeploy", self, queued); }
		bool IIssueDeployOrder.CanIssueDeployOrder(Actor self, bool queued) { return true; }

		string IOrderVoice.VoicePhraseForOrder(Actor self, Order order) { return order.OrderString == "SniperDeploy" ? "Action" : null; }

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			// Players' snipers hold fire so they can sneak in; Guard (G) lets them shoot on their own until
			// they're given some other order. Computer players' snipers fire at will.
			var autoTarget = self.TraitOrDefault<AutoTarget>();
			if (autoTarget != null && !self.Owner.IsBot && self.Owner.Playable)
			{
				if (order.OrderString == "Guard")
					autoTarget.SetStance(self, UnitStance.Defend);
				else if (order.OrderString is not ("SniperShot" or "SniperDeploy" or "SetUnitStance") && autoTarget.Stance != UnitStance.HoldFire)
					autoTarget.SetStance(self, UnitStance.HoldFire);
			}

			if (order.OrderString == "SniperDeploy")
			{
				if (Planted)
				{
					token = self.RevokeCondition(token);
					if (self.Owner == self.World.LocalPlayer)
						SniperScopeWidget.CloseFor(self);
				}
				else
				{
					self.CancelActivity();
					token = self.GrantCondition(Info.PlantedCondition);
					if (self.Owner == self.World.LocalPlayer && !self.World.IsReplay)
						SniperScopeWidget.Open(self, this);
				}
			}
			else if (order.OrderString == "SniperShot" && Planted && Cooldown <= 0 && order.Target.Type == TargetType.Terrain)
				Shoot(order.Target.CenterPosition);
		}

		void Shoot(WPos aim)
		{
			var w = self.World;
			var weapon = w.Map.Rules.Weapons[Info.Weapon.ToLowerInvariant()];
			var range = weapon.Range.Length;
			var d = aim - self.CenterPosition;
			if (d.HorizontalLengthSquared > (long)range * range)
				aim = self.CenterPosition + d * range / Math.Max(1, d.HorizontalLength);

			Cooldown = Info.ReloadTicks;
			foreach (var c in self.TraitsImplementing<Cloak>())
				c.Uncloak(Info.RevealTicks);
			if (weapon.Report.Length > 0)
				Game.Sound.Play(SoundType.World, weapon.Report, w, self.CenterPosition);

			// Whoever is closest to the crosshair (and not on our side) is the target.
			var victim = w.FindActorsInCircle(aim, new WDist(Info.AimTolerance))
				.Where(a => a != self && !a.IsDead && a.IsInWorld && a.Owner.RelationshipWith(self.Owner) != PlayerRelationship.Ally
					&& a.TraitOrDefault<IHealth>() != null && !a.GetEnabledTargetTypes().IsEmpty && a.CanBeViewedByPlayer(self.Owner))
				.MinByOrDefault(a => (a.CenterPosition - aim).HorizontalLengthSquared);

			var dist = (int)Math.Min(range, (aim - self.CenterPosition).HorizontalLength);
			var chance = Info.CloseHitChance - (Info.CloseHitChance - Info.FarHitChance) * dist / Math.Max(1, range);
			var hit = victim != null && w.SharedRandom.Next(100) < chance;
			var shooter = self;
			w.AddFrameEndTask(ww => ww.Add(new OpenRA.Effects.DelayedAction(Math.Max(1, dist / 1024 / 2), () =>
			{
				if (hit && !victim.IsDead && victim.IsInWorld)
					weapon.Impact(Target.FromActor(victim), shooter);
				else
				{
					var miss = aim + new WVec(ww.SharedRandom.Next(-512, 513), ww.SharedRandom.Next(-512, 513), 0);
					weapon.Impact(Target.FromPos(new WPos(miss.X, miss.Y, ww.Map.CenterOfCell(ww.Map.CellContaining(miss)).Z)), shooter);
				}
			})));
		}

		int spotTimer;

		void ITick.Tick(Actor self)
		{
			if (Cooldown > 0)
				Cooldown--;

			// Hidden until he fires or an enemy unit gets close.
			if (--spotTimer > 0 || !self.IsInWorld)
				return;
			spotTimer = 10;
			var spotted = self.World.FindActorsInCircle(self.CenterPosition, Info.SpottedRange)
				.Any(a => a != self && !a.IsDead && a.Owner.Playable && !a.Owner.NonCombatant
					&& a.Owner.RelationshipWith(self.Owner) == PlayerRelationship.Enemy && a.Info.HasTraitInfo<IMoveInfo>());
			if (spotted)
				foreach (var c in self.TraitsImplementing<Cloak>())
					c.Uncloak(30);
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			if (self.Owner == self.World.LocalPlayer)
				SniperScopeWidget.CloseFor(self, true);
		}

		/// <summary>Hit chance (percent) for a target at this point, for the scope readout.</summary>
		public int ChanceAt(WPos aim)
		{
			var range = Range.Length;
			var dist = (int)Math.Min(range, (aim - self.CenterPosition).HorizontalLength);
			return Info.CloseHitChance - (Info.CloseHitChance - Info.FarHitChance) * dist / Math.Max(1, range);
		}
	}

	[TraitLocation(SystemActors.World)]
	[Desc("Keeps the world renderer around for the sniper scope screen.")]
	public class SniperScopeUIInfo : TraitInfo<SniperScopeUI> { }

	public class SniperScopeUI : IWorldLoaded
	{
		public static WorldRenderer WorldRenderer;
		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr) { WorldRenderer = wr; }
	}

	// The scope screen. Covers the whole screen and takes all mouse and keyboard input while open.
	public class SniperScopeWidget : Widget
	{
		static readonly PropertyInfo ZoomProp = typeof(Viewport).GetProperty("Zoom");
		static SniperScopeWidget current;

		readonly Actor sniper;
		readonly SniperScope scope;
		readonly WorldRenderer wr;
		readonly float oldZoom;
		readonly WPos oldCenter;
		readonly int startHp;
		WPos look;
		float zoom = 3f, breath = 1f, t;
		bool holding, closed;
		int redFlash;

		public static void Open(Actor sniper, SniperScope scope)
		{
			if (SniperScopeUI.WorldRenderer == null)
				return;
			current?.Close();
			var wdg = current = new SniperScopeWidget(sniper, scope, SniperScopeUI.WorldRenderer);
			Game.RunAfterTick(() =>
			{
				if (wdg.closed)
					return;
				Ui.Root.AddChild(wdg);
				wdg.TakeKeyboardFocus();
			});
		}

		public static void CloseFor(Actor sniper, bool killed = false)
		{
			if (current != null && current.sniper == sniper)
			{
				if (killed)
					Game.Sound.Play(SoundType.UI, "kaboom12.aud");
				current.Close();
			}
		}

		SniperScopeWidget(Actor sniper, SniperScope scope, WorldRenderer wr)
		{
			this.sniper = sniper;
			this.scope = scope;
			this.wr = wr;
			var res = Game.Renderer.Resolution;
			Bounds = new WidgetBounds(0, 0, res.Width, res.Height);
			oldZoom = wr.Viewport.Zoom;
			oldCenter = wr.Viewport.CenterPosition;
			startHp = sniper.Trait<IHealth>().HP;
			var facing = sniper.TraitOrDefault<IFacing>()?.Facing ?? WAngle.Zero;
			look = sniper.CenterPosition + new WVec(0, -scope.Range.Length / 2, 0).Rotate(WRot.FromYaw(facing));
			SetZoom(zoom);
		}

		void SetZoom(float z) { ZoomProp.SetValue(wr.Viewport, z); }

		public void Close()
		{
			if (closed)
				return;
			closed = true;
			SetZoom(oldZoom);
			if (!sniper.IsDead && sniper.IsInWorld)
				wr.Viewport.Center(sniper.CenterPosition);
			else
				wr.Viewport.Center(oldCenter);
			Game.RunAfterTick(() =>
			{
				YieldKeyboardFocus();
				Ui.Root.RemoveChild(this);
			});
			if (current == this)
				current = null;
		}

		WPos Sway()
		{
			var amp = holding && breath > 0 ? 70 : 340;
			return new WPos((int)(Math.Sin(t * 1.3) * amp + Math.Sin(t * 0.47) * amp * 0.5), (int)(Math.Sin(t * 1.9 + 1) * amp * 0.8), 0);
		}

		WPos Aim => look + (Sway() - WPos.Zero);

		public override void Tick()
		{
			if (closed)
				return;
			if (sniper.IsDead || !sniper.IsInWorld || sniper.Owner != sniper.World.LocalPlayer || !scope.Planted)
			{
				Close();
				return;
			}

			// Got shot at? Flash red and pull the eye off the scope.
			if (sniper.Trait<IHealth>().HP < startHp)
			{
				redFlash = 12;
				Close();
				return;
			}

			t += 1 / 25f;
			holding = Game.GetModifierKeys().HasModifier(Modifiers.Shift);
			breath = holding ? Math.Max(0, breath - 1 / 75f) : Math.Min(1, breath + 1 / 150f);

			// The scope drifts toward the mouse pointer, within the rifle's range.
			var mouse = wr.ProjectedPosition(wr.Viewport.ViewToWorldPx(Viewport.LastMousePos));
			var d = mouse - look;
			look += new WVec(d.X / 8, d.Y / 8, 0);
			var off = look - sniper.CenterPosition;
			var range = scope.Range.Length;
			if (off.HorizontalLengthSquared > (long)range * range)
				look = sniper.CenterPosition + new WVec((int)((long)off.X * range / off.HorizontalLength), (int)((long)off.Y * range / off.HorizontalLength), 0);

			wr.Viewport.Center(Aim);
		}

		public override bool HandleMouseInput(MouseInput mi)
		{
			if (closed)
				return false;
			if (mi.Event == MouseInputEvent.Down && mi.Button == MouseButton.Left)
			{
				if (scope.Cooldown <= 0)
					sniper.World.IssueOrder(new Order("SniperShot", sniper, Target.FromPos(Aim), false));
				return true;
			}

			if (mi.Event == MouseInputEvent.Down && mi.Button == MouseButton.Right)
			{
				Close();
				return true;
			}

			if (mi.Event == MouseInputEvent.Scroll)
			{
				zoom = mi.Delta.Y > 0 ? Math.Min(5f, zoom + 1) : Math.Max(2f, zoom - 1);
				SetZoom(zoom);
				return true;
			}

			return true;
		}

		public override bool HandleKeyPress(KeyInput e)
		{
			if (!closed && e.Event == KeyInputEvent.Down && e.Key == Keycode.ESCAPE)
			{
				Close();
				return true;
			}

			return false;
		}

		static Vector3 P(float cx, float cy, float r, double a) => new((float)(cx + r * Math.Cos(a)), (float)(cy + r * Math.Sin(a)), 0);

		static void DrawScope(Size res)
		{
			var rgba = Game.Renderer.RgbaColorRenderer;
			float cx = res.Width / 2f, cy = res.Height / 2f, r = res.Height * 0.46f, outer = res.Width + res.Height;
			const int N = 72;

			// Black everywhere outside the lens.
			for (var i = 0; i < N; i++)
			{
				double a0 = i * 2 * Math.PI / N, a1 = (i + 1) * 2 * Math.PI / N;
				rgba.FillRect(P(cx, cy, r, a0), P(cx, cy, r, a1), P(cx, cy, outer, a1), P(cx, cy, outer, a0), Color.Black);
			}

			// Dark vignette at the edge of the lens and the metal rim.
			for (var k = 0; k < 12; k++)
			{
				var pts = new Vector3[N];
				for (var i = 0; i < N; i++)
					pts[i] = P(cx, cy, r - k * 3, i * 2 * Math.PI / N);
				rgba.DrawPolygon(pts, 3.5f, Color.FromArgb(Math.Max(0, 170 - k * 15), 0, 0, 0));
			}

			var rim = new Vector3[N];
			for (var i = 0; i < N; i++)
				rim[i] = P(cx, cy, r, i * 2 * Math.PI / N);
			rgba.DrawPolygon(rim, 6, Color.FromArgb(30, 30, 30));

			// Reticle: thick posts, thin crosshair, range dots, red centre dot.
			var k1 = Color.FromArgb(230, 10, 10, 10);
			var inner = r * 0.27f;
			rgba.FillRect(new Vector3(cx - r, cy - 3, 0), new Vector3(cx - inner, cy + 3, 0), k1);
			rgba.FillRect(new Vector3(cx + inner, cy - 3, 0), new Vector3(cx + r, cy + 3, 0), k1);
			rgba.FillRect(new Vector3(cx - 3, cy + inner, 0), new Vector3(cx + 3, cy + r, 0), k1);
			rgba.DrawLine(new Vector3(cx - inner, cy, 0), new Vector3(cx + inner, cy, 0), 1.5f, k1);
			rgba.DrawLine(new Vector3(cx, cy - r, 0), new Vector3(cx, cy + inner, 0), 1.5f, k1);
			var step = r * 0.06f;
			for (var d = 1; d <= 4; d++)
			{
				foreach (var (dx, dy) in new[] { (d, 0), (-d, 0), (0, d), (0, -d) })
				{
					var px = cx + dx * step;
					var py = cy + dy * step;
					rgba.FillEllipse(new Vector3(px - 2.5f, py - 2.5f, 0), new Vector3(px + 2.5f, py + 2.5f, 0), k1);
				}
			}

			rgba.FillEllipse(new Vector3(cx - 3, cy - 3, 0), new Vector3(cx + 3, cy + 3, 0), Color.FromArgb(230, 30, 30));
		}

		public override void Draw()
		{
			if (closed)
				return;
			var res = Game.Renderer.Resolution;
			DrawScope(res);

			var font = Game.Renderer.Fonts["Bold"];
			var small = Game.Renderer.Fonts["Regular"];
			var green = Color.FromArgb(120, 230, 120);
			var aim = Aim;
			var dist = (aim - sniper.CenterPosition).HorizontalLength;
			var cx = res.Width / 2;
			var cy = res.Height / 2;

			var target = sniper.World.FindActorsInCircle(aim, new WDist(scope.Info.AimTolerance))
				.Where(a => a != sniper && !a.IsDead && a.Owner.RelationshipWith(sniper.Owner) != PlayerRelationship.Ally
					&& a.TraitOrDefault<IHealth>() != null && a.CanBeViewedByPlayer(sniper.Owner))
				.MinByOrDefault(a => (a.CenterPosition - aim).HorizontalLengthSquared);

			small.DrawTextWithShadow($"RNG {dist * 100 / 1024} m", new Vector2(cx + 40, cy - 70), Color.White, Color.Black, 1);
			if (target != null)
			{
				var name = target.TraitsImplementing<Tooltip>().FirstOrDefault(tt => !tt.IsTraitDisabled)?.Info.Name;
				name = name != null ? FluentProvider.GetMessage(name) : target.Info.Name;
				font.DrawTextWithShadow($"{name}  HIT {scope.ChanceAt(aim)}%", new Vector2(cx + 40, cy - 50), Color.FromArgb(255, 210, 90), Color.Black, 1);
			}

			font.DrawTextWithShadow("SCOPE", new Vector2(24, 24), green, Color.Black, 1);
			small.DrawTextWithShadow(scope.Cooldown > 0 ? "CHAMBERING..." : "READY", new Vector2(24, 48), scope.Cooldown > 0 ? Color.Orange : green, Color.Black, 1);
			small.DrawTextWithShadow($"ZOOM {zoom:0}x", new Vector2(24, 68), green, Color.Black, 1);

			small.DrawTextWithShadow("BREATH  (hold Shift)", new Vector2(24, res.Height - 104), green, Color.Black, 1);
			WidgetUtils.FillRectWithColor(new Rectangle(24, res.Height - 84, 200, 14), Color.FromArgb(40, 60, 40));
			WidgetUtils.FillRectWithColor(new Rectangle(26, res.Height - 82, (int)(196 * breath), 10), breath > 0.25f ? green : Color.OrangeRed);
			small.DrawTextWithShadow("Click: fire    Wheel: zoom    Esc / right-click: exit    Deploy again: stand up",
				new Vector2(24, res.Height - 56), Color.LightGray, Color.Black, 1);
		}
	}
}
