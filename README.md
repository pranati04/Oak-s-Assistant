# PokeAI — BizHawk FireRed AI Assistant

A dockable sidebar AI advisor for Pokémon FireRed / LeafGreen inside BizHawk.
Reads live game memory every 5 seconds and gives real-time strategy advice
powered by Google Gemini.

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
PokeAI/
├── PokeAITool/
│   ├── PokeAITool.cs          ← C# BizHawk External Tool (the sidebar panel)
│   └── PokeAITool.csproj      ← .NET Framework 4.8 project file
├── ai_server.py               ← Local Python bridge (holds your Gemini API key)
├── ai_server.example.py       ← Safe template — copy this and add your key
├── pokeai_hud.lua             ← Optional: in-game HUD overlay via Lua Console
└── README.md
```

---

## How It Works

```
BizHawk EmuHawk.exe
  └── External Tool: PokeAITool.dll
        │  reads GBA memory via IMemoryApi every 5s
        │  follows save block pointers (save data moves in EWRAM)
        │  species names read directly from ROM — no external files needed
        └─► HTTP POST localhost:8765/chat
                │
        ai_server.py (Flask)
                │  holds your Gemini API key
                └─► generativelanguage.googleapis.com (Gemini)
                        │
                    gemini-2.0-flash-lite
                        │
                    ◄── advice text
```

The C# plugin **never holds your API key** — it only talks to `localhost:8765`.
The Python bridge holds the key and forwards requests to Gemini.

Species names are read directly out of the loaded ROM at startup — no JSON
files or external data needed.

---

## Requirements

| Component      | Version / Notes                              |
|----------------|----------------------------------------------|
| BizHawk        | 2.11+ (win-x64)                              |
| .NET SDK       | 4.8 (Framework, not .NET Core/8)             |
| Python         | 3.10+                                        |
| pip packages   | `pip install flask requests`                 |
| Gemini API key | aistudio.google.com/app/apikey (free tier)   |

> **OS:** Windows only — BizHawk External Tools use WinForms/.NET Framework.

---

## Setup

### 1. Clone the repository

```powershell
git clone https://github.com/pranati04/Oak-s-Assistant.git
cd PokeAI-BizHawk
```

### 2. Add your Gemini API key

Copy the example server file and add your key:

```powershell
Copy-Item ai_server.example.py ai_server.py
```

Open `ai_server.py` and paste your key on **line 28**:

```python
API_KEY = "AIzaSyYOURREALKEYHERE"
```

Get your free key at **aistudio.google.com/app/apikey**.

### 3. Install Python dependencies

```powershell
pip install flask requests
```

### 4. Fix the BizHawk path in the .csproj

Open `PokeAITool/PokeAITool.csproj` and update the `BIZHAWK_HOME` property
to match where BizHawk is installed on your machine:

```xml
<BIZHAWK_HOME>C:\BizHawk-2.11-win-x64\</BIZHAWK_HOME>
```

### 5. Create the ExternalTools folder and build

```powershell
New-Item -ItemType Directory -Force -Path "C:\BizHawk-2.11-win-x64\ExternalTools"

cd PokeAITool
dotnet build -c Release
```

If BizHawk is closed, the build will automatically copy the DLL into
`BizHawk\ExternalTools\`. If BizHawk is open, copy it manually:

```powershell
Copy-Item "bin\Release\net48\PokeAITool.dll" "C:\BizHawk-2.11-win-x64\ExternalTools\" -Force
```

### 6. Run

Open two terminals:

**Terminal 1 — AI bridge (keep open while playing):**
```powershell
python ai_server.py
```

**Terminal 2 — or just use BizHawk directly:**
1. Launch BizHawk
2. Load your **FireRed (US v1.0)** ROM — wait for the game screen
3. Go to **Tools → External Tools → PokeAI Assistant**
4. Click **Refresh + Get AI Advice**

### 7. Optional — Lua HUD overlay

For a minimal in-game party HP overlay:
1. **Tools → Lua Console → Open Script** → select `pokeai_hud.lua`

---

## Memory Architecture

FireRed's save data (party, badges, Pokédex, money) lives in EWRAM but
**moves around every time a menu opens or a warp triggers**. The tool
resolves this correctly by reading the save block pointers from IWRAM
(which are always at fixed addresses) before reading any save data.

| Data              | How accessed                                      |
|-------------------|---------------------------------------------------|
| Species names     | Read from ROM at `0x245EE0` — no JSON file needed |
| Party / Badges    | Via SaveBlock pointer at IWRAM `0x03005008`        |
| Pokédex / Money   | Via SaveBlock pointer at IWRAM `0x03005008`        |
| Map location      | Via SaveBlock pointer at IWRAM `0x03005008`        |
| ROM game code     | ROM `0x0000AC` — detects FireRed vs LeafGreen      |

---

## ROM Compatibility

| ROM                | Status      | Notes                              |
|--------------------|-------------|------------------------------------|
| FireRed US v1.0    | ✅ Working  | Primary target, fully tested       |
| LeafGreen US v1.0  | ⚠ Partial  | ROM code detected, addresses differ |
| FireRed US v1.1    | ⚠ Untested | Save block pointers may differ     |
| FireRed EU         | ⚠ Untested | Save block pointers may differ     |

---

## Troubleshooting

| Problem | Fix |
|---|---|
| Tool not in Tools menu | Make sure `PokeAITool.dll` is in `BizHawk/ExternalTools/` and you restarted BizHawk |
| Wrong map / "Map 0:0" | Open the ROM first, then open the tool — load order matters |
| "Cannot find save blocks" | The game hasn't initialised yet — get past the title screen |
| AI error / connection refused | Start `ai_server.py` before clicking Refresh |
| Gemini 429 quota error | Free tier limit hit — wait a minute or switch to `gemini-1.5-flash` in `ai_server.py` |
| DLL locked on build | Close BizHawk before building, then copy manually |
| Species showing `#25` instead of name | ROM not loaded yet — Refresh after the game screen appears |

---

## Roadmap

- [ ] Route encounter table (suggest catches by current route)
- [ ] Item bag reader (show what items you already have)
- [ ] Move reader (show party moves for better advice)
- [ ] LeafGreen full support
- [ ] Auto-trigger AI advice on map change
- [ ] Export session notes to text file

---

## License

MIT. Not affiliated with Nintendo, Game Freak, or Google.
BizHawk is © its contributors (MIT License).