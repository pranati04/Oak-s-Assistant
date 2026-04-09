// ============================================================
//  PokeAI — BizHawk 2.11 External Tool
//  AI sidebar advisor for Pokémon FireRed / LeafGreen (GBA)
//
//  [ExternalTool] goes on the CLASS (not assembly) in 2.11.
//  ToolFormBase lives in BizHawk.Client.EmuHawk (EmuHawk.exe).
// ============================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;   // net48 built-in JSON — no NuGet needed
using System.Windows.Forms;
using BizHawk.Client.Common;
using BizHawk.Client.EmuHawk;            // ToolFormBase, IExternalToolForm, [ExternalTool]

namespace PokeAITool
{
    [ExternalTool("PokeAI Assistant", Description = "AI advisor for Pokémon FireRed / LeafGreen")]
    public sealed class PokeAIForm : ToolFormBase, IExternalToolForm
    {
        // ── BizHawk API injection ─────────────────────────────
        // BizHawk sets these via property injection before calling Restart().
        [RequiredApi] private IMemoryApi    Mem    { get; set; } = null!;
        [RequiredApi] private IEmuClientApi Client { get; set; } = null!;

        // Must be protected in 2.11 — matches ToolFormBase signature
        protected override string WindowTitleStatic => "PokeAI Assistant";

        // ── HTTP (shared for the tool's lifetime) ─────────────
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        // ── Memory domains ────────────────────────────────────
        private const string DOMAIN_EWRAM = "EWRAM";
        private const string DOMAIN_IWRAM = "Combined WRAM"; // BizHawk combines IWRAM+EWRAM here
        private const string DOMAIN_ROM   = "ROM";

        // ── Save block pointers (IWRAM — these are FIXED addresses) ──────
        // FireRed IWRAM 0x03005008 holds a pointer to SaveBlock1 (party, badges, money, dex)
        // IWRAM starts at 0x03000000 on GBA, so offset in Combined WRAM = 0x03000000 - 0x02000000 = 0x01000000
        // But BizHawk "Combined WRAM" = EWRAM(256KB) + IWRAM(32KB), so IWRAM starts at offset 0x40000
        private const long IWRAM_SAVEBLOCK1_PTR = 0x45008L; // 0x40000 + (0x03005008 - 0x03000000)
        private const long IWRAM_SAVEBLOCK2_PTR = 0x4500CL; // SaveBlock2 = trainer name, badges

        // ── SaveBlock1 offsets (from block start) ─────────────
        private const int SB1_PARTY_COUNT   = 0x034;  // number of pokemon in party
        private const int SB1_PARTY_BASE    = 0x038;  // party slot 0 (100 bytes each)
        private const int SB1_MONEY         = 0x290;  // money (4 bytes BCD encrypted)
        private const int SB1_MAP_BANK      = 0x004;  // current map bank (group)
        private const int SB1_MAP_ID        = 0x005;  // current map number

        // ── SaveBlock2 offsets ────────────────────────────────
        private const int SB2_PLAYER_NAME   = 0x000;  // trainer name (7 bytes)
        private const int SB2_POKEDEX_CATCH = 0x028;  // pokedex caught bitfield (within Pokedex struct)
        private const int SB2_POKEDEX_SEEN  = 0x05C;  // pokedex seen bitfield (within Pokedex struct)
        private const int SB2_BADGE_FLAGS   = 0x0C5;  // badge bitfield (1 byte)

        // ── ROM offsets ───────────────────────────────────────
        private const long ROM_SPECIES_BASE  = 0x245EE0L;
        private const int  ROM_SPECIES_SIZE  = 11;
        private const int  SPECIES_COUNT     = 386;
        private const long ROM_GAME_CODE     = 0x0000ACL;

        // ── Cached save block base addresses ──────────────────
        private long _saveBlock1Base = -1;
        private long _saveBlock2Base = -1;

        private Dictionary<int, string> _speciesNames = new Dictionary<int, string>();
        private bool   _speciesLoaded = false;
        private string _romId         = "";

