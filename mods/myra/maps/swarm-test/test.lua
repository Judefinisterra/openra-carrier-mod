-- Drone Swarm test: a plain attack order on an enemy (right-click), then a second order during the reload.

local tick = 0
local truck
local targets = { }

WorldLoaded = function()
	local player = Player.GetPlayer("Multi0")
	local creeps = Player.GetPlayer("Creeps")
	local x, y = 54, 40
	truck = Actor.Create("dswarm", true, { Owner = player, Location = CPos.New(x - 6, y), Facing = Angle.East })
	Actor.Create("camera", true, { Owner = player, Location = CPos.New(x + 6, y) })
	for i = 0, 5 do
		local e = Actor.Create(i < 3 and "e1" or "2tnk", true, { Owner = creeps, Location = CPos.New(x + 6 + (i % 3), y - 2 + i) })
		e.Stance = "HoldFire"
		targets[#targets + 1] = e
	end

	Camera.Position = Map.CenterOfCell(CPos.New(x, y))
	Trigger.AfterDelay(25, function()
		print("TEST order attack on " .. targets[4].Type)
		truck.Attack(targets[4])
	end)
end

Tick = function()
	tick = tick + 1
	if tick % 25 ~= 0 then return end
	local parts = { }
	local alive = 0
	for i, t in ipairs(targets) do
		if t.IsDead then parts[#parts + 1] = "dead" else alive = alive + 1; parts[#parts + 1] = tostring(t.Health) end
	end
	print("TEST t=" .. tick .. " " .. table.concat(parts, ",") .. " truckIdle=" .. tostring(truck.IsIdle))
	if alive == 0 or tick >= 1000 then print("TEST DONE t=" .. tick .. " alive=" .. alive) end
end
