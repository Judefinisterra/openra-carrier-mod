-- Automated carrier test: spawn a carrier next to an enemy shore, attack, log what happens.

local function isWater(x, y)
	return Map.TerrainType(CPos.New(x, y)) == "Water"
end

local function findSpot()
	for y = 10, 140, 3 do
		for x = 10, 130, 3 do
			local deep = true
			for dx = -2, 2 do
				for dy = -2, 2 do
					if not isWater(x + dx, y + dy) then deep = false end
				end
			end
			if deep then
				local land = true
				for dx = 7, 9 do
					for dy = -1, 1 do
						if Map.TerrainType(CPos.New(x + dx, y + dy)) ~= "Clear" then land = false end
					end
				end
				if land then return CPos.New(x, y), CPos.New(x + 8, y) end
			end
		end
	end
end

local tick = 0
local maxPlanes = 0
local planeSeenLeaving = false
local enemies = { }
local carrier
local done = false

WorldLoaded = function()
	local player = Player.GetPlayer("Multi0")
	local creeps = Player.GetPlayer("Creeps")
	local sea, land = findSpot()
	print("TEST spot sea=" .. tostring(sea) .. " land=" .. tostring(land))

	carrier = Actor.Create("carrier", true, { Owner = player, Location = sea })
	Actor.Create("camera", true, { Owner = player, Location = land })
	for i = -1, 1 do
		enemies[#enemies + 1] = Actor.Create("2tnk", true, { Owner = creeps, Location = CPos.New(land.X + 1, land.Y + i) })
	end

	Camera.Position = Map.CenterOfCell(sea)
	Trigger.AfterDelay(5, function() carrier.Attack(enemies[1]) end)
end

Tick = function()
	tick = tick + 1
	if done or tick % 25 ~= 0 then return end

	local planes = Utils.Where(Map.ActorsInWorld, function(a) return a.Type == "carrier.plane" end)
	if #planes > maxPlanes then maxPlanes = #planes end
	if maxPlanes > 0 and #planes < maxPlanes then planeSeenLeaving = true end

	local hp = { }
	local alive = 0
	for _, e in ipairs(enemies) do
		if e.IsDead then hp[#hp + 1] = "dead" else alive = alive + 1; hp[#hp + 1] = tostring(e.Health) end
	end

	print("TEST t=" .. tick .. " planesAirborne=" .. #planes .. " enemies=" .. table.concat(hp, ",") .. " carrierHP=" .. (carrier.IsDead and "dead" or carrier.Health) .. " idle=" .. tostring(carrier.IsIdle) .. " stance=" .. tostring(carrier.Stance))
	if tick == 600 then for _, e in ipairs(enemies) do if not e.IsDead then print("TEST manual attack order"); carrier.Attack(e); break end end end

	if alive == 0 or tick >= 3000 then
		print("TEST DONE t=" .. tick .. " maxPlanes=" .. maxPlanes .. " planesReturned=" .. tostring(planeSeenLeaving) .. " enemiesAlive=" .. alive)
		done = true
	end
end
