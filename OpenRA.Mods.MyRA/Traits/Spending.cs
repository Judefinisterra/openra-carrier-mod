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

using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// Rich AIs spend. The normal unit builder makes one unit at a time per factory, which can't keep up with oil,
	// farms and trade - AIs were ending games sitting on $100,000+. While an AI has more than RichCash, this
	// module builds extra factories (more factories = faster production) and keeps every factory's queue
	// topped up from the AI's own unit mix (its UnitsToBuild), still respecting that AI's unit limits.
	[TraitLocation(SystemActors.Player)]
	[Desc("AI: when rich, build extra factories and keep every production queue busy.")]
	public class RichSpenderBotModuleInfo : ConditionalTraitInfo
	{
		[Desc("Start spending above this much cash.")]
		public readonly int RichCash = 15000;

		[Desc("Factory buildings to add while rich.")]
		public readonly HashSet<string> FactoryTypes = ["barr", "tent", "weap", "afld", "kenn", "animalpen"];

		[Desc("Most of each factory type to own.")]
		public readonly int MaxEachFactory = 5;

		[Desc("Never mass-produce these (workers, transports, merchant ships, special units).")]
		public readonly HashSet<string> ExcludeTypes = ["harv", "mcv", "lst", "canoe", "containership", "oiltanker", "oiltruck", "oilhauler",
			"supplytruck", "e6", "spy", "spy.hermit", "protester", "dog", "doctor", "ambulance", "u2", "badr.troop", "decoy.v2", "decoy.tank"];

		[Desc("Keep this many items in each unit queue.")]
		public readonly int QueueDepth = 5;

		public readonly int Interval = 250;

		public override object Create(ActorInitializer init) { return new RichSpenderBotModule(init.Self, this); }
	}

	public class RichSpenderBotModule : ConditionalTrait<RichSpenderBotModuleInfo>, IBotTick
	{
		readonly World world;
		readonly Player player;
		int ticks, lastNote = -10000;

		public RichSpenderBotModule(Actor self, RichSpenderBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (IsTraitDisabled || ++ticks % Info.Interval != 0)
				return;

			var cash = player.PlayerActor.Trait<PlayerResources>().GetCashAndResources();
			if (cash < Info.RichCash)
				return;

			var builder = player.PlayerActor.TraitsImplementing<UnitBuilderBotModule>().FirstOrDefault(m => !m.IsTraitDisabled);
			var mix = builder?.Info.UnitsToBuild;
			var limits = builder?.Info.UnitLimits;
			var queues = BotHelpers.Queues(player);
			var owned = world.Actors.Where(a => a.Owner == player && !a.IsDead && a.IsInWorld).GroupBy(a => a.Info.Name).ToDictionary(g => g.Key, g => g.Count());
			var added = new List<string>();

			// 1. One more factory at a time, if none is being built already.
			if (!Info.FactoryTypes.Any(t => BotHelpers.Queued(player, t) > 0))
			{
				var factory = Info.FactoryTypes.Where(t => owned.GetValueOrDefault(t) > 0 && owned.GetValueOrDefault(t) < Info.MaxEachFactory && BotHelpers.CanBuild(player, t))
					.OrderBy(t => owned.GetValueOrDefault(t)).FirstOrDefault();
				if (factory != null && BotHelpers.Queue(bot, player, factory))
					added.Add(factory);
			}

			// 2. Top every unit queue up from this AI's own mix.
			if (mix != null && mix.Count > 0)
			{
				foreach (var q in queues)
				{
					if (q.Info.Type is "Building" or "Defense")
						continue;

					var queued = q.AllQueued().Count();
					if (queued >= Info.QueueDepth)
						continue;

					var options = q.BuildableItems().Where(b => mix.ContainsKey(b.Name) && mix[b.Name] > 0 && !Info.ExcludeTypes.Contains(b.Name)
						&& (limits == null || !limits.TryGetValue(b.Name, out var lim) || owned.GetValueOrDefault(b.Name) + BotHelpers.Queued(player, b.Name) < lim)).ToList();
					if (options.Count == 0)
						continue;

					for (var i = queued; i < Info.QueueDepth; i++)
					{
						// Weighted pick by the AI's own unit shares.
						var total = options.Sum(o => mix[o.Name]);
						var roll = world.LocalRandom.Next(total);
						var pick = options[0];
						foreach (var o in options)
						{
							roll -= mix[o.Name];
							if (roll < 0)
							{
								pick = o;
								break;
							}
						}

						bot.QueueOrder(Order.StartProduction(q.Actor, pick.Name, 1));
						added.Add(pick.Name);
					}
				}
			}

			if (added.Count > 0 && world.WorldTick - lastNote > 1500)
			{
				lastNote = world.WorldTick;
				GameRecorder.Note(player, $"rich (${cash}): spending on " + string.Join(", ", added.GroupBy(x => x).Select(g => $"{g.Count()} {g.Key}")));
			}
		}
	}
}
