-- Claude demo on Four Islands: Israel (the player, north-west island) against a passive Russian base across the
-- channel (north-east island). Everything is placed by this script; the demo is driven through the control server.

local mine = {
	{ "apwr", 12, 12 },
	{ "apwr", 13, 27 },
	{ "powr", 15, 17 },
	{ "powr", 15, 21 },
	{ "powr", 14, 24 },
	{ "powr", 18, 28 },
	{ "tent", 26, 17 },
	{ "dome", 26, 21 },
	{ "weap", 25, 26 },
	{ "atek", 21, 28 },
	{ "zlab", 29, 23 },
	{ "slapshrine", 21, 17 },
	{ "apwr", 9, 19 },
	{ "afld", 29, 31 },
	{ "spyctr", 13, 18 },
	{ "apwr", 6, 14 },
	{ "apwr", 6, 28 },
	{ "nreact", 12, 34 },
	{ "nlab", 24, 13 },
	{ "mslo", 31, 14 },
	{ "mslo", 34, 16 },
	{ "gstation", 17, 13 },
	{ "apwr", 9, 23 },
	{ "hpad", 34, 29 },
	{ "b52", 17, 33 },
	{ "mig", 24, 33 },
	{ "ac130", 20, 33 },
	{ "idome", 30, 18 },
	{ "gun", 34, 20 },
	{ "gun", 34, 25 },
	{ "pbox", 36, 22 },
	{ "moses", 40, 21 },
	{ "2tnk", 38, 20 },
	{ "2tnk", 38, 24 },
	{ "jeep", 37, 22 },
}

local theirs = {
	{ "asat", 92, 24 },
	-- Missile defence: three Iron Domes and two Nike sites, with the power to run them.
	{ "idome", 92, 13 },
	{ "idome", 79, 30 },
	{ "nike", 98, 24 },
	{ "nike", 84, 12 },
	{ "apwr", 104, 20 },
	{ "apwr", 104, 14 },
	{ "apwr", 100, 32 },
	{ "apwr", 100, 26 },
	{ "apwr", 100, 30 },
	{ "fact", 85, 18 },
	{ "powr", 94, 15 },
	{ "powr", 97, 18 },
	{ "powr", 95, 21 },
	{ "barr", 83, 15 },
	{ "proc", 92, 27 },
	{ "tsla", 80, 20 },
	{ "ftur", 80, 25 },
	{ "idome", 85, 27 },
	{ "weap", 87, 31 },
	{ "powr", 68, 17 },
	{ "barr", 70, 22 },
	{ "ftur", 66, 26 },
	{ "gun", 67, 21 },
	{ "harv", 90, 31 },
	{ "3tnk", 76, 22 },
	{ "3tnk", 73, 27 },
	{ "e3", 78, 14 },
	{ "e1", 80, 14 },
	{ "e1", 82, 14 },
	{ "e3", 84, 14 },
	{ "e1", 86, 14 },
	{ "e1", 78, 16 },
	{ "e3", 79, 15 },
	{ "e1", 82, 16 },
	{ "e1", 83, 17 },
	{ "e3", 86, 16 },
	{ "e1", 78, 18 },
	{ "e1", 80, 18 },
	{ "e3", 82, 18 },
	{ "e1", 84, 18 },
	{ "e1", 85, 17 },
}

local wave = {
	{ "v2rl", 22, 42 },
	{ "arty", 24, 42 },
	{ "v2rl", 25, 43 },
	{ "arty", 28, 42 },
	{ "v2rl", 30, 42 },
	{ "arty", 32, 42 },
}

WorldLoaded = function()
	local me = Player.GetPlayer("Multi0")
	local ai = Player.GetPlayer("AI")
	me.Cash = 1000000
	ai.Cash = 10000
	for _, a in ipairs(mine) do
		Actor.Create(a[1], true, { Owner = me, Location = CPos.New(a[2], a[3]) })
	end
	-- Four spies already inside the enemy base.
	for i = 0, 3 do
		Actor.Create("coupspy", true, { Owner = me, Location = CPos.New(83 + i, 22) })
	end

	-- A Black Hawk on the helipad with Tanya and five SEALs aboard, ready to fast-rope.
	local heli = Actor.Create("mh60", true, { Owner = me, Location = CPos.New(34, 29) })
	heli.LoadPassenger(Actor.Create("e7", false, { Owner = me }))
	for i = 1, 5 do
		heli.LoadPassenger(Actor.Create("seal", false, { Owner = me }))
	end
	-- Six more SEALs on the ground by the helipads, ready to board.
	for i = 0, 5 do
		Actor.Create("seal", true, { Owner = me, Location = CPos.New(36 + i % 3, 27 + math.floor(i / 3)) })
	end
	-- A second Black Hawk, empty, for the ground squad to board.
	Actor.Create("mh60", true, { Owner = me, Location = CPos.New(37, 31) })
	-- Test kit for the newest units.
	-- Nuclear test already done and research finished, so every nuke is on the menu.
	Actor.Create("nuketested", true, { Owner = me })
	Actor.Create("nuctech", true, { Owner = me })
	-- A-10 for strafing runs, and a C-130 full of SEALs for HALO jumps.
	Actor.Create("a10", true, { Owner = me, Location = CPos.New(32, 36) })
	local c130 = Actor.Create("c130", true, { Owner = me, Location = CPos.New(27, 37) })
	for i = 1, 6 do
		c130.LoadPassenger(Actor.Create("seal", false, { Owner = me }))
	end
	-- Naval Yard on the east shore with a Yamamoto already afloat.
	Actor.Create("syrd", true, { Owner = me, Location = CPos.New(53, 19) })
	Actor.Create("yamamoto", true, { Owner = me, Location = CPos.New(57, 26) })
	-- Three paramotors by the airfield, with riflemen beside them to pick them up.
	for i = 0, 2 do
		Actor.Create("paramotor", true, { Owner = me, Location = CPos.New(21 + i * 2, 38) })
		Actor.Create("e1", true, { Owner = me, Location = CPos.New(21 + i * 2, 40) })
	end
	for _, a in ipairs(theirs) do
		Actor.Create(a[1], true, { Owner = ai, Location = CPos.New(a[2], a[3]) })
	end

	-- After 100 seconds, enemy rockets and artillery land on our island and shell the base.
	Trigger.AfterDelay(DateTime.Seconds(100), function()
		for _, a in ipairs(wave) do
			local u = Actor.Create(a[1], true, { Owner = ai, Location = CPos.New(a[2], a[3]) })
			u.AttackMove(CPos.New(24, 26))
		end
	end)
end
