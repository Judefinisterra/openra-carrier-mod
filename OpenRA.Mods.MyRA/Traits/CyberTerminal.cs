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
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Widgets;
using OpenRA.Orders;
using OpenRA.Primitives;
using OpenRA.Traits;
using OpenRA.Widgets;

// The Cyber Attack hacking terminal: after you pick a target, a Matrix-green command window opens, "boots" (VPN,
// firewall bypass...), lists the exploits on the left, and you type the one to throw at that enemy.
namespace OpenRA.Mods.MyRA.Traits
{
	public static class CyberExploits
	{
		public static readonly (string Command, string Effect)[] All =
		[
			("wire_transfer", "steal 50% of their money"),
			("radar_blackout", "radar dark 1 min + fake alarms"),
			("grid_kill", "their power grid shuts down"),
			("turret_hijack", "their defences fire on them"),
			("harvester_scatter", "ore trucks drive off to the corners"),
			("tech_theft", "build their units and buildings"),
			("rig_blowout", "oil rigs: 30% blow up, rest burn"),
		];

		/// <summary>1-based exploit number for a typed command (also accepts ".exe" and the number itself), or 0.</summary>
		public static int Parse(string typed)
		{
			var t = typed.Trim().ToLowerInvariant();
			if (t.EndsWith(".exe", StringComparison.Ordinal))
				t = t[..^4];
			if (int.TryParse(t, out var n) && n >= 1 && n <= All.Length)
				return n;
			for (var i = 0; i < All.Length; i++)
				if (All[i].Command == t || All[i].Command.Replace("_", "") == t.Replace("_", "").Replace(" ", ""))
					return i + 1;
			return 0;
		}
	}

	// Picking the target: instead of firing straight away, open the terminal.
	public class SelectCyberTarget : SelectGenericPowerTarget
	{
		readonly SupportPowerManager manager;

		public SelectCyberTarget(string order, SupportPowerManager manager, SupportPowerInfo info)
			: base(order, manager, info)
		{
			this.manager = manager;
		}

		protected override IEnumerable<Order> OrderInner(World world, CPos cell, int2 worldPixel, MouseInput mi)
		{
			world.CancelInputMode();
			if (world.Map.Contains(cell))
				CyberTerminalWidget.Open(world, OrderKey, manager, Target.FromCell(world, cell));
			yield break;
		}
	}

	public class CyberTerminalWidget : Widget
	{
		static CyberTerminalWidget current;

		readonly World world;
		readonly string orderKey;
		readonly SupportPowerManager manager;
		readonly Target target;
		readonly string victimName;
		readonly List<(string Text, Color Color)> lines = [];
		readonly Queue<(string Text, Color Color, int Pause, string Suffix)> script = new();
		readonly Random rng = new();
		readonly List<(float X, float Y, float Speed, char[] Chars)> rain = [];

		string typing, typingSuffix;
		Color typingColor;
		int typed, pause, dots, ticks, closeIn = -1, fireIn = -1, chosen;
		bool prompt, closed, armed;
		string input = "";

		static readonly Color Green = Color.FromArgb(70, 255, 90);
		static readonly Color Dim = Color.FromArgb(30, 150, 50);
		static readonly Color Bright = Color.FromArgb(200, 255, 200);
		static readonly Color Red = Color.FromArgb(255, 70, 60);
		const string RainChars = "0123456789ABCDEFXZ$#@%&*+=<>{}[]/\\|";

		public static void Open(World world, string orderKey, SupportPowerManager manager, Target target)
		{
			current?.Close();
			var wdg = current = new CyberTerminalWidget(world, orderKey, manager, target);
			Game.RunAfterTick(() =>
			{
				if (wdg.closed)
					return;
				Ui.Root.AddChild(wdg);
				wdg.TakeKeyboardFocus();
			});
		}

		CyberTerminalWidget(World world, string orderKey, SupportPowerManager manager, Target target)
		{
			this.world = world;
			this.orderKey = orderKey;
			this.manager = manager;
			this.target = target;
			var res = Game.Renderer.Resolution;
			Bounds = new WidgetBounds(0, 0, res.Width, res.Height);

			var me = manager.Self.Owner;
			var pos = target.CenterPosition;
			var victim = world.ActorsHavingTrait<Building>()
				.Where(a => !a.IsDead && a.IsInWorld && !a.Owner.NonCombatant && a.Owner != me && me.RelationshipWith(a.Owner) != PlayerRelationship.Ally
					&& a.Owner.WinState == WinState.Undefined)
				.MinByOrDefault(a => (a.CenterPosition - pos).LengthSquared)?.Owner;
			victimName = victim?.ResolvedPlayerName ?? "UNKNOWN HOST";

			var ip = $"{rng.Next(11, 223)}.{rng.Next(0, 255)}.{rng.Next(0, 255)}.{rng.Next(2, 254)}";
			Say($"> target acquired: {victimName.ToUpperInvariant()}  [{ip}]", Dim, 8);
			Say("Establishing VPN Tunnel", Green, 18, " [OK]");
			Say("Accessing network", Green, 18, " [OK]");
			Say("Firewall bypass implant activation", Green, 22, " [ACTIVE]");
			Say("Secure anonymous tunnel established", Green, 14, " [OK]");
			Say("Exploit Arsenal Compatibility Scan", Green, 26, $" [{CyberExploits.All.Length} COMPATIBLE]");
			Say("", Green, 2);
			Say("Enter Exploit Command:", Bright, 0);

			var font = Game.Renderer.Fonts["TerminalSmall"];
			var cols = res.Width / 14;
			for (var i = 0; i < cols; i++)
				rain.Add((i * 14 + 4, rng.Next(-res.Height, 0), 2 + rng.Next(0, 5), Enumerable.Range(0, 18).Select(_ => RainChars[rng.Next(RainChars.Length)]).ToArray()));
		}