        private static readonly Dictionary<string, string> MapNames =
            new Dictionary<string, string>
            {
                {"3:0","Pallet Town"},  {"3:1","Viridian City"}, {"3:2","Pewter City"},
                {"3:3","Cerulean City"},{"3:4","Lavender Town"}, {"3:5","Vermilion City"},
                {"3:6","Celadon City"}, {"3:7","Fuchsia City"},  {"3:8","Cinnabar Island"},
                {"3:9","Indigo Plateau"},{"3:10","Saffron City"}, {"3:11","Saffron City"},
                {"3:12","One Island"},   {"3:13","Two Island"},   {"3:14","Three Island"},
                {"3:15","Four Island"},  {"3:16","Five Island"},  {"3:17","Seven Island"},
                {"3:18","Six Island"},
                {"3:19","Route 1"},  {"3:20","Route 2"},  {"3:21","Route 3"},
                {"3:22","Route 4"},  {"3:23","Route 5"},  {"3:24","Route 6"},
                {"3:25","Route 7"},  {"3:26","Route 8"},  {"3:27","Route 9"},
                {"3:28","Route 10"}, {"3:29","Route 11"}, {"3:30","Route 12"},
                {"3:31","Route 13"}, {"3:32","Route 14"}, {"3:33","Route 15"},
                {"3:34","Route 16"}, {"3:35","Route 17"}, {"3:36","Route 18"},
                {"3:37","Route 19"}, {"3:38","Route 20"}, {"3:39","Route 21 North"},
                {"3:40","Route 21 South"}, {"3:41","Route 22"}, {"3:42","Route 23"},
                {"3:43","Route 24"}, {"3:44","Route 25"}
            };

        // ── UI controls ───────────────────────────────────────
        private Label       _lblStatus   = null!;
        private Label       _lblLocation = null!;
        private Label       _lblBadges   = null!;
        private Label       _lblPokedex  = null!;
        private Panel       _partyPanel  = null!;
        private RichTextBox _rtbAdvisor  = null!;
        private RichTextBox _rtbChat     = null!;
        private TextBox     _txtInput    = null!;
        private Button      _btnSend     = null!;
        private Button      _btnRefresh  = null!;
        private ProgressBar _pbLoading   = null!;

        // ── Runtime state ─────────────────────────────────────
        private GameState  _state     = new GameState();
        private bool       _aiRunning = false;
        private readonly List<ChatMessage> _chatHistory = new List<ChatMessage>();

        // ── Constructor ───────────────────────────────────────
        public PokeAIForm()
        {
            BuildUI();
        }

        // ── BizHawk lifecycle ─────────────────────────────────
        protected override void UpdateAfter()
        {
            // Called every emulated frame — intentionally empty.
            // Reads happen on the 5-second timer only.
        }

        public override void Restart()
        {
            _state         = new GameState();
            _speciesLoaded = false;
            LoadSpeciesFromROM();
            RefreshGameState();
        }

        // ─────────────────────────────────────────────────────
        //  UI CONSTRUCTION
        // ─────────────────────────────────────────────────────
        private void BuildUI()
        {
            // Do NOT set Text= here — BizHawk's FormBase blocks it.
            // WindowTitleStatic handles the window title instead.
            // Set size/style via the Load event to avoid FormBase restrictions.
            Load += (s, e) =>
            {
                Size        = new Size(350, 760);
                MinimumSize = new Size(350, 600);
                BackColor   = Color.FromArgb(26, 26, 46);
                ForeColor   = Color.FromArgb(224, 224, 224);
                Font        = new Font("Segoe UI", 9f);
            };

            // Header
            var header = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Color.FromArgb(20, 20, 40) };
            var lblTitle = new Label
            {
                Text      = "◈ POKEAI ASSISTANT",
                ForeColor = Color.FromArgb(220, 30, 30),
                Font      = new Font("Segoe UI", 11f, FontStyle.Bold),
                AutoSize  = true,
                Location  = new Point(10, 8)
            };
            _lblStatus = new Label
            {
                Text      = "● Waiting for ROM...",
                ForeColor = Color.FromArgb(144, 164, 174),
                Font      = new Font("Segoe UI", 8f),
                AutoSize  = true,
                Location  = new Point(12, 30)
            };
            header.Controls.AddRange(new Control[] { lblTitle, _lblStatus });

            // Trainer info strip
            var infoPanel = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Color.FromArgb(15, 52, 96) };
            _lblLocation = MkLabel("📍 Location: —", 8, 5);
            _lblBadges   = MkLabel("🏅 Badges: —",   8, 24);
            _lblPokedex  = MkLabel("📖 Pokédex: —",  8, 43);
            infoPanel.Controls.AddRange(new Control[] { _lblLocation, _lblBadges, _lblPokedex });

