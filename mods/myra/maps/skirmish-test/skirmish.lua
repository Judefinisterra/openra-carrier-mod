-- Skirmish test: the local player (Soviet) against a normal Allied AI on Desert Rats.
-- The local side is driven from outside through the Claude control server; this script only
-- gives the AI its base and logs the state of the game.

local tick = 0
local me
local ai

local function count(player, filter)
	local n = 0
	for _, a in ipairs(player.GetActors()) do
		if a.IsInWorld and not a.IsDead and filter(a) then n = n + 1 end
	end
	return n
end

local function isUnit(a) return a.HasProperty("Move") end
local function isBuilding(a) return a.HasProperty("StartBuildingRepairs") end

WorldLoaded = function()
	me = Player.GetPlayer("Multi0")
	ai = Player.GetPlayer("AI")
	Actor.Create("mcv", true, { Owner = ai, Location = CPos.New(49, 55) })
	ai.Cash = 10000
	me.Cash = 25000
	print("SKIRMISH start me=" .. me.Faction .. " ai=" .. ai.Faction)
end

Tick = function()
	tick = tick + 1
	if tick % 250 ~= 0 then return end

	local fpv = count(me, function(a) return a.Type == "fpv" end)
	local swarm = count(me, function(a) return a.Type == "dswarm" end)
	print("SKIRMISH t=" .. tick
		.. " me units=" .. count(me, isUnit) .. " buildings=" .. count(me, isBuilding) .. " fpv=" .. fpv .. " dswarm=" .. swarm .. " cash=" .. me.Cash
		.. " | ai units=" .. count(ai, isUnit) .. " buildings=" .. count(ai, isBuilding) .. " cash=" .. ai.Cash
		.. " | kills me=" .. me.KillsCost .. " ai=" .. ai.KillsCost)

end