		void Say(string text, Color color, int pauseAfter, string suffix = null) => script.Enqueue((text, color, pauseAfter, suffix));

		void Close()
		{
			if (closed)
				return;
			closed = true;
			Game.RunAfterTick(() =>
			{
				YieldKeyboardFocus();
				Ui.Root.RemoveChild(this);
			});
			if (current == this)
				current = null;
		}

		bool PowerReady => manager.Powers.TryGetValue(orderKey, out var p) && p.Active && p.Ready;

		public override void Tick()
		{
			if (closed)
				return;
			ticks++;

			// Digital rain.
			var h = Game.Renderer.Resolution.Height;
			for (var i = 0; i < rain.Count; i++)
			{
				var r = rain[i];
				r.Y += r.Speed;
				if (r.Y > h + 300)
					r.Y = -rng.Next(50, 600);
				if (ticks % 4 == 0)
					r.Chars[rng.Next(r.Chars.Length)] = RainChars[rng.Next(RainChars.Length)];
				rain[i] = r;
			}

			if (fireIn > 0 && --fireIn == 0)
			{
				if (PowerReady)
					world.IssueOrder(new Order(orderKey, manager.Self, target, false) { ExtraData = (uint)chosen, SuppressVisualFeedback = true });
				Game.Sound.Play(SoundType.UI, "termok.wav");
				closeIn = 40;
			}

			if (closeIn > 0 && --closeIn == 0)
			{
				Close();
				return;
			}

			if (!PowerReady && fireIn < 0 && closeIn < 0)
			{
				Close();
				return;
			}

			if (pause > 0)
			{
				pause--;
				return;
			}

			// Type the current line out, a couple of characters a tick, then animate "..." and the suffix.
			if (typing != null)
			{
				if (typed < typing.Length)
				{
					typed = Math.Min(typing.Length, typed + 2);
					if (typed % 4 == 0)
						Game.Sound.Play(SoundType.UI, "termkey.wav");
					return;
				}

				if (typingSuffix != null && dots < 3)
				{
					if (ticks % 5 == 0)
						dots++;
					return;
				}

				lines.Add((typing + (typingSuffix != null ? "..." + typingSuffix : ""), typingColor));
				typing = null;
				return;
			}

			if (script.Count > 0)
			{
				var next = script.Dequeue();
				if (next.Text.Length == 0)
				{
					lines.Add(("", next.Color));
					pause = next.Pause;
					return;
				}

				typing = next.Text;
				typingColor = next.Color;
				typingSuffix = next.Suffix;
				typed = 0;
				dots = 0;
				pause = next.Pause;
				return;
			}

			if (armed)
			{
				armed = false;
				fireIn = 20;
			}
			else if (chosen == 0 && fireIn < 0 && closeIn < 0)
				prompt = true;
		}

		void Submit()
		{
			var cmd = input.Trim();
			lines.Add(("C:\\> " + cmd, Bright));
			input = "";
			if (cmd.Length == 0)
				return;
			var lower = cmd.ToLowerInvariant();
			if (lower is "exit" or "quit" or "logout")
			{
				Close();
				return;
			}

			if (lower is "help" or "?" or "dir" or "ls")
			{
				foreach (var (c, e) in CyberExploits.All)
					lines.Add(($"  {c,-18} {e}", Dim));
				return;
			}

			var n = CyberExploits.Parse(cmd);
			if (n == 0)
			{
				Game.Sound.Play(SoundType.UI, "termerr.wav");
				lines.Add(($"'{cmd}' is not recognized as an internal or external command.", Red));
				return;
			}

			chosen = n;
			prompt = false;
			var name = CyberExploits.All[n - 1].Command;
			Say($"Loading payload {name}.exe", Green, 8, " [LOADED]");
			Say($"Injecting into {victimName.ToUpperInvariant()} mainframe", Green, 10, " [INJECTED]");
			Say("Covering tracks", Green, 6, " [CLEAN]");
			Say($"EXPLOIT DELIVERED: {CyberExploits.All[n - 1].Effect.ToUpperInvariant()}", Bright, 0);
			armed = true;
		}

