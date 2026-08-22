using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Magecraft
{
    /// <summary>
    /// Trois dossiers, un bouton : on prend les .scx d'origine, on y injecte les textes
    /// traduits avec sc3tools, et on remballe le tout dans une archive .mpk.
    /// </summary>
    public sealed class PackerForm : Form
    {
        private readonly TextBox _scxDir = new TextBox();
        private readonly TextBox _txtDir = new TextBox();
        private readonly TextBox _outMpk = new TextBox();
        private readonly TextBox _sc3 = new TextBox();
        private readonly CheckBox _keepScx = new CheckBox();
        private readonly CheckBox _packAnyway = new CheckBox();
        private readonly Button _go = new Button();
        private readonly TextBox _log = new TextBox();
        private readonly ProgressBar _bar = new ProgressBar();
        private readonly Label _status = new Label();

        private string SettingsFile
        {
            get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "magecraft-packer-settings.txt"); }
        }

        public PackerForm()
        {
            Text = "Magecraft Packer — SCX vers MPK";
            Width = 900;
            Height = 620;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildUi();
            LoadSettings();
            if (_sc3.Text.Length == 0) _sc3.Text = GuessSc3Tools();
        }

        private void BuildUi()
        {
            var top = new Panel { Dock = DockStyle.Top, Height = 196, Padding = new Padding(12, 10, 12, 6) };

            int y = 10;
            AddRow(top, ref y, "Dossier des .scx", _scxDir, () => PickFolder(_scxDir, "Dossier contenant les scripts .scx d'origine"));
            AddRow(top, ref y, "Dossier des .txt", _txtDir, () => PickFolder(_txtDir, "Dossier contenant les textes traduits"));
            AddRow(top, ref y, "Archive .mpk à créer", _outMpk, () => PickSaveFile(_outMpk));
            AddRow(top, ref y, "sc3tools.exe", _sc3, () => PickOpenFile(_sc3));

            _keepScx.SetBounds(150, y + 4, 330, 22);
            _keepScx.Text = "Écrire aussi les .scx compilés à côté du .mpk";
            _packAnyway.SetBounds(490, y + 4, 330, 22);
            _packAnyway.Text = "Créer le .mpk même si des scripts échouent";
            top.Controls.Add(_keepScx);
            top.Controls.Add(_packAnyway);

            y += 32;
            _go.SetBounds(150, y, 250, 34);
            _go.Text = "Compiler et créer le MPK";
            _go.Click += async (s, e) => await RunAsync();
            top.Controls.Add(_go);

            _log.Multiline = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.ReadOnly = true;
            _log.Dock = DockStyle.Fill;
            _log.Font = new Font("Consolas", 9f);
            _log.BackColor = Color.White;

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 28 };
            _bar.SetBounds(12, 4, 280, 18);
            _status.SetBounds(302, 6, 560, 18);
            bottom.Controls.Add(_bar);
            bottom.Controls.Add(_status);

            Controls.Add(_log);
            Controls.Add(bottom);
            Controls.Add(top);
        }

        private void AddRow(Panel host, ref int y, string label, TextBox box, Action browse)
        {
            var lbl = new Label { Text = label, Bounds = new Rectangle(12, y + 4, 132, 20) };
            box.SetBounds(150, y, 640, 24);
            var btn = new Button { Text = "...", Bounds = new Rectangle(796, y - 1, 40, 26) };
            btn.Click += (s, e) => browse();
            host.Controls.Add(lbl);
            host.Controls.Add(box);
            host.Controls.Add(btn);
            y += 34;
        }

        // ------------------------------------------------------------------ travail

        private async Task RunAsync()
        {
            if (!CheckInputs()) return;

            _go.Enabled = false;
            _log.Clear();
            Cursor = Cursors.WaitCursor;
            try
            {
                var result = await Task.Run(() => Build());
                _status.Text = result;
            }
            catch (Exception ex)
            {
                Log("ERREUR : " + ex.Message);
                MessageBox.Show(this, ex.Message, "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _go.Enabled = true;
                Cursor = Cursors.Default;
                _bar.Value = 0;
                SaveSettings();
            }
        }

        private bool CheckInputs()
        {
            if (!Directory.Exists(_scxDir.Text)) return Complain("Le dossier des .scx est introuvable.");
            if (!Directory.Exists(_txtDir.Text)) return Complain("Le dossier des .txt est introuvable.");
            if (_outMpk.Text.Trim().Length == 0) return Complain("Indique le fichier .mpk à créer.");
            if (!File.Exists(_sc3.Text)) return Complain("sc3tools.exe est introuvable.");
            if (Directory.GetFiles(_scxDir.Text, "*.scx").Length == 0) return Complain("Aucun fichier .scx dans ce dossier.");
            return true;
        }

        private bool Complain(string message)
        {
            MessageBox.Show(this, message, "Vérifie les chemins", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        private string Build()
        {
            var sc3 = new Sc3Tools(_sc3.Text);

            // L'ordre alphabetique et les identifiants 0..n-1 reproduisent la table
            // d'origine des archives du jeu.
            var scxFiles = Directory.GetFiles(_scxDir.Text, "*.scx")
                                    .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                                    .ToList();

            string work = Path.Combine(Path.GetTempPath(), "magecraft-pack-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);

            int compiled = 0, untouched = 0;
            var failures = new List<string>();

            try
            {
                Log(string.Format("{0} script(s) trouvé(s).", scxFiles.Count));

                for (int i = 0; i < scxFiles.Count; i++)
                {
                    string name = Path.GetFileName(scxFiles[i]);
                    Progress(i + 1, scxFiles.Count * 2);

                    string dest = Path.Combine(work, name);
                    File.Copy(scxFiles[i], dest, true);

                    string txt = FindTxt(name);
                    if (txt == null)
                    {
                        untouched++;
                        Log("  (sans texte) " + name);
                        continue;
                    }

                    // replace-text modifie le .scx sur place : la copie de travail sert de cible.
                    File.Copy(txt, dest + ".txt", true);
                    var res = sc3.ReplaceText(name, name + ".txt", work);
                    if (res.Ok)
                    {
                        compiled++;
                        Log("  ok           " + name);
                    }
                    else
                    {
                        failures.Add(name + " : " + (res.FirstError ?? "échec de sc3tools"));
                        Log("  ÉCHEC        " + name + " -> " + (res.FirstError ?? "?"));
                        // Le .scx d'origine reste en place : l'archive gardera la version non traduite.
                        File.Copy(scxFiles[i], dest, true);
                    }
                }

                Log(string.Format("Scripts : {0} compilé(s), {1} sans texte, {2} en échec.",
                                  compiled, untouched, failures.Count));

                if (failures.Count > 0 && !_packAnyway.Checked)
                {
                    Log("Archive non créée. Coche « Créer le .mpk même si des scripts échouent » pour forcer.");
                    return string.Format("{0} script(s) en échec : archive non créée.", failures.Count);
                }

                var toc = new List<MpkEntry>();
                for (int i = 0; i < scxFiles.Count; i++)
                    toc.Add(new MpkEntry { Id = (uint)i, Name = Path.GetFileName(scxFiles[i]) });

                Log(string.Format("Écriture de l'archive ({0} entrées)...", toc.Count));
                string outPath = _outMpk.Text.Trim();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));

                if (File.Exists(outPath))
                {
                    string backup = outPath + ".bak";
                    try { File.Delete(backup); } catch { }
                    File.Move(outPath, backup);
                    Log("Archive existante sauvegardée : " + Path.GetFileName(backup));
                }

                Mpk.Write(outPath, toc, n => Path.Combine(work, n),
                          (done, total) => Progress(scxFiles.Count + done, scxFiles.Count * 2));

                if (_keepScx.Checked)
                {
                    string dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath)), "scx-compiles");
                    Directory.CreateDirectory(dir);
                    foreach (var e in toc) File.Copy(Path.Combine(work, e.Name), Path.Combine(dir, e.Name), true);
                    Log("Scripts compilés copiés dans : " + dir);
                }

                Log(string.Format("Terminé : {0} ({1:N0} octets).", outPath, new FileInfo(outPath).Length));
                return string.Format("Archive créée : {0} entrées, {1} compilée(s), {2} en échec.",
                                     toc.Count, compiled, failures.Count);
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { }
            }
        }

        /// <summary>Accepte cc_01_01.scx.txt comme cc_01_01.txt.</summary>
        private string FindTxt(string scxName)
        {
            string a = Path.Combine(_txtDir.Text, scxName + ".txt");
            if (File.Exists(a)) return a;
            string b = Path.Combine(_txtDir.Text, Path.GetFileNameWithoutExtension(scxName) + ".txt");
            return File.Exists(b) ? b : null;
        }

        // ------------------------------------------------------------------- divers

        private static string GuessSc3Tools()
        {
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            for (int i = 0; i < 5 && dir != null; i++)
            {
                string candidate = Path.Combine(dir, @"text-replace-extract\sc3tools.exe");
                if (File.Exists(candidate)) return candidate;
                candidate = Path.Combine(dir, @"tool\text-replace-extract\sc3tools.exe");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return "";
        }

        private void PickFolder(TextBox box, string description)
        {
            using (var dlg = new FolderBrowserDialog { Description = description, SelectedPath = box.Text })
                if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.SelectedPath;
        }

        private void PickOpenFile(TextBox box)
        {
            using (var dlg = new OpenFileDialog { Filter = "sc3tools.exe|sc3tools.exe|Programmes|*.exe" })
                if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.FileName;
        }

        private void PickSaveFile(TextBox box)
        {
            using (var dlg = new SaveFileDialog { Filter = "Archive MPK|*.mpk", FileName = "enscript.mpk", OverwritePrompt = false })
                if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.FileName;
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
                var v = File.ReadAllLines(SettingsFile);
                if (v.Length > 0) _scxDir.Text = v[0];
                if (v.Length > 1) _txtDir.Text = v[1];
                if (v.Length > 2) _outMpk.Text = v[2];
                if (v.Length > 3) _sc3.Text = v[3];
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                File.WriteAllLines(SettingsFile, new[] { _scxDir.Text, _txtDir.Text, _outMpk.Text, _sc3.Text });
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
