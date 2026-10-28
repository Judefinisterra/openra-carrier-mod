-- Automated FPV drone test: drone operators against fleeing infantry, a jeep and a medium tank.

local function clear(x, y)
	local t = Map.TerrainType(CPos.New(x, y))
	return t == "Clear" or t == "Road" or t == "Rough"
end

local function findSpot()
	for y = 10, 140, 2 do
		for x = 10, 130, 2 do
			local ok = true
			for dx = 0, 14 do
				for dy = -4, 4 do
					if not clear(x + dx, y + dy) then ok = false end
				end
			end
			if ok then return x, y end
		end
	end
end

local tick = 0
local done = false
local targets = { }
local names = { }
local operators = { }
local ambushed = false
local player
local creeps
local spotX, spotY

local function add(name, actor)
	actor.Stance = "HoldFire"
	targets[#targets + 1] = actor
	names[#names + 1] = name
end

WorldLoaded = function()
	player = Player.GetPlayer("Multi0")
	creeps = Player.GetPlayer("Creeps")
	local x, y = findSpot()
	spotX, spotY = x, y
	print("TEST spot " .. x .. "," .. y)

	operators[1] = Actor.Create("fpv", true, { Owner = player, Location = CPos.New(x, y) })
	operators[2] = Actor.Create("fpv", true, { Owner = player, Location = CPos.New(x, y + 1) })
	Actor.Create("camera", true, { Owner = player, Location = CPos.New(x + 6, y) })

	for i = 0, 2 do
		local e = Actor.Create("e1", true, { Owner = creeps, Location = CPos.New(x + 5, y - 1 + i) })
		add("e1#" .. i, e)
		-- Run away so the drones have to chase.
		Trigger.AfterDelay(30, function() if not e.IsDead then e.Move(CPos.New(x + 14, y - 3 + i * 3)) end end)
	end

	add("jeep", Actor.Create("jeep", true, { Owner = creeps, Location = CPos.New(x + 6, y + 4), Facing = Angle.East }))
	add("2tnk", Actor.Create("2tnk", true, { Owner = creeps, Location = CPos.New(x + 6, y - 4), Facing = Angle.East }))

	Camera.Position = Map.CenterOfCell(CPos.New(x + 5, y))
end

Tick = function()
	tick = tick + 1

	-- Keep the view on the fight so the drones can be watched.
	if spotX and not done then
		Camera.Position = Map.CenterOfCell(CPos.New(spotX + 4, spotY))
	end
	if done or tick % 25 ~= 0 then return end

	local parts = { }
	local alive = 0
	for i, t in ipairs(targets) do
		if t.IsDead then
			parts[#parts + 1] = names[i] .. "=dead"
		else
			alive = alive + 1
			parts[#parts + 1] = names[i] .. "=" .. t.Health .. "@" .. t.Location.X .. "," .. t.Location.Y
		end
	end

	for i, o in ipairs(operators) do
		parts[#parts + 1] = "operator" .. i .. "=" .. (o.IsDead and "dead" or o.Health)
	end

	print("TEST t=" .. tick .. " " .. table.concat(parts, " "))

	-- Once the targets are gone, a rifleman sneaks up next to the operators.
	if alive == 0 and not ambushed then
		ambushed = true
		print("TEST ambush")
		local hunter = Actor.Create("e1", true, { Owner = creeps, Location = CPos.New(spotX - 1, spotY) })
		hunter.Attack(operators[1])
		Trigger.OnKilled(operators[1], function() if not operators[2].IsDead then hunter.Attack(operators[2]) end end)
	end

	local operatorsAlive = (operators[1].IsDead and 0 or 1) + (operators[2].IsDead and 0 or 1)
	if (ambushed and operatorsAlive == 0) or tick >= 2500 then
		print("TEST DONE t=" .. tick .. " alive=" .. alive)
		done = true
	end
end
