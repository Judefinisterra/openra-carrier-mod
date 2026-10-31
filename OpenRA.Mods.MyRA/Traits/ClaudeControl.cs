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
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Serves the local player's view of the game and accepts orders over HTTP on localhost,",
		"so an outside program (such as Claude Code) can play. GET /state, POST /command.")]
	public class ClaudeControlInfo : TraitInfo
	{
		[Desc("Port to listen on. Only localhost connections are accepted.")]
		public readonly int Port = 7777;

		[Desc("Milliseconds a request waits for the game to tick before failing (the game does not tick while paused).")]
		public readonly int RequestTimeout = 5000;

		public override object Create(ActorInitializer init) { return new ClaudeControl(this); }
	}

	public class ClaudeControl : ITick, IRenderAboveShroud, IWorldLoaded, INotifyActorDisposing
	{
		sealed class Request
		{
			public string Method;
			public string Path;
			public string Body;
			public readonly TaskCompletionSource<(int Status, string Json)> Result =
				new(TaskCreationOptions.RunContinuationsAsynchronously);
		}

		public sealed class CommandException : Exception
		{
			public CommandException(string message)
				: base(message) { }
		}

		static readonly JsonSerializerOptions JsonOptions = new()
		{
			PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		};

		readonly ClaudeControlInfo info;
		readonly ConcurrentQueue<Request> requests = new();
		World world;
		WorldRenderer worldRenderer;
		Player player;
		HttpListener listener;

		public ClaudeControl(ClaudeControlInfo info)
		{
			this.info = info;
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			// Only real games with a playing local player: not the menu shellmap, the editor, or observers.
			if (w.Type != WorldType.Regular || w.LocalPlayer == null || w.LocalPlayer.Spectating)
				return;

			world = w;
			worldRenderer = wr;
			player = w.LocalPlayer;

			try
			{
				listener = new HttpListener();
				listener.Prefixes.Add($"http://localhost:{info.Port}/");
				listener.Start();
			}
			catch (Exception e)
			{
				Log.Write("debug", $"ClaudeControl: could not listen on port {info.Port}: {e.Message}");
				TextNotificationsManager.AddSystemLine("Claude", $"Control server failed to start on port {info.Port}: {e.Message}");
				listener = null;
				return;
			}

			var thread = new Thread(Listen) { IsBackground = true, Name = "ClaudeControl" };
			thread.Start();
			TextNotificationsManager.AddSystemLine("Claude", $"Control server listening on http://localhost:{info.Port}/");
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			var l = listener;
			listener = null;
			try
			{
				l?.Close();
			}
			catch (ObjectDisposedException) { }

			while (requests.TryDequeue(out var r))
				r.Result.TrySetResult((503, Error("game ended")));
		}

		// Runs on the listener thread. Hands each request to the game thread and waits for the answer.
		void Listen()
		{
			while (true)
			{
				HttpListenerContext context;
				try
				{
					var l = listener;
					if (l == null)
						return;

					context = l.GetContext();
				}
				catch (Exception)
				{
					return;
				}

				ThreadPool.QueueUserWorkItem(_ => Serve(context));
			}
		}

		void Serve(HttpListenerContext context)
		{
			int status;
			string json;
			try
			{
				string body;
				using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
					body = reader.ReadToEnd();

				var request = new Request
				{
					Method = context.Request.HttpMethod,
					Path = context.Request.Url.AbsolutePath.TrimEnd('/'),
					Body = body
				};

				requests.Enqueue(request);
				if (request.Result.Task.Wait(info.RequestTimeout))
					(status, json) = request.Result.Task.Result;
				else
					(status, json) = (503, Error("the game did not respond in time (is the window minimised?)"));
			}
			catch (Exception e)
			{
				(status, json) = (500, Error(e.Message));
			}

			try
			{
				var bytes = Encoding.UTF8.GetBytes(json);
				context.Response.StatusCode = status;
				context.Response.ContentType = "application/json";
				context.Response.ContentLength64 = bytes.Length;
				context.Response.OutputStream.Write(bytes, 0, bytes.Length);
				context.Response.Close();
			}
			catch (Exception) { }
		}

		void ITick.Tick(Actor self) { ProcessRequests(); }

		// Also answer every rendered frame, so requests still work while the game is paused or over.
		IEnumerable<IRenderable> IRenderAboveShroud.RenderAboveShroud(Actor self, WorldRenderer wr)
		{
			ProcessRequests();
			return [];
		}

		bool IRenderAboveShroud.SpatiallyPartitionable => false;

		void ProcessRequests()
		{
			if (world == null)
				return;

			while (requests.TryDequeue(out var request))
			{
				try
				{
					request.Result.TrySetResult(Handle(request));
				}
				catch (CommandException e)
				{
					request.Result.TrySetResult((400, Error(e.Message)));
				}
				catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException)
				{
					// Malformed JSON, or a field of the wrong type.
					request.Result.TrySetResult((400, Error($"bad request: {e.Message}")));
				}
				catch (Exception e)
				{
					Log.Write("debug", $"ClaudeControl: {e}");
					request.Result.TrySetResult((500, Error(e.Message)));
				}
			}
		}

		(int Status, string Json) Handle(Request request)
		{
			switch ((request.Method, request.Path))
			{
				case ("GET", "/state"):
					return (200, Serialize(State()));
				case ("POST", "/command"):
				{
					if (player.WinState != WinState.Undefined || world.IsGameOver)
						throw new CommandException($"the game is over ({player.WinState})");

					using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
					return (200, Serialize(Command(doc.RootElement)));
				}

				default:
					return (404, Error("unknown endpoint: use GET /state or POST /command"));
			}
		}

		static string Error(string message) { return JsonSerializer.Serialize(new { ok = false, error = message }, JsonOptions); }
		static string Serialize(object o) { return JsonSerializer.Serialize(o, JsonOptions); }

		static string Category(ActorInfo ai)
		{
			if (ai.HasTraitInfo<BuildingInfo>())
				return "building";

			if (ai.HasTraitInfo<AircraftInfo>())
				return "aircraft";

			var types = ai.TraitInfos<TargetableInfo>().SelectMany(t => t.TargetTypes).ToHashSet();
			if (types.Contains("Infantry"))
				return "infantry";

			if (types.Contains("Ship"))
				return "ship";

			if (types.Contains("Vehicle"))
				return "vehicle";

			return "other";
		}

		int[] Cell(WPos pos)
		{
			var c = world.Map.CellContaining(pos);
			return [c.X, c.Y];
		}

		static string Name(ActorInfo ai)
		{
			var tooltip = ai.TraitInfos<TooltipInfo>().FirstOrDefault();
			return tooltip != null ? FluentProvider.GetMessage(tooltip.Name) : ai.Name;
		}

		bool IsEnemy(Player p) { return p != null && player.RelationshipWith(p) == PlayerRelationship.Enemy; }

		IEnumerable<ProductionQueue> Queues()
		{
			return world.ActorsWithTrait<ProductionQueue>()
				.Where(p => p.Actor.Owner == player && !p.Actor.IsDead && p.Actor.IsInWorld && p.Trait.Enabled)
				.Select(p => p.Trait);
		}

		object State()
		{
			var resources = player.PlayerActor.Trait<PlayerResources>();
			var power = player.PlayerActor.TraitOrDefault<PowerManager>();

			var own = world.Actors
				.Where(a => a.Owner == player && a.IsInWorld && !a.IsDead && a.OccupiesSpace != null && a.Info.HasTraitInfo<ISelectableInfo>())
				.Select(a =>
				{
					var health = a.TraitOrDefault<IHealth>();
					return new
					{
						id = a.ActorID,
						type = a.Info.Name,
						name = Name(a.Info),
						category = Category(a.Info),
						cell = Cell(a.CenterPosition),
						hp = health?.HP,
						max_hp = health?.MaxHP,
						idle = a.IsIdle,
						can_attack = a.Info.HasTraitInfo<AttackBaseInfo>(),
						can_move = a.Info.HasTraitInfo<IMoveInfo>()
					};
				})
				.ToList();

			var seenIds = new HashSet<uint>();
			var enemies = new List<object>();
			foreach (var a in world.Actors)
			{
				if (!a.IsInWorld || a.IsDead || a.OccupiesSpace == null || !a.Info.HasTraitInfo<ISelectableInfo>()
					|| !IsEnemy(a.Owner) || !a.CanBeViewedByPlayer(player))
					continue;

				var health = a.TraitOrDefault<IHealth>();
				seenIds.Add(a.ActorID);
				enemies.Add(new
				{
					id = a.ActorID,
					type = a.Info.Name,
					name = Name(a.Info),
					category = Category(a.Info),
					owner = a.Owner.ResolvedPlayerName,
					cell = Cell(a.CenterPosition),
					hp = health?.HP,
					max_hp = health?.MaxHP,
					last_seen_under_fog = false
				});
			}

			// Buildings seen earlier and now under the fog: the player knows they were there.
			foreach (var fa in player.FrozenActorLayer.FrozenActorsInRegion(world.Map.AllCells))
			{
				if (!fa.IsValid || !fa.Visible || seenIds.Contains(fa.ID) || !fa.Info.HasTraitInfo<ISelectableInfo>() || !IsEnemy(fa.Owner))
					continue;

				seenIds.Add(fa.ID);
				enemies.Add(new
				{
					id = fa.ID,
					type = fa.Info.Name,
					name = Name(fa.Info),
					category = Category(fa.Info),
					owner = fa.Owner.ResolvedPlayerName,
					cell = Cell(fa.CenterPosition),
					hp = (int?)fa.HP,
					max_hp = (int?)null,
					last_seen_under_fog = true
				});
			}

			var queues = Queues().Select(q => new
			{
				queue = q.Info.Type,
				producer = q.Actor == player.PlayerActor ? null : (uint?)q.Actor.ActorID,
				buildable = q.BuildableItems().Select(ai => new
				{
					type = ai.Name,
					name = Name(ai),
					cost = ai.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0
				}),
				in_progress = q.AllQueued().Select(i => new
				{
					type = i.Item,
					percent_done = i.TotalTime > 0 ? 100 - 100 * i.RemainingTime / i.TotalTime : 0,
					done = i.Done,
					paused = i.Paused
				})
			}).ToList();

			return new
			{
				tick = world.WorldTick,
				player = player.ResolvedPlayerName,
				faction = player.Faction.InternalName,
				win_state = player.WinState.ToString(),
				paused = world.Paused,
				game_over = world.IsGameOver,
				cash = resources.GetCashAndResources(),
				power = power == null ? null : new { provided = power.PowerProvided, drained = power.PowerDrained },
				map = new { title = world.Map.Title, width = world.Map.MapSize.Width, height = world.Map.MapSize.Height },
				units = own,
				enemies,
				production = queues
			};
		}

		object Command(JsonElement cmd)
		{
			var action = GetString(cmd, "action") ?? throw new CommandException("missing \"action\"");
			var queued = cmd.TryGetProperty("queued", out var q) && q.ValueKind == JsonValueKind.True;

			switch (action)
			{
				case "move":
				case "attack_move":
				case "assault_move":
				{
					var target = Target.FromCell(world, GetCell(cmd));
					var orderString = action switch { "move" => "Move", "attack_move" => "AttackMove", _ => "AssaultMove" };
					var units = SelectUnits(cmd).Where(a => a.Info.HasTraitInfo<IMoveInfo>()).ToList();
					foreach (var a in units)
						world.IssueOrder(new Order(orderString, a, target, queued));

					return Issued(units);
				}

				case "attack":
				{
					var target = GetTarget(cmd);
					var units = SelectUnits(cmd).Where(a => a.Info.HasTraitInfo<AttackBaseInfo>()).ToList();
					foreach (var a in units)
						world.IssueOrder(new Order("Attack", a, target, queued));

					return Issued(units);
				}

				case "attack_ground":
				{
					// Force-fire at a spot, e.g. to send a Drone Swarm that way.
					var target = Target.FromCell(world, GetCell(cmd));
					var units = SelectUnits(cmd).Where(a => a.Info.HasTraitInfo<AttackBaseInfo>()).ToList();
					foreach (var a in units)
						world.IssueOrder(new Order("ForceAttack", a, target, queued));

					return Issued(units);
				}

				case "stop":
				{
					var units = SelectUnits(cmd).ToList();
					foreach (var a in units)
						world.IssueOrder(new Order("Stop", a, false));

					return Issued(units);
				}

				case "deploy":
				{
					// Unpacks an MCV into a construction yard where it stands.
					var units = SelectUnits(cmd).Where(a => a.Info.HasTraitInfo<TransformsInfo>()).ToList();
					foreach (var a in units)
						world.IssueOrder(new Order("DeployTransform", a, queued));

					return Issued(units);
				}

				case "build":
				{
					var item = GetString(cmd, "type")
						?? throw new CommandException("missing \"type\" (the actor type to build, see production[].buildable)");
					var count = cmd.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? Math.Clamp(n, 1, 50) : 1;
					var queue = Queues().FirstOrDefault(pq => pq.BuildableItems().Any(ai => ai.Name == item))
						?? throw new CommandException($"\"{item}\" cannot be built right now (missing prerequisites, or no factory)");

					world.IssueOrder(Order.StartProduction(queue.Actor, item, count));
					return new { ok = true, queue = queue.Info.Type, type = item, count };
				}

				case "cancel":
				{
					var item = GetString(cmd, "type") ?? throw new CommandException("missing \"type\"");
					var count = cmd.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? Math.Clamp(n, 1, 50) : 1;
					var queue = Queues().FirstOrDefault(pq => pq.AllQueued().Any(i => i.Item == item))
						?? throw new CommandException($"\"{item}\" is not in production");

					world.IssueOrder(Order.CancelProduction(queue.Actor, item, count));
					return new { ok = true, queue = queue.Info.Type, type = item, count };
				}

				case "place":
					return Place(cmd);

				case "chat":
				{
					// Runs a chat command (e.g. "/claude ...") as if typed into the chat box, for testing.
					var text = GetString(cmd, "text") ?? throw new CommandException("missing \"text\"");
					if (!text.StartsWith('/'))
						throw new CommandException("only chat commands starting with / can be run");

					foreach (var t in world.WorldActor.TraitsImplementing<INotifyChat>())
						t.OnChat(player.PlayerName, text);

					return new { ok = true };
				}

				case "look":
				{
					// Moves the local camera to a cell.
					var cell = GetCell(cmd);
					worldRenderer.Viewport.Center(world.Map.CenterOfCell(cell));
					return new { ok = true };
				}

				case "nuke":
				{
					// Launches a charged sidebar nuke (Missile Silo) at a cell.
					var manager = player.PlayerActor.Trait<SupportPowerManager>();
					var power = manager.Powers.Values.FirstOrDefault(p => p.Info is NukePowerInfo && p.Ready)
						?? throw new CommandException("no nuke is ready");

					var cell = GetCell(cmd);
					world.IssueOrder(new Order(power.Key, player.PlayerActor, Target.FromCell(world, cell), false) { SuppressVisualFeedback = true });
					return new { ok = true, cell = new[] { cell.X, cell.Y } };
				}

				default:
					throw new CommandException(
						$"unknown action \"{action}\": use move, attack_move, assault_move, attack, attack_ground, stop, deploy, build, cancel, place, chat, nuke or look");
			}
		}

		static object Issued(List<Actor> units)
		{
			if (units.Count == 0)
				throw new CommandException("no matching units can do that");

			return new { ok = true, units = units.Select(a => a.ActorID) };
		}

		object Place(JsonElement cmd)
		{
			var item = GetString(cmd, "type") ?? throw new CommandException("missing \"type\"");
			var queue = Queues().FirstOrDefault(pq => pq.AllQueued().Any(i => i.Item == item && i.Done))
				?? throw new CommandException($"\"{item}\" is not finished building yet");

			var ai = world.Map.Rules.Actors[item];
			var bi = ai.TraitInfoOrDefault<BuildingInfo>() ?? throw new CommandException($"\"{item}\" is not a building");

			bool CanPlace(CPos c) => world.CanPlaceBuilding(c, ai, bi, null) && bi.IsCloseEnoughToBase(world, player, ai, c);

			CPos cell;
			if (cmd.TryGetProperty("cell", out _))
			{
				cell = GetCell(cmd);
				if (!CanPlace(cell))
					throw new CommandException($"cannot place \"{item}\" at {cell.X},{cell.Y}");
			}
			else
			{
				// No cell given: take the nearest free spot around the construction yard.
				var origin = world.Actors
					.Where(a => a.Owner == player && !a.IsDead && a.IsInWorld && a.Info.HasTraitInfo<BaseProviderInfo>())
					.Select(a => a.Location)
					.Cast<CPos?>()
					.FirstOrDefault() ?? throw new CommandException("no construction yard to build around");

				cell = world.Map.FindTilesInAnnulus(origin, 3, 16).Where(CanPlace).Cast<CPos?>().FirstOrDefault()
					?? throw new CommandException($"no room near the base for \"{item}\"");
			}

			world.IssueOrder(new Order("PlaceBuilding", player.PlayerActor, Target.FromCell(world, cell), false)
			{
				TargetString = item,
				ExtraLocation = new CPos(0, 0),
				ExtraData = queue.Actor.ActorID,
				SuppressVisualFeedback = true
			});

			return new { ok = true, type = item, cell = new[] { cell.X, cell.Y } };
		}

		// Units are picked by "units": [ids], or by "select": "all" | a category (infantry, vehicle, ship, aircraft) | an actor type (e1, 3tnk).
		// "idle_only": true keeps only units with nothing to do.
		IEnumerable<Actor> SelectUnits(JsonElement cmd)
		{
			IEnumerable<Actor> units;
			if (cmd.TryGetProperty("units", out var ids) && ids.ValueKind == JsonValueKind.Array)
			{
				var wanted = ids.EnumerateArray().Select(e => e.GetUInt32()).ToHashSet();
				units = world.Actors.Where(a => wanted.Contains(a.ActorID));
			}
			else
			{
				var select = GetString(cmd, "select")
					?? throw new CommandException("give \"units\": [ids] or \"select\": all | infantry | vehicle | ship | aircraft | <type>");
				var selectors = select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				units = world.Actors.Where(a => select == "all"
					? Category(a.Info) != "building"
					: selectors.Contains(Category(a.Info)) || selectors.Contains(a.Info.Name));
			}

			units = units.Where(a => a.Owner == player && a.IsInWorld && !a.IsDead
				&& a.Info.HasTraitInfo<ISelectableInfo>() && Category(a.Info) != "building"
				&& !a.Info.HasTraitInfo<HarvesterInfo>() && !a.Info.HasTraitInfo<CarrierChildInfo>());

			if (cmd.TryGetProperty("idle_only", out var idle) && idle.ValueKind == JsonValueKind.True)
				units = units.Where(a => a.IsIdle);

			return units;
		}

		CPos GetCell(JsonElement cmd)
		{
			if (!cmd.TryGetProperty("cell", out var c) || c.ValueKind != JsonValueKind.Array || c.GetArrayLength() != 2)
				throw new CommandException("missing \"cell\": [x, y]");

			var cell = new CPos(c[0].GetInt32(), c[1].GetInt32());
			if (!world.Map.Contains(cell))
				throw new CommandException($"cell {cell.X},{cell.Y} is off the map");

			return cell;
		}

		Target GetTarget(JsonElement cmd)
		{
			if (!cmd.TryGetProperty("target", out var t) || !t.TryGetUInt32(out var id))
				throw new CommandException("missing \"target\": an enemy id from /state");

			var actor = world.GetActorById(id);
			if (actor != null && actor.IsInWorld && !actor.IsDead && actor.CanBeViewedByPlayer(player))
				return Target.FromActor(actor);

			var frozen = player.FrozenActorLayer.FromID(id);
			if (frozen != null && frozen.IsValid && frozen.Visible)
				return Target.FromFrozenActor(frozen);

			throw new CommandException($"target {id} is not visible or no longer exists");
		}

		static string GetString(JsonElement cmd, string name)
		{
			return cmd.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
		}
	}
}