            // Party panel
            _partyPanel = new Panel { Dock = DockStyle.Top, Height = 130, BackColor = Color.FromArgb(22, 33, 62) };
            var partyHdr = MkLabel("PARTY ──────────────────────────", 6, 3, bold: true);
            partyHdr.ForeColor = Color.FromArgb(100, 120, 150);
            _partyPanel.Controls.Add(partyHdr);

            // Loading bar
            _pbLoading = new ProgressBar { Dock = DockStyle.Top, Height = 3, Style = ProgressBarStyle.Marquee, Visible = false };

            // Tabs
            var tabs = new TabControl { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 8.5f) };
            StyleTabs(tabs);

            var tabAdvisor = new TabPage("Advisor") { BackColor = Color.FromArgb(22, 33, 62) };
            var tabChat    = new TabPage("Chat AI")  { BackColor = Color.FromArgb(22, 33, 62) };

            // Advisor tab
            _rtbAdvisor = new RichTextBox
            {
                Dock = DockStyle.Fill, BackColor = Color.FromArgb(22, 33, 62),
                ForeColor = Color.FromArgb(200, 200, 200), Font = new Font("Consolas", 8.5f),
                ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = RichTextBoxScrollBars.Vertical
            };
            _btnRefresh = new Button
            {
                Dock = DockStyle.Bottom, Height = 36, Text = "⟳  Refresh + Get AI Advice",
                BackColor = Color.FromArgb(180, 28, 0), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            _btnRefresh.FlatAppearance.BorderSize = 0;
            _btnRefresh.Click += async (s, e) => await DoFullRefresh();
            tabAdvisor.Controls.Add(_rtbAdvisor);
            tabAdvisor.Controls.Add(_btnRefresh);

            // Chat tab
            var chatOuter = new Panel { Dock = DockStyle.Fill };
            _rtbChat = new RichTextBox
            {
                Dock = DockStyle.Fill, BackColor = Color.FromArgb(22, 33, 62),
                ForeColor = Color.FromArgb(200, 200, 200), Font = new Font("Consolas", 8.5f),
                ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = RichTextBoxScrollBars.Vertical
            };
            var inputRow = new Panel { Dock = DockStyle.Bottom, Height = 32, BackColor = Color.FromArgb(15, 22, 48) };
            _txtInput = new TextBox
            {
                Dock = DockStyle.Fill, BackColor = Color.FromArgb(28, 40, 75),
                ForeColor = Color.White, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9f)
            };
            _txtInput.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; var _ = SendChat(); }
            };
            _btnSend = new Button
            {
                Dock = DockStyle.Right, Width = 64, Text = "Send ▶",
                BackColor = Color.FromArgb(180, 28, 0), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };
            _btnSend.FlatAppearance.BorderSize = 0;
            _btnSend.Click += async (s, e) => await SendChat();
            inputRow.Controls.Add(_txtInput);
            inputRow.Controls.Add(_btnSend);
            chatOuter.Controls.Add(_rtbChat);
            chatOuter.Controls.Add(inputRow);
            tabChat.Controls.Add(chatOuter);

            tabs.TabPages.Add(tabAdvisor);
            tabs.TabPages.Add(tabChat);

            // Assemble (DockStyle.Top stacks in reverse order of Controls.Add)
            Controls.Add(tabs);
            Controls.Add(_pbLoading);
            Controls.Add(_partyPanel);
            Controls.Add(infoPanel);
            Controls.Add(header);

            // 5-second refresh timer
            var timer = new System.Windows.Forms.Timer { Interval = 5000 };
            timer.Tick += (s, e) =>
            {
                if (!_speciesLoaded) LoadSpeciesFromROM();
                RefreshGameState();
            };
            timer.Start();

            AppendChat("PokeAI", "Hello! Load your FireRed ROM then click 'Refresh + Get AI Advice'.", isAI: true);
        }

        // ─────────────────────────────────────────────────────
        //  MEMORY READING
        // ─────────────────────────────────────────────────────
        // ── Resolve save block pointers ───────────────────────
        // FireRed's save data moves around in EWRAM each warp/menu open.
        // The IWRAM pointers at fixed addresses always point to current location.
        private bool ResolveSaveBlocks()
        {
            try
            {
                // Read 4-byte pointer from Combined WRAM (IWRAM section)
                // Pointer value is a GBA bus address (0x02xxxxxx), subtract 0x02000000 for EWRAM offset
                uint sb1ptr = Mem.ReadU32(IWRAM_SAVEBLOCK1_PTR, DOMAIN_IWRAM);
                uint sb2ptr = Mem.ReadU32(IWRAM_SAVEBLOCK2_PTR, DOMAIN_IWRAM);

                if (sb1ptr < 0x02000000 || sb1ptr > 0x02040000) return false;
                if (sb2ptr < 0x02000000 || sb2ptr > 0x02040000) return false;

                _saveBlock1Base = (long)(sb1ptr - 0x02000000);
                _saveBlock2Base = (long)(sb2ptr - 0x02000000);
                return true;
            }
            catch { return false; }
        }

        private void RefreshGameState()
        {
            if (Mem == null) { SetStatus("● Waiting for emulator...", false); return; }
            try
            {
                if (!ResolveSaveBlocks())
                {
                    SetStatus("⚠ Cannot find save blocks — load ROM first", false);
                    return;
                }

                // Trainer name from SaveBlock2
                var nameBytes = Mem.ReadByteRange(_saveBlock2Base + SB2_PLAYER_NAME, 7, DOMAIN_EWRAM);
                _state.TrainerName = DecodeGBString(nameBytes);

                // Badges from SaveBlock2
                byte badgeByte = (byte)Mem.ReadByte(_saveBlock2Base + SB2_BADGE_FLAGS, DOMAIN_EWRAM);
                _state.Badges     = CountBits(badgeByte);
                _state.BadgeNames = GetBadgeNames(badgeByte);

                // Map from SaveBlock1
                int mapId = (int)Mem.ReadByte(_saveBlock1Base + SB1_MAP_ID,   DOMAIN_EWRAM);
                int bank  = (int)Mem.ReadByte(_saveBlock1Base + SB1_MAP_BANK,  DOMAIN_EWRAM);
                string key = bank + ":" + mapId;
                _state.Location = MapNames.ContainsKey(key) ? MapNames[key] : "Map " + key;
                _state.MapBank = bank;
                _state.MapId   = mapId;

                // Pokédex from SaveBlock2
                _state.PokedexSeen   = CountDexBits(_saveBlock2Base + SB2_POKEDEX_SEEN);
                _state.PokedexCaught = CountDexBits(_saveBlock2Base + SB2_POKEDEX_CATCH);

                // Money from SaveBlock1
                _state.Money = ReadBCDMoney();

                // Party from SaveBlock1
                _state.Party.Clear();
                int partyCount = Math.Min(6, (int)Mem.ReadByte(_saveBlock1Base + SB1_PARTY_COUNT, DOMAIN_EWRAM));

                long activePartyBase = _saveBlock1Base + SB1_PARTY_BASE; // Fallback
                if (partyCount > 0)
                {
                    uint targetPersonality = Mem.ReadU32(activePartyBase, DOMAIN_EWRAM);
                    uint targetOtId        = Mem.ReadU32(activePartyBase + 4, DOMAIN_EWRAM);
                    
                    // Scan EWRAM for the exact active daily party array to get live HP
                    for (long offset = 0x20000; offset < 0x38000; offset += 4)
                    {
                        if (offset == activePartyBase) continue;
                        
                        if (Mem.ReadU32(offset, DOMAIN_EWRAM) == targetPersonality &&
                            Mem.ReadU32(offset + 4, DOMAIN_EWRAM) == targetOtId)
                        {
                            activePartyBase = offset;
                            break;
                        }
                    }
                }

                for (int i = 0; i < partyCount; i++)
                {
                    var mon = ReadPartySlot(activePartyBase, i);
                    if (mon != null) _state.Party.Add(mon);
                }

                Invoke(new Action(UpdateInfoStrip));
                SetStatus("● Live  " + DateTime.Now.ToString("HH:mm:ss"), true);
            }
            catch (Exception ex)
            {
                string msg = ex.Message.Length > 45 ? ex.Message.Substring(0, 45) : ex.Message;
                SetStatus("⚠ " + msg, false);
            }
        }

        private static readonly int[] GrowthIndices = {
            0,0,0,0,0,0, 1,1,2,3,2,3, 1,1,2,3,2,3, 1,1,2,3,2,3
        };

        private PartyMon ReadPartySlot(long partyBase, int slot)
        {
            long baseAddr = partyBase + slot * 100;
            try
            {
                // Decrypt species from BoxPokemon substructures
                uint personality = Mem.ReadU32(baseAddr + 0x00, DOMAIN_EWRAM);
                uint otId        = Mem.ReadU32(baseAddr + 0x04, DOMAIN_EWRAM);
                uint key         = personality ^ otId;

                int gIndex = GrowthIndices[personality % 24];
                uint gWord0 = Mem.ReadU32(baseAddr + 0x20 + gIndex * 12, DOMAIN_EWRAM);
                uint decryptedG0 = gWord0 ^ key;
                int species = (int)(decryptedG0 & 0xFFFF);

                if (species == 0 || species > 386) return null;

                // Party-specific data starts at offset 0x50 (80 bytes)
                int level = (int)Mem.ReadByte(baseAddr + 0x54, DOMAIN_EWRAM);
                int curHp = (int)Mem.ReadU16(baseAddr + 0x56, DOMAIN_EWRAM);
                int maxHp = (int)Mem.ReadU16(baseAddr + 0x58, DOMAIN_EWRAM);
                
                string name;
                if (!_speciesNames.TryGetValue(species, out name)) name = "#" + species;

                var nickBytes = Mem.ReadByteRange(baseAddr + 0x08, 10, DOMAIN_EWRAM);
                string nickname = DecodeGBString(nickBytes);
                if (!string.IsNullOrWhiteSpace(nickname) && nickname != "???")
                    name = nickname;

                return new PartyMon { Species = species, Name = name, Level = level, CurHP = curHp, MaxHP = Math.Max(1, maxHp) };
            }
            catch { return null; }
        }

        private void UpdateInfoStrip()
        {
            _lblLocation.Text = "📍 " + _state.Location;
            _lblBadges.Text   = "🏅 " + _state.Badges + "/8  [" + string.Join(", ", _state.BadgeNames) + "]";
            _lblPokedex.Text  = "📖 " + _state.PokedexSeen + " seen · " + _state.PokedexCaught + " caught";

            var old = new List<Control>();
            foreach (Control c in _partyPanel.Controls)
                if (c.Tag != null && c.Tag.ToString() == "slot") old.Add(c);
            old.ForEach(c => _partyPanel.Controls.Remove(c));

            for (int i = 0; i < Math.Min(6, _state.Party.Count); i++)
            {
                var mon   = _state.Party[i];
                int hpPct = (int)((float)mon.CurHP / mon.MaxHP * 100);
                var hpCol = hpPct > 50 ? Color.FromArgb(0, 220, 110)
                          : hpPct > 20 ? Color.FromArgb(255, 210, 0)
                          :              Color.FromArgb(220, 60, 60);

                int col = i % 2;
                int row = i / 2;
                var slot = new Panel { Size = new Size(150, 32), Location = new Point(8 + col * 156, 18 + row * 36), BackColor = Color.FromArgb(15, 40, 80), Tag = "slot" };
                
                string displayName = mon.Name.Length > 10 ? mon.Name.Substring(0, 10) : mon.Name;
                slot.Controls.Add(new Label { Text = displayName, ForeColor = Color.FromArgb(220, 220, 220), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Location = new Point(2, 2), Size = new Size(110, 15) });
                slot.Controls.Add(new Label { Text = "Lv" + mon.Level, ForeColor = Color.FromArgb(255, 210, 0), Font = new Font("Segoe UI", 7.5f, FontStyle.Bold), Location = new Point(120, 3), Size = new Size(28, 14), TextAlign = ContentAlignment.TopRight });
                
                var hpBg = new Panel { Size = new Size(80, 6), Location = new Point(5, 20), BackColor = Color.FromArgb(30, 30, 55) };
                var hpFg = new Panel { Size = new Size(Math.Max(1, hpPct * 80 / 100), 6), BackColor = hpCol };
                hpBg.Controls.Add(hpFg);
                slot.Controls.Add(hpBg);
                
                slot.Controls.Add(new Label { Text = mon.CurHP + "/" + mon.MaxHP, ForeColor = Color.FromArgb(130, 145, 165), Font = new Font("Segoe UI", 7.5f), Location = new Point(90, 16), Size = new Size(58, 14) });
                _partyPanel.Controls.Add(slot);
            }
        }

        // ─────────────────────────────────────────────────────
        //  ROM SPECIES LOADER
        // ─────────────────────────────────────────────────────
        private void LoadSpeciesFromROM()
        {
            if (_speciesLoaded || Mem == null) return;
            try
            {
                var code = Mem.ReadByteRange(ROM_GAME_CODE, 4, DOMAIN_ROM);
                var codeArr = new byte[code.Count];
                for (int ci = 0; ci < code.Count; ci++) codeArr[ci] = code[ci];
                _romId = Encoding.ASCII.GetString(codeArr).TrimEnd('\0');
            }
            catch { _romId = "UNKNOWN"; }

            if (_romId != "BPRE" && _romId != "BPGE")
            {
                SetStatus("⚠ ROM '" + _romId + "' — need FireRed or LeafGreen", false);
                return;
            }

            var loaded = new Dictionary<int, string>();
            int skipped = 0;
            for (int i = 1; i <= SPECIES_COUNT; i++)
            {
                try
                {
                    long addr  = ROM_SPECIES_BASE + (long)(i - 1) * ROM_SPECIES_SIZE;
                    var  bytes = Mem.ReadByteRange(addr, ROM_SPECIES_SIZE, DOMAIN_ROM);
                    string name = DecodeGBString(bytes);
                    if (!string.IsNullOrWhiteSpace(name) && name != "???" && !name.StartsWith("????"))
                        loaded[i] = name;
                    else skipped++;
                }
                catch { skipped++; }
            }

            _speciesNames  = loaded;
            _speciesLoaded = true;
            string msg = skipped == 0
                ? "● " + _romId + "  " + loaded.Count + " species loaded"
                : "● " + _romId + "  " + loaded.Count + " species (" + skipped + " skipped)";
            SetStatus(msg, true);
        }

        // ─────────────────────────────────────────────────────
        //  AI CALLS
        // ─────────────────────────────────────────────────────
        private async Task DoFullRefresh()
        {
            if (_aiRunning) return;
            RefreshGameState();
            _aiRunning = true; _pbLoading.Visible = true; _btnRefresh.Enabled = false;
            _rtbAdvisor.Clear();
            AppendAdvisor("Analyzing game state...\n", Color.FromArgb(130, 150, 170));
            try
            {
                string advice = await CallAI(AdvisorPrompt(), isChat: false);
                _rtbAdvisor.Clear();
                RenderAdvice(advice);
            }
            catch (Exception ex) { AppendAdvisor("\n⚠ Error: " + ex.Message, Color.FromArgb(220, 80, 80)); }
            finally { _aiRunning = false; _pbLoading.Visible = false; _btnRefresh.Enabled = true; }
        }

        private async Task SendChat()
        {
            string msg = _txtInput.Text.Trim();
            if (string.IsNullOrEmpty(msg) || _aiRunning) return;
            _txtInput.Clear();
            AppendChat("You", msg, isAI: false);
            _chatHistory.Add(new ChatMessage { Role = "user", Content = msg });
            _aiRunning = true; _pbLoading.Visible = true; _btnSend.Enabled = false;
            try
            {
                string reply = await CallAI(ChatPrompt(), isChat: true);
                AppendChat("PokeAI", reply, isAI: true);
                _chatHistory.Add(new ChatMessage { Role = "assistant", Content = reply });
                if (_chatHistory.Count > 20) _chatHistory.RemoveRange(0, 2);
            }
            catch (Exception ex) { AppendChat("Error", ex.Message, isAI: true); }
            finally { _aiRunning = false; _pbLoading.Visible = false; _btnSend.Enabled = true; }
        }

        private async Task<string> CallAI(string systemPrompt, bool isChat)
        {
            var messages = new List<object>();
            if (isChat)
                foreach (var m in _chatHistory)
                    messages.Add(new { role = m.Role, content = m.Content });
            else
                messages.Add(new { role = "user", content = "Analyze my current game state and give recommendations." });

            var body = new { model = "gpt-4o-mini", max_tokens = 1000, system = systemPrompt, messages = messages };
            var ser  = new JavaScriptSerializer();
            var json = ser.Serialize(body);

            var content  = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await Http.PostAsync("http://localhost:5000/chat", content);
            var raw      = await response.Content.ReadAsStringAsync();

            var parsed = ser.DeserializeObject(raw) as Dictionary<string, object>;
            if (parsed == null) throw new Exception("Empty response from AI server");

            if (parsed.ContainsKey("error"))
            {
                string detail = parsed.ContainsKey("detail") ? parsed["detail"].ToString() : "";
                throw new Exception(parsed["error"].ToString() + " " + detail);
            }

            var contentArr = (parsed["content"] as object[])!;
            var first      = (contentArr[0] as Dictionary<string, object>)!;
            return first["text"]!.ToString();
        }

        // ─────────────────────────────────────────────────────
        //  PROMPTS
        // ─────────────────────────────────────────────────────
        private string AdvisorPrompt()
        {
            return "You are PokeAI, an expert Pokémon FireRed/LeafGreen advisor in BizHawk emulator.\n\n"
                + "GAME STATE:\n"
                + "Trainer  : " + _state.TrainerName + "\n"
                + "Location : " + _state.Location + "\n"
                + "Badges   : " + _state.Badges + "/8 (" + string.Join(", ", _state.BadgeNames) + ")\n"
                + "Money    : ₽" + _state.Money.ToString("N0") + "\n"
                + "Pokédex  : " + _state.PokedexSeen + " seen / " + _state.PokedexCaught + " caught\n"
                + "ROM      : " + _romId + "\n\n"
                + "PARTY:\n" + PartyString() + "\n\n"
                + "Use these section headers: [NEXT GYM] [CATCH] [ITEMS] [QUESTS] [WARNINGS]\n"
                + "Keep under 280 words. No markdown. Be specific and direct.";
        }

        private string ChatPrompt()
        {
            return "You are PokeAI, a friendly Pokémon FireRed/LeafGreen advisor in BizHawk. "
                + "Trainer=" + _state.TrainerName + " | Location=" + _state.Location + " | Badges=" + _state.Badges + "/8\n"
                + "Party: " + PartyString() + "\n"
                + "Pokédex: " + _state.PokedexSeen + " seen / " + _state.PokedexCaught + " caught | Money: ₽" + _state.Money.ToString("N0") + "\n"
                + "Keep replies to 3-5 sentences. No markdown. Plain text.";
        }

        private string PartyString()
        {
            if (_state.Party.Count == 0) return "  (empty)";
            var sb = new StringBuilder();
            foreach (var m in _state.Party)
                sb.AppendLine("  • " + m.Name + " Lv" + m.Level + "  HP " + m.CurHP + "/" + m.MaxHP);
            return sb.ToString().TrimEnd();
        }

        // ─────────────────────────────────────────────────────
        //  UI HELPERS
        // ─────────────────────────────────────────────────────
        private void AppendAdvisor(string text, Color color)
        {
            if (_rtbAdvisor.InvokeRequired) { _rtbAdvisor.Invoke(new Action(() => AppendAdvisor(text, color))); return; }
            _rtbAdvisor.SelectionStart = _rtbAdvisor.TextLength;
            _rtbAdvisor.SelectionLength = 0;
            _rtbAdvisor.SelectionColor = color;
            _rtbAdvisor.AppendText(text);
        }

        private void RenderAdvice(string text)
        {
            foreach (var line in text.Split('\n'))
            {
                Color c = Color.FromArgb(195, 205, 215);
                if (line.StartsWith("["))                                          c = Color.FromArgb(0, 210, 100);
                else if (line.TrimStart().StartsWith("•") || line.TrimStart().StartsWith("-")) c = Color.FromArgb(170, 200, 225);
                else if (line.Contains("WARNING") || line.Contains("⚠"))          c = Color.FromArgb(255, 185, 50);
                AppendAdvisor(line + "\n", c);
            }
        }

        private void AppendChat(string who, string text, bool isAI)
        {
            if (_rtbChat.InvokeRequired) { _rtbChat.Invoke(new Action(() => AppendChat(who, text, isAI))); return; }
            _rtbChat.SelectionColor = isAI ? Color.FromArgb(0, 210, 100) : Color.FromArgb(220, 80, 80);
            _rtbChat.AppendText(who + ": ");
            _rtbChat.SelectionColor = Color.FromArgb(200, 200, 200);
            _rtbChat.AppendText(text + "\n\n");
            _rtbChat.ScrollToCaret();
        }

        private void SetStatus(string text, bool ok)
        {
            if (_lblStatus.InvokeRequired) { _lblStatus.Invoke(new Action(() => SetStatus(text, ok))); return; }
            _lblStatus.Text      = text;
            _lblStatus.ForeColor = ok ? Color.FromArgb(0, 210, 100) : Color.FromArgb(220, 100, 60);
        }

        private static Label MkLabel(string text, int x, int y, bool bold = false) => new Label
        {
            Text = text, ForeColor = Color.FromArgb(185, 200, 215),
            Font = new Font("Segoe UI", 9f, bold ? FontStyle.Bold : FontStyle.Regular),
            AutoSize = true, Location = new Point(x, y)
        };

        private static void StyleTabs(TabControl tc)
        {
            tc.DrawMode = TabDrawMode.OwnerDrawFixed;
            tc.DrawItem += (s, e) =>
            {
                bool sel = e.Index == tc.SelectedIndex;
                e.Graphics.FillRectangle(new SolidBrush(sel ? Color.FromArgb(15, 50, 90) : Color.FromArgb(18, 28, 52)), e.Bounds);
                TextRenderer.DrawText(e.Graphics, tc.TabPages[e.Index].Text, tc.Font, e.Bounds,
                    sel ? Color.FromArgb(0, 210, 100) : Color.FromArgb(130, 150, 170));
                if (sel)
                    e.Graphics.DrawLine(new Pen(Color.FromArgb(180, 28, 0), 2),
                        e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            };
        }

        // ─────────────────────────────────────────────────────
        //  MEMORY DECODING
        // ─────────────────────────────────────────────────────
        private static string DecodeGBString(System.Collections.Generic.IReadOnlyList<byte> bytes)
        {
            var map = new Dictionary<byte, char>
            {
                [0xBB]='A',[0xBC]='B',[0xBD]='C',[0xBE]='D',[0xBF]='E',
                [0xC0]='F',[0xC1]='G',[0xC2]='H',[0xC3]='I',[0xC4]='J',
                [0xC5]='K',[0xC6]='L',[0xC7]='M',[0xC8]='N',[0xC9]='O',
                [0xCA]='P',[0xCB]='Q',[0xCC]='R',[0xCD]='S',[0xCE]='T',
                [0xCF]='U',[0xD0]='V',[0xD1]='W',[0xD2]='X',[0xD3]='Y',[0xD4]='Z',
                [0xD5]='a',[0xD6]='b',[0xD7]='c',[0xD8]='d',[0xD9]='e',
                [0xDA]='f',[0xDB]='g',[0xDC]='h',[0xDD]='i',[0xDE]='j',
                [0xDF]='k',[0xE0]='l',[0xE1]='m',[0xE2]='n',[0xE3]='o',
                [0xE4]='p',[0xE5]='q',[0xE6]='r',[0xE7]='s',[0xE8]='t',
                [0xE9]='u',[0xEA]='v',[0xEB]='w',[0xEC]='x',[0xED]='y',[0xEE]='z',
                [0xA1]='0',[0xA2]='1',[0xA3]='2',[0xA4]='3',[0xA5]='4',
                [0xA6]='5',[0xA7]='6',[0xA8]='7',[0xA9]='8',[0xAA]='9',
                [0xAD]='-',[0xAE]='.'
            };
            var sb = new StringBuilder();
            foreach (byte b in bytes)
            {
                if (b == 0xFF) break;
                char c;
                sb.Append(map.TryGetValue(b, out c) ? c : '?');
            }
            return sb.Length == 0 ? "???" : sb.ToString();
        }

        private int CountDexBits(long baseAddr)
        {
            int count = 0;
            for (int i = 0; i < 49; i++)
                count += CountBits((byte)Mem.ReadByte(baseAddr + i, DOMAIN_EWRAM));
            return Math.Min(count, 386);
        }

        private int ReadBCDMoney()
        {
            try
            {
                var b = Mem.ReadByteRange(_saveBlock1Base + SB1_MONEY, 4, DOMAIN_EWRAM);
                int v = 0;
                for (int i = 3; i >= 0; i--)
                    v = v * 100 + (b[i] >> 4) * 10 + (b[i] & 0x0F);
                return v;
            }
            catch { return 0; }
        }

        private static int CountBits(byte b)
        {
            int n = 0;
            while (b != 0) { n += b & 1; b >>= 1; }
            return n;
        }

        private static List<string> GetBadgeNames(byte flags)
        {
            var names  = new[] { "Boulder","Cascade","Thunder","Rainbow","Soul","Marsh","Volcano","Earth" };
            var result = new List<string>();
            for (int i = 0; i < 8; i++)
                if ((flags & (1 << i)) != 0) result.Add(names[i]);
            return result;
        }
    }

    // ── Data models ───────────────────────────────────────────
    public class GameState
    {
        public string       TrainerName   = "TRAINER";
        public string       Location      = "Unknown";
        public int          Badges        = 0;
        public List<string> BadgeNames    = new List<string>();
        public int          Money         = 0;
        public int          PokedexSeen   = 0;
        public int          PokedexCaught = 0;
        public int          MapBank       = 0;
        public int          MapId         = 0;
        public List<PartyMon> Party       = new List<PartyMon>();
    }

    public class PartyMon
    {
        public int    Species = 0;
        public string Name    = "";
        public int    Level   = 0;
        public int    CurHP   = 0;
        public int    MaxHP   = 1;
    }

    public class ChatMessage
    {
        public string Role    = "";
        public string Content = "";
    }
}