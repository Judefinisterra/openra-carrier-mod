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
using System.Runtime.CompilerServices;

namespace OpenRA.Mods.Common
{
	/// <summary>
	/// MyRA: where each player's money comes from (and goes). Code that hands out money wraps the call in
	/// <c>using (MoneyLedger.Source("ore"))</c>; PlayerResources records every payment under the current source.
	/// </summary>
	public static class MoneyLedger
	{
		static readonly ConditionalWeakTable<Player, Dictionary<string, long>> Books = new();
		static string current;

		sealed class Scope : IDisposable
		{
			readonly string previous;

			public Scope(string source)
			{
				previous = current;
				current = source;
			}

			public void Dispose() { current = previous; }
		}

		public static IDisposable Source(string source) => new Scope(source);

		static Dictionary<string, long> Book(Player p) => Books.GetValue(p, _ => []);

		/// <summary>Money received (called by PlayerResources).</summary>
		public static void Income(Player p, int amount)
		{
			if (p == null || amount <= 0)
				return;
			var key = current ?? "other";
			var b = Book(p);
			b[key] = b.GetValueOrDefault(key) + amount;
		}

		/// <summary>Money lost or paid that isn't ordinary spending (upkeep, storage cap...). Recorded as "-name".</summary>
		public static void Loss(Player p, string what, int amount)
		{
			if (p == null || amount <= 0)
				return;
			var key = "-" + what;
			var b = Book(p);
			b[key] = b.GetValueOrDefault(key) + amount;
		}

		public static IReadOnlyDictionary<string, long> Of(Player p) => Book(p);
	}
}
