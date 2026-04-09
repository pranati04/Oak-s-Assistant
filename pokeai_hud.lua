--[[
  PokeAI — BizHawk Lua Companion Script (optional)
  ─────────────────────────────────────────────────
  Draws a minimal HUD overlay directly on the game screen.
  The full sidebar panel comes from the C# External Tool;
  this script is optional — it just adds in-game overlays.

  Load via: BizHawk → Tools → Lua Console → Open Script
--]]

-- ── FireRed US v1.0 memory addresses ─────────────────────────
local ADDR = {
  BADGE_FLAGS   = 0x20244F2,
  MAP_BANK      = 0x02036DFC,
  MAP_ID        = 0x02036DFD,
  PARTY_COUNT   = 0x02024284,
  PARTY_BASE    = 0x02024288,   -- 100 bytes per slot
  MONEY         = 0x02025A00,
}

-- ── Helpers ───────────────────────────────────────────────────
local function read_byte(addr)
  return mainmemory.read_u8(addr)
end

local function read_u16(addr)
  return mainmemory.read_u16_le(addr)
end

local function count_bits(b)
  local n = 0
  while b > 0 do n = n + (b & 1); b = b >> 1 end
  return n
end

-- ── Species names — read directly from ROM ───────────────────
-- FireRed/LeafGreen store all 386 names at 0x00245EE0
-- 11 bytes each, FireRed charset, 0xFF terminated
local ROM_SPECIES_BASE = 0x00245EE0
local ROM_SPECIES_SIZE = 11

local CHARSET = {}
for i = 0, 25 do CHARSET[0xBB + i] = string.char(65 + i) end   -- A-Z
for i = 0, 25 do CHARSET[0xD5 + i] = string.char(97 + i) end   -- a-z
for i = 0, 9  do CHARSET[0xA1 + i] = string.char(48 + i) end   -- 0-9
CHARSET[0xAD] = "-"

local function decode_fr_string(addr, maxlen)
  local s = ""
  for i = 0, maxlen - 1 do
    local b = memory.read_u8(addr + i, "System Bus")
    if b == 0xFF then break end
    s = s .. (CHARSET[b] or "?")
  end
  return s == "" and "???" or s
end

-- Build the full species table by reading the ROM once at script load
local SPECIES = {}
local species_ok, species_err = pcall(function()
  for i = 1, 386 do
    local addr = ROM_SPECIES_BASE + (i - 1) * ROM_SPECIES_SIZE
    local name = decode_fr_string(addr, ROM_SPECIES_SIZE)
    if name ~= "???" then
      SPECIES[i] = name
    end
  end
end)

if species_ok then
  print(string.format("[PokeAI Lua] Loaded %d species names from ROM", #SPECIES))
else
  print("[PokeAI Lua] WARNING: Could not read species from ROM — " .. tostring(species_err))
  -- Minimal fallback for the HUD to still function
  SPECIES = { [4]="Charmander", [25]="Pikachu", [129]="Magikarp" }
end
  [0]={[0]="Pallet Town",[1]="Viridian",[2]="Pewter",[3]="Cerulean",
       [5]="Vermilion",[6]="Celadon",[7]="Fuchsia",[9]="Indigo Plateau",
       [10]="Saffron",[8]="Cinnabar"},
  [12]={[0]="Route 1",[1]="Route 2",[2]="Route 3",[3]="Route 4",
        [4]="Route 5",[5]="Route 6",[6]="Route 7",[7]="Route 8",
        [8]="Route 9",[9]="Route 10",[10]="Route 11"},
}

local BADGE_NAMES = {"Boulder","Cascade","Thunder","Rainbow",
                     "Soul","Marsh","Volcano","Earth"}

-- ── HUD state (updated every 60 frames to reduce overhead) ───
local hud = {
  location  = "---",
  badges    = 0,
  party     = {},
  frame_cnt = 0,
}

local function refresh_hud()
  -- Map
  local bank = read_byte(ADDR.MAP_BANK)
  local id   = read_byte(ADDR.MAP_ID)
  local bmap = MAP_NAMES[bank]
  hud.location = (bmap and bmap[id]) or string.format("Map %d:%d", bank, id)

  -- Badges
  local bflags = read_byte(ADDR.BADGE_FLAGS)
  hud.badges = count_bits(bflags)

  -- Party
  hud.party = {}
  local count = math.min(6, read_byte(ADDR.PARTY_COUNT))
  for i = 0, count - 1 do
    local base   = ADDR.PARTY_BASE + i * 100
    local species = read_u16(base)
    local level   = read_byte(base + 0x38)
    local cur_hp  = read_u16(base + 0x22)
    local max_hp  = read_u16(base + 0x24)
    if species > 0 and species <= 386 then
      table.insert(hud.party, {
        name   = SPECIES[species] or ("#"..species),
        level  = level,
        cur_hp = cur_hp,
        max_hp = math.max(1, max_hp),
      })
    end
  end
end

-- ── Drawing ───────────────────────────────────────────────────
local function draw_hud()
  local x, y = 2, 2
  local bg    = 0xCC000000   -- translucent black
  local white = 0xFFFFFFFF
  local green = 0xFF00FF87
  local red   = 0xFFCC2200
  local gold  = 0xFFFFD700
  local gray  = 0xFF90A4AE

  -- Background panel
  gui.drawRectangle(x, y, 110, 10 + #hud.party * 18, bg, bg)

  -- Location
  gui.drawText(x+2, y+2, "📍 "..hud.location, white, bg, 8, "Consolas")

  -- Badges
  local badge_str = string.format("🏅 %d/8", hud.badges)
  gui.drawText(x+2, y+12, badge_str, gold, 0, 8, "Consolas")

  -- Party
  for i, mon in ipairs(hud.party) do
    local py      = y + 24 + (i-1)*18
    local hp_pct  = mon.cur_hp / mon.max_hp
    local hp_col  = hp_pct > 0.5 and green or (hp_pct > 0.2 and gold or red)
    local bar_w   = math.floor(hp_pct * 60)

    gui.drawText(x+2, py,
      string.format("%-10s L%d", mon.name:sub(1,10), mon.level),
      white, 0, 7, "Consolas")

    -- HP bar background
    gui.drawRectangle(x+2, py+9, 60, 4, 0xFF333355, 0xFF333355)
    -- HP bar fill
    if bar_w > 0 then
      gui.drawRectangle(x+2, py+9, bar_w, 4, hp_col, hp_col)
    end
    gui.drawText(x+66, py+8,
      string.format("%d/%d", mon.cur_hp, mon.max_hp),
      gray, 0, 6, "Consolas")
  end
end

-- ── Main loop ─────────────────────────────────────────────────
event.onframeend(function()
  hud.frame_cnt = hud.frame_cnt + 1

  -- Refresh memory every 60 frames (~1 second at 60fps)
  if hud.frame_cnt % 60 == 0 then
    local ok, err = pcall(refresh_hud)
    if not ok then
      hud.location = "read error"
    end
  end

  draw_hud()
end)

print("[PokeAI Lua] HUD overlay active. Open the PokeAI sidebar panel for full AI advice.")