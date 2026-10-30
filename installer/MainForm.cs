using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ManagerAgentSetup
{
    internal sealed class MainForm : Form
    {
        // ---------------------------------------------------------------- look
        private static readonly Color Navy = Color.FromArgb(17, 24, 39);
        private static readonly Color Blue = Color.FromArgb(37, 99, 235);
        private static readonly Color Green = Color.FromArgb(22, 163, 74);
        private static readonly Color Amber = Color.FromArgb(217, 119, 6);
        private static readonly Color Red = Color.FromArgb(220, 38, 38);
        private static readonly Color Gray = Color.FromArgb(107, 114, 128);
        private static readonly Color LightBg = Color.FromArgb(249, 250, 251);

        private enum StepState { Todo, Working, Done, Failed, Skipped }

        private sealed class StepRow
        {
            public Label Icon, Title, Detail;
            public void Set(StepState s, string detail = null)
            {
                switch (s)
                {
                    case StepState.Done: Icon.Text = "✓"; Icon.ForeColor = Green; break;
                    case StepState.Working: Icon.Text = "●"; Icon.ForeColor = Blue; break;
                    case StepState.Failed: Icon.Text = "!"; Icon.ForeColor = Red; break;
                    case StepState.Skipped: Icon.Text = "–"; Icon.ForeColor = Gray; break;
                    default: Icon.Text = "○"; Icon.ForeColor = Gray; break;
                }
                if (detail != null) Detail.Text = detail;
            }
            public bool Visible { set { Icon.Visible = Title.Visible = Detail.Visible = value; } }
        }

        // ---------------------------------------------------------------- controls
        private readonly ComboBox _games = new ComboBox();
        private readonly Button _browse = new Button();
        private readonly Label _path = new Label();
        private readonly StepRow _stepLoader, _stepAgent, _stepCheck, _stepRepair;
        private readonly Label _status = new Label();
        private readonly ProgressBar _progress = new ProgressBar();
        private readonly Label _message = new Label();
        private readonly LinkLabel _link = new LinkLabel();
        private readonly Button _primary = new Button();
        private readonly Button _close = new Button();
        private readonly Button _uninstall = new Button();
        private readonly LinkLabel _detailsToggle = new LinkLabel();
        private readonly TextBox _log = new TextBox();

        // ---------------------------------------------------------------- state
        private enum Next { Install, StartAndCheck, Repair, Finish, Retry, None }
        private Next _next = Next.Install;
        private bool _busy;
        private CancellationTokenSource _cts;
        private string _linkUrl;
        private readonly string _autoAction;
        private const int CollapsedHeight = 584, ExpandedHeight = 784;

        private GameState Current => _games.SelectedItem is GameInstall g ? new GameState(g) : null;

        public MainForm(string preselectFolder, string autoAction)
        {
            _autoAction = autoAction;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Manager Agent Setup";
            Font = new Font("Segoe UI", 9.75F);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(640, CollapsedHeight);
            BackColor = Color.White;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // Header
            var header = new Panel { BackColor = Navy, Location = new Point(0, 0), Size = new Size(640, 92) };
            header.Controls.Add(new Label { Text = "Manager Agent", ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 18F), AutoSize = true, Location = new Point(24, 16), BackColor = Navy });
            header.Controls.Add(new Label { Text = "Your agent for Football Manager — setup in one click", ForeColor = Color.FromArgb(156, 163, 175), AutoSize = true, Location = new Point(27, 56), BackColor = Navy });
            Controls.Add(header);

            // Game picker
            Controls.Add(new Label { Text = "Football Manager on this PC", Font = new Font("Segoe UI Semibold", 10F), AutoSize = true, Location = new Point(24, 108) });
            _games.DropDownStyle = ComboBoxStyle.DropDownList;
            _games.Location = new Point(24, 134);
            _games.Size = new Size(480, 28);
            _games.DisplayMember = "Display";
            _games.SelectedIndexChanged += (s, e) => Refresh(true);
            Controls.Add(_games);
            _browse.Text = "Browse…";
            _browse.Location = new Point(514, 133);
            _browse.Size = new Size(102, 30);
            _browse.Click += (s, e) => Browse();
            Controls.Add(_browse);
            _path.Location = new Point(24, 166);
            _path.Size = new Size(592, 20);
            _path.ForeColor = Gray;
            _path.Font = new Font("Segoe UI", 8.5F);
            _path.AutoEllipsis = true;
            Controls.Add(_path);

            // Steps
            int y = 198;
            _stepLoader = AddStep(ref y, "BepInEx mod loader", "Lets Football Manager load mods.");
            _stepAgent = AddStep(ref y, "Manager Agent", "The agent itself (version " + SetupInfo.Version + ").");
            _stepCheck = AddStep(ref y, "First start and check", "Setup starts the game once and checks everything works.");
            _stepRepair = AddStep(ref y, "Build for your game version", "Only needed if your game version needs it.");
            _stepRepair.Visible = false;

            // Status + progress
            _status.Location = new Point(24, 392);
            _status.Size = new Size(592, 40);
            _status.Font = new Font("Segoe UI Semibold", 10F);
            _status.UseMnemonic = false;
            Controls.Add(_status);
            _progress.Location = new Point(24, 434);
            _progress.Size = new Size(592, 8);
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Visible = false;
            Controls.Add(_progress);
            _message.Location = new Point(24, 448);
            _message.Size = new Size(592, 56);
            _message.ForeColor = Color.FromArgb(55, 65, 81);
            Controls.Add(_message);
            _link.Location = new Point(24, 506);
            _link.Size = new Size(592, 20);
            _link.Visible = false;
            _link.LinkClicked += (s, e) => { if (_linkUrl != null) Open(_linkUrl); };
            Controls.Add(_link);

            // Footer
            var footer = new Panel { BackColor = LightBg, Location = new Point(0, CollapsedHeight - 52), Size = new Size(640, 52), Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            _uninstall.Text = "Uninstall";
            _uninstall.Location = new Point(24, 11);
            _uninstall.Size = new Size(96, 30);
            _uninstall.Click += (s, e) => Uninstall();
            footer.Controls.Add(_uninstall);
            _detailsToggle.Text = "Show details";
            _detailsToggle.AutoSize = true;
            _detailsToggle.Location = new Point(132, 17);
            _detailsToggle.BackColor = LightBg;
            _detailsToggle.LinkClicked += (s, e) => ToggleDetails();
            footer.Controls.Add(_detailsToggle);
            _close.Text = "Close";
            _close.Location = new Point(400, 11);
            _close.Size = new Size(96, 30);
            _close.Click += (s, e) => { if (_busy && _cts != null) _cts.Cancel(); else Close(); };
            footer.Controls.Add(_close);
            _primary.Location = new Point(504, 11);
            _primary.Size = new Size(112, 30);
            _primary.BackColor = Blue;
            _primary.ForeColor = Color.White;
            _primary.FlatStyle = FlatStyle.Flat;
            _primary.FlatAppearance.BorderSize = 0;
            _primary.Font = new Font("Segoe UI Semibold", 9.75F);
            _primary.Click += (s, e) => Primary();
            footer.Controls.Add(_primary);
            Controls.Add(footer);
            AcceptButton = _primary;

            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.Font = new Font("Consolas", 8.5F);
            _log.Location = new Point(24, CollapsedHeight + 8);
            _log.Size = new Size(592, ExpandedHeight - CollapsedHeight - 20);
            _log.BackColor = LightBg;
            Controls.Add(_log);

            foreach (var l in AllLabels(this)) l.UseMnemonic = false;

            Log.Line += line => Ui(() => { _log.AppendText(line + Environment.NewLine); });
            ResumeLayout(false);

            LoadGames(preselectFolder);
        }

        private static IEnumerable<Label> AllLabels(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (c is Label l && !(c is LinkLabel)) yield return l;
                foreach (var inner in AllLabels(c)) yield return inner;
            }
        }

        private StepRow AddStep(ref int y, string title, string detail)
        {
            var row = new StepRow
            {
                Icon = new Label { Text = "○", Font = new Font("Segoe UI", 13F, FontStyle.Bold), ForeColor = Gray, Location = new Point(24, y - 2), Size = new Size(28, 28), TextAlign = ContentAlignment.MiddleCenter },
                Title = new Label { Text = title, Font = new Font("Segoe UI Semibold", 10F), Location = new Point(56, y), AutoSize = true },
                Detail = new Label { Text = detail, ForeColor = Gray, Location = new Point(56, y + 21), Size = new Size(560, 20), AutoEllipsis = true },
            };
            Controls.Add(row.Icon);
            Controls.Add(row.Title);
            Controls.Add(row.Detail);
            y += 48;
            return row;
        }

        // ================================================================ game selection

        private void LoadGames(string preselect)
        {
            Log.Write("Manager Agent Setup " + SetupInfo.Version + " — looking for Football Manager…");
            var list = GameLocator.FindAll();
            foreach (var g in list) Log.Write("Found: " + g.Display + " at " + g.Folder);
            if (!string.IsNullOrEmpty(preselect))
            {
                var pre = list.FirstOrDefault(g => string.Equals(g.Folder.TrimEnd('\\'), preselect.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                          ?? GameLocator.FromFolder(preselect);
                if (pre != null && !list.Contains(pre)) list.Insert(0, pre);
                if (pre != null) { _games.Items.AddRange(list.Cast<object>().ToArray()); _games.SelectedItem = pre; return; }
            }
            _games.Items.AddRange(list.Cast<object>().ToArray());
            if (_games.Items.Count > 0) _games.SelectedIndex = 0;
            else Refresh(true);
        }

        private void Browse()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Choose the Football Manager 26 (or 27) game folder — the one that contains fm.exe.", ShowNewFolderButton = false })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var g = GameLocator.FromFolder(dlg.SelectedPath);
                if (g == null)
                {
                    MessageBox.Show(this, "That folder doesn't look like Football Manager 26 or newer.\n\nPick the folder that contains fm.exe and GameAssembly.dll.",
                        "Not a Football Manager folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                var existing = _games.Items.Cast<GameInstall>().FirstOrDefault(x => string.Equals(x.Folder, g.Folder, StringComparison.OrdinalIgnoreCase));
                if (existing == null) { _games.Items.Insert(0, g); existing = g; }
                _games.SelectedItem = existing;
            }
        }

        // ================================================================ state → UI

        private void Refresh(bool resetMessages)
        {
            if (_busy) return;
            var st = Current;
            if (resetMessages) SetMessage(null);
            _stepRepair.Visible = false;

            if (st == null)
            {
                _path.Text = "";
                _stepLoader.Set(StepState.Todo, "Lets Football Manager load mods.");
                _stepAgent.Set(StepState.Todo);
                _stepCheck.Set(StepState.Todo);
                SetStatus("Football Manager 26 wasn't found automatically.", Amber);
                SetMessage("Click Browse… and choose the game folder (the one with fm.exe in it).");
                SetNext(Next.None);
                _uninstall.Enabled = false;
                return;
            }

            _path.Text = st.Game.Folder;
            _stepLoader.Set(st.BepInExInstalled ? StepState.Done : StepState.Todo,
                st.BepInExInstalled ? "Installed." : "Will be downloaded and installed (about 40 MB).");
            var installedVersion = InstalledPluginVersion(st);
            bool agentCurrent = installedVersion != null && installedVersion >= new Version(SetupInfo.Version);
            _stepAgent.Set(agentCurrent ? StepState.Done : StepState.Todo,
                installedVersion == null ? "Will be installed (version " + SetupInfo.Version + ")."
                : agentCurrent ? "Installed (version " + installedVersion.ToString(3) + ")."
                : "Will be updated from " + installedVersion.ToString(3) + " to " + SetupInfo.Version + ".");
            string details;
            var verdict = st.SelfTestStatus(out details);
            _uninstall.Enabled = st.PluginInstalled;

            if (st.BepInExInstalled && agentCurrent && verdict == "OK")
            {
                _stepCheck.Set(StepState.Done, "Checked — working with your game.");
                SetStatus("Manager Agent is installed and ready.", Green);
                SetMessage("Start Football Manager and look for the Agent tab at the top-right of the screen.");
                SetNext(Next.Finish);
            }
            else if (st.BepInExInstalled && agentCurrent && verdict == "FAILED")
            {
                _stepCheck.Set(StepState.Failed, "Your game version needs a matching build.");
                _stepRepair.Visible = true;
                _stepRepair.Set(StepState.Todo, "Setup will build one for you (one-time download of about 200 MB).");
                SetStatus("One more step needed.", Amber);
                SetMessage("Your version of Football Manager needs Manager Agent built to match it. Click Repair and Setup will do it.");
                SetNext(Next.Repair);
            }
            else if (st.BepInExInstalled && agentCurrent)
            {
                _stepCheck.Set(StepState.Todo, "Setup starts the game once and checks everything works.");
                SetStatus("Almost done — the game needs to start once.", Blue);
                SetMessage("Click Start & check. The very first start can take 5–10 minutes while BepInEx prepares the game.");
                SetNext(Next.StartAndCheck);
            }
            else
            {
                _stepCheck.Set(StepState.Todo, "Setup starts the game once and checks everything works.");
                SetStatus("Ready to install.", Navy);
                SetMessage(st.Game.Store == Store.Xbox
                    ? "Xbox app / Game Pass versions don't always allow mods. If it doesn't work, the Steam or Epic version will."
                    : "Click Install. Setup will add the mod loader, add Manager Agent, then start the game once to check it.");
                SetNext(Next.Install);
            }
        }

        private static Version InstalledPluginVersion(GameState st)
        {
            try
            {
                if (!st.PluginInstalled) return null;
                return System.Reflection.AssemblyName.GetAssemblyName(st.PluginDll).Version;
            }
            catch { return new Version(0, 0, 0); }
        }

        private void SetNext(Next n)
        {
            _next = n;
            _primary.Visible = n != Next.None;
            _primary.Enabled = n != Next.None;
            _primary.Text = n == Next.Install ? "Install"
                : n == Next.StartAndCheck ? "Start && check"
                : n == Next.Repair ? "Repair"
                : n == Next.Retry ? "Try again"
                : n == Next.Finish ? "Start FM" : "";
            _close.Text = "Close";
        }

        private void SetStatus(string text, Color color)
        {
            _status.Text = text;
            _status.ForeColor = color;
        }

        private void SetMessage(string text, string linkText = null, string linkUrl = null)
        {
            _message.Text = text ?? "";
            _linkUrl = linkUrl;
            _link.Visible = linkText != null;
            _link.Text = linkText ?? "";
        }

        private void SetProgress(int pct)
        {
            _progress.Visible = pct >= 0;
            if (pct >= 0) _progress.Value = Math.Max(0, Math.Min(100, pct));
        }

        private void ToggleDetails()
        {
            bool open = ClientSize.Height < ExpandedHeight;
            ClientSize = new Size(ClientSize.Width, open ? ExpandedHeight : CollapsedHeight);
            _detailsToggle.Text = open ? "Hide details" : "Show details";
        }

        // ================================================================ actions

        private void Primary()
        {
            switch (_next)
            {
                case Next.Install: case Next.Retry: RunFlow(installFirst: true, repairFirst: false); break;
                case Next.StartAndCheck: RunFlow(installFirst: false, repairFirst: false); break;
                case Next.Repair: RunFlow(installFirst: false, repairFirst: true); break;
                case Next.Finish:
                    var st = Current;
                    if (st != null && !st.IsRunning) { try { GameRunner.Launch(st); } catch (Exception ex) { Log.Write("Launch failed: " + ex.Message); } }
                    Close();
                    break;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_autoAction == "install" && _next == Next.Install) RunFlow(true, false);
            else if (_autoAction == "repair" && _next == Next.Repair) RunFlow(false, true);
            else if (_autoAction == "uninstall") Uninstall();
        }

        private async void RunFlow(bool installFirst, bool repairFirst)
        {
            var st = Current;
            if (st == null || _busy) return;
            if (!st.CanWrite())
            {
                Elevate(st, repairFirst ? "repair" : "install");
                return;
            }

            SetBusy(true);
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            try
            {
                if (installFirst)
                {
                    await WaitForGameClosed(st, ct);

                    if (!st.BepInExInstalled)
                    {
                        _stepLoader.Set(StepState.Working, "Installing…");
                        await Task.Run(() => BepInExInstaller.Install(st, Report, ct), ct);
                        _stepLoader.Set(StepState.Done, "Installed.");
                    }

                    _stepAgent.Set(StepState.Working, "Installing…");
                    await Task.Run(() => PluginInstaller.Install(st), ct);
                    _stepAgent.Set(StepState.Done, "Installed (version " + SetupInfo.Version + ").");
                }

                if (repairFirst)
                {
                    _stepRepair.Visible = true;
                    await WaitForGameClosed(st, ct);
                    _stepRepair.Set(StepState.Working, "Building…");
                    await Task.Run(() => LocalBuilder.Build(st, Report, ct), ct);
                    _stepRepair.Set(StepState.Done, "Built for your game version.");
                }

                // Start the game and wait for the plugin's own check.
                _stepCheck.Set(StepState.Working, "Waiting for the game…");
                SetProgress(-1);
                // The game must start fresh so it loads the mod.
                await WaitForGameClosed(st, ct);
                SetStatus("Starting Football Manager…", Blue);
                var since = DateTime.Now;
                bool launched = await Task.Run(() => GameRunner.Launch(st), ct);
                SetMessage(launched
                    ? "Leave Football Manager open until this window says it's done. The very first start can take 5–10 minutes."
                    : "Start Football Manager from the Xbox app now. Setup will wait and check it.");

                string details = null;
                var result = await Task.Run(() => GameRunner.Watch(st, since, s => Ui(() => SetStatus(s, Blue)), ct, out details), ct);
                Log.Write("Check result: " + result + (string.IsNullOrEmpty(details) ? "" : Environment.NewLine + details));
                switch (result)
                {
                    case WatchResult.Ok:
                        _stepCheck.Set(StepState.Done, "Checked — working with your game.");
                        if (_stepRepair.Icon.Visible) _stepRepair.Set(StepState.Done);
                        SetStatus("All set! Manager Agent is working.", Green);
                        SetMessage("In Football Manager, click the Agent tab (top-right — drag it anywhere). Set your wage range under \"Your terms\", " +
                                   "and switch on Job hunting when you want a new job.");
                        SetNext(Next.Finish);
                        _primary.Text = "Finish";
                        FlashAttention();
                        break;

                    case WatchResult.Failed:
                    case WatchResult.NotLoaded:
                        if (repairFirst)
                        {
                            _stepCheck.Set(StepState.Failed, "Still not matching after the build.");
                            throw new SetupException("Manager Agent still doesn't match this game version.",
                                "Use \"Show details\" → open the log and send it to whoever gave you this mod.", null);
                        }
                        _stepCheck.Set(StepState.Failed, "Your game version needs a matching build.");
                        _stepRepair.Visible = true;
                        _stepRepair.Set(StepState.Todo, "Setup will build one for you (one-time download of about 200 MB).");
                        SetStatus("One more step needed.", Amber);
                        SetMessage("Close Football Manager, then click Repair. Setup will build Manager Agent to match your game version (needs internet, a few minutes).");
                        SetNext(Next.Repair);
                        FlashAttention();
                        break;

                    case WatchResult.GameClosed:
                        _stepCheck.Set(StepState.Todo, "The game closed before the check finished.");
                        SetStatus("Football Manager closed before setup could check it.", Amber);
                        SetMessage("Click Start & check and leave the game running until this window says it's done.");
                        SetNext(Next.StartAndCheck);
                        break;

                    default:
                        _stepCheck.Set(StepState.Failed, "No answer from the game.");
                        SetStatus("The check took too long.", Amber);
                        SetMessage("If the game is at its main menu, close it and click Start & check again.");
                        SetNext(Next.StartAndCheck);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                Log.Write("Cancelled.");
                SetBusy(false);
                Refresh(true);
                SetStatus("Stopped.", Gray);
                return;
            }
            catch (SetupException ex)
            {
                Log.Write("Problem: " + ex.Message + (ex.Advice != null ? " — " + ex.Advice : ""));
                SetStatus(ex.Message, Red);
                SetMessage(ex.Advice ?? "Click Try again. If it keeps happening, use Show details to see what went wrong.",
                    ex.Link != null ? "Open the download page" : null, ex.Link);
                SetNext(Next.Retry);
            }
            catch (Exception ex)
            {
                Log.Write("Error: " + ex);
                SetStatus("Something went wrong: " + ex.Message, Red);
                SetMessage("Check your internet connection and click Try again. Show details has the full log.");
                SetNext(Next.Retry);
            }
            finally
            {
                SetBusy(false);
                SetProgress(-1);
            }
        }

        private async Task WaitForGameClosed(GameState st, CancellationToken ct)
        {
            if (!st.IsRunning) return;
            SetStatus("Please close Football Manager to continue…", Amber);
            SetMessage("Setup needs the game closed for this step, and will carry on by itself as soon as it is.");
            while (st.IsRunning)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(1500, ct);
            }
            await Task.Delay(1500, ct); // let it release its files
        }

        private void Report(string text, int pct)
        {
            Ui(() =>
            {
                SetStatus(text, Blue);
                SetProgress(pct);
            });
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _games.Enabled = _browse.Enabled = _uninstall.Enabled = !busy;
            _primary.Enabled = !busy;
            if (busy) _primary.Text = "Working…";
            _close.Text = busy ? "Cancel" : "Close";
            UseWaitCursor = false;
        }

        private void Elevate(GameState st, string action)
        {
            var ok = MessageBox.Show(this,
                "Windows needs your permission for Setup to add files to the game folder:\n\n" + st.Game.Folder + "\n\nClick OK, then Yes on the Windows prompt.",
                "Permission needed", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (ok != DialogResult.OK) return;
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath,
                    "--game \"" + st.Game.Folder.TrimEnd('\\') + "\" --auto " + action) { UseShellExecute = true, Verb = "runas" });
                Close();
            }
            catch (Exception ex) { Log.Write("Elevation cancelled: " + ex.Message); }
        }

        private void Uninstall()
        {
            var st = Current;
            if (st == null || _busy) return;
            if (!st.PluginInstalled && !PluginInstaller.BepInExWasInstalledByUs(st))
            {
                MessageBox.Show(this, "Manager Agent isn't installed in this game folder.", "Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, "Remove Manager Agent from " + st.Game.Title + "?", "Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (!st.CanWrite()) { Elevate(st, "uninstall"); return; }
            if (st.IsRunning)
            {
                MessageBox.Show(this, "Please close Football Manager first, then click Uninstall again.", "Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            bool removeData = MessageBox.Show(this, "Also delete your saved agent preferences (wage range, clauses, job filters)?\n\nChoose No to keep them for later.",
                "Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            bool removeLoader = PluginInstaller.BepInExWasInstalledByUs(st) &&
                MessageBox.Show(this, "Also remove the BepInEx mod loader that Setup installed?\n\nChoose No if you use any other Football Manager mods.",
                    "Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            try
            {
                PluginInstaller.Uninstall(st, removeData, removeLoader);
                Refresh(true);
                SetStatus("Manager Agent was removed.", Gray);
                SetMessage(removeData ? "" : "Your preferences were kept in BepInEx\\config\\ManagerAgent.");
            }
            catch (Exception ex)
            {
                Log.Write("Uninstall error: " + ex);
                SetStatus("Couldn't remove everything: " + ex.Message, Red);
            }
        }

        // ================================================================ helpers

        private void Ui(Action a)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(a); } catch { } }
            else a();
        }

        private static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        private void FlashAttention()
        {
            try
            {
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                TopMost = true;
                Activate();
                TopMost = false;
                System.Media.SystemSounds.Asterisk.Play();
            }
            catch { }
        }

        // Used by the screenshot/demo mode to show each state without a game.
        internal void Demo(string state)
        {
            var fake = new GameInstall { Folder = @"C:\Program Files (x86)\Steam\steamapps\common\Football Manager 26", ExeName = "fm", Store = Store.Steam, Title = "Football Manager 26", SteamAppId = "1234" };
            _games.Items.Clear();
            _games.Items.Add(fake);
            _games.SelectedIndex = 0;
            _path.Text = fake.Folder;
            _uninstall.Enabled = false;
            switch (state)
            {
                case "ready":
                    _stepLoader.Set(StepState.Todo, "Will be downloaded and installed (about 40 MB).");
                    _stepAgent.Set(StepState.Todo, "Will be installed (version " + SetupInfo.Version + ").");
                    _stepCheck.Set(StepState.Todo);
                    SetStatus("Ready to install.", Navy);
                    SetMessage("Click Install. Setup will add the mod loader, add Manager Agent, then start the game once to check it.");
                    SetNext(Next.Install);
                    break;
                case "working":
                    _stepLoader.Set(StepState.Done, "Installed.");
                    _stepAgent.Set(StepState.Done, "Installed (version " + SetupInfo.Version + ").");
                    _stepCheck.Set(StepState.Working, "Waiting for the game…");
                    SetStatus("Preparing Football Manager for mods (first time only)…", Blue);
                    SetMessage("Leave Football Manager open until this window says it's done. The very first start can take 5–10 minutes.");
                    SetNext(Next.Install);
                    SetBusy(true);
                    break;
                case "repair":
                    _stepLoader.Set(StepState.Done, "Installed.");
                    _stepAgent.Set(StepState.Done, "Installed (version " + SetupInfo.Version + ").");
                    _stepCheck.Set(StepState.Failed, "Your game version needs a matching build.");
                    _stepRepair.Visible = true;
                    _stepRepair.Set(StepState.Todo, "Setup will build one for you (one-time download of about 200 MB).");
                    SetStatus("One more step needed.", Amber);
                    SetMessage("Close Football Manager, then click Repair. Setup will build Manager Agent to match your game version (needs internet, a few minutes).");
                    SetNext(Next.Repair);
                    break;
                case "done":
                    _stepLoader.Set(StepState.Done, "Installed.");
                    _stepAgent.Set(StepState.Done, "Installed (version " + SetupInfo.Version + ").");
                    _stepCheck.Set(StepState.Done, "Checked — working with your game.");
                    SetStatus("All set! Manager Agent is working.", Green);
                    SetMessage("In Football Manager, click the Agent tab (top-right — drag it anywhere). Set your wage range under \"Your terms\", " +
                                   "and switch on Job hunting when you want a new job.");
                    SetNext(Next.Finish);
                    _primary.Text = "Finish";
                    _uninstall.Enabled = true;
                    break;
                case "error":
                    _stepLoader.Set(StepState.Failed, "Not installed.");
                    _stepAgent.Set(StepState.Todo);
                    _stepCheck.Set(StepState.Todo);
                    SetStatus("Couldn't find a BepInEx download automatically.", Red);
                    SetMessage("Open the BepInEx page, download the file whose name starts with \"BepInEx-Unity.IL2CPP-win-x64\", put it in the same folder as this installer, then press Install again.",
                        "Open the download page", SetupInfo.BepInExBuildsPage);
                    SetNext(Next.Retry);
                    break;
            }
        }
    }
}
