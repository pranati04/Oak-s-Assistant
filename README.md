# PokeAI — BizHawk Fire Red AI Assistant

A dockable sidebar AI advisor for Pokémon FireRed inside BizHawk.
Reads live memory, understands your game state, and gives real-time
strategy advice powered by Claude.

```
┌─────────────────────────────────────┐
│  ◈ POKEAI ASSISTANT                │  ← Dockable sidebar
│  ● Live — 14:22:01                 │
├─────────────────────────────────────┤
│  📍 Vermilion City                 │
│  🏅 3/8  [Boulder, Cascade, Thunder]│
│  📖 47 seen · 31 caught            │
├──── PARTY ──────────────────────────┤
│  Charmeleon L28  ████████░░  88/100 │
│  Pidgeotto  L24  ██████░░░░  72/100 │
│  Pikachu    L22  ██████████ 100/100 │
├─ Advisor ──┬─ Chat AI ─────────────┤
│            │                        │
│  [NEXT GYM]│  You: Should I evolve │
│  Celadon   │  my Magikarp first?   │
│  Gym...    │                        │
│            │  AI: Yes — only 10    │
│  [CATCH]   │  more levels to       │
│  • Drowzee │  Gyarados. Use the    │
│  • Psyduck │  Exp. Share...        │
│            │                        │
│  [REFRESH] │  [ type here... ] [▶] │
└────────────┴────────────────────────┘
```

---

## Project Structure

```
PokeAI_BizHawk/
├── PokeAITool/
│   ├── PokeAITool.cs       ← C# BizHawk External Tool (the sidebar panel)
│   └── PokeAITool.csproj   ← .NET 8 project file
├── ai_server.py            ← Local Python bridge (holds your API key)
├── pokeai_hud.lua          ← Optional: in-game HUD overlay
├── data/
│   └── species.json        ← Full 386-species name table (see below)
└── README.md
```

---

## Architecture

```
BizHawk EmuHawk.exe
  └── External Tool: PokeAITool.dll
        │  reads memory via IMemoryApi every 5s
        │  draws WinForms sidebar panel
        └─► HTTP POST localhost:8765/chat
                │
        ai_server.py (Flask)
                │  injects Anthropic API key
                └─► api.anthropic.com/v1/messages
                        │
                    Claude Sonnet
                        │
                    ◄── advice text
```

The C# plugin **never holds your API key** — it only talks to
`localhost:8765`. The Python server holds the key and forwards requests.

---

## Requirements

| Component       | Version         |
|-----------------|-----------------|
| BizHawk         | 2.9.1+          |
| .NET SDK        | 8.0+            |
| Python          | 3.10+           |
| Flask           | `pip install flask requests` |
| Anthropic key   | claude.ai/settings |

> **OS:** Windows only (BizHawk External Tools use WinForms/.NET)

---

## Setup — Step by Step

### 1. Clone / download this project

```
git clone https://github.com/you/pokeai-bizhawk
cd pokeai-bizhawk
```

### 2. Set your Anthropic API key

Option A — environment variable (recommended):
```powershell
$env:ANTHROPIC_API_KEY = "sk-ant-..."
python ai_server.py
```

Option B — edit `ai_server.py` directly:
```python
API_KEY = "sk-ant-YOUR_KEY_HERE"   # line 20
```

### 3. Start the bridge server

```bash
pip install flask requests
python ai_server.py
```

You should see:
```
PokeAI Bridge Server — listening on port 8765
API key: ✓ set
```

Leave this terminal open while playing.

### 4. Build the C# plugin

First, edit `PokeAITool.csproj` to point to your BizHawk install:

```xml
<HintPath>C:\BizHawk-2.9.1\BizHawk.Client.Common.dll</HintPath>
```

Then build:
```powershell
cd PokeAITool
dotnet build -c Release
```

This outputs `PokeAITool.dll` into `BizHawk\ExternalTools\` automatically
(if you set the OutputPath correctly) — or copy it manually:

```
copy bin\Release\PokeAITool.dll "C:\BizHawk-2.9.1\ExternalTools\"
```

### 5. Open in BizHawk

1. Start BizHawk
2. Load your **Pokémon FireRed (US v1.0)** ROM
3. Go to **Tools → External Tools → PokeAI Assistant**
4. The sidebar appears — dock it anywhere
5. Click **Refresh** to get your first AI analysis

### 6. Optional: Lua HUD overlay

1. Go to **Tools → Lua Console**
2. Click **Open Script** → select `pokeai_hud.lua`
3. A minimal overlay appears on the game screen showing party HP + location

---

## ROM Compatibility

| ROM              | Status  | Notes                          |
|------------------|---------|--------------------------------|
| Fire Red US v1.0 | ✅ Full | All addresses verified         |
| Fire Red US v1.1 | ⚠ Partial | Some addresses differ       |
| Fire Red EU      | ⚠ Partial | Map/badge addresses differ  |
| Leaf Green US    | 🔧 WIP  | Party addresses same, map differs |

To add a ROM variant, update the `ADDR_*` constants in `PokeAITool.cs`
and the address table in `pokeai_hud.lua`.

---

## Key Memory Addresses (FireRed US v1.0)

| Data          | Address      | Size | Notes                    |
|---------------|-------------|------|--------------------------|
| Player name   | 0x2024284   | 7 B  | FireRed charset          |
| Badge flags   | 0x20244F2   | 1 B  | Bit 0=Boulder … Bit 7=Earth |
| Map bank      | 0x02036DFC  | 1 B  |                          |
| Map ID        | 0x02036DFD  | 1 B  |                          |
| Party count   | 0x02024284  | 1 B  |                          |
| Party slot 0  | 0x02024288  | 100B | Species=U16@+0, Lv=U8@+0x38 |
| Money         | 0x02025A00  | 4 B  | Packed BCD               |
| Pokédex seen  | 0x02024540  | 49B  | Bitfield, 1 bit/species  |
| Pokédex caught| 0x02024584  | 49B  | Bitfield                 |

---

## Expanding Species Data

`PokeAITool.cs` only includes a partial species table.
To load all 386, create `data/species.json`:

```json
{
  "1": "Bulbasaur",
  "2": "Ivysaur",
  ...
  "386": "Deoxys"
}
```

Then load it in `BuildSpeciesTable()`:
```csharp
var json = File.ReadAllText("ExternalTools/data/species.json");
return JsonSerializer.Deserialize<Dictionary<int,string>>(json);
```

---

## Troubleshooting

| Problem | Fix |
|---|---|
| Tool not in menu | Make sure `PokeAITool.dll` is in `BizHawk/ExternalTools/` |
| "No emulator connected" | Load a ROM first, then open the tool |
| "Connection error" | Start `ai_server.py` before clicking Refresh |
| Wrong locations | Verify you're using **FireRed US v1.0** |
| Bad party data | Check ROM version matches address table |
| API errors | Verify API key and billing in Anthropic console |

---

## Roadmap

- [ ] Full 386 species table via JSON resource
- [ ] Route encounter table (all routes, all methods)
- [ ] Item bag reader (show held items)
- [ ] Move reader (show party moves)
- [ ] Leafgreen support
- [ ] Auto-trigger AI advice on location change
- [ ] Export session notes to text file

---

## License

MIT. Not affiliated with Nintendo, Game Freak, or Anthropic.
BizHawk is © its contributors (MIT License).