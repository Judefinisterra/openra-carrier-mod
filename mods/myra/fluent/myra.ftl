myra-mod-title = Red Alert (Modded)
myra-mod-windowtitle = OpenRA - Red Alert (Modded)

actor-carrier =
    .name = Aircraft Carrier
    .description =
    Built at the Seaport. Carries 3 F-14 Tomcats that
    launch against its target, then land to rearm.
    Lost F-14s are replaced from the Airfield.
    Also carries up to 50 infantry and vehicles.
      Strong vs Ground units, Buildings, Aircraft
      Weak vs Submarines

actor-carrier-plane =
    .name = Carrier Bomber

actor-fpv =
    .name = Zelensky's Little Friend
    .description =
    Unarmed operator who flies one-way attack drones from long range.
    Drones dart about, chase down their target and dive onto it.
    Cannot launch at enemies that get close.
      Strong vs Infantry, Light vehicles
      Weak vs Buildings, Aircraft, anything up close

actor-dswarm =
    .name = Drone Swarm
    .description =
    Truck carrying 30 one-way attack drones.
    Force-fire at the ground to send the swarm that way:
    each drone attacks the first enemy it finds.
    Never fires on its own. Long reload.
      Strong vs Infantry, Light vehicles
      Weak vs Buildings, Aircraft

actor-subcarrier =
    .name = Typhoon Sub-Carrier
    .description =
    Submarine aircraft carrier. Hides underwater when idle.
    Carries 4 attack helicopters, torpedoes, deck guns and AA.
    Holds up to 6 infantry or vehicles and can beach to
    unload them. Ctrl+click to launch an Atom Bomb.
      Strong vs Ground units, Buildings, Ships and Submarines
      Weak vs Aircraft

actor-subcarrier-heli =
    .name = Sub-Carrier Hind
    .description =
    Attack helicopter for the Typhoon Sub-Carrier. Flies to the
    nearest of your subs with an empty slot and joins it.
      Requires a Sub-Carrier

actor-seal =
    .name = Navy SEAL
    .description =
    Elite commando. Swims through water, snipes from
    long range and plants C4 on buildings.
      Strong vs Infantry and Buildings
      Weak vs Vehicles and Aircraft

actor-shahed =
    .name = Shahed Drone
    .description =
    One-way attack drone. Fly it at a target and it
    dives in and explodes with the force of a V2 rocket.
    Unlimited range. Waits parked on the ground.
      Strong vs Infantry, Buildings and Vehicles
      Weak vs Anti-air

actor-spire =
    .name = Skywatch Spire
    .description =
    Observatory tower. Reveals the entire map while it stands.

actor-tribalhero =
    .name = Tribal Hero
    .description =
    Robed warrior who throws spears. Spears fly slowly
    but kill any infantry they hit. Unlocks the Lion.
      Strong vs Infantry
      Weak vs Vehicles and Buildings

actor-lion =
    .name = Lion
    .description =
    Pounces on enemy infantry and kills them instantly.
    Trained at the Kennel while you have a Tribal Hero.
      Strong vs Infantry
      Weak vs Vehicles and Buildings

actor-fantasy =
    .name = Fortress
    .description =
    A Cold War concrete command bunker. Required to build the
    Sub-Carrier and Navy SEAL.
    Defended by 5 Tesla coils, 6 pillbox guns and 10 SAM launchers.

actor-protester =
    .name = War Protester
    .description =
    Carries a protest sign. Unarmed.

## Truce chat commands
description-truce = click an opponent's unit to propose a truce (or /truce PlayerName).
description-accept-truce = accept a truce that was proposed to you.
description-decline-truce = refuse a truce that was proposed to you.

## Truce pop-ups
dialog-truce-propose =
    .title = Propose a Truce
    .prompt =
        Offer { $player } a truce?
        Neither side will attack the other until someone breaks it.
    .confirm = Propose Truce
    .cancel = Cancel

dialog-truce-offer =
    .title = Truce Offered
    .prompt =
        { $player } proposes a truce.
        If you accept, neither side will attack the other
        until someone breaks it.
    .accept = Accept
    .decline = Decline

## Tribal Kingdom
faction-tribal =
    .name = Tribal Kingdom
    .description = Tribal Kingdom: Spears and Claws
     Builds only Power, Refineries, Barracks, Kennels and a War Factory.
     Fields hordes of Tribal Heroes and Lions, plus Jeeps.

bot-tribal-ai =
    .name = Tribal Warlord AI

## Cheat codes
description-cheat-cash = gives you $10,000 (also works as plain "cash").
description-cheat-clear = removes or restores the fog of war (also works as plain "clear").
description-cheat-nuke = instantly charges all your nukes (also works as plain "nuke").
description-cheat-guns = 100 infantry appear around your Construction Yard (also works as plain "guns").
description-cheat-sadam = 20 tanks appear around your Construction Yard (also works as plain "sadam").
description-cheat-faster = everything builds instantly, like debug instant-build; type again to switch off (also works as plain "faster").
description-cheat-power = unlimited power; type again to switch off (also works as plain "power").
description-cheat-far = build anywhere on the map: no construction radius, terrain rules still apply; type again to switch off.
description-cheat-advice = "advice: <message>" sends a strategic directive to the AI commander at its next briefing.
description-cheat-anywhere = place buildings anywhere; type again to switch off (also works as plain "anywhere").
description-cheat-heal = fully heals all your units and buildings (also works as plain "heal").
description-cheat-all = every cheat at once; type again to switch the on/off ones off (type plain "all").
description-cheat-jubilee = every player in the game gets $10,000 (type plain "jubilee").
description-cheat-god = every faction's buildings and units in your build menus; type again to switch off (type plain "god").
description-cheat-lips = show every order the AI Commander gives the computer players in the chat; type again to hide (type plain "lips").

actor-oiltruck =
    .name = Oil Rig Truck
    .description =
    Drives to a spot and deploys (F) into an Oil Derrick.
    Comes with a free Oil Hauler that drives the oil
    to your Refinery: $600 a load.
      Unarmed

actor-oiltruck-derrick =
    .name = Oil Derrick
    .desc = Pumping oil. Its Oil Hauler takes it to your Refinery.

## Iran
faction-iran =
    .name = Iran
    .description = Iran: Fortress and Swarm
     Defenses are tougher and 25% cheaper. Oil Rig Trucks 25% cheaper.
     Special Units: Fast Attack Boat, Shahed drones from the Barracks

actor-fab =
    .name = Fast Attack Boat
    .description =
    Very fast rocket boat. Built at the Sub Pen.
      Strong vs Ships and Ground units
      Weak vs Submarines and Aircraft

bot-iran-ai =
    .name = Iranian Command AI

actor-npp =
    .name = Nuclear Power Plant
    .description =
    Provides 10x the power of an Advanced Power Plant.
    WARNING: explodes like an atom bomb when destroyed.

actor-tribalhouse =
    .name = Hideout
    .description =
    Hides up to 10 Tribal Heroes or Lions, safe from airstrikes.
    They rush out when enemies come near or it is attacked (or press D).

## Tribal Boat Dock and War Canoe
actor-tribaldock =
    .name = Boat Dock
    .description = A wooden jetty built on the water beside your base.
      Builds War Canoes.

actor-canoe =
    .name = War Canoe
    .description = Big dugout canoe that carries up to 5 infantry
      (Tribal Heroes and Lions) across water and lands them on any shore.
      Its paddlers throw spears at nearby enemies - more with warriors aboard.

## Offshore Oil Platform
actor-oilplatform =
    .name = Offshore Oil Platform
    .description = Drilling platform you can build on any open water on the map.
      Comes with a free Oil Tanker that carries the oil to your docks for cash.
    .desc = Pumping oil. Its Oil Tanker carries the oil to your docks.

## RPG Fella
actor-rpgfella =
    .name = RPG Fella
    .description = Tribal warrior with a rocket launcher.
      Strong vs tanks, buildings and aircraft.
      Weak vs infantry.

## AI difficulty (lobby option)
dropdown-ai-difficulty =
    .label = AI Difficulty
    .description = How tough the computer players are: build speed, income, and how cleverly they fight.

options-ai-difficulty =
    .easy = Easy
    .normal = Normal
    .hard = Hard
    .brutal = Brutal

actor-containership =
    .name = Container Ship
    .description = Huge unarmed cargo ship. Needs a Seaport.
      Sails on its own between your Seaport and the Seaport of a
      player you're in a truce with: $5000
      to them every time it docks there, and $5000 to you every time
      it gets back. Waits at home if you have no truce partner with
      a port. Deploy (F) to (re)start its trade route.
      Protect it at sea.

## Seaport
actor-seaport =
    .name = Seaport
    .description = Commercial harbour built on the water near your base.
      Container Ships and Oil Tankers dock here to unload their cargo.
      Required to build Container Ships.

## Oil Tanker
actor-oiltanker =
    .name = Oil Tanker
    .description = Carries oil from your Offshore Oil Platforms to your
      Seaport (or, without one, your Naval Yard, Sub Pen or Boat Dock):
      $1000 a load.
      Works on its own, like an ore harvester. Unarmed.

## Psy-Op support power (Tech Center)
actor-psyop =
    .name = Psy-Op
    .description = Stirs up a mass protest: 100 War Protesters pour into
      the streets wherever you choose. They block roads and soak up
      enemy fire. Recharges like the other support powers.

## Land units - Tribal Kingdom
actor-animalpen =
    .name = Animal Pen
    .description = Raises war beasts for the tribe.
      Trains War Elephants and Rhino Riders.

actor-elephant =
    .name = War Elephant
    .description = A walking fortress. Tramples infantry, gores vehicles
      and buildings with its tusks, and two spearmen throw from its back.
      Slowly heals itself.
      Strong vs everything up close.
      Weak vs rockets and massed tanks.

actor-rhino =
    .name = Rhino Rider
    .description = Fast raider that charges through infantry,
      knocking them flat, and rams vehicles with its horn.
      Strong vs infantry and light vehicles.
      Weak vs tanks and defenses.

actor-witchdoctor =
    .name = Witch Doctor
    .description = Heals tribal warriors and lions.
      While you have one you can cast the Witch's Curse.
      Unarmed.
    .curse-name = Witch's Curse
    .curse-description = Curses an area: enemy units there move at half
      speed and fire half as fast for 20 seconds.
      Needs a Witch Doctor.

actor-pitdigger =
    .name = Pit Digger
    .description = Digs hidden Ambush Pits (3 at a time, one more every
      12 seconds). Enemies that step in are hurt and stuck in the
      mud for 10 seconds. Your own units walk around them.
      Click an area (or drag one) to dig there.

actor-ambushpit =
    .name = Ambush Pit

actor-spirittotem =
    .name = Spirit Totem
    .description = Tribal units within 6 cells become brave:
      +25% damage, take 25% less damage, 15% faster.

actor-drumcircle =
    .name = Drum Circle
    .description = The beat of the drums: Barracks, Kennels and
      Animal Pens train 40% faster.

## Land units - Iran
actor-technical =
    .name = Technical
    .description = Pickup truck with a heavy machine gun.
      Cheap and very fast, but fragile. Can use the Tunnels.
      Strong vs infantry.
      Weak vs tanks.

actor-technicalrpg =
    .name = Rocket Technical
    .description = Pickup truck with a rocket pod.
      Cheap and very fast, but fragile. Can use the Tunnels.
      Strong vs tanks and buildings.
      Weak vs infantry.

actor-decoy-v2 =
    .description = Inflatable V2 Rocket Launcher. Looks real to the
      enemy and draws their fire, but has no weapon.

actor-decoy-tank =
    .description = Inflatable Heavy Tank. Looks real to the
      enemy and draws their fire, but has no weapon.

actor-decoy =
    .ally-description = Inflatable decoy - no weapon.

actor-dronetruck =
    .name = Shahed Launch Truck
    .description = Carries 5 Shahed drones. Order it to attack anything
      up to 14 cells away and it launches the whole rack.
      The rack reloads in 30 seconds.

actor-tunnel =
    .name = Tunnel Entrance
    .description = Build two or more. Infantry, jeeps and technicals
      that go in come out of the next entrance a moment later.

actor-missilecity =
    .name = Missile City
    .description = Hardened underground missile base.
      Fires the Missile Volley.
      Maximum 1 can be built.
    .power-name = Missile Volley
    .power-description = Fires 8 ballistic missiles at an area.

actor-oilfire =
    .name = Burning Oil

## Land - everyone
checkbox-aicommander =
    .label = AI Commander
    .description = A language model (online or local) acts as the general for the computer players: one consultation a minute, with separate orders for each. Set it up in ai-commander.json in your OpenRA folder.

checkbox-merccamp =
    .label = Mercenary Camp
    .description = A neutral Mercenary Camp near the middle of the map.
      Capture it with an Engineer to hire mercenaries.

actor-merccamp =
    .name = Mercenary Camp
    .captured-desc = Hire Mercenary Commandos and Mercenary Flamers here.
    .capturable-desc = Capture with an Engineer to hire mercenaries.

actor-merc-commando =
    .name = Mercenary Commando
    .description = Hired gun: deadly against infantry and can blow up
      buildings with C4. Maximum 2.

actor-merc-flamer =
    .name = Mercenary Flamer
    .description = Hired flamethrower trooper. Tougher than the regular one.
      Strong vs infantry and buildings.

actor-supplytruck =
    .name = Supply Convoy
    .description = Drives food from your Farms to your Barracks, and oil
      from your Oil Derricks to your Refinery or Construction Yard:
      $500 a trip.
      Works on its own. Unarmed - protect it on the road.

actor-militia =
    .name = Militia

actor-fieldhosp =
    .name = Field Hospital
    .description = Heals your infantry within 5 cells.

actor-checkpoint =
    .name = Checkpoint

actor-farm =
    .name = Farm
    .description = Huge farm (6x6). Comes with a free Supply Convoy that
      hauls food from the farm to your Barracks: $500 a trip.
      Build more Supply Convoys to earn faster.

actor-myra-paratroopers =
    .description = Five Badgers fly in together and drop 25 soldiers
      (15 riflemen, 10 rocket soldiers) at the selected location.

## Badgers
actor-badrtroop =
    .name = Troop Badger
    .description = Transport plane for up to 10 infantry.
      Load troops while it is parked on the Airfield, then
      right-click a target: it flies there, drops everyone
      by parachute, and flies home.

actor-badrkamikaze =
    .name = Kamikaze Badger
    .description = A Badger packed with explosives. Order it to attack
      and it dives into the target - enough to flatten a building.
      Explodes wherever it crashes if shot down.

## Cyber Attack
actor-cyber =
    .name = Cyber Attack
    .description = Hack an enemy: click near their base. Half of their
      money is wired to you, their radar goes dark for a minute,
      and false "base under attack" alarms go off. Their power
      grid shuts down, their defences turn on their own side for
      30 seconds, and their ore trucks drive off to the far
      corners of the map. Recharges every 10 minutes.

## Hermit Nation
faction-hermit =
    .name = Hermit Nation
    .description = Hermit Nation: Sabotage and Lies
     Cyber Command: hack an enemy (half their money, radar down) every 10 minutes,
     no Tech Center needed.
     Cheap spies, Suicide Bombers, Kamikaze Badgers, and a Psy-Op
     whose protesters turn into suicide bombers.

bot-hermit-ai =
    .name = Hermit Leader AI

actor-spyhermit =
    .description = Cheap spy. Sneak into an enemy Refinery or Silo to
      steal money. Drops a box of cash when killed.

actor-moneybox =
    .name = Spy's Cash

actor-suicidebomber =
    .name = Suicide Bomber
    .description = Runs at the target and blows himself up.
      Also explodes when killed. Deploy to detonate.
      Strong vs infantry, defenses and buildings.

actor-psyop-hermit =
    .name = Psy-Op
    .description = 100 protesters pour into the streets wherever you
      choose. Left alone for 40 seconds, 1 in 10 of them becomes
      a Suicide Bomber loyal to you.

actor-cybercommand =
    .name = Cyber Command
    .description = Hermit Nation hacker bunker.
      Cyber Attack every 10 minutes.
      Maximum 1 can be built.

actor-truckbomb =
    .name = Truck Bomb
    .description = Yellow box truck packed with explosives. Drive it into
      a target (or deploy it) for a huge blast. Explodes if destroyed.
      Strong vs buildings and everything nearby.

actor-oilhauler =
    .name = Oil Hauler
    .description = Tanker truck. Drives oil from your Oil Derricks to your
      nearest Refinery: $600 a load. Works on its own.
      Every derrick you deploy comes with one free.

## Stable Society
faction-stable =
    .name = Stable Society
    .description = Stable Society: Peace and Order
     Starts the game in a truce with every other side, until someone attacks it.
     Government Building (taxes) unlocks the Hospital and Houses.
     Special units: Doctors, Ambulances, White Vans.

bot-stable-ai =
    .name = Civic Council AI

actor-govbuilding =
    .name = Government Building
    .description = The courthouse at the heart of the town.
      Collects taxes: $300 every 20 seconds.
      Unlocks the Hospital and Houses. Maximum 1.

actor-hospital =
    .name = Hospital
    .description = Heals your infantry within 8 cells.
      Trains Doctors and Ambulances.

actor-stablehouse =
    .name = House
    .description = A family home. Up to 6 soldiers can shelter inside;
      they come out if the house is threatened.
      Builds White Vans.

actor-doctor =
    .name = Doctor
    .description = Heals everyone around him all the time, and walks
      over to heal the wounded on his own. Unarmed.

actor-ambulance =
    .name = Ambulance
    .description = Fast transport for 5 infantry.
      Heals infantry standing next to it. Unarmed.

actor-whitevan =
    .name = White Van
    .description = People carrier for 8 infantry. Unarmed.

actor-checkpointv =
    .name = Checkpoint (east-west)

actor-mercbike =
    .name = Motorcycle Trooper
    .description = Mercenary on a fast motorcycle with a bazooka.
      Strong vs tanks and vehicles. Weak vs infantry.

actor-mansion =
    .name = Dream Mansion
    .description = A big pink mansion with a pool.
      Builds Pink Jeeps and Ponies.

actor-pinkjeep =
    .name = Pink Jeep
    .description = Fast jeep with a machine gun - in pink.
      Strong vs infantry. Weak vs tanks.

actor-pony =
    .name = Pony
    .description = Fast mounted scout with a carbine.
      Good for scouting and chasing infantry.

actor-f14 =
    .name = F-14 Tomcat
    .description =
    Swing-wing fighter. Air-to-air missiles, a V2
    rocket for land and sea targets, and a cannon
    when the missiles run out. Send it to a spot and
    it patrols there. Rearms at Airfields and Carriers.
      Strong vs Aircraft, Vehicles, Buildings
      Weak vs Anti-air

actor-launchpad =
    .name = Space Launch Facility
    .description =
    Load up to 10 infantry into the rocket, then
    launch it at any spot on the map: the troops
    parachute in. Vehicles can't board.

actor-launchpad-power =
    .name = Orbital Troop Drop
    .description = Board infantry by right-clicking the pad with them selected, then launch the rocket anywhere on the map. The troops land by parachute.

actor-molotovfire =
    .name = Fire

actor-nycbuilding =
    .name = City Building
    .description = Infantry can climb onto the roof and fire from it.

actor-nyclandmark =
    .name = Landmark

actor-nycbridge =
    .name = Bridge

actor-bridgehut =
    .name = Bridge Repair Hut
    .description = When the bridge is down, send an engineer in to rebuild it.

actor-a10 =
    .name = A-10 Thunderbolt
    .description =
    Armoured ground-attack plane. 30mm cannon in
    5-shot bursts plus a V2 rocket. Send it to a spot
    and it patrols there.
      Strong vs Tanks, Ships, Buildings
      Can't attack Aircraft

checkbox-plague =
    .label = Plague
    .description = Infantry crowded together for too long catch a deadly disease that spreads

