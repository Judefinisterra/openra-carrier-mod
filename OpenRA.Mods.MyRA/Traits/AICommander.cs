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
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// ================================================================================================
	// AI Commander: lets a large language model (online or local) act as the general for the computer players.
	//
	// Switched on with the "AI Commander" checkbox in the lobby. Every computer player writes its own situation
	// report, and CallsPerMinute times a minute (default 1) all of them go to the model in ONE request. The reply
	// holds a separate short plan for each player (attack or defend, whom to attack, what to build, truces, which
	// support power to use, a line of trash talk), and each player only ever receives its own. The model is told to
	// decide for each army using only that army's report. The normal AI keeps playing; the model only steers it.
	//
	// Settings live in ai-commander.json in the OpenRA support folder (on Windows: %APPDATA%\OpenRA). A template
	// is written there the first time. Any OpenAI-compatible chat API works: Groq, Cerebras, Google Gemini,
	// OpenRouter, GitHub Models, Mistral, Meta's Model API, or a local Ollama/llama.cpp server.
	//
	// Only the host runs bots, and the plan is carried out with normal orders, so online games stay in sync.
	// ================================================================================================
	[TraitLocation(SystemActors.World)]
	[Desc("Adds the 'AI Commander' lobby checkbox. Attach to the world actor.")]
	public class AICommanderOptionsInfo : TraitInfo<AICommanderOptions>, ILobbyOptions
	{
		[FluentReference]
		public readonly string CheckboxLabel = "checkbox-aicommander.label";

		[FluentReference]
		public readonly string CheckboxDescription = "checkbox-aicommander.description";

		public readonly bool CheckboxEnabled = false;
		public readonly int CheckboxDisplayOrder = 61;

		IEnumerable<LobbyOption> ILobbyOptions.LobbyOptions(MapPreview map)
		{
			yield return new LobbyBooleanOption(map, "aicommander", CheckboxLabel, CheckboxDescription, true, CheckboxDisplayOrder, CheckboxEnabled, false);
		}
	}

	public class AICommanderOptions { }

	// The model connection, shared by every AI player on this computer (so the call limit is global).
	static class AICommanderLink
	{
		public sealed class Settings
		{
			public string Provider { get; set; } = "groq";
			public string Endpoint { get; set; } = "https://api.groq.com/openai/v1/chat/completions";
			public string Model { get; set; } = "openai/gpt-oss-120b";
			public string ApiKey { get; set; } = "";
			public string ApiKeyEnvironmentVariable { get; set; } = "AI_COMMANDER_API_KEY";
			public double CallsPerMinute { get; set; } = 1;
			public bool JsonMode { get; set; } = true;
			public int TimeoutSeconds { get; set; } = 20;
			public bool ShowTaunts { get; set; } = true;
			public string Personality { get; set; } = "";
			public string ReasoningEffort { get; set; } = "low";
		}

		static readonly object Sync = new();

		/// <summary>Set by the 'lips' cheat: print every plan the model gives an AI in the chat.</summary>
		public static bool ShowDirections;
		static Settings settings;
		static string settingsError;
		static HttpClient http;
		static DateTime lastCall = DateTime.MinValue;

		public static string SettingsPath => Path.Combine(Platform.SupportDir, "ai-commander.json");

		public static string LogPath => Path.Combine(Platform.SupportDir, "Logs", "ai-commander.log");

		static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

		public static void WriteLog(string text)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
				File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {text}\n");
			}
			catch
			{
				// Logging must never break the game.
			}
		}

		public static Settings Load(out string error)
		{
			lock (Sync)
			{
				if (settings != null || settingsError != null)
				{
					error = settingsError;
					return settings;
				}

				try
				{
					if (!File.Exists(SettingsPath))
					{
						Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
						File.WriteAllText(SettingsPath, Template);
					}

					settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new Settings();
					if (string.IsNullOrWhiteSpace(settings.ApiKey) && !string.IsNullOrWhiteSpace(settings.ApiKeyEnvironmentVariable))
						settings.ApiKey = Environment.GetEnvironmentVariable(settings.ApiKeyEnvironmentVariable) ?? "";

					var local = settings.Endpoint.Contains("localhost") || settings.Endpoint.Contains("127.0.0.1");
					if (string.IsNullOrWhiteSpace(settings.ApiKey) && !local)
						settingsError = $"no API key - put your key in {SettingsPath}";

					http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(5, settings.TimeoutSeconds)) };
				}
				catch (Exception e)
				{
					settingsError = $"couldn't read {SettingsPath}: {e.Message}";
				}

				error = settingsError;
				return settings;
			}
		}

		// One combined consultation: every computer player hands in its latest situation report, and at most
		// CallsPerMinute times a minute (default 1) they all go to the model in ONE request. The reply holds a
		// separate set of orders for each player, which is handed back to that player only.
		public sealed class Entry
		{
			public string Label;
			public string Header;
			public string Report;
			public Action<string> Deliver;
			public DateTime Updated;
		}

		// Player directives typed as "advice: ..." - sent with the next regular exchange (no extra call), then kept
		// in force for a few minutes so the model keeps following them.
		sealed class Directive
		{
			public string From;
			public string Text;
			public DateTime Given;
			public bool Delivered;
		}

		static readonly List<Directive> Directives = [];

		public static void AddDirective(string from, string text)
		{
			lock (Sync)
			{
				Directives.Add(new Directive { From = from, Text = text, Given = DateTime.UtcNow });
				if (Directives.Count > 8)
					Directives.RemoveAt(0);
			}

			WriteLog($"DIRECTIVE from {from}: {text}");
		}

		static string DirectiveBlock()
		{
			lock (Sync)
			{
				Directives.RemoveAll(d => DateTime.UtcNow - d.Given > TimeSpan.FromMinutes(5));
				if (Directives.Count == 0)
					return "";

				var lines = Directives.Select(d => (d.Delivered ? "  (still in force) " : "  NEW: ") + $"{d.From} says: \"{d.Text}\"");
				foreach (var d in Directives)
					d.Delivered = true;

				return "===== HUMAN PLAYER DIRECTIVES =====\n" + string.Join("\n", lines) + "\n";
			}
		}

		static readonly Dictionary<object, Entry> Pending = [];
		static readonly Dictionary<object, DateTime> Armies = [];
		static bool batchInFlight;
		static DateTime lastBatch = DateTime.MinValue;

		public static void Submit(object who, Entry e)
		{
			lock (Sync)
			{
				Pending[who] = e;
				Armies[who] = e.Updated;
			}
		}

		public static void Pump()
		{
			List<Entry> batch;
			lock (Sync)
			{
				if (settings == null || settingsError != null || batchInFlight)
					return;

				var now = DateTime.UtcNow;
				if (now - lastBatch < TimeSpan.FromSeconds(60.0 / Math.Max(0.1, settings.CallsPerMinute)))
					return;

				// Wait (up to 15 seconds) until every computer player still playing has handed in a report.
				foreach (var gone in Armies.Where(kv => now - kv.Value > TimeSpan.FromSeconds(30)).Select(kv => kv.Key).ToList())
					Armies.Remove(gone);

				var oldest = Pending.Values.Select(e => e.Updated).DefaultIfEmpty(now).Min();
				if (Armies.Keys.Any(a => !Pending.ContainsKey(a)) && now - oldest < TimeSpan.FromSeconds(15))
					return;

				// At the start of a game, give every computer player a moment to hand in its first report.
				if (lastBatch == DateTime.MinValue && now - oldest < TimeSpan.FromSeconds(10))
					return;

				// Only reports handed in recently (players still in a running game).
				batch = Pending.Values.Where(e => now - e.Updated < TimeSpan.FromSeconds(30)).OrderBy(e => e.Label).ToList();
				Pending.Clear();
				if (batch.Count == 0)
					return;

				batchInFlight = true;
				lastBatch = now;
			}

			var system = SystemPrompt(batch.Count);
			var user = DirectiveBlock() + string.Join("\n", batch.Select(e => $"===== ARMY: {e.Label} =====\n{e.Header}\n{e.Report}"));
			WriteLog($"BATCH ({batch.Count} armies) -> {Trim(user, 4000)}");
			Task.Run(async () =>
			{
				try
				{
					var answer = await Ask(system, user, 500 + 450 * batch.Count);
					if (answer != null)
						Distribute(answer, batch);
				}
				finally
				{
					lock (Sync)
						batchInFlight = false;
				}
			});
		}

		static void Distribute(string text, List<Entry> batch)
		{
			try
			{
				var start = text.IndexOf('{');
				var end = text.LastIndexOf('}');
				using var doc = JsonDocument.Parse(text[start..(end + 1)]);
				var root = doc.RootElement;
				if (root.TryGetProperty("orders", out var orders) && orders.ValueKind == JsonValueKind.Object)
					root = orders;

				foreach (var e in batch)
				{
					var found = root.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name.Trim(), e.Label, StringComparison.OrdinalIgnoreCase));
					if (found.Value.ValueKind == JsonValueKind.Object)
						e.Deliver(found.Value.GetRawText());
					else
						WriteLog($"no orders for {e.Label} in the reply");
				}
			}
			catch (Exception ex)
			{
				WriteLog($"couldn't split the reply ({ex.Message}): {Trim(text, 400)}");
			}
		}

		static string SystemPrompt(int armies)
		{
			return $"You are a referee advising {armies} SEPARATE, COMPETING generals in the real-time strategy game Red Alert (Cold War era). "
				+ "Each army section below is that general's private report, starting with its faction playbook: lean on the units and "
				+ "strategies in that playbook throughout the game.  Decide each army's orders independently, as if you were only that "
				+ "general: use ONLY the information in that army's own section, never anything from another section, and never coordinate the "
				+ "armies, favour one, or have them protect each other. Every other player is that army's rival unless its report says \"ally\" "
				+ "(set up in the game lobby); truces are temporary tools for its own benefit. Players are named \"Player N\".\n"
				+ "Reply with ONE JSON object (no other text): {\"orders\": {\"Player N\": {...}, ...}} with one entry per army section, using these keys:\n"
				+ "  \"stance\": \"attack\" | \"defend\" | \"economy\" | \"raid\" | \"invade\" (load troops into transports and land them on a rival across water)\n"
				+ "  \"target_player\": the enemy to attack (or null)\n"
				+ "  \"plan\": one sentence describing that army's strategy right now\n"
				+ "  \"build\": 2-6 exact items from that army's \"Can build\" lists, as [{\"item\": \"<id>\", \"count\": N}] (count = how many it should HAVE in total, not how many more: units up to 30, buildings 1-2)\n"
				+ "  \"propose_truce\": list of player names (or [])\n"
				+ "  \"break_truce\": list of player names (or [])\n"
				+ "  \"use_power\": a ready support power from that army's report (or null)\n"
				+ "  \"power_target\": player to use it on (or null)\n"
				+ "  \"taunt\": one short in-character line (or null)\n"
				+ "Pick each army's target on its own merits - who is nearest to it, who is attacking it, who is weakest - and remember every other "
				+ "player is a rival, computer or not: don't send every army at the same player, and don't always pick the strongest one. "
				+ "Ordering an attack on a truce partner ends that truce. Attack when the army is stronger than its target, defend when enemies are at its base, build what counters what its enemies field, "
				+ "and keep enough harvesters and power. If a rival is ACROSS WATER, armies can't walk there: order \"invade\", build a shipyard, "
				+ "transports and warships, and don't waste money on a land army that can't reach anyone. An island AI that isn't under attack and has "
				+ "transports should usually be ordered to \"invade\" - \"defend\"/\"economy\" leaves its army idle. One shipyard is enough; never order a second. Be creative and specific: mix unit types into combined-arms forces, use each faction's special "
				+ "units and buildings, add defences where it is being attacked, expand the economy when it is rich (a Seaport plus Offshore Oil Platforms "
				+ "pay $1000 per Oil Tanker trip, and a Container Ship pays $5000 per trip between its Seaport and a truce partner's - a good reason to "
				+ "propose truces), and vary the plan as the game "
				+ "changes rather than repeating the same orders. "
				+ "If a HUMAN PLAYER DIRECTIVES section is present, those are strategic directives from the human player: layer them on top "
				+ "of your normal judgement and carry them out through each army's orders (attack targets and directions, defence, what to "
				+ "build, naval/air/ground operations, expansion, retreats). If a directive asks the armies to coordinate or work together, "
				+ "do so for that directive even though they normally act alone. " + (settings?.Personality ?? "");
		}

		public static async Task<string> Ask(string system, string user, int maxTokens = 1200)
		{
			try
			{
				var s = settings;
				var body = new Dictionary<string, object>
				{
					["model"] = s.Model,
					["temperature"] = 0.5,
					["max_tokens"] = maxTokens,
					["messages"] = new object[]
					{
						new Dictionary<string, string> { ["role"] = "system", ["content"] = system },
						new Dictionary<string, string> { ["role"] = "user", ["content"] = user },
					},
				};

				if (s.JsonMode)
					body["response_format"] = new Dictionary<string, string> { ["type"] = "json_object" };

				// Reasoning models (gpt-oss) think before answering: keep that short so it stays quick and cheap.
				if (!string.IsNullOrWhiteSpace(s.ReasoningEffort) && s.Model.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase))
					body["reasoning_effort"] = s.ReasoningEffort;

				var reply = await Post(s, body);
				if (reply.Status == 400 && s.JsonMode)
				{
					// Some providers don't support JSON mode: ask again without it.
					body.Remove("response_format");
					reply = await Post(s, body);
				}

				if (reply.Status != 200)
				{
					WriteLog($"HTTP {reply.Status}: {Trim(reply.Body, 400)}");

					// Wrong model name, bad key, no access: retrying won't help, so stop calling for this session.
					if (reply.Status is 400 or 401 or 403 or 404)
						lock (Sync)
							settingsError = reply.Status switch
							{
								401 or 403 => $"the API key was refused (HTTP {reply.Status}) - check ApiKey in {SettingsPath}",
								404 => $"model \"{s.Model}\" not found (HTTP 404) - pick a current model in {SettingsPath}",
								_ => $"the request was rejected (HTTP 400) - see Logs/ai-commander.log",
							};

					return null;
				}

				using var doc = JsonDocument.Parse(reply.Body);
				return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
			}
			catch (Exception e)
			{
				WriteLog("call failed: " + e.Message);
				return null;
			}

		}

		static async Task<(int Status, string Body)> Post(Settings s, Dictionary<string, object> body)
		{
			using var request = new HttpRequestMessage(HttpMethod.Post, s.Endpoint)
			{
				Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
			};

			if (!string.IsNullOrWhiteSpace(s.ApiKey))
				request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + s.ApiKey.Trim());

			using var response = await http.SendAsync(request);
			return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
		}

		public static string Trim(string s, int n) => s == null ? "" : s.Length <= n ? s : s[..n] + "...";

		const string Template = @"{
  // AI Commander settings for Red Alert (Modded). Tick 'AI Commander' in the game lobby to use it.
  // Any OpenAI-compatible chat API works. Pick one block of three lines (Provider/Endpoint/Model), paste your key.
  //
  // FREE (no credit card):
  //   Groq      - Endpoint: https://api.groq.com/openai/v1/chat/completions
  //               Model:    openai/gpt-oss-120b               (key: console.groq.com)
  //               (or qwen/qwen3.6-27b; Groq's free plan allows about 200,000 tokens a day per model -
  //               one consultation a minute for 3 computer players is roughly 1.5 hours of play a day)
  //   Cerebras  - Endpoint: https://api.cerebras.ai/v1/chat/completions
  //               Model:    llama-3.3-70b                     (key: cloud.cerebras.ai)
  //   Gemini    - Endpoint: https://generativelanguage.googleapis.com/v1beta/openai/chat/completions
  //               Model:    gemini-flash-lite-latest          (key: aistudio.google.com)
  //   OpenRouter- Endpoint: https://openrouter.ai/api/v1/chat/completions
  //               Model:    meta-llama/llama-3.3-70b-instruct:free   (key: openrouter.ai; 50 free calls/day)
  // PAID:
  //   Meta Muse Spark - Endpoint/Model from Meta's Model API docs ($20 free credit for new US accounts)
  // LOCAL (free, runs on your computer, no key):
  //   Ollama    - Endpoint: http://localhost:11434/v1/chat/completions
  //               Model:    qwen3.5:4b
  //
  // Model names change over time - check your provider's model list if you get an error.
  // Instead of pasting the key here you can set the environment variable named in ApiKeyEnvironmentVariable.
  // Problems are written to Logs/ai-commander.log in this folder.
  ""Provider"": ""groq"",
  ""Endpoint"": ""https://api.groq.com/openai/v1/chat/completions"",
  ""Model"": ""openai/gpt-oss-120b"",
  ""ApiKey"": """",
  ""ApiKeyEnvironmentVariable"": ""AI_COMMANDER_API_KEY"",
  // Consultations per minute. Each one covers ALL computer players in a single request.
  ""CallsPerMinute"": 1,
  ""JsonMode"": true,
  ""TimeoutSeconds"": 20,
  // For reasoning models (gpt-oss): how hard to think before answering - low, medium or high.
  ""ReasoningEffort"": ""low"",
  // Show the AI's one-line taunts in the chat.
  ""ShowTaunts"": true,
  // Optional extra instructions for the AI, e.g. ""Play like a cautious turtle."" or ""Be ruthless.""
  ""Personality"": """"
}
";
	}

	[TraitLocation(SystemActors.Player)]
	[Desc("AI: when the lobby's AI Commander option is on, a language model steers this computer player's strategy.")]
	public class AICommanderBotModuleInfo : ConditionalTraitInfo
	{
		[Desc("Seconds between handing in fresh reports for the next combined consultation.")]
		public readonly int ReportIntervalSeconds = 10;

		[Desc("How long a plan stays in force without a new answer (ticks).")]
		public readonly int PlanTicks = 2250;

		[Desc("Units to queue per plan in the requested build focus.")]
		public readonly int BuildPerPlan = 4;

		[Desc("Army units sent per attack wave (taken from units idling at the base).")]
		public readonly int MaxWaveSize = 30;

		public readonly int Interval = 50;

		public override object Create(ActorInitializer init) { return new AICommanderBotModule(init.Self, this); }
	}

	public class AICommanderBotModule : ConditionalTrait<AICommanderBotModuleInfo>, IBotTick, IBotRequestPauseUnitProduction
	{
		sealed class Plan
		{
			public string Stance = "defend";
			public Player Target;
			public string[] BuildFocus = [];
			public (string Item, int Count)[] BuildItems = [];
			public string Summary;
			public Player[] ProposeTruce = [];
			public Player[] BreakTruce = [];
			public string Power;
			public Player PowerTarget;
			public string Taunt;
			public int Received;
		}

		readonly World world;
		readonly Player player;
		readonly ConcurrentQueue<string> replies = new();
		readonly List<Actor> wave = [];
		bool enabled, warned;

		/// <summary>The commander's current stance, or null when no order is in force.</summary>
		public string ActiveStance => plan != null && world.WorldTick - plan.Received <= Info.PlanTicks ? plan.Stance : null;

		/// <summary>The player the current order says to attack/raid/invade (null if none).</summary>
		public Player ActiveTarget => ActiveStance is "attack" or "raid" or "invade" ? plan.Target : null;

		/// <summary>True while the order is to stay home (defend or economy): no new offensives.</summary>
		public bool HoldOffensives => ActiveStance is "defend" or "economy";

		/// <summary>The AI Commander module of a player whose orders are in force, if any.</summary>
		public static AICommanderBotModule For(Player p) =>
			p.PlayerActor.TraitsImplementing<AICommanderBotModule>().FirstOrDefault(m => m.enabled && m.ActiveStance != null);
		int ticks, lastReport = -100000, waveSent;
		Plan plan;
		TruceManager truces;

		public AICommanderBotModule(Actor self, AICommanderBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		protected override void Created(Actor self)
		{
			enabled = world.LobbyInfo.GlobalSettings.OptionOrDefault("aicommander", false);
			truces = world.WorldActor.TraitOrDefault<TruceManager>();
			ticks = world.LocalRandom.Next(Info.Interval);
			base.Created(self);
		}

		// Game time at normal speed (25 ticks a second), whatever speed the game is actually running at.
		static int TicksPerSecond(World w) => 25;

		bool Alive(Actor a) => a != null && !a.IsDead && a.IsInWorld;

		// An opponent: an enemy, or someone we're only in a truce with (a truce makes the two sides neutral).
		bool Rival(Player p) => p != null && p.Playable && !p.NonCombatant && p != player && p.WinState == WinState.Undefined
			&& (player.RelationshipWith(p) == PlayerRelationship.Enemy || (truces != null && truces.InTruce(player, p)));

		void IBotTick.BotTick(IBot bot)
		{
			if (!enabled || ++ticks < Info.Interval)
				return;

			ticks = 0;
			var settings = AICommanderLink.Load(out var error);
			if (error != null || settings == null)
			{
				if (!warned)
				{
					warned = true;
					AICommanderLink.WriteLog($"{player.ResolvedPlayerName.Trim()}: AI Commander is off - {error}");
					// Shown once on the host's screen.
					TextNotificationsManager.AddSystemLine("AI Commander", "Off: " + error);
				}

				return;
			}

			while (replies.TryDequeue(out var text))
				Apply(bot, text);

			if (plan != null)
				Carry(bot);

			// Hand in a fresh report every few seconds; the combined consultation picks up the latest one.
			if (world.WorldTick - lastReport >= Info.ReportIntervalSeconds * TicksPerSecond(world))
			{
				lastReport = world.WorldTick;
				AICommanderLink.Submit(this, new AICommanderLink.Entry
				{
					Label = Label(player),
					Header = $"Faction: {player.Faction.InternalName}. Playbook: {FactionHint(player.Faction)}",
					Report = Report(),
					Deliver = replies.Enqueue,
					Updated = DateTime.UtcNow,
				});
			}

			AICommanderLink.Pump();
		}

		// --------------------------------------------------------------------------------------------
		// The situation report.
		// Each army's playbook: what its side is good at and which units and strategies to lean on all game.
		// Only the army's own playbook goes into its section (nothing about the other armies).
		static string FactionHint(FactionInfo f)
		{
			const string Allied = "ALLIED side: fast and flexible. Light and Medium Tanks, Rangers, artillery for sieges, Rocket Soldiers against armour, "
				+ "Mobile Gap Generators to hide forces, spies and thieves, Tanya for raids. Strongest navy: Destroyers, Cruisers, the Aircraft Carrier. "
				+ "Late game: Chronosphere and GPS. Win with mobility, artillery from range and naval bombardment rather than head-on tank fights.";
			const string Soviet = "SOVIET side: brute force. Heavy and Mammoth Tanks, V2 rockets, Tesla Coils and Flame Towers for defence, "
				+ "MiGs, Yaks and Hinds, submarines and the Typhoon Sub-Carrier, Shock Troopers, FPV drone operators and the Drone Swarm truck. "
				+ "Late game: Iron Curtain, nukes, paratroopers and Badger bombers. Win with massed heavy armour and overwhelming pushes.";
			return f.InternalName switch
			{
				"france" => Allied + " France can build fake buildings to mislead the enemy.",
				"england" or "germany" or "allies" => Allied,
				"russia" => Soviet + " Russia gets Shock Troopers early.",
				"ukraine" => Soviet + " Ukraine can drop Parabombs from the air.",
				"soviet" => Soviet,
				"tribal" => "TRIBAL KINGDOM: wins by sheer numbers and animals. Mass cheap Tribal Heroes and Lions from the barracks, RPG fighters "
					+ "against vehicles, War Elephants to smash tanks and buildings, fast Rhino riders to raid harvesters. Witch Doctors curse enemy "
					+ "groups, Pit Diggers lay ambush pits, Drum Circles and Spirit Totems boost troops, canoes ferry warriors over water. "
					+ "No high tech: overwhelm early and keep the pressure on.",
				"iran" => "IRAN (Soviet-based): asymmetric war. Shahed drone swarms and Shahed Launch Trucks against power and refineries, "
					+ "Missile City volleys on bases, cheap Technicals and rocket technicals for raids, decoy tanks to soak fire, tunnel networks "
					+ "to move infantry unseen, fast attack boats in swarms, oil fires and militias. Bleed the enemy with constant raids and missiles.",
				"hermit" => "HERMIT NATION (Soviet-based): fights dirty. Spies to steal money, Cyber Command to hack the richest rival and fake alarms, "
					+ "Psy-Op crowds that turn into suicide bombers, packs of suicide bombers against defences and harvesters, Truck Bombs into "
					+ "key buildings, kamikaze Badgers that flatten a building. Offer truces, move into position during the peace, then break the "
					+ "truce with a surprise strike; ask for a truce and pull back when losing.",
				"stable" => "STABLE SOCIETY (Allied-based): peaceful and well organised. Keeps every truce and only fights whoever attacks it. "
					+ "Government Building for tax income, Hospitals and Doctors to heal, Houses to garrison troops, farms and oil for money, "
					+ "a walled street-grid town with guard towers, Dream Mansion with pink jeeps and ponies. Turtle up, get rich, and hit back hard "
					+ "with big defensive squads.",
				_ => f.Side == "Soviet" ? Soviet : Allied,
			};
		}

		static string Category(ActorInfo ai)
		{
			var b = ai.TraitInfoOrDefault<BuildableInfo>();
			if (ai.HasTraitInfo<AircraftInfo>())
				return "aircraft";
			if (b != null && b.Queue.Contains("Ship"))
				return "navy";
			if (b != null && b.Queue.Contains("Infantry"))
				return "infantry";
			return "vehicles";
		}

		string Report()
		{
			var sb = new StringBuilder();
			var sec = world.WorldTick / TicksPerSecond(world);
			var res = player.PlayerActor.Trait<PlayerResources>();
			var power = player.PlayerActor.TraitOrDefault<PowerManager>();
			var mine = world.Actors.Where(a => a.Owner == player && Alive(a)).ToList();
			var units = mine.Where(a => a.Info.HasTraitInfo<IMoveInfo>()).ToList();
			var buildings = mine.Where(a => a.Info.HasTraitInfo<BuildingInfo>()).ToList();
			var home = BotHelpers.BaseCenter(player, world.Map.CenterOfCell(player.HomeLocation));

			sb.AppendLine($"Game time {sec / 60}m{sec % 60:00}s. You are {Label(player)} ({player.Faction.InternalName}).");
			sb.AppendLine($"Cash {res.Cash + res.Resources}, power {(power != null ? power.ExcessPower.ToString() : "?")}, {buildings.Count} buildings, "
				+ $"army value {units.Sum(u => BotHelpers.Cost(u.Info))}.");
			sb.AppendLine("Your units: " + string.Join(", ", units.GroupBy(u => u.Info.Name).OrderByDescending(g => g.Count()).Take(12)
				.Select(g => $"{g.Count()} {Name(g.First().Info)}")));
			sb.AppendLine("Your army by type: " + string.Join(", ", units.GroupBy(u => Category(u.Info)).Select(g => $"{g.Key} {g.Count()}")));

			var threat = world.Actors.Where(a => Alive(a) && player.RelationshipWith(a.Owner) == PlayerRelationship.Enemy && a.Info.HasTraitInfo<IMoveInfo>() && a.CanBeViewedByPlayer(player)
				&& (a.CenterPosition - home).LengthSquared < WDist.FromCells(20).LengthSquared).ToList();
			sb.AppendLine(threat.Count > 0
				? $"ENEMIES AT YOUR BASE: {threat.Count} units ({string.Join(", ", threat.GroupBy(t => t.Owner).Select(g => $"{g.Count()} of {Label(g.Key)}"))})."
				: "No enemies at your base.");

			// Island check: can our infantry walk to each rival's base?
			var amphib = player.PlayerActor.TraitsImplementing<AmphibiousAssaultBotModule>().FirstOrDefault(m => !m.IsTraitDisabled);
			var rivals = world.Players.Where(p => p.Playable && p != player && p.WinState == WinState.Undefined && !player.IsAlliedWith(p)).ToList();
			var overWater = rivals.Where(p => !ByLand(p)).ToList();

			sb.AppendLine("Other players:");
			foreach (var p in world.Players.Where(p => p.Playable && p != player))
			{
				var rel = p.WinState != WinState.Undefined ? "defeated" : player.IsAlliedWith(p) ? "ally"
					: truces != null && truces.InTruce(player, p) ? "enemy, IN TRUCE" : "enemy, at war";
				var seen = world.Actors.Where(a => a.Owner == p && Alive(a) && Sees(a)).ToList();
				var army = seen.Where(a => a.Info.HasTraitInfo<IMoveInfo>()).ToList();
				var dist = (world.Map.CenterOfCell(p.HomeLocation) - home).Length / 1024;
				sb.AppendLine($"- {Label(p)} ({p.Faction.InternalName}, {rel}), base {dist} cells away {(overWater.Contains(p) ? "ACROSS WATER (no land route)" : "reachable over land")}, "
					+ $"known: {seen.Count(a => a.Info.HasTraitInfo<BuildingInfo>())} buildings, army value {army.Sum(u => BotHelpers.Cost(u.Info))} "
					+ $"({string.Join(", ", army.GroupBy(u => Category(u.Info)).Select(g => $"{g.Key} {g.Count()}"))})");
			}

			if (overWater.Count > 0)
			{
				var boats = amphib?.TransportTypes.Where(t => BotHelpers.CanBuild(player, t)).ToList() ?? [];
				sb.AppendLine((overWater.Count == rivals.Count ? "ISLAND: none of your rivals can be reached over land. " : "Some rivals are across water. ")
					+ "To fight them you must cross by sea: use stance \"invade\" with that target, build transports"
					+ (boats.Count > 0 ? $" ({string.Join(", ", boats)})" : " (you need a shipyard first)")
					+ " and warships to escort them.");
				if (amphib != null)
				{
					var transports = world.Actors.Count(a => a.Owner == player && Alive(a) && amphib.TransportTypes.Contains(a.Info.Name));
					sb.AppendLine($"Invasion readiness: {transports} transports, {Army(BotHelpers.Squads(player)).Count} troops. "
						+ (overWater.Count == rivals.Count ? "Stance \"defend\" or \"economy\" keeps this army sitting at home doing nothing - only \"invade\" lets it fight."
							: "Only \"invade\" sends them across the water."));
				}
			}

			// What it can build right now, by internal name, so the model can give exact build orders.
			var buildable = BotHelpers.Queues(player).SelectMany(q => q.BuildableItems().Select(b => (q.Info.Type, b)))
				.Where(x => BotHelpers.Cost(x.b) > 0).GroupBy(x => x.b.Name).Select(g => g.First()).ToList();
			foreach (var g in buildable.GroupBy(x => x.Type))
				sb.AppendLine($"Can build ({g.Key}): " + string.Join("; ", g.Take(25).Select(x => $"{x.b.Name} {Name(x.b)} ${BotHelpers.Cost(x.b)}")));

			var spm = player.PlayerActor.TraitOrDefault<SupportPowerManager>();
			var ready = spm?.Powers.Values.Where(p => p.Ready && p.Instances.Any(i => !i.IsTraitDisabled)).Select(p => p.Key).ToList() ?? [];
			sb.AppendLine("Support powers ready: " + (ready.Count > 0 ? string.Join(", ", ready) : "none"));
			if (plan != null)
				sb.AppendLine($"Your current orders: stance {plan.Stance}{(plan.Target != null ? " against " + Label(plan.Target) : "")}.");

			return sb.ToString();
		}

		// Other players are shown to the model only as "Player 1", "Player 2"... (by lobby slot), never by name, so
		// it can't tell humans from computer players and has no reason to treat the other AIs as friends.
		static string Label(Player p) => $"Player {p.World.Players.Where(x => x.Playable).ToList().IndexOf(p) + 1}";

		static string Name(ActorInfo ai) => ai.TraitInfoOrDefault<TooltipInfo>() is TooltipInfo t ? FluentProvider.GetMessage(t.Name) : ai.Name;

		// --------------------------------------------------------------------------------------------
		// Reading the answer.
		Player FindPlayer(JsonElement root, string key)
		{
			if (!root.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String)
				return null;
			return FindPlayer(v.GetString());
		}

		Player FindPlayer(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				return null;
			return world.Players.FirstOrDefault(p => p.Playable && p != player && string.Equals(Label(p), name.Trim(), StringComparison.OrdinalIgnoreCase))
				?? world.Players.FirstOrDefault(p => p.Playable && p != player && name.Trim().EndsWith(" " + Label(p)[7..], StringComparison.OrdinalIgnoreCase));
		}

		static string[] Strings(JsonElement root, string key)
		{
			if (!root.TryGetProperty(key, out var v))
				return [];
			if (v.ValueKind == JsonValueKind.String)
				return [v.GetString()];
			if (v.ValueKind != JsonValueKind.Array)
				return [];
			return v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()).ToArray();
		}

		// "build": [{"item": "3tnk", "count": 4}, ...]
		static (string Item, int Count)[] Items(JsonElement root)
		{
			if (!root.TryGetProperty("build", out var v) || v.ValueKind != JsonValueKind.Array)
				return [];

			var list = new List<(string, int)>();
			foreach (var e in v.EnumerateArray())
			{
				if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("item", out var it) || it.ValueKind != JsonValueKind.String)
					continue;

				var count = e.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? n : 1;
				list.Add((it.GetString().Trim().ToLowerInvariant(), Math.Clamp(count, 1, 10)));
			}

			return list.Take(6).ToArray();
		}

		static string Str(JsonElement root, string key)
		{
			return root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
		}

		void Apply(IBot bot, string text)
		{
			try
			{
				var start = text.IndexOf('{');
				var end = text.LastIndexOf('}');
				if (start < 0 || end <= start)
					throw new FormatException("no JSON object");

				using var doc = JsonDocument.Parse(text[start..(end + 1)]);
				var root = doc.RootElement;
				var p = new Plan
				{
					Stance = (Str(root, "stance") ?? "defend").ToLowerInvariant(),
					Target = FindPlayer(root, "target_player"),
					BuildFocus = Strings(root, "build").Select(s => s.ToLowerInvariant()).Take(2).ToArray(),
					BuildItems = Items(root),
					Summary = Str(root, "plan"),
					ProposeTruce = Strings(root, "propose_truce").Select(FindPlayer).Where(x => x != null).ToArray(),
					BreakTruce = Strings(root, "break_truce").Select(FindPlayer).Where(x => x != null).ToArray(),
					Power = Str(root, "use_power"),
					PowerTarget = FindPlayer(root, "power_target"),
					Taunt = Str(root, "taunt"),
					Received = world.WorldTick,
				};

				AICommanderLink.WriteLog($"{player.ResolvedPlayerName.Trim()} <- {AICommanderLink.Trim(text, 600)}");
				plan = p;
				OneOff(bot, p);
				GameRecorder.Note(player, "COMMANDER " + Describe(p));
				if (AICommanderLink.ShowDirections)
					TextNotificationsManager.AddSystemLine("Lips", Describe(p));
			}
			catch (Exception e)
			{
				AICommanderLink.WriteLog($"{player.ResolvedPlayerName.Trim()}: couldn't use the reply ({e.Message}): {AICommanderLink.Trim(text, 300)}");
			}
		}

		// The plan in plain words, with real player names (for the 'lips' cheat).
		string Describe(Plan p)
		{
			static string N(Player x) => x?.ResolvedPlayerName.Trim();
			var parts = new List<string> { p.Stance.ToUpperInvariant() + (p.Target != null ? " " + N(p.Target) : "") };
			if (p.BuildItems.Length > 0)
				parts.Add("build " + string.Join(", ", p.BuildItems.Select(b => $"{b.Count}x {(world.Map.Rules.Actors.TryGetValue(b.Item, out var ai) ? Name(ai) : b.Item)}")));
			else if (p.BuildFocus.Length > 0)
				parts.Add("build " + string.Join(" + ", p.BuildFocus));
			if (!string.IsNullOrWhiteSpace(p.Summary))
				parts.Add("plan: " + AICommanderLink.Trim(p.Summary.Trim(), 160));
			if (p.ProposeTruce.Length > 0)
				parts.Add("offer truce to " + string.Join(", ", p.ProposeTruce.Select(N)));
			if (p.BreakTruce.Length > 0)
				parts.Add("BREAK truce with " + string.Join(", ", p.BreakTruce.Select(N)));
			if (!string.IsNullOrEmpty(p.Power))
				parts.Add($"use {p.Power}" + (p.PowerTarget != null ? " on " + N(p.PowerTarget) : ""));
			return $"{N(player)}: " + string.Join("; ", parts);
		}

		// Things done once per answer: diplomacy, production, support powers, trash talk.
		static readonly HashSet<string> SingleTypes = ["syrd", "spen", "tribaldock", "seaport"];

		static string ShipyardKind(string type) => type == "seaport" ? "seaport" : "yard";

		void OneOff(IBot bot, Plan p)
		{
			var index = (Player x) => (uint)world.Players.IndexOf(x);
			foreach (var x in p.BreakTruce.Where(x => truces != null && truces.InTruce(player, x)))
				bot.QueueOrder(new Order("BreakTruce", player.PlayerActor, false) { ExtraData = index(x) });
			// Ordered to attack someone we're in a truce with: that order ends the truce (otherwise the army
			// refuses to touch them and the order is wasted).
			if (p.Stance is "attack" or "raid" or "invade" && p.Target != null && truces != null && truces.InTruce(player, p.Target)
				&& !p.BreakTruce.Contains(p.Target))
			{
				bot.QueueOrder(new Order("BreakTruce", player.PlayerActor, false) { ExtraData = index(p.Target) });
				GameRecorder.Note(player, $"truce with {p.Target.ResolvedPlayerName.Trim()} broken to carry out the {p.Stance} order");
			}

			foreach (var x in p.ProposeTruce.Where(x => truces != null && Rival(x) && !truces.InTruce(player, x) && x != p.Target))
				bot.QueueOrder(new Order("ProposeTruce", player.PlayerActor, false) { ExtraData = index(x) });

			// Specific items the model picked from the "Can build now" list.
			foreach (var (item, count) in p.BuildItems)
			{
				var queue = BotHelpers.Queues(player).FirstOrDefault(q => q.BuildableItems().Any(b => b.Name == item));
				if (queue == null)
					continue;

				var isBuilding = world.Map.Rules.Actors[item].HasTraitInfo<BuildingInfo>();

				// "build 10 canoes" means "have 10 canoes", not "10 more every minute".
				var have = world.Actors.Count(a => a.Owner == player && Alive(a) && a.Info.Name == item) + BotHelpers.Queued(player, item);
				var want = count;

				// One shipyard and one seaport is all anyone needs; transports and container ships are capped too.
				if (SingleTypes.Contains(item))
				{
					if (world.Actors.Any(a => a.Owner == player && Alive(a) && SingleTypes.Contains(a.Info.Name) && ShipyardKind(a.Info.Name) == ShipyardKind(item))
						|| SingleTypes.Where(t => ShipyardKind(t) == ShipyardKind(item)).Sum(t => BotHelpers.Queued(player, t)) > 0)
						continue;
					want = 1;
				}

				var amphib = player.PlayerActor.TraitsImplementing<AmphibiousAssaultBotModule>().FirstOrDefault(m => !m.IsTraitDisabled);
				if (amphib != null && amphib.TransportTypes.Contains(item))
					want = Math.Min(want, amphib.Info.MaxTransports);

				var merchant = player.PlayerActor.TraitsImplementing<MerchantBotModule>().FirstOrDefault(m => !m.IsTraitDisabled);
				if (merchant != null && item == merchant.Info.ShipType)
					want = Math.Min(want, merchant.Info.MaxShips);
				if (merchant != null && item == merchant.Info.PlatformType)
					want = Math.Min(want, merchant.Info.MaxPlatforms);

				var add = want - have;
				if (add <= 0)
					continue;

				bot.QueueOrder(Order.StartProduction(queue.Actor, item, isBuilding ? Math.Min(add, 2) : add));
			}

			// Vague orders ("more infantry") top the queues up to BuildPerPlan items; they don't pile up every minute.
			foreach (var focus in p.BuildFocus)
				for (var i = BotHelpers.Queues(player).Sum(q => q.AllQueued().Count()); i < Info.BuildPerPlan; i++)
					QueueFocus(bot, focus);

			if (!string.IsNullOrEmpty(p.Power))
			{
				var spm = player.PlayerActor.TraitOrDefault<SupportPowerManager>();
				var power = spm?.Powers.Values.FirstOrDefault(x => x.Ready && string.Equals(x.Key, p.Power, StringComparison.OrdinalIgnoreCase));
				if (power != null && power.Key.Contains("PsyOp", StringComparison.OrdinalIgnoreCase)
					&& world.Actors.Count(a => a.Owner == player && Alive(a) && a.Info.Name.StartsWith("protester", StringComparison.Ordinal)) >= 40)
					power = null;
				var victim = p.PowerTarget ?? p.Target ?? world.Players.Where(Rival).MinByOrDefault(x => (x.HomeLocation - player.HomeLocation).LengthSquared);
				var spot = victim == null ? null : TargetCell(victim);
				if (power != null && spot != null)
					bot.QueueOrder(new Order(power.Key, player.PlayerActor, Target.FromCell(world, spot.Value), false) { SuppressVisualFeedback = true, ExtraData = uint.MaxValue });
			}

			var settings = AICommanderLink.Load(out _);
			if (settings?.ShowTaunts == true && !string.IsNullOrWhiteSpace(p.Taunt))
				TextNotificationsManager.AddMissionLine(player.ResolvedPlayerName, AICommanderLink.Trim(p.Taunt.Trim(), 140), player.Color);
		}

		// Only what this AI's own army has seen: explored buildings and units in view (no shared intel).
		bool Sees(Actor a) => a.Info.HasTraitInfo<BuildingInfo>() ? player.Shroud.IsExplored(a.Location) : a.CanBeViewedByPlayer(player);

		// Can our infantry walk from our base to this player's base?
		bool ByLand(Player p)
		{
			var walker = world.Map.Rules.Actors["e1"].TraitInfoOrDefault<MobileInfo>();
			var loco = walker == null ? null : world.WorldActor.TraitsImplementing<Locomotor>().FirstOrDefault(l => l.Info.Name == walker.Locomotor);
			var pf = world.WorldActor.TraitOrDefault<IPathFinder>();
			if (loco == null || pf == null || !world.Map.Contains(p.HomeLocation))
				return true;

			var from = world.Map.CellContaining(BotHelpers.BaseCenter(player, world.Map.CenterOfCell(player.HomeLocation)));
			return world.Map.FindTilesInCircle(p.HomeLocation, 3).Any(c => world.Map.Contains(c) && pf.PathExistsForLocomotor(loco, from, c));
		}

		CPos? TargetCell(Player victim)
		{
			var buildings = world.Actors.Where(a => a.Owner == victim && Alive(a) && a.Info.HasTraitInfo<BuildingInfo>() && BotHelpers.Known(player, a)).ToList();
			if (buildings.Count == 0)
				return world.Map.Contains(victim.HomeLocation) ? victim.HomeLocation : null;
			return buildings.MinBy(b => buildings.Sum(o => (o.Location - b.Location).LengthSquared)).Location;
		}

		void QueueFocus(IBot bot, string focus)
		{
			var options = BotHelpers.Queues(player).SelectMany(q => q.BuildableItems().Select(b => (Queue: q, Item: b))).Where(x => Fits(x.Item, focus)).ToList();
			if (options.Count == 0)
				return;

			var pick = options.Random(world.LocalRandom);
			bot.QueueOrder(Order.StartProduction(pick.Queue.Actor, pick.Item.Name, 1));
		}

		static bool Fits(ActorInfo ai, string focus)
		{
			var b = ai.TraitInfoOrDefault<BuildableInfo>();
			if (b == null || BotHelpers.Cost(ai) <= 0)
				return false;

			var armed = ai.TraitInfos<ArmamentInfo>().ToList();
			var building = ai.HasTraitInfo<BuildingInfo>();
			var antiAir = armed.Any(a => a.WeaponInfo?.ValidTargets.Contains("AirborneActor") == true || a.WeaponInfo?.ValidTargets.Contains("Air") == true);
			return focus switch
			{
				"infantry" => b.Queue.Contains("Infantry") && armed.Count > 0,
				"tanks" => b.Queue.Contains("Vehicle") && armed.Count > 0 && !ai.HasTraitInfo<HarvesterInfo>(),
				"artillery" => b.Queue.Contains("Vehicle") && armed.Any(a => a.WeaponInfo != null && a.WeaponInfo.Range.Length > 6 * 1024),
				"anti_air" => !building && armed.Count > 0 && antiAir,
				"aircraft" => b.Queue.Contains("Aircraft") && armed.Count > 0,
				"navy" => b.Queue.Contains("Ship") && armed.Count > 0,
				"defenses" => b.Queue.Contains("Defense") && armed.Count > 0,
				"harvesters" => ai.HasTraitInfo<HarvesterInfo>(),
				_ => false,
			};
		}

		// The commander's orders are obeyed: while a plan stands, the AI's whole army (every armed ground unit that
		// isn't a harvester/engineer/special unit run by another module) is taken away from the normal squads and
		// does what the plan says. When the plan runs out without a new one, the units go back to the squads.
		List<Actor> Army(SquadManagerBotModule squads)
		{
			var exclude = squads?.Info.ExcludeFromSquadsTypes;
			return world.Actors.Where(a => a.Owner == player && Alive(a)
					&& a.Info.HasTraitInfo<MobileInfo>() && a.Info.HasTraitInfo<AttackBaseInfo>() && !a.Info.HasTraitInfo<AircraftInfo>()
					&& !a.Info.HasTraitInfo<HarvesterInfo>() && (exclude == null || !exclude.Contains(a.Info.Name))
					&& (wave.Contains(a) || squads == null || !squads.IsClaimed(a)))
				.ToList();
		}

		void Send(IBot bot, IEnumerable<Actor> units, CPos cell, bool all)
		{
			foreach (var u in units.Where(u => all || u.IsIdle))
				bot.QueueOrder(new Order("AttackMove", u, Target.FromCell(world, cell), false));
		}

		void Carry(IBot bot)
		{
			if (world.WorldTick - plan.Received > Info.PlanTicks)
			{
				ReleaseWave();
				plan = null;
				return;
			}

			wave.RemoveAll(a => !Alive(a) || a.Owner != player);
			var squads = BotHelpers.Squads(player);
			var home = BotHelpers.BaseCenter(player, world.Map.CenterOfCell(player.HomeLocation));
			var homeCell = world.Map.CellContaining(home);

			// An attack or raid on someone across the water can only be done by sea.
			if (plan.Stance is "attack" or "raid" && (plan.Target != null ? !ByLand(plan.Target)
				: !world.Players.Any(p => Rival(p) && (truces == null || !truces.InTruce(player, p)) && ByLand(p))))
				plan.Stance = "invade";

			// Sea invasion: hand the army to the invasion code (which loads them into transports).
			if (plan.Stance == "invade")
			{
				ReleaseWave();
				var amphib = player.PlayerActor.TraitsImplementing<AmphibiousAssaultBotModule>().FirstOrDefault(m => !m.IsTraitDisabled);
				if (amphib != null)
				{
					amphib.PreferredTarget = plan.Target;
					amphib.ForceSeaUntil = world.WorldTick + Info.PlanTicks;
				}

				return;
			}

			if (world.WorldTick - waveSent < 125)
				return;

			var fresh = plan.Stance == "raid" ? [] : Army(squads).Where(u => !wave.Contains(u)).ToList();
			if (plan.Stance == "raid" && wave.Count == 0)
				fresh = Army(squads).OrderByDescending(u => u.Info.TraitInfoOrDefault<MobileInfo>()?.Speed ?? 0).Take(Math.Max(4, Army(squads).Count / 3)).ToList();

			if (fresh.Count > 0)
			{
				squads?.ClaimUnits(fresh);
				wave.AddRange(fresh);
			}

			var newOrders = waveSent < plan.Received;
			waveSent = world.WorldTick;

			switch (plan.Stance)
			{
				case "defend":
				case "economy":
				{
					// Defend: everyone fights whatever is at the base, otherwise holds around it.
					// Economy: no attacks - the army stays home and guards it.
					var intruder = world.Actors.Where(a => Alive(a) && player.RelationshipWith(a.Owner) == PlayerRelationship.Enemy && a.Info.HasTraitInfo<IMoveInfo>()
							&& a.CanBeViewedByPlayer(player) && (a.CenterPosition - home).LengthSquared < WDist.FromCells(22).LengthSquared)
						.MinByOrDefault(a => (a.CenterPosition - home).LengthSquared);
					if (intruder != null)
						Send(bot, wave, intruder.Location, true);
					else
						Send(bot, wave.Where(u => (u.Location - homeCell).LengthSquared > 12 * 12), homeCell, true);
					return;
				}

				case "attack":
				case "raid":
				{
					var target = plan.Target != null && Rival(plan.Target) && (truces == null || !truces.InTruce(player, plan.Target)) ? plan.Target
						: world.Players.Where(p => Rival(p) && (truces == null || !truces.InTruce(player, p))).MinByOrDefault(p => (p.HomeLocation - player.HomeLocation).LengthSquared);
					var goal = target == null ? null : TargetCell(target);
					if (goal == null)
						return;

					// A raid goes for the economy: harvesters and refineries first.
					if (plan.Stance == "raid")
					{
						var soft = world.Actors.Where(a => a.Owner == target && Alive(a) && (a.Info.HasTraitInfo<HarvesterInfo>() || a.Info.Name == "proc")
							&& a.CanBeViewedByPlayer(player)).MinByOrDefault(a => (a.Location - homeCell).LengthSquared);
						if (soft != null)
							goal = soft.Location;
					}

					// New orders: everyone goes now. Afterwards just keep anyone who stopped moving going.
					Send(bot, wave, goal.Value, newOrders || fresh.Count > 0);
					return;
				}

				default:
					ReleaseWave();
					return;
			}
		}

		// While the commander's own build orders are still waiting in the queues, the normal unit builder holds off,
		// so what the commander asked for gets built first.
		bool IBotRequestPauseUnitProduction.PauseUnitProduction =>
			plan != null && plan.BuildItems.Any(b => world.Map.Rules.Actors.TryGetValue(b.Item, out var ai) && !ai.HasTraitInfo<BuildingInfo>() && BotHelpers.Queued(player, b.Item) > 0);

		void ReleaseWave()
		{
			BotHelpers.Squads(player)?.ReleaseUnits(wave);
			wave.Clear();
		}
	}
}
