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
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

// VIP Bunker (El Presidente mode): El Presidente and his bodyguards can shelter inside a nuke-proof bunker - but while
// he's in there his side can't build anything (production and building placement freeze) and none of its units can
// board transports.
namespace OpenRA.Mods.MyRA.Traits
{
	[Desc("Marks a VIP bunker: while its owner's El Presidente is inside, that player is locked down.")]
	public class VipBunkerInfo : TraitInfo<VipBunker> { }

	public class VipBunker : INotifyPassengerEntered, INotifyPassengerExited
	{
		void INotifyPassengerEntered.OnPassengerEntered(Actor self, Actor passenger)
		{
			if (passenger.Info.HasTraitInfo<PresidenteInfo>() && self.World.LocalPlayer == self.Owner)
				TextNotificationsManager.AddSystemLine("VIP Bunker", "El Presidente is in the bunker: production is frozen and transports can't be loaded until he comes out.");
			if (passenger.Info.HasTraitInfo<PresidenteInfo>())
				GameRecorder.Note(self.Owner, "El Presidente entered the VIP Bunker (lockdown)");
		}

		void INotifyPassengerExited.OnPassengerExited(Actor self, Actor passenger)
		{
			if (passenger.Info.HasTraitInfo<PresidenteInfo>() && self.World.LocalPlayer == self.Owner)
				TextNotificationsManager.AddSystemLine("VIP Bunker", "El Presidente has left the bunker: production resumes.");
		}
	}

	[TraitLocation(SystemActors.World)]
	[Desc("Production and transport loading stop for a player whose El Presidente is inside a VIP bunker.")]
	public class VipLockdownInfo : TraitInfo<VipLockdown> { }

	public class VipLockdown : IPlayerLockdown
	{
		static bool Locked(Player p)
		{
			var mgr = p.World.WorldActor.TraitOrDefault<PresidenteManager>();
			if (mgr == null || !mgr.Enabled || !mgr.Presidents.TryGetValue(p, out var pres) || pres.IsDead)
				return false;
			var carrier = PresidenteManager.CarrierOf(pres);
			return carrier != null && carrier.Info.HasTraitInfo<VipBunkerInfo>();
		}

		bool IPlayerLockdown.BlocksProduction(Player player) => Locked(player);

		// The bunker itself still takes its VIPs; nothing else of theirs can board anything.
		bool IPlayerLockdown.BlocksLoading(Actor transport, Actor passenger) =>
			!transport.Info.HasTraitInfo<VipBunkerInfo>() && Locked(passenger.Owner);
	}

	[TraitLocation(SystemActors.Player)]
	[Desc("Provides prerequisites only when the El Presidente lobby option is on.")]
	public class PresidenteModePrerequisiteInfo : TraitInfo, ITechTreePrerequisiteInfo
	{
		public readonly ImmutableArray<string> Prerequisites = ["presidentemode"];

		IEnumerable<string> ITechTreePrerequisiteInfo.Prerequisites(ActorInfo info) => Prerequisites;

		public override object Create(ActorInitializer init) { return new PresidenteModePrerequisite(this, init); }
	}

	public class PresidenteModePrerequisite : ITechTreePrerequisite
	{
		readonly PresidenteModePrerequisiteInfo info;
		readonly bool enabled;

		public PresidenteModePrerequisite(PresidenteModePrerequisiteInfo info, ActorInitializer init)
		{
			this.info = info;
			enabled = init.World.LobbyInfo.GlobalSettings.OptionOrDefault("presidente", false);
		}

		public IEnumerable<string> ProvidesPrerequisites => enabled ? info.Prerequisites : Enumerable.Empty<string>();
	}
}