actor-b2 =
    .name = B-2 Spirit
    .description =
    Stealth bomber: invisible except just after it
    attacks. Fires V2 rockets in bursts of 4 and
    reloads in flight, so it never lands.
      Strong vs Buildings, Vehicles

actor-sr71 =
    .name = SR-71 Blackbird
    .description =
    Faster than any missile. Unarmed, never lands.
    Everywhere it flies stays revealed for good.

actor-p3 =
    .name = P-3 Orion
    .description =
    Anti-submarine patrol plane. Spots submarines within
    5 cells and drops depth charges on them. Send it
    somewhere and it patrols there. Never lands.
      Strong vs Submarines

actor-ac130 =
    .name = AC-130 Spectre
    .description =
    Gunship: circles its target while three side guns
    (25mm, 40mm, 105mm) fire. Never lands.
      Strong vs Infantry, Vehicles

actor-c130 =
    .name = C-130 Hercules
    .description =
    Transport that lands wherever you send it. Load
    troops or vehicles on the ground, fly them anywhere
    and unload them there.

actor-harrier =
    .name = Harrier
    .description =
    Jump jet: takes off and lands vertically. Missiles
    for aircraft and ground targets.

actor-apache =
    .name = Apache
    .description =
    Attack helicopter with Hellfire missiles and a
    chain gun.
      Strong vs Vehicles, Infantry

actor-newsheli =
    .name = News Helicopter

actor-mobilesam =
    .name = Mobile SAM
    .description =
    Anti-aircraft missile truck that keeps up with
    your army.
      Strong vs Aircraft
      Can't attack ground targets

actor-zodiac =
    .name = SEAL Zodiac
    .description =
    Fast inflatable that runs up onto any beach.
    Comes with 4 Navy SEALs aboard.

actor-lcs =
    .name = Littoral Combat Ship
    .description =
    Deck gun, depth charges, anti-air missiles and
    Tomahawk cruise missiles (40-cell range, one every
    15 seconds). Fires at enemy buildings it can see.

actor-cutter =
    .name = Coast Guard Cutter
    .description =
    Cheap, fast patrol boat that spots submarines
    from far away.

actor-cop =
    .name = Cop
    .description =
    Police officer with a taser: a deadly zap at very
    short range.
      Strong vs Infantry

actor-sniper =
    .name = Sniper
    .description =
    Ghillie-suited marksman, invisible until it fires.
    Very long range, kills infantry in one shot.
      Strong vs Infantry
      Weak vs Vehicles, Buildings

## ClaudeChat
description-claude-command = give Claude Code an order for your army, e.g. /claude attack with all tanks
description-claude-terminal-command = open a terminal running Claude Code to control your army

## Armored Bulldozer
actor-dozer =
    .name = Armored Bulldozer
    .description =
    Armoured bulldozer built at the Construction Yard.
    Cannot be damaged. Drive it into any enemy unit or
    building to flatten it. England only.
      Strong vs Everything it touches
      Weak vs Nothing

## Israel
faction-israel =
    .name = Israel
    .description = Israel: Pager Attack
     A clone of England with one more trick.
     Pager Attack (Radar Dome): every enemy infantryman's pager
     goes off at once, and they all explode.

actor-pager =
    .name = Pager Attack
    .description = Every enemy infantryman's pager goes off at once.
     A second later, they all explode. No target needed.

notification-pager-ready = Pager Attack ready.

## Zombie Apocalypse
actor-zlab =
    .name = Zombie Lab
    .description =
    Superweapon. When charged, every infantryman who has
    died this game rises from his grave as a zombie and
    attacks the nearest living unit. Zombies attack everyone,
    including you. Requires power.
    .power-name = Zombie Apocalypse
    .power-description = Raise every dead infantryman on the map as a zombie.
     No target needed. Zombies attack everyone.

notification-zombies-charging = Zombie Apocalypse charging.
notification-zombies-ready = Zombie Apocalypse ready.

## Pirate Ship
actor-pirate =
    .name = Pirate Ship
    .description =
    A galleon flying the Jolly Roger. Fires cannon
    broadsides from both sides at once.
      Strong vs Ships, Infantry, Buildings near the shore
