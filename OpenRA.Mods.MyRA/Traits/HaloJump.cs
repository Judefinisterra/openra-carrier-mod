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
	// ================================================================================================
	// HALO jump: unload a transport plane while it is flying and the infantry aboard (Navy SEALs and the like)
	// jump out one after another as it flies on, and parachute down onto whatever is below. The plane doesn't
	// land; anything that can't parachute (vehicles) stays aboard.
	// ================================================================================================
	[Desc("Infantry aboard jump out and parachute down when the plane is unloaded in the air.")]
	public class HaloJumpInfo : TraitInfo, Requires<CargoInfo>, Requires<AircraftInfo>
	{
		[Desc("Ticks between jumpers.")]
		public readonly int JumpInterval = 7;

		public readonly string ChuteSound = "chute1.aud";

		public override object Create(ActorInitializer init) { return new HaloJump(this); }
	}

	public class HaloJump : IResolveOrder, ITick
	{
		readonly HaloJumpInfo info;
		readonly Queue<Actor> jumpers = new();
		int wait;

		public HaloJump(HaloJumpInfo info) { this.info = info; }

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString != "Unload" || self.World.Map.DistanceAboveTerrain(self.CenterPosition).Length <= 0)
				return;

			var cargo = self.Trait<Cargo>();
			var ready = cargo.Passengers.Where(p => p.Info.HasTraitInfo<ParachutableInfo>() && !jumpers.Contains(p)).ToList();
			if (ready.Count == 0)
				return;

			foreach (var p in ready)
				jumpers.Enqueue(p);

			// Keep flying: the cargo unload would otherwise bring the plane down to land.
			self.World.AddFrameEndTask(_ => self.CancelActivity());
		}

		void ITick.Tick(Actor self)
		{
			if (jumpers.Count == 0 || --wait > 0)
				return;

			wait = info.JumpInterval;
			var p = jumpers.Dequeue();
			var cargo = self.Trait<Cargo>();
			if (p.IsDead || !cargo.Passengers.Contains(p) || self.World.Map.DistanceAboveTerrain(self.CenterPosition).Length <= 0)
				return;

			var map = self.World.Map;
			var cell = map.CellContaining(self.CenterPosition);
			if (!map.Contains(cell))
			{
				jumpers.Enqueue(p);
				return;
			}

			cargo.Unload(self, p);
			var altitude = self.CenterPosition.Z;
			self.World.AddFrameEndTask(w =>
			{
				if (p.Disposed)
					return;

				var pos = p.Trait<IPositionable>();
				var sub = pos.GetAvailableSubCell(cell);
				pos.SetPosition(p, cell, sub == SubCell.Invalid ? SubCell.Any : sub);
				pos.SetCenterPosition(p, p.CenterPosition + new WVec(0, 0, altitude - p.CenterPosition.Z));
				p.Trait<Passenger>().OnBeforeAddedToWorld(p);
				w.Add(p);
			});

			Game.Sound.Play(SoundType.World, info.ChuteSound, self.CenterPosition);
		}
	}
}
