-- Claude demo: Israel (local player) against an English AI. Builds an enemy outpost next to the
-- player's base for the bulldozer, pager, zombies and cyber attack to hit.

local outpost = {
	{ "gun", 30, 23 }, { "powr", 34, 24 }, { "barr", 38, 24 }, { "3tnk", 42, 25 },
	{ "ftur", 36, 29 }, { "tsla", 41, 29 }, { "proc", 45, 22 }, { "harv", 46, 26 },
	{ "idome", 43, 50 }, { "powr", 40, 50 }, { "powr", 40, 53 },
}

WorldLoaded = function()
	local me = Player.GetPlayer("Multi0")
	local ai = Player.GetPlayer("AI")
	Actor.Create("mcv", true, { Owner = ai, Location = CPos.New(49, 55) })
	ai.Cash = 10000
	me.Cash = 25000
	for _, a in ipairs(outpost) do
		Actor.Create(a[1], true, { Owner = ai, Location = CPos.New(a[2], a[3]) })
	end
	-- The player's base: a garrison, power, a Tech Center and an Iron Dome.
	for _, a in ipairs({ { "gun", 26, 19 }, { "gun", 27, 14 }, { "pbox", 25, 22 }, { "2tnk", 25, 17 }, { "2tnk", 26, 17 }, { "2tnk", 25, 18 },
		{ "powr", 17, 19 }, { "powr", 19, 20 }, { "powr", 15, 18 }, { "dome", 15, 14 }, { "weap", 17, 11 }, { "atek", 21, 11 }, { "idome", 21, 20 } }) do
		Actor.Create(a[1], true, { Owner = me, Location = CPos.New(a[2], a[3]) })
	end
	for i = 0, 13 do
		local t = i % 4 == 0 and "e3" or "e1"
		Actor.Create(t, true, { Owner = ai, Location = CPos.New(43 + (i % 7), 45 + math.floor(i / 7)) })
	end

	-- A wave of rockets and artillery shells the player's base.
	Trigger.AfterDelay(DateTime.Seconds(80), function()
		for i = 0, 5 do
			local t = i % 2 == 0 and "v2rl" or "arty"
			local a = Actor.Create(t, true, { Owner = ai, Location = CPos.New(36 + i, 9 + (i % 2)) })
			a.AttackMove(CPos.New(23, 17))
		end
	end)
end
