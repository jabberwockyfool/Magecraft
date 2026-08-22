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
        private ProjectPaths _paths;
        private Sc3Tools _sc3;
        private Charset _charset;
        private ValidationReport _report;
        private string _savedRoot = "";

        private readonly Label _lblRoot = new Label();
        private readonly Button _btnRoot = new Button();
        private readonly Button _btnCheck = new Button();
        private readonly Button _btnFix = new Button();
        private readonly Button _btnBuild = new Button();
        private readonly Button _btnExport = new Button();
        private readonly CheckBox _chkErr = new CheckBox();
        private readonly CheckBox _chkWarn = new CheckBox();
        private readonly CheckBox _chkInfo = new CheckBox();
        private readonly CheckBox _chkRebuild = new CheckBox();
        private readonly CheckBox _chkMpk = new CheckBox();
        private readonly CheckBox _chkInstall = new CheckBox();
        private readonly TextBox _txtGame = new TextBox();
        private readonly Button _btnGame = new Button();
        private readonly DataGridView _grid = new DataGridView();
        private readonly TextBox _log = new TextBox();
        private readonly ProgressBar _bar = new ProgressBar();
        private readonly Label _status = new Label();

        private string SettingsFile
        {
            get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "magecraft-settings.txt"); }
        }

        private string CacheFile
        {
            get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "charset-cache.txt"); }
        }

        public MainForm()
        {
            Text = "Magecraft";
            Width = 1180;
            Height = 780;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildUi();
            LoadSettings();

            // Magecraft vit hors du depot traduit : la racine retenue au dernier lancement
            // prime, et la detection automatique ne sert que de secours.
            SetRoot((_savedRoot.Length > 0 && ProjectPaths.LooksLikeRoot(_savedRoot)
                        ? ProjectPaths.Discover(_savedRoot) : null)
                    ?? ProjectPaths.Discover(Path.GetDirectoryName(Application.ExecutablePath))
                    ?? ProjectPaths.Discover(Environment.CurrentDirectory));
        }

        // --------------------------------------------------------------------- UI

        private void BuildUi()
        {
            var top = new Panel { Dock = DockStyle.Top, Height = 96, Padding = new Padding(10, 8, 10, 4) };

            _lblRoot.SetBounds(12, 10, 900, 18);
            _lblRoot.AutoEllipsis = true;
            _btnRoot.SetBounds(920, 6, 110, 26);
            _btnRoot.Text = "Changer...";
            _btnRoot.Click += (s, e) => ChooseRoot();

            _btnCheck.SetBounds(12, 40, 120, 32);
            _btnCheck.Text = "Vérifier";
            _btnCheck.Click += async (s, e) => await CheckAsync();

            _btnFix.SetBounds(140, 40, 190, 32);
            _btnFix.Text = "Corriger les caractères";
            _btnFix.Enabled = false;
            _btnFix.Click += async (s, e) => await FixAsync();

            _btnBuild.SetBounds(338, 40, 120, 32);
            _btnBuild.Text = "Construire";
            _btnBuild.Click += async (s, e) => await BuildAsync();

            _btnExport.SetBounds(466, 40, 150, 32);
            _btnExport.Text = "Exporter le rapport";
            _btnExport.Enabled = false;
            _btnExport.Click += (s, e) => ExportReport();

            _chkErr.SetBounds(640, 46, 90, 20); _chkErr.Text = "Erreurs"; _chkErr.Checked = true;
            _chkWarn.SetBounds(730, 46, 130, 20); _chkWarn.Text = "Avertissements"; _chkWarn.Checked = true;
            _chkInfo.SetBounds(866, 46, 110, 20); _chkInfo.Text = "À compiler"; _chkInfo.Checked = false;
            foreach (var c in new[] { _chkErr, _chkWarn, _chkInfo }) c.CheckedChanged += (s, e) => Refill();

            top.Controls.AddRange(new Control[] { _lblRoot, _btnRoot, _btnCheck, _btnFix, _btnBuild, _btnExport, _chkErr, _chkWarn, _chkInfo });

            var opts = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(10, 0, 10, 0) };
            _chkRebuild.SetBounds(12, 6, 150, 22); _chkRebuild.Text = "Tout recompiler";
            _chkMpk.SetBounds(170, 6, 210, 22); _chkMpk.Text = "Reconstruire enscript.mpk"; _chkMpk.Checked = true;
            _chkInstall.SetBounds(390, 6, 160, 22); _chkInstall.Text = "Installer dans le jeu";
            _chkInstall.CheckedChanged += (s, e) => { _txtGame.Enabled = _chkInstall.Checked; _btnGame.Enabled = _chkInstall.Checked; };
            _txtGame.SetBounds(552, 5, 430, 24); _txtGame.Enabled = false;
            _btnGame.SetBounds(988, 4, 40, 25); _btnGame.Text = "..."; _btnGame.Enabled = false;
            _btnGame.Click += (s, e) => ChooseGame();
            opts.Controls.AddRange(new Control[] { _chkRebuild, _chkMpk, _chkInstall, _txtGame, _btnGame });

            _grid.Dock = DockStyle.Fill;
            _grid.ReadOnly = true;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.Columns.Add(Col("Gravité", 60));
            _grid.Columns.Add(Col("Fichier", 110));
            _grid.Columns.Add(Col("Ligne", 45));
            _grid.Columns.Add(Col("Règle", 70));
            _grid.Columns.Add(Col("Message", 320));
            _grid.Columns.Add(Col("Extrait", 300));
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
                SplitterDistance = 420,
            };
            split.Panel1.Controls.Add(_grid);
            split.Panel2.Controls.Add(_log);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 28 };
            _bar.SetBounds(10, 4, 300, 18);
            _status.SetBounds(322, 6, 800, 18);
            bottom.Controls.AddRange(new Control[] { _bar, _status });

            Controls.Add(split);
            Controls.Add(bottom);
            Controls.Add(opts);
            Controls.Add(top);
        }

        private static DataGridViewTextBoxColumn Col(string header, int width)
        {
            return new DataGridViewTextBoxColumn { HeaderText = header, FillWeight = width };
        }

        // ---------------------------------------------------------------- actions

        private void SetRoot(ProjectPaths paths)
        {
            _paths = paths;
            if (_paths == null)
            {
                _lblRoot.Text = "Aucun dépôt sélectionné. Utilise « Changer... » et indique le dossier contenant script-decompile et script-compile.";
                _btnCheck.Enabled = _btnBuild.Enabled = false;
                return;
            }

            _lblRoot.Text = "Dépôt : " + _paths.Root;
            var problems = _paths.Check();
            _btnCheck.Enabled = _btnBuild.Enabled = problems.Length == 0;
            foreach (var p in problems) Log("⚠ " + p);

            if (problems.Length == 0)
            {
                _sc3 = new Sc3Tools(_paths.Sc3Tools);
                _charset = new Charset(_sc3, CacheFile);
                Log("Prêt. " + Directory.GetFiles(_paths.ScriptFr, "*.scx.txt").Length + " scripts français détectés.");
            }
        }

        private void ChooseRoot()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Racine du dépôt Chaos-Child-FR" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (!ProjectPaths.LooksLikeRoot(dlg.SelectedPath))
                {
                    MessageBox.Show(this, "Ce dossier ne contient pas script-decompile et script-compile.",
                                    "Dépôt invalide", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                SetRoot(ProjectPaths.Discover(dlg.SelectedPath));
            }
        }

        private void ChooseGame()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Racine du jeu (dossier contenant Game.exe)" })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _txtGame.Text = dlg.SelectedPath;
                    SaveSettings();
                }
            }
        }

        private async Task CheckAsync()
        {
            Busy(true);
            _log.Clear();
            Log("Vérification en cours...");
            try
            {
                var validator = new Validator(_paths, _sc3, _charset);
                _report = await Task.Run(() => validator.Run(Log, Progress));

                Refill();
                _btnFix.Enabled = _report.Findings.Any(f => f.Rule == "charset" && f.Fix != null);
                _btnExport.Enabled = true;

                var rejected = string.Join(" ", _charset.Rejected.Select(c => c.ToString()));
                if (rejected.Length > 0) Log("Caractères refusés par le jeu : " + rejected);

                _status.Text = string.Format("{0} fichiers · {1} erreur(s), {2} avertissement(s) · {3} ligne(s) à compiler sur {4}",
                    _report.FilesChecked, _report.Errors, _report.Warnings,
                    _report.PendingLines, _report.TotalLines);
                Log(_status.Text);
            }
            catch (Exception ex) { Fail(ex); }
            finally { Busy(false); }
        }

        private async Task FixAsync()
        {
            if (MessageBox.Show(this,
                    "Les caractères que le jeu ne connaît pas seront remplacés directement dans script-decompile " +
                    "(œ → oe, « » → “ ”, … → ...).\n\nLes fichiers seront modifiés. Continuer ?",
                    "Corriger les caractères", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            Busy(true);
            try
            {
                var builder = new Builder(_paths, _sc3);
                await Task.Run(() => builder.AutoFix(_charset, Log));
                await CheckAsync();
            }
            catch (Exception ex) { Fail(ex); }
            finally { Busy(false); }
        }

        private async Task BuildAsync()
        {
            Busy(true);
            Log("--- Construction ---");
            try
            {
                var opt = new BuildOptions
                {
                    Rebuild = _chkRebuild.Checked,
                    PackMpk = _chkMpk.Checked,
                    InstallDir = _chkInstall.Checked && _txtGame.Text.Length > 0 ? _txtGame.Text : null,
                };
                var builder = new Builder(_paths, _sc3);
                var res = await Task.Run(() => builder.Run(opt, Log, Progress));

                if (res.Ok)
                {
                    _status.Text = string.Format("Construction terminée : {0} script(s) compilé(s).", res.Compiled);
                    Log("Terminé.");
                }
                else
                {
                    _status.Text = string.Format("Construction interrompue : {0} script(s) en échec.", res.Failures.Count);
                    Log("Échecs :");
                    foreach (var f in res.Failures.Take(20)) Log("  " + f);
                    MessageBox.Show(this, "Certains scripts n'ont pas pu être compilés. Lance « Vérifier » pour voir pourquoi.",
                                    "Construction incomplète", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex) { Fail(ex); }
            finally { Busy(false); }
        }

        private void Refill()
        {
            _grid.Rows.Clear();
            if (_report == null) return;

            foreach (var f in _report.Findings)
            {
                if (f.Severity == Severity.Erreur && !_chkErr.Checked) continue;
                if (f.Severity == Severity.Avertissement && !_chkWarn.Checked) continue;
                if (f.Severity == Severity.Info && !_chkInfo.Checked) continue;

                int i = _grid.Rows.Add(f.Severity.ToString(), f.File, f.Line == 0 ? "" : f.Line.ToString(),
                                       f.Rule, f.Message, f.Excerpt ?? "");
                _grid.Rows[i].Tag = f;
                _grid.Rows[i].DefaultCellStyle.ForeColor =
                    f.Severity == Severity.Erreur ? Color.FromArgb(176, 0, 32)
                    : f.Severity == Severity.Avertissement ? Color.FromArgb(150, 90, 0)
                    : Color.DimGray;
            }
        }

        private void OpenSelected()
        {
            if (_grid.CurrentRow == null) return;
            var f = _grid.CurrentRow.Tag as Finding;
            if (f == null) return;
            try { Process.Start(Path.Combine(_paths.ScriptFr, f.File)); }
            catch (Exception ex) { Log("Ouverture impossible : " + ex.Message); }
        }

        private void ExportReport()
        {
            using (var dlg = new SaveFileDialog { FileName = "rapport-traduction.txt", Filter = "Texte|*.txt" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var sb = new StringBuilder();
                sb.AppendLine("Rapport de vérification - " + _paths.Root);
                sb.AppendLine(_status.Text);
                sb.AppendLine();
                foreach (var f in _report.Findings)
                    sb.AppendLine(string.Format("{0,-14} {1,-24} L{2,-6} {3,-10} {4}",
                        f.Severity, f.File, f.Line == 0 ? "-" : f.Line.ToString(), f.Rule, f.Message));
                File.WriteAllText(dlg.FileName, sb.ToString(), Charset.Utf8NoBom);
                Log("Rapport écrit : " + dlg.FileName);
            }
        }

        // ------------------------------------------------------------------ divers

        private void Busy(bool busy)
        {
            _btnCheck.Enabled = _btnBuild.Enabled = _btnRoot.Enabled = !busy;
            _btnFix.Enabled = !busy && _btnFix.Enabled;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            if (!busy) { _bar.Value = 0; _bar.Style = ProgressBarStyle.Blocks; }
        }

        private void Fail(Exception ex)
        {
            Log("ERREUR : " + ex.Message);
            MessageBox.Show(this, ex.Message, "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return;
                foreach (var line in File.ReadAllLines(SettingsFile))
                {
                    var kv = line.Split(new[] { '=' }, 2);
                    if (kv.Length != 2) continue;
                    if (kv[0] == "root") { _savedRoot = kv[1]; }
                    if (kv[0] == "game") { _txtGame.Text = kv[1]; }
                    if (kv[0] == "install") { _chkInstall.Checked = kv[1] == "1"; }
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                File.WriteAllLines(SettingsFile, new[]
                {
                    "root=" + (_paths != null ? _paths.Root : _savedRoot),
                    "game=" + _txtGame.Text,
                    "install=" + (_chkInstall.Checked ? "1" : "0"),
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