		public override bool HandleTextInput(string text)
		{
			if (closed || !prompt)
				return true;
			foreach (var ch in text)
				if (ch >= 32 && ch < 127 && input.Length < 40)
					input += ch;
			Game.Sound.Play(SoundType.UI, "termkey.wav");
			return true;
		}

		public override bool HandleKeyPress(KeyInput e)
		{
			if (closed)
				return false;
			if (e.Event != KeyInputEvent.Down)
				return true;
			if (e.Key == Keycode.ESCAPE)
			{
				Close();
				return true;
			}

			if (!prompt)
				return true;
			if (e.Key == Keycode.BACKSPACE && input.Length > 0)
				input = input[..^1];
			else if (e.Key is Keycode.RETURN or Keycode.KP_ENTER)
				Submit();
			return true;
		}

		public override bool HandleMouseInput(MouseInput mi) => !closed;

		public override void Draw()
		{
			if (closed)
				return;

			var res = Game.Renderer.Resolution;
			var font = Game.Renderer.Fonts["Terminal"];
			var small = Game.Renderer.Fonts["TerminalSmall"];
			WidgetUtils.FillRectWithColor(new Rectangle(0, 0, res.Width, res.Height), Color.FromArgb(215, 0, 6, 0));

			// Falling code behind everything.
			foreach (var r in rain)
				for (var k = 0; k < r.Chars.Length; k++)
				{
					var y = r.Y - k * 16;
					if (y < -16 || y > res.Height)
						continue;
					var a = k == 0 ? 200 : Math.Max(15, 120 - k * 7);
					small.DrawText(r.Chars[k].ToString(), new Vector2(r.X, y), k == 0 ? Color.FromArgb(a, 180, 255, 180) : Color.FromArgb(a, 30, 200, 60));
				}

			// The terminal window.
			var w = Math.Min(1100, res.Width - 80);
			var hgt = Math.Min(620, res.Height - 80);
			var x0 = (res.Width - w) / 2;
			var y0 = (res.Height - hgt) / 2;
			WidgetUtils.FillRectWithColor(new Rectangle(x0, y0, w, hgt), Color.FromArgb(235, 0, 0, 0));
			WidgetUtils.FillRectWithColor(new Rectangle(x0 - 2, y0 - 2, w + 4, 2), Green);
			WidgetUtils.FillRectWithColor(new Rectangle(x0 - 2, y0 + hgt, w + 4, 2), Green);
			WidgetUtils.FillRectWithColor(new Rectangle(x0 - 2, y0, 2, hgt), Green);
			WidgetUtils.FillRectWithColor(new Rectangle(x0 + w, y0, 2, hgt), Green);
			WidgetUtils.FillRectWithColor(new Rectangle(x0, y0, w, 24), Color.FromArgb(255, 0, 60, 10));
			small.DrawText("CYBER-OPS TERMINAL  -  C:\\Windows\\System32\\cmd.exe", new Vector2(x0 + 10, y0 + 5), Bright);
			small.DrawText("[ESC] abort", new Vector2(x0 + w - 110, y0 + 5), Dim);

			// Left: the exploit arsenal.
			var lw = 300;
			WidgetUtils.FillRectWithColor(new Rectangle(x0 + lw, y0 + 24, 1, hgt - 24), Dim);
			font.DrawText("EXPLOIT ARSENAL", new Vector2(x0 + 12, y0 + 36), Green);
			var ly = y0 + 64;
			for (var i = 0; i < CyberExploits.All.Length; i++)
			{
				var (cmd, eff) = CyberExploits.All[i];
				font.DrawText($"{i + 1}. {cmd}", new Vector2(x0 + 12, ly), chosen == i + 1 ? Bright : Green);
				small.DrawText("   " + eff, new Vector2(x0 + 12, ly + 18), Dim);
				ly += 44;
			}

			small.DrawText("Type a command (or its number)", new Vector2(x0 + 12, y0 + hgt - 54), Dim);
			small.DrawText("and press ENTER.  help / exit", new Vector2(x0 + 12, y0 + hgt - 36), Dim);

			// Right: the console.
			var cx = x0 + lw + 14;
			var lineH = 22;
			var maxLines = (hgt - 50) / lineH;
			var show = new List<(string, Color, bool)>(lines.Select(l => (l.Text, l.Color, false)));
			if (typing != null)
				show.Add((typing[..typed] + (typed >= typing.Length && typingSuffix != null ? new string('.', dots) : ""), typingColor, true));
			if (prompt)
				show.Add(("C:\\> " + input + ((ticks / 12) % 2 == 0 ? "_" : " "), Bright, false));

			var cy = y0 + 36;
			foreach (var (text, color, live) in show.Skip(Math.Max(0, show.Count - maxLines)))
			{
				font.DrawText(text, new Vector2(cx, cy), color);

				// The newest character glows.
				if (live && text.Length > 0 && typed < typing.Length)
				{
					var before = font.Measure(text[..^1]).X;
					font.DrawText(text[^1].ToString(), new Vector2(cx + before, cy), Color.White);
				}

				cy += lineH;
			}
		}
	}
}
