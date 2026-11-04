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
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.MyRA.Traits
{
	// ================================================================================================
	// Geostationary satellite: launched from the Ground Station at a spot on the map (an enemy base, say). It climbs,
	// flies there and parks overhead for good, showing its side everything under it, until an ASAT missile brings
	// it down.
	// ================================================================================================
	[Desc("Support power: launches a geostationary satellite that parks over the target spot.")]
	public class GeoSatellitePowerInfo : SupportPowerInfo
	{
		[ActorReference]
		public readonly string Satellite = "geosat";

		public readonly WDist LaunchAltitude = new(1024);

		public override object Create(ActorInitializer init) { return new GeoSatellitePower(init.Self, this); }
	}

	public class GeoSatellitePower : SupportPower
	{
		readonly GeoSatellitePowerInfo info;

		public GeoSatellitePower(Actor self, GeoSatellitePowerInfo info)
			: base(self, info)
		{
			this.info = info;
		}

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			// Needs somewhere to aim (an order sent without a target would crash the game).
			if (order.Target.Type == TargetType.Invalid)
				return;

			base.Activate(self, order, manager);
			PlayLaunchSounds();

			var station = order.Target.CenterPosition;
			self.World.AddFrameEndTask(w =>
			{
				if (self.IsDead || self.Disposed)
					return;

				var sat = w.CreateActor(info.Satellite,
				[
					new LocationInit(self.Location),
					new OwnerInit(self.Owner),
					new CenterPositionInit(self.CenterPosition + new WVec(0, 0, info.LaunchAltitude.Length)),
					new FacingInit(WAngle.Zero),
				]);
				sat.TraitOrDefault<GeoStationary>()?.Station(new WPos(station.X, station.Y, 0));

				if (w.LocalPlayer == self.Owner)
					TextNotificationsManager.AddSystemLine("Ground Station", "Geostationary satellite launched. It will hold its position over the target.");
			});
		}
	}

	[Desc("Flies to its station and stays there, high up. Ignores orders.")]
	public class GeoStationaryInfo : TraitInfo, Requires<AircraftInfo>
	{
		public readonly int ClimbSpeed = 32;

		[Desc("Turns slowly in place once on station (1024ths of a turn per tick).")]
		public readonly int Spin = 2;

		public override object Create(ActorInitializer init) { return new GeoStationary(this); }
	}

	public class GeoStationary : ITick
	{
		readonly GeoStationaryInfo info;
		Aircraft aircraft;
		WPos? station;

		public GeoStationary(GeoStationaryInfo info) { this.info = info; }

		public void Station(WPos pos) { station = pos; }

		void ITick.Tick(Actor self)
		{
			if (!self.IsInWorld || self.IsDead)
				return;

			aircraft ??= self.Trait<Aircraft>();

			// Nothing else gets to steer it.
			if (self.CurrentActivity != null)
				self.CancelActivity();

			var pos = self.CenterPosition;
			var dest = station ?? new WPos(pos.X, pos.Y, 0);
			var z = Math.Min(pos.Z + info.ClimbSpeed, aircraft.Info.CruiseAltitude.Length);
			var d = new WVec(dest.X - pos.X, dest.Y - pos.Y, 0);
			var speed = aircraft.Info.Speed;
			var len = d.HorizontalLength;
			WVec step;
			if (len <= speed)
			{
				step = d;
				aircraft.Facing += new WAngle(info.Spin);
			}
			else
			{
				step = d * speed / len;
				aircraft.Facing = d.Yaw;
			}

			aircraft.SetPosition(self, new WPos(pos.X + step.X, pos.Y + step.Y, z));
		}
	}
}
