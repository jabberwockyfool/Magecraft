using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Magecraft
{
    public sealed class MainForm : Form
    {
        private readonly Workspace _workspace = new Workspace();
        private Sc3Tools _sc3;
        private Charset _charset;
        private ValidationReport _report;
        private BuildResult _preview;

        private readonly RadioButton _modeScripts = new RadioButton();
        private readonly RadioButton _modeFiles = new RadioButton();
        private readonly RadioButton _modeScenario = new RadioButton();

        private readonly TextBox _sourceDir = new TextBox();
        private readonly TextBox _txtDir = new TextBox();
        private readonly TextBox _template = new TextBox();
        private readonly TextBox _outMpk = new TextBox();
        private readonly TextBox _sc3Path = new TextBox();

        private readonly TextBox _key = new TextBox();
        private readonly ComboBox _language = new ComboBox();

        private Label _lblSource, _lblTxt, _lblTemplate, _lblSc3, _lblKey, _lblOut;
        private Button _btnTxtBrowse, _btnSc3Browse, _btnKeyBrowse;

        private readonly Button _btnCheck = new Button();
        private readonly Button _btnFix = new Button();
        private readonly Button _btnBuild = new Button();
        private readonly Button _btnExport = new Button();

        private readonly CheckBox _chkErrors = new CheckBox();
        private readonly CheckBox _chkWarnings = new CheckBox();
        private readonly CheckBox _chkInfo = new CheckBox();
        private readonly CheckBox _chkKeepScx = new CheckBox();
        private readonly CheckBox _chkForce = new CheckBox();

        private Label _lblLanguage;
        private readonly DataGridView _grid = new DataGridView();
        private readonly TextBox _log = new TextBox();
        private readonly ProgressBar _bar = new ProgressBar();
        private readonly Label _status = new Label();

        private static string ExeDir { get { return Path.GetDirectoryName(Application.ExecutablePath); } }
        private static string SettingsFile { get { return Path.Combine(ExeDir, "magecraft-settings.txt"); } }
        private static string CacheFile { get { return Path.Combine(ExeDir, "charset-cache.txt"); } }
        private const string SettingsHeader = "magecraft-paths-3";

        public MainForm()
        {
            Text = "Magecraft";
            Width = 1120;
            Height = 800;
            MinimumSize = new Size(880, 600);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildUi();
            LoadSettings();
            if (_sc3Path.Text.Length == 0) _sc3Path.Text = Workspace.GuessSc3Tools(ExeDir);
            if (_language.Text.Length == 0) _language.Text = "en";
            ApplyMode();
        }

        // ----------------------------------------------------------------------- UI

        private void BuildUi()
        {
            // Layout panels rather than absolute bounds: anchors are computed against the
            // parent's size at insertion time, so hand-placed controls end up off-screen
            // once the panel is docked and resized.
            _modeScripts.Text = "Scripts (.scx + text)";
            _modeFiles.Text = "Files (CG, textures...)";
            _modeScenario.Text = "Scenario (PSB archive)";
            foreach (var r in new[] { _modeScripts, _modeFiles, _modeScenario })
            {
                r.AutoSize = true;
                r.Margin = new Padding(6, 6, 24, 6);
                r.CheckedChanged += (s, e) => { if (((RadioButton)s).Checked) ApplyMode(); };
            }
            _modeScripts.Checked = true;

            var modes = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(146, 6, 12, 0),
            };
            modes.Controls.Add(_modeScripts);
            modes.Controls.Add(_modeFiles);
            modes.Controls.Add(_modeScenario);

            var paths = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 3,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12, 4, 12, 4),
            };
            paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));

            Button unusedButton;
            _lblSource = AddPathRow(paths, "Script folder (.scx)", _sourceDir, out unusedButton,
                                    () => PickFolder(_sourceDir, "Folder holding the files to pack"));
            _lblTxt = AddPathRow(paths, "Text folder (.scx.txt)", _txtDir, out _btnTxtBrowse,
                                 () => PickFolder(_txtDir, "Folder holding the translated text files"));
            _lblTemplate = AddPathRow(paths, "Original archive (.mpk)", _template, out unusedButton,
                                      () => PickOpenMpk(_template));
            // In Scenario mode the output is a pair of files whose names the game expects,
            // so the user picks the folder and Magecraft names them.
            _lblOut = AddPathRow(paths, "Archive to write (.mpk)", _outMpk, out unusedButton,
                                 () =>
                                 {
                                     if (_modeScenario.Checked)
                                         PickFolder(_outMpk, "Folder to write the archive into");
                                     else
                                         PickSaveFile(_outMpk);
                                 });
            _lblSc3 = AddPathRow(paths, "sc3tools.exe", _sc3Path, out _btnSc3Browse, () => PickExe(_sc3Path));
            _lblKey = AddPathRow(paths, "Archive key", _key, out _btnKeyBrowse, () => { });
            _btnKeyBrowse.Visible = false;

            _chkKeepScx.Text = "Also write the built .scx next to the archive";
            _chkKeepScx.AutoSize = true;
            _chkKeepScx.Margin = new Padding(6, 6, 24, 6);
            _chkForce.Text = "Build even if some scripts fail";
            _chkForce.AutoSize = true;
            _chkForce.Margin = new Padding(6, 6, 6, 6);

            var options = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(146, 0, 12, 0),
            };
            _language.DropDownStyle = ComboBoxStyle.DropDown;
            _language.Items.AddRange(new object[] { "en", "tc", "sc", "jp" });
            _language.Width = 70;
            _language.Margin = new Padding(6, 4, 6, 6);
            var lblLang = new Label { Text = "Language:", AutoSize = true, Margin = new Padding(18, 8, 2, 6) };

            options.Controls.Add(_chkKeepScx);
            options.Controls.Add(_chkForce);
            options.Controls.Add(lblLang);
            options.Controls.Add(_language);
            _lblLanguage = lblLang;

            _btnCheck.Text = "Check";
            _btnCheck.Click += async (s, e) => await CheckAsync();
            _btnFix.Text = "Fix characters";
            _btnFix.Enabled = false;
            _btnFix.Click += async (s, e) => await FixAsync();
            _btnBuild.Text = "Build";
            _btnBuild.Click += async (s, e) => await BuildAsync();
            _btnExport.Text = "Export report";
            _btnExport.Enabled = false;
            _btnExport.Click += (s, e) => ExportReport();

            foreach (var b in new[] { _btnCheck, _btnFix, _btnBuild, _btnExport })
            {
                b.AutoSize = true;
                b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                b.MinimumSize = new Size(110, 32);
                b.Margin = new Padding(4, 6, 4, 8);
            }

            _chkErrors.Text = "Errors"; _chkErrors.Checked = true;
            _chkWarnings.Text = "Warnings"; _chkWarnings.Checked = true;
            _chkInfo.Text = "Info";
            foreach (var c in new[] { _chkErrors, _chkWarnings, _chkInfo })
            {
                c.AutoSize = true;
                c.Margin = new Padding(8, 14, 8, 6);
                c.CheckedChanged += (s, e) => Refill();
            }

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(142, 0, 12, 0),
            };
            actions.Controls.AddRange(new Control[]
            {
                _btnCheck, _btnFix, _btnBuild, _btnExport,
                new Label { Text = "", Width = 24, Height = 1 },
                _chkErrors, _chkWarnings, _chkInfo
            });

            _grid.Dock = DockStyle.Fill;
            _grid.ReadOnly = true;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.BackgroundColor = SystemColors.Window;
            _grid.Columns.Add(Col("Severity", 55));
            _grid.Columns.Add(Col("File", 105));
            _grid.Columns.Add(Col("Line", 40));
            _grid.Columns.Add(Col("Rule", 70));
            _grid.Columns.Add(Col("Message", 330));
            _grid.Columns.Add(Col("Excerpt", 300));
            _grid.CellDoubleClick += (s, e) => OpenSelected();

            _log.Multiline = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.ReadOnly = true;
            _log.Dock = DockStyle.Fill;
            _log.Font = new Font("Consolas", 9f);
            _log.BackColor = Color.White;

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 320,
            };
            split.Panel1.Controls.Add(_grid);
            split.Panel2.Controls.Add(_log);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 28 };
            _bar.SetBounds(12, 4, 280, 18);
            _status.SetBounds(302, 6, 780, 18);
            _status.AutoEllipsis = true;
            bottom.Controls.Add(_bar);
            bottom.Controls.Add(_status);

            // Top-docked controls stack outwards in reverse order of insertion.
            Controls.Add(split);
            Controls.Add(bottom);
            Controls.Add(actions);
            Controls.Add(options);
            Controls.Add(paths);
            Controls.Add(modes);
        }

        private Label AddPathRow(TableLayoutPanel host, string label, TextBox box, out Button browse, Action onBrowse)
        {
            box.Dock = DockStyle.Fill;
            box.Margin = new Padding(3, 4, 3, 4);

            var lbl = new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 8, 3, 4),
            };
            browse = new Button
            {
                Text = "...",
                Dock = DockStyle.Fill,
                Margin = new Padding(3, 3, 3, 3),
            };
            browse.Click += (s, e) => onBrowse();

            int row = host.RowCount++;
            host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            host.Controls.Add(lbl, 0, row);
            host.Controls.Add(box, 1, row);
            host.Controls.Add(browse, 2, row);
            return lbl;
        }

        private static DataGridViewTextBoxColumn Col(string header, int width)
        {
            return new DataGridViewTextBoxColumn { HeaderText = header, FillWeight = width };
        }

        /// <summary>Shows only what the selected mode uses.</summary>
        private void ApplyMode()
        {
            // Setting Checked while building the form fires this before the rows exist.
            if (_lblSource == null) return;

            bool scripts = _modeScripts.Checked;
            bool scenario = _modeScenario.Checked;

            _lblSource.Text = scenario ? "Workspace folder" : scripts ? "Script folder (.scx)" : "File folder";
            _lblTemplate.Text = scenario ? "Original archive (_info.psb.m)"
                              : scripts ? "Original archive (optional)" : "Original archive (.mpk)";
            _lblSc3.Text = scenario ? "FreeMote folder" : "sc3tools.exe";
            _lblOut.Text = scenario ? "Output folder" : "Archive to write (.mpk)";

            foreach (var c in new Control[] { _lblTxt, _txtDir, _btnTxtBrowse })
                c.Visible = scripts;
            foreach (var c in new Control[] { _lblSc3, _sc3Path, _btnSc3Browse })
                c.Visible = scripts || scenario;
            foreach (var c in new Control[] { _lblKey, _key })
                c.Visible = scenario;
            _lblLanguage.Visible = _language.Visible = scenario;

            _chkKeepScx.Visible = scripts;
            _chkForce.Visible = scripts;
            if (!scripts) _btnFix.Enabled = false;

            _btnCheck.Text = scenario ? "Extract" : scripts ? "Check" : "Preview";
            _btnFix.Visible = scripts;
            _btnExport.Visible = !scenario;
        }

        // ------------------------------------------------------------------ actions

        /// <summary>Copies the form's fields into the workspace and reports what is missing.</summary>
        private bool Ready(bool needOutput)
        {
            _workspace.Mode = _modeScripts.Checked ? BuildMode.Scripts
                            : _modeScenario.Checked ? BuildMode.Scenario : BuildMode.Files;
            _workspace.SourceDir = _sourceDir.Text.Trim();
            _workspace.TxtDir = _txtDir.Text.Trim();
            _workspace.TemplateMpk = _template.Text.Trim();
            _workspace.OutMpk = _outMpk.Text.Trim();
            _workspace.Sc3Path = _sc3Path.Text.Trim();
            _workspace.FreeMoteDir = _sc3Path.Text.Trim();
            _workspace.MdfKey = _key.Text.Trim();
            _workspace.Language = (_language.Text.Trim().Length > 0 ? _language.Text.Trim() : "en");

            var problems = _workspace.Problems(needOutput);
            if (problems.Length > 0)
            {
                MessageBox.Show(this, string.Join(Environment.NewLine, problems),
                                "Check the paths", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (File.Exists(_workspace.Sc3Path))
            {
                _sc3 = new Sc3Tools(_workspace.Sc3Path);
                if (_charset == null) _charset = new Charset(_sc3, CacheFile);
            }
            return true;
        }

        private async Task CheckAsync()
        {
            if (!Ready(false)) return;

            Busy(true);
            _log.Clear();
            _report = null;
            _preview = null;
            try
            {
                if (_workspace.Mode == BuildMode.Scripts) { await CheckScriptsAsync(); _btnExport.Enabled = true; }
                else if (_workspace.Mode == BuildMode.Scenario) await ExtractScenarioAsync();
                else { await PreviewFilesAsync(); _btnExport.Enabled = true; }
            }
            catch (Exception ex) { Fail(ex); }
            finally { Busy(false); }
        }

        private async Task CheckScriptsAsync()
        {
            Log("Checking...");
            var validator = new Validator(_workspace, _sc3, _charset);
            _report = await Task.Run(() => validator.Run(Log, Progress));

            Refill();
            _btnFix.Enabled = _report.Findings.Any(f => f.Fix != null);

            var rejected = string.Join(" ", _charset.Rejected.Select(c => c.ToString()));
            if (rejected.Length > 0) Log("Characters the game rejects: " + rejected);

            _status.Text = string.Format(
                "{0} script(s) · {1} error(s), {2} warning(s) · {3} line(s) to build out of {4}{5}",
                _report.FilesChecked, _report.Errors, _report.Warnings,
                _report.PendingLines, _report.TotalLines,
                _report.Untranslated > 0 ? " · " + _report.Untranslated + " without text" : "");
            Log(_status.Text);
        }

        private async Task PreviewFilesAsync()
        {
            Log("Reading the original archive...");
            var builder = new Builder(_workspace, _sc3);
            _preview = await Task.Run(() => builder.PreviewFiles(Log));

            Refill();
            _status.Text = string.Format("{0} entry(ies) replaced, {1} kept from the original{2}",
                _preview.Replaced, _preview.Kept,
                _preview.Ignored.Count > 0 ? " · " + _preview.Ignored.Count + " file(s) ignored" : "");
            Log(_status.Text);
        }

        private async Task FixAsync()
        {
            if (!Ready(false)) return;

            if (MessageBox.Show(this,
                    "Characters the game does not know will be replaced directly in the text files " +
                    "(oe ligature to \"oe\", guillemets to curly quotes, ellipsis to \"...\"), and stray " +
                    "carriage returns removed.\n\nThe files will be modified. Continue?",
                    "Fix characters", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            Busy(true);
            try
            {
                var builder = new Builder(_workspace, _sc3);
                await Task.Run(() => builder.FixCharacters(_charset, Log));
            }
            catch (Exception ex) { Fail(ex); }
            finally { Busy(false); }

            await CheckAsync();
        }

        private async Task BuildAsync()
        {
            if (!Ready(true)) return;

            Busy(true);
            _log.Clear();
            Log("--- Build ---");
            try
            {
                var options = new BuildOptions
                {
                    KeepCompiledScripts = _chkKeepScx.Checked,
                    PackDespiteFailures = _chkForce.Checked,
                };
                if (_workspace.Mode == BuildMode.Scenario) { await BuildScenarioAsync(); return; }

                var builder = new Builder(_workspace, _sc3);
                var res = await Task.Run(() => builder.Run(options, Log, Progress));

                if (res.ArchivePath != null)
                {
                    _status.Text = string.Format("Archive written: {0} replaced, {1} kept{2}.",
                        res.Replaced, res.Kept,
                        res.Failures.Count > 0 ? ", " + res.Failures.Count + " failed" : "");
                }
                else
                {
                    _status.Text = string.Format("{0} script(s) failed: archive not written.", res.Failures.Count);
                    Log("Failures:");
                    foreach (var f in res.Failures.Take(20)) Log("  " + f);
                    MessageBox.Show(this, "Some scripts could not be built. Run \"Check\" to see why.",
                                    "Build stopped", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex) { Fail(ex); }
            finally { Busy(false); }
        }

        private async Task ExtractScenarioAsync()
        {
            Log("Extracting the scenario...");
            var project = new ScenarioProject(_workspace, new FreeMoteRunner(_workspace.FreeMoteDir));
            var res = await Task.Run(() => project.Extract(Log, Progress));

            _status.Text = string.Format("{0} file(s), {1} line(s) extracted to {2}",
                                         res.Files, res.Lines, project.TextDir);
            Log(_status.Text);
            Log("Translate the files in \\text, keeping one line per entry, then press Build.");
        }

        private async Task BuildScenarioAsync()
        {
            var project = new ScenarioProject(_workspace, new FreeMoteRunner(_workspace.FreeMoteDir));
            var res = await Task.Run(() => project.Build(Log, Progress));

            if (res.Ok)
            {
                _status.Text = string.Format("Archive written: {0} line(s) updated across {1} file(s).",
                                             res.Changed, res.Files);
            }
            else
            {
                _status.Text = string.Format("{0} file(s) rejected: archive not written.", res.Failures.Count);
                Log("Failures:");
                foreach (var f in res.Failures.Take(20)) Log("  " + f);
                MessageBox.Show(this, string.Join(Environment.NewLine, res.Failures.Take(10)),
                                "Build stopped", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Refill()
        {
            _grid.Rows.Clear();
            foreach (var f in Rows())
            {
                if (f.Severity == Severity.Error && !_chkErrors.Checked) continue;
                if (f.Severity == Severity.Warning && !_chkWarnings.Checked) continue;
                if (f.Severity == Severity.Info && !_chkInfo.Checked) continue;

                int i = _grid.Rows.Add(f.Severity.ToString(), f.File, f.Line == 0 ? "" : f.Line.ToString(),
                                       f.Rule, f.Message, f.Excerpt ?? "");
                _grid.Rows[i].Tag = f;
                _grid.Rows[i].DefaultCellStyle.ForeColor =
                    f.Severity == Severity.Error ? Color.FromArgb(176, 0, 32)
                    : f.Severity == Severity.Warning ? Color.FromArgb(150, 90, 0)
                    : Color.DimGray;
            }
        }

        /// <summary>Findings of the last run, whichever mode produced them.</summary>
        private IEnumerable<Finding> Rows()
        {
            if (_report != null) return _report.Findings;
            if (_preview == null) return Enumerable.Empty<Finding>();

            return _preview.Ignored.Select(name => new Finding
            {
                Severity = Severity.Warning,
                File = name,
                Rule = "not-in-archive",
                Message = "The original archive has no entry with this name, so the file is left out. " +
                          "Entries are addressed by id: a new one would not be loaded.",
            });
        }

        private void OpenSelected()
        {
            if (_grid.CurrentRow == null) return;
            var f = _grid.CurrentRow.Tag as Finding;
            if (f == null) return;
            string dir = _workspace.Mode == BuildMode.Scripts ? _workspace.TxtDir : _workspace.SourceDir;
            try { Process.Start(Path.Combine(dir, f.File)); }
            catch (Exception ex) { Log("Could not open the file: " + ex.Message); }
        }

        private void ExportReport()
        {
            using (var dlg = new SaveFileDialog { FileName = "magecraft-report.txt", Filter = "Text|*.txt" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var sb = new StringBuilder();
                sb.AppendLine("Magecraft report");
                sb.AppendLine("Source:   " + _workspace.SourceDir);
                if (_workspace.Mode == BuildMode.Scripts) sb.AppendLine("Text:     " + _workspace.TxtDir);
                if (_workspace.TemplateMpk.Length > 0) sb.AppendLine("Original: " + _workspace.TemplateMpk);
                sb.AppendLine(_status.Text);
                sb.AppendLine();
                foreach (var f in Rows())
                    sb.AppendLine(string.Format("{0,-8} {1,-24} L{2,-6} {3,-14} {4}",
                        f.Severity, f.File, f.Line == 0 ? "-" : f.Line.ToString(), f.Rule, f.Message));
                File.WriteAllText(dlg.FileName, sb.ToString(), Charset.Utf8NoBom);
                Log("Report written to " + dlg.FileName);
            }
        }

        // ------------------------------------------------------------------- helpers

        private void Busy(bool busy)
        {
            _btnCheck.Enabled = _btnBuild.Enabled = !busy;
            if (busy) _btnFix.Enabled = false;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            if (!busy) _bar.Value = 0;
        }

        private void Fail(Exception ex)
        {
            Log("ERROR: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void Log(string message)
        {
            if (InvokeRequired) { BeginInvoke((Action<string>)Log, message); return; }
            _log.AppendText(message + Environment.NewLine);
        }

        private void Progress(int done, int total)
        {
            if (InvokeRequired) { BeginInvoke((Action<int, int>)Progress, done, total); return; }
            _bar.Maximum = Math.Max(1, total);
            _bar.Value = Math.Min(done, _bar.Maximum);
        }

        private void PickFolder(TextBox box, string description)
        {
            using (var dlg = new FolderBrowserDialog { Description = description, SelectedPath = box.Text })
                if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.SelectedPath;
        }

        private void PickExe(TextBox box)
        {
            using (var dlg = new OpenFileDialog { Filter = "sc3tools.exe|sc3tools.exe|Programs|*.exe" })
                if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.FileName;
        }

        private void PickOpenMpk(TextBox box)
        {
            using (var dlg = new OpenFileDialog { Filter = "MPK archive|*.mpk|All files|*.*" })
                if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.FileName;
        }

        private void PickSaveFile(TextBox box)
        {
            using (var dlg = new SaveFileDialog { Filter = "MPK archive|*.mpk", FileName = "enscript.mpk", OverwritePrompt = false })
                if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.FileName;
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return;
                var v = File.ReadAllLines(SettingsFile);
                // Anything without the header comes from an older layout: ignore it rather
                // than pour unrelated values into the path fields.
                if (v.Length < 9 || v[0] != SettingsHeader) return;
                _modeFiles.Checked = v[1] == "files";
                _modeScenario.Checked = v[1] == "scenario";
                _modeScripts.Checked = !_modeFiles.Checked && !_modeScenario.Checked;
                _sourceDir.Text = v[2];
                _txtDir.Text = v[3];
                _template.Text = v[4];
                _outMpk.Text = v[5];
                _sc3Path.Text = v[6];
                _key.Text = v[7];
                _language.Text = v[8];
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                File.WriteAllLines(SettingsFile, new[]
                {
                    SettingsHeader,
                    _modeFiles.Checked ? "files" : _modeScenario.Checked ? "scenario" : "scripts",
                    _sourceDir.Text, _txtDir.Text, _template.Text, _outMpk.Text, _sc3Path.Text,
                    _key.Text, _language.Text,
                });
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveSettings();
            base.OnFormClosing(e);
        }
    }
}
