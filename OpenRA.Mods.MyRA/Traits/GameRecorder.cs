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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// Game recorder: writes a plain-text timeline of every match to
	//   <OpenRA support dir>/Logs/myra-games/game-<date>-<time>-<map>.txt
	// so a game can be reviewed afterwards: every 30 game-seconds a snapshot of each player (cash, income, army,
	// buildings, kills/losses, AI Commander orders, invasion state, whose troops are at whose base), plus
	// events as they happen (AI Commander orders, invasion steps, truces, buildings won/lost, defeats).
	// It only reads the game, so it can't affect sync; each client writes its own file.
	[TraitLocation(SystemActors.World)]
	[Desc("Writes a timeline of the game to Logs/myra-games for reviewing afterwards.")]
	public class GameRecorderInfo : TraitInfo
	{
		[Desc("Ticks between snapshots (750 = 30 seconds at normal speed).")]
		public readonly int Interval = 750;

		[Desc("Cells around a base that count as 'at their base'.")]
		public readonly int BaseRadius = 22;

		public override object Create(ActorInitializer init) { return new GameRecorder(this); }
	}

	public class GameRecorder : ITick, IWorldLoaded, IGameOver, INotifyActorDisposing
	{
		static readonly ConcurrentQueue<string> Pending = new();
		static GameRecorder current;

		readonly GameRecorderInfo info;
		World world;
		string path;
		readonly StringBuilder buffer = new();
		readonly Dictionary<Player, Dictionary<string, int>> lastCounts = [];
		readonly HashSet<(Player, Player)> lastTruces = [];
		readonly HashSet<Player> lastDefeated = [];
		int ticks;
		bool failed;

		public GameRecorder(GameRecorderInfo info) { this.info = info; }

		/// <summary>Add an event line (from any AI module) to the current game's record.</summary>
		public static void Note(Player p, string text)
		{
			if (current == null)
				return;

			var who = p == null ? "" : Name(p) + ": ";
			Pending.Enqueue($"[{Clock(current.world)}] {who}{text}");
		}

		static string Clock(World w)
		{
			var s = w.WorldTick / 25;
			return $"{s / 60:00}:{s % 60:00}";
		}

		static string Name(Player p) => p.ResolvedPlayerName.Trim();

		void IWorldLoaded.WorldLoaded(World w, OpenRA.Graphics.WorldRenderer wr)
		{
			world = w;
			if (w.Type != WorldType.Regular)
			{
				failed = true;
				return;
			}

			current = this;
			while (Pending.TryDequeue(out _)) { }

			try
			{
				var dir = Path.Combine(Platform.SupportDir, "Logs", "myra-games");
				Directory.CreateDirectory(dir);
				var map = new string(w.Map.Title.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
				path = Path.Combine(dir, $"game-{DateTime.Now:yyyyMMdd-HHmmss}-{map}.txt");
			}
			catch
			{
				failed = true;
				return;
			}

			buffer.AppendLine($"MyRA game record - {DateTime.Now:yyyy-MM-dd HH:mm} - map \"{w.Map.Title}\" ({w.Map.MapSize.Width}x{w.Map.MapSize.Height})");
			buffer.AppendLine("Clock is game time (mm:ss). Players:");
			foreach (var p in Playable())
			{
				var bot = p.IsBot ? $"AI ({p.BotType})" : "human";
				buffer.AppendLine($"  {Name(p)} - {FluentProvider.GetMessage(p.Faction.Name)} ({p.Faction.InternalName}), {bot}, team {p.PlayerReference.Team}, start {p.HomeLocation}");
			}

			buffer.AppendLine();
			Flush();
		}

		IEnumerable<Player> Playable() => world.Players.Where(p => p.Playable && !p.NonCombatant);

		void ITick.Tick(Actor self)
		{
			if (failed || world == null)
				return;

			while (Pending.TryDequeue(out var line))
				buffer.AppendLine(line);

			Events();

			if (++ticks % info.Interval == 0)
			{
				Snapshot();
				Flush();
			}
		}

		void Events()
		{
			// Truces made and broken.
			var truces = world.WorldActor.TraitOrDefault<TruceManager>();
			if (truces != null && world.WorldTick % 25 == 0)
			{
				var now = new HashSet<(Player, Player)>();
				var ps = Playable().ToList();
				for (var i = 0; i < ps.Count; i++)
					for (var j = i + 1; j < ps.Count; j++)
						if (truces.InTruce(ps[i], ps[j]))
							now.Add((ps[i], ps[j]));

				foreach (var t in now.Except(lastTruces))
					buffer.AppendLine($"[{Clock(world)}] TRUCE: {Name(t.Item1)} and {Name(t.Item2)}");
				foreach (var t in lastTruces.Except(now))
					buffer.AppendLine($"[{Clock(world)}] TRUCE ENDED: {Name(t.Item1)} and {Name(t.Item2)}");
				lastTruces.Clear();
				lastTruces.UnionWith(now);
			}

			foreach (var p in Playable().Where(p => p.WinState == WinState.Lost && !lastDefeated.Contains(p)))
			{
				lastDefeated.Add(p);
				buffer.AppendLine($"[{Clock(world)}] DEFEATED: {Name(p)}");
			}
		}

		static string Kind(Actor a)
		{
			if (a.Info.HasTraitInfo<BuildingInfo>())
				return "building";
			if (a.Info.HasTraitInfo<AircraftInfo>())
				return "air";
			var types = a.GetEnabledTargetTypes();
			if (types.Contains("Ship") || types.Contains("Submarine") || types.Contains("WaterActor"))
				return "naval";
			if (types.Contains("Infantry"))
				return "infantry";
			return "vehicle";
		}

		void Snapshot()
		{
			var all = world.Actors.Where(a => !a.IsDead && a.IsInWorld && a.Owner != null && a.Owner.Playable && !a.Owner.NonCombatant
				&& (a.Info.HasTraitInfo<BuildingInfo>() || a.Info.HasTraitInfo<IMoveInfo>()) && !a.Info.HasTraitInfo<HuskInfo>()).ToList();
			var players = Playable().ToList();
			var bases = players.ToDictionary(p => p, p =>
			{
				var b = world.ActorsHavingTrait<BaseBuilding>().FirstOrDefault(a => a.Owner == p && !a.IsDead && a.IsInWorld)
					?? all.Where(a => a.Owner == p && a.Info.HasTraitInfo<BuildingInfo>()).OrderBy(a => (a.Location - p.HomeLocation).LengthSquared).FirstOrDefault();
				return b?.Location ?? p.HomeLocation;
			});

			buffer.AppendLine($"===== {Clock(world)} snapshot =====");
			foreach (var p in players)
			{
				var mine = all.Where(a => a.Owner == p).ToList();
				var res = p.PlayerActor.TraitOrDefault<PlayerResources>();
				var power = p.PlayerActor.TraitOrDefault<PowerManager>();
				var stats = p.PlayerActor.TraitOrDefault<PlayerStatistics>();
				var state = p.WinState == WinState.Lost ? " [DEFEATED]" : p.WinState == WinState.Won ? " [WON]" : "";
				buffer.AppendLine($"{Name(p)}{state}: cash {res?.GetCashAndResources() ?? 0}, earned {res?.Earned ?? 0}, spent {res?.Spent ?? 0}, "
					+ $"power {power?.PowerProvided ?? 0}/{power?.PowerDrained ?? 0}, army value {stats?.ArmyValue ?? 0}, "
					+ $"kills {stats?.UnitsKilled ?? 0} units/{stats?.BuildingsKilled ?? 0} bldgs, lost {stats?.UnitsDead ?? 0} units/{stats?.BuildingsDead ?? 0} bldgs");

				foreach (var g in mine.GroupBy(Kind).OrderBy(g => g.Key))
					buffer.AppendLine($"    {g.Key} {g.Count()}: " + string.Join(", ", g.GroupBy(a => a.Info.Name).OrderByDescending(x => x.Count()).Select(x => $"{x.Key} {x.Count()}")));

				// What changed since the last snapshot.
				var counts = mine.GroupBy(a => a.Info.Name).ToDictionary(g => g.Key, g => g.Count());
				if (lastCounts.TryGetValue(p, out var before))
				{
					var diff = counts.Keys.Union(before.Keys)
						.Select(k => (k, d: counts.GetValueOrDefault(k) - before.GetValueOrDefault(k))).Where(x => x.d != 0).ToList();
					if (diff.Count > 0)
						buffer.AppendLine("    change: " + string.Join(", ", diff.OrderByDescending(x => x.d).Select(x => $"{(x.d > 0 ? "+" : "")}{x.d} {x.k}")));
				}

				lastCounts[p] = counts;

				// AI state: what the AI Commander told it and what its invasion code is doing.
				if (p.IsBot)
				{
					var cmd = p.PlayerActor.TraitsImplementing<AICommanderBotModule>().FirstOrDefault();
					if (cmd?.ActiveStance != null)
						buffer.AppendLine($"    commander: {cmd.ActiveStance}{(cmd.ActiveTarget != null ? " -> " + Name(cmd.ActiveTarget) : "")}");
					var amphib = p.PlayerActor.TraitsImplementing<AmphibiousAssaultBotModule>().FirstOrDefault(m => !m.IsTraitDisabled);
					if (amphib != null)
						buffer.AppendLine($"    invasion: {amphib.Status}");
				}

				// Whose troops are at this player's base (shows invasions that actually arrived).
				var visitors = all.Where(a => a.Owner != p && !a.Info.HasTraitInfo<BuildingInfo>() && (a.Location - bases[p]).LengthSquared <= info.BaseRadius * info.BaseRadius)
					.GroupBy(a => a.Owner).Where(g => players.Contains(g.Key)).ToList();
				if (visitors.Count > 0)
					buffer.AppendLine("    at this base: " + string.Join("; ", visitors.Select(g => $"{Name(g.Key)} {g.Count()} ({string.Join(", ", g.GroupBy(a => a.Info.Name).Select(x => $"{x.Key} {x.Count()}"))})")));
			}

			buffer.AppendLine();
		}

		void Flush()
		{
			if (failed || path == null || buffer.Length == 0)
				return;

			try
			{
				File.AppendAllText(path, buffer.ToString());
				buffer.Clear();
			}
			catch
			{
				// Disk trouble: keep the buffer and try again at the next snapshot.
				if (buffer.Length > 2_000_000)
					buffer.Clear();
			}
		}

		void IGameOver.GameOver(World w)
		{
			if (failed)
				return;

			while (Pending.TryDequeue(out var line))
				buffer.AppendLine(line);
			buffer.AppendLine($"[{Clock(w)}] GAME OVER: " + string.Join(", ", Playable().Select(p => $"{Name(p)} {p.WinState}")));
			Snapshot();
			Flush();
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			// Leaving the game (quit/surrender/back to menu): write whatever is left.
			while (Pending.TryDequeue(out var line))
				buffer.AppendLine(line);
			if (world != null)
				buffer.AppendLine($"[{Clock(world)}] (game closed)");
			Flush();
			if (current == this)
				current = null;
		}
	}
}
