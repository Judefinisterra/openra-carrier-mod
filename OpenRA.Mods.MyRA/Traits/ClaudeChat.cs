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
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Commands;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// ================================================================================================
	// Talk to Claude Code from inside the game.
	//
	//   /claude <order>     runs Claude Code in the background (claude -p) and shows its reply in the chat.
	//                       Every order in one game continues the same Claude conversation, so "do it again"
	//                       and "now pull them back" work.
	//   /claude-terminal    opens a terminal window running an interactive claude session.
	//
	// Both run in the engine folder, where CLAUDE.md explains the ClaudeControl HTTP API on localhost, so
	// Claude drives the army the same way it does from an outside terminal. Commands typed with a leading '/'
	// are never sent to the other players. Uses whatever Claude Code login the computer already has.
	// ================================================================================================
	[TraitLocation(SystemActors.World)]
	[IncludeStaticFluentReferences(typeof(ClaudeChat))]
	[Desc("Adds the /claude and /claude-terminal chat commands. Attach to the world actor.")]
	public class ClaudeChatInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new ClaudeChat(this); }

		[Desc("Claude Code executable, found on PATH if not a full path.")]
		public readonly string Executable = "claude";

		[Desc("Model passed to claude --model. Empty uses the account default.")]
		public readonly string Model = "";

		[Desc("Tools the background run may use without asking. It only needs curl to reach the control server.")]
		public readonly string AllowedTools = "Bash(curl:*)";

		[Desc("Give up on a background order after this many seconds.")]
		public readonly int TimeoutSeconds = 180;

		[Desc("Most chat lines shown from one reply.")]
		public readonly int MaxReplyLines = 6;
	}

	public class ClaudeChat : IChatCommand, IWorldLoaded, INotifyActorDisposing
	{
		const string Prefix = "Claude";

		[FluentReference]
		const string OrderDescription = "description-claude-command";

		[FluentReference]
		const string TerminalDescription = "description-claude-terminal-command";

		const string Instructions = "You are commanding the local player's army in a live OpenRA game, through the "
			+ "HTTP control server described in CLAUDE.md (curl localhost:7777). Read /state, then act at once. The game "
			+ "keeps running while you think, so prefer few, decisive calls. Reply in one or two short sentences: the "
			+ "reply is shown in the in-game chat.";

		readonly ClaudeChatInfo info;
		readonly string sessionId = Guid.NewGuid().ToString();

		World world;
		Process running;
		bool sessionStarted;

		public ClaudeChat(ClaudeChatInfo info)
		{
			this.info = info;
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			world = w;
			if (w.Type != WorldType.Regular)
				return;

			var console = w.WorldActor.TraitOrDefault<ChatCommands>();
			var help = w.WorldActor.TraitOrDefault<HelpCommand>();
			if (console == null)
				return;

			console.RegisterCommand("claude", this);
			console.RegisterCommand("claude-terminal", this);
			help?.RegisterHelp("claude", OrderDescription);
			help?.RegisterHelp("claude-terminal", TerminalDescription);
		}

		public void InvokeCommand(string command, string arg)
		{
			if (world.LocalPlayer == null || world.LocalPlayer.Spectating)
			{
				Say("Only a player can give Claude orders.");
				return;
			}

			if (command == "claude-terminal")
				OpenTerminal();
			else if (string.IsNullOrWhiteSpace(arg))
				Say("Type an order after /claude, e.g. /claude attack the nearest enemy base with all infantry");
			else
				RunOrder(arg);
		}

		void RunOrder(string order)
		{
			if (running != null)
			{
				Say("Still working on the last order. Wait for the reply, or open /claude-terminal.");
				return;
			}

			var psi = new ProcessStartInfo(info.Executable)
			{
				WorkingDirectory = Platform.EngineDir,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};

			psi.ArgumentList.Add("-p");
			psi.ArgumentList.Add(order);
			psi.ArgumentList.Add(sessionStarted ? "--resume" : "--session-id");
			psi.ArgumentList.Add(sessionId);
			psi.ArgumentList.Add("--append-system-prompt");
			psi.ArgumentList.Add(Instructions);
			psi.ArgumentList.Add("--allowedTools");
			psi.ArgumentList.Add(info.AllowedTools);
			if (!string.IsNullOrEmpty(info.Model))
			{
				psi.ArgumentList.Add("--model");
				psi.ArgumentList.Add(info.Model);
			}

			Process p;
			try
			{
				p = Process.Start(psi);
			}
			catch (Win32Exception e)
			{
				Say($"Could not start Claude Code ({e.Message}). Is it installed and on PATH?");
				return;
			}

			p.StandardInput.Close();
			running = p;
			sessionStarted = true;
			Say("On it...");

			Task.Run(async () =>
			{
				var output = p.StandardOutput.ReadToEndAsync();
				var errors = p.StandardError.ReadToEndAsync();
				var finished = p.WaitForExitAsync();
				var timedOut = await Task.WhenAny(finished, Task.Delay(info.TimeoutSeconds * 1000)) != finished;
				if (timedOut)
					try { p.Kill(true); } catch (InvalidOperationException) { }

				var reply = timedOut ? $"Gave up after {info.TimeoutSeconds} seconds."
					: p.ExitCode != 0 ? "Error: " + LastLine(await errors, await output)
					: (await output).Trim();

				Game.RunAfterTick(() =>
				{
					if (running == p)
						running = null;

					p.Dispose();
					ShowReply(reply);
				});
			});
		}

		void OpenTerminal()
		{
			var dir = Platform.EngineDir;
			var attempts = new List<ProcessStartInfo>();
			if (Platform.CurrentPlatform == PlatformType.Windows)
			{
				attempts.Add(new ProcessStartInfo("wt.exe", $"-d \"{dir.TrimEnd('\\')}\" {info.Executable}") { UseShellExecute = true });
				attempts.Add(new ProcessStartInfo("cmd.exe", $"/k {info.Executable}") { UseShellExecute = true, WorkingDirectory = dir });
			}
			else if (Platform.CurrentPlatform == PlatformType.OSX)
				attempts.Add(new ProcessStartInfo("open", $"-a Terminal \"{dir}\"") { UseShellExecute = false });
			else
				attempts.Add(new ProcessStartInfo("x-terminal-emulator", $"-e {info.Executable}") { UseShellExecute = false, WorkingDirectory = dir });

			foreach (var psi in attempts)
			{
				try
				{
					Process.Start(psi)?.Dispose();
					Say(Platform.CurrentPlatform == PlatformType.OSX
						? "Terminal opened in the game folder. Type claude to start."
						: "Terminal opened with Claude Code. Tell it what your army should do.");
					return;
				}
				catch (Win32Exception) { }
			}

			Say("Could not open a terminal window.");
		}

		void ShowReply(string reply)
		{
			var lines = reply.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
			if (lines.Count == 0)
				lines.Add("Done.");

			foreach (var line in lines.Take(info.MaxReplyLines))
				Say(line.Length > 220 ? line[..220] + "..." : line);

			if (lines.Count > info.MaxReplyLines)
				Say("(reply cut short)");
		}

		// Claude Code prints settings warnings to stderr before the real error, so the last line is the useful one.
		static string LastLine(string errors, string output)
		{
			var text = errors + "\n" + output;
			var line = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "unknown";
			return line.Length > 200 ? line[..200] : line;
		}

		static void Say(string text)
		{
			TextNotificationsManager.AddSystemLine(Prefix, text);
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			try { running?.Kill(true); } catch (InvalidOperationException) { }
			running = null;
		}
	}
}
