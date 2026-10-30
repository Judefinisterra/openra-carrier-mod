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

using System.Linq;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// Psy-Op: a support power on the Tech Center. When charged, click anywhere on the map and a huge crowd of
	// War Protesters appears there. The crowd belongs to whichever side's buildings are closest to that spot, so
	// the local army doesn't just gun them down - they clog that side's roads and base instead.
	[Desc("Support power that spawns a crowd of units around the target location.")]
	public class PsyOpPowerInfo : SupportPowerInfo
	{
		[ActorReference]
		[Desc("Unit to spawn.")]
		public readonly string Actor = "protester";

		[Desc("How many to spawn.")]
		public readonly int Count = 300;

		[Desc("Spread them over at most this many cells from the target.")]
		public readonly int MaxRadius = 30;

		public override object Create(ActorInitializer init) { return new PsyOpPower(init.Self, this); }
	}

	public class PsyOpPower : SupportPower
	{
		public PsyOpPower(Actor self, PsyOpPowerInfo info)
			: base(self, info) { }

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			base.Activate(self, order, manager);
			var info = (PsyOpPowerInfo)Info;
			var center = self.World.Map.CellContaining(order.Target.CenterPosition);
			var caster = self.Owner;
			var targetPos = order.Target.CenterPosition;

			// Owner: the playable side with a building nearest the target (the caster if nobody has one).
			var nearest = self.World.ActorsHavingTrait<Building>()
				.Where(a => !a.IsDead && a.IsInWorld && a.Owner.Playable && !a.Owner.NonCombatant && a.Owner.WinState == WinState.Undefined)
				.MinByOrDefault(a => (a.CenterPosition - targetPos).LengthSquared);
			var owner = nearest?.Owner ?? caster;

			self.World.AddFrameEndTask(w =>
			{
				PlayLaunchSounds();
				var unitInfo = w.Map.Rules.Actors[info.Actor];
				var mobile = unitInfo.TraitInfoOrDefault<MobileInfo>();
				var cells = w.Map.FindTilesInAnnulus(center, 0, info.MaxRadius).Where(w.Map.Contains).ToList();
				var made = 0;

				// One per cell first so they spread into a crowd, then pack more in (up to 5 infantry per cell).
				for (var perCell = 1; perCell <= 5 && made < info.Count; perCell++)
				{
					foreach (var cell in cells)
					{
						if (made >= info.Count)
							break;

						if (mobile != null && !mobile.CanEnterCell(w, null, cell, SubCell.FullCell, null, BlockedByActor.None))
							continue;

						if (w.ActorMap.GetActorsAt(cell).Count() >= perCell)
							continue;

						var sub = w.ActorMap.FreeSubCell(cell);
						if (sub == SubCell.Invalid)
							continue;

						var protester = w.CreateActor(info.Actor, new TypeDictionary
						{
							new LocationInit(cell),
							new SubCellInit(sub),
							new OwnerInit(owner),
							new FacingInit(WAngle.FromFacing(w.SharedRandom.Next(256))),
						});

						// Left alone too long, some of them take up arms for whoever sent them.
						var uprising = protester.TraitOrDefault<MilitiaUprising>();
						if (uprising != null)
							uprising.Instigator = caster;

						made++;
					}
				}

				if (w.LocalPlayer == caster)
					TextNotificationsManager.AddSystemLine("Psy-Op", owner == caster
						? $"{made} protesters have taken to the streets!"
						: $"{made} protesters have taken to the streets of {owner.ResolvedPlayerName}!");
				else if (w.LocalPlayer == owner)
					TextNotificationsManager.AddSystemLine("Psy-Op", $"{made} protesters have flooded into your territory!");
			});
		}
	}
}
