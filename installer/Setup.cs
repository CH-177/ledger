// Ledger's Windows installer and uninstaller: one source file, compiled twice by scripts/package.ps1
// (once as Setup with the app embedded, once with UNINSTALLER defined). It targets the .NET Framework
// that ships with Windows 10 and 11, so it runs on any PC without installing anything first.
//
// Install: per user, no administrator rights. Program files go to %LOCALAPPDATA%\Programs\Ledger;
// Ledger's data (%LOCALAPPDATA%\Ledger) is never touched, by installing, updating or uninstalling.
//
// Switches (for testing): /dir=<folder> installs elsewhere, /shortcutdir=<folder> puts shortcuts there
// instead of the desktop and Start menu, /noregistry skips the Installed apps entry, /quiet installs
// or uninstalls without any window (errors go to %TEMP%\LedgerSetup.log, exit code 1).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyCompany("Ledger")]
[assembly: AssemblyProduct("Ledger")]
[assembly: AssemblyCopyright("Ledger")]
#if UNINSTALLER
[assembly: AssemblyTitle("Ledger Uninstall")]
#else
[assembly: AssemblyTitle("Ledger Setup")]
#endif

namespace LedgerSetup
{
    internal sealed class Options
    {
        public string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ledger");
        public string ShortcutDir;
        public bool NoRegistry;
        public bool Quiet;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            foreach (var arg in args)
            {
                var a = arg.TrimStart('/', '-');
                if (a.StartsWith("dir=", StringComparison.OrdinalIgnoreCase)) o.InstallDir = Path.GetFullPath(a.Substring(4).Trim('"'));
                else if (a.StartsWith("shortcutdir=", StringComparison.OrdinalIgnoreCase)) o.ShortcutDir = Path.GetFullPath(a.Substring(12).Trim('"'));
                else if (a.Equals("noregistry", StringComparison.OrdinalIgnoreCase)) o.NoRegistry = true;
                else if (a.Equals("quiet", StringComparison.OrdinalIgnoreCase)) o.Quiet = true;
            }
            return o;
        }
    }

    internal static class Program
    {
        public const string RegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Ledger";
        public const string InfoFile = "install-info.txt";
        public const string UninstallerName = "Uninstall Ledger.exe";

        public static string DataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ledger"); }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            var options = Options.Parse(args);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
#if UNINSTALLER
                return Uninstaller.Run(options);
#else
                return Installer.Run(options);
#endif
            }
            catch (Exception ex)
            {
                return Fail(options, ex.Message, ex);
            }
        }

        public static int Fail(Options options, string message, Exception ex = null)
        {
            if (options.Quiet)
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "LedgerSetup.log"),
                    DateTime.Now.ToString("s") + "  " + message + (ex == null ? "" : Environment.NewLine + ex) + Environment.NewLine);
            }
            else
            {
                MessageBox.Show(message, "Ledger", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return 1;
        }

        /// <summary>True when Ledger is running from <paramref name="installDir"/>.</summary>
        public static bool IsLedgerRunning(string installDir)
        {
            var exe = Path.Combine(installDir, "Ledger.exe");
            foreach (var p in Process.GetProcessesByName("Ledger"))
            {
                try
                {
                    if (string.Equals(p.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception)
                {
                    // Another user's process, or one that just exited: not ours.
                }
            }
            return false;
        }

        public static Icon AppIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { return SystemIcons.Application; }
        }
    }

#if !UNINSTALLER
    internal static class Installer
    {
        public static string Version
        {
            get { return Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion; }
        }

        public static int Run(Options options)
        {
            if (options.Quiet)
            {
                if (Program.IsLedgerRunning(options.InstallDir)) return Program.Fail(options, "Ledger is running. Close it and run Setup again.");
                Install(options, _ => { });
                return 0;
            }
            Application.Run(new InstallForm(options));
            return 0;
        }

        /// <summary>The installed version, or null when Ledger isn't installed there.</summary>
        public static string InstalledVersion(string installDir)
        {
            var exe = Path.Combine(installDir, "Ledger.exe");
            if (!File.Exists(exe)) return null;
            var v = FileVersionInfo.GetVersionInfo(exe);
            return string.IsNullOrEmpty(v.ProductVersion) ? "an earlier version" : v.ProductVersion;
        }

        /// <summary>
        /// Unpacks the app beside the install folder, then swaps it in, so a failure part-way leaves
        /// the existing install as it was. Reports progress from 0 to 100.
        /// </summary>
        public static void Install(Options options, Action<int> progress)
        {
            var dir = options.InstallDir.TrimEnd('\\');
            var staging = dir + ".new";
            var old = dir + ".old";
            Directory.CreateDirectory(Path.GetDirectoryName(dir));
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            if (Directory.Exists(old)) Directory.Delete(old, true);

            long bytes = 0;
            var asm = Assembly.GetExecutingAssembly();
            using (var payload = asm.GetManifestResourceStream("payload.zip"))
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
            {
                var entries = zip.Entries.Where(e => e.Name.Length > 0).ToList();
                for (var i = 0; i < entries.Count; i++)
                {
                    var target = Path.GetFullPath(Path.Combine(staging, entries[i].FullName));
                    if (!target.StartsWith(staging + "\\", StringComparison.OrdinalIgnoreCase)) continue; // never write outside the folder
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entries[i].ExtractToFile(target, true);
                    bytes += entries[i].Length;
                    progress(i * 90 / entries.Count);
                }
            }
            using (var uninstaller = asm.GetManifestResourceStream("uninstall.exe"))
            using (var file = File.Create(Path.Combine(staging, Program.UninstallerName)))
            {
                uninstaller.CopyTo(file);
            }

            // Swap: current → .old, new → current, then remove .old (best effort).
            var hadOld = Directory.Exists(dir);
            if (hadOld) Directory.Move(dir, old);
            try
            {
                Directory.Move(staging, dir);
            }
            catch
            {
                if (hadOld) Directory.Move(old, dir);
                throw;
            }
            progress(94);
            if (hadOld)
            {
                try { Directory.Delete(old, true); } catch (Exception) { /* removed next time */ }
            }

            var exe = Path.Combine(dir, "Ledger.exe");
            var info = new List<string>();
            foreach (var folder in ShortcutFolders(options))
            {
                Directory.CreateDirectory(folder);
                var lnk = Path.Combine(folder, "Ledger.lnk");
                CreateShortcut(lnk, exe, dir);
                info.Add("shortcut=" + lnk);
            }
            progress(97);

            if (!options.NoRegistry)
            {
                using (var key = Registry.CurrentUser.CreateSubKey(Program.RegistryKey))
                {
                    key.SetValue("DisplayName", "Ledger");
                    key.SetValue("DisplayVersion", Version);
                    key.SetValue("Publisher", "Ledger");
                    key.SetValue("DisplayIcon", exe + ",0");
                    key.SetValue("InstallLocation", dir);
                    key.SetValue("UninstallString", "\"" + Path.Combine(dir, Program.UninstallerName) + "\"");
                    key.SetValue("QuietUninstallString", "\"" + Path.Combine(dir, Program.UninstallerName) + "\" /quiet");
                    key.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                }
                info.Add("registry=1");
            }
            File.WriteAllLines(Path.Combine(dir, Program.InfoFile), info);
            progress(100);
        }

        private static IEnumerable<string> ShortcutFolders(Options options)
        {
            if (options.ShortcutDir != null)
            {
                yield return options.ShortcutDir;
                yield break;
            }
            yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        }

        private static void CreateShortcut(string path, string target, string workingDir)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            var shell = Activator.CreateInstance(shellType);
            var lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            var t = lnk.GetType();
            t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
            t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workingDir });
            t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
            t.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Ledger - personal finance, kept on this PC" });
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
        }
    }

    /// <summary>The Setup window: what's about to happen, Install, progress, then Open Ledger.</summary>
    internal sealed class InstallForm : Form
    {
        private readonly Options _options;
        private readonly Label _title = new Label();
        private readonly Label _body = new Label();
        private readonly ProgressBar _progress = new ProgressBar();
        private readonly Button _primary = new Button();
        private readonly Button _secondary = new Button();
        private bool _done;

        public InstallForm(Options options)
        {
            _options = options;
            var installed = Installer.InstalledVersion(options.InstallDir);

            Text = "Ledger " + Installer.Version + " Setup";
            Icon = Program.AppIcon();
            Font = new Font("Segoe UI", 9.75f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(0xF7, 0xF5, 0xF0);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(28, 24, 28, 20);

            var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var icon = new PictureBox
            {
                Image = new Icon(Program.AppIcon(), 48, 48).ToBitmap(),
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(48, 48),
                Margin = new Padding(0, 2, 18, 0),
            };
            layout.Controls.Add(icon, 0, 0);
            layout.SetRowSpan(icon, 4);

            _title.Font = new Font("Segoe UI Semibold", 15f);
            _title.AutoSize = true;
            _title.Margin = new Padding(0, 0, 0, 10);
            _title.Text = installed == null ? "Install Ledger" : installed == Installer.Version ? "Reinstall Ledger" : "Update Ledger";
            layout.Controls.Add(_title, 1, 0);

            _body.AutoSize = true;
            _body.MaximumSize = new Size(440, 0);
            _body.Margin = new Padding(0, 0, 0, 16);
            _body.Text = installed == null
                ? "Ledger keeps track of your money on this PC. There's no account to create, and nothing you enter is ever sent anywhere.\n\n" +
                  "It installs just for you (no administrator rights needed), with shortcuts on the desktop and in the Start menu."
                : (installed == Installer.Version
                      ? "Ledger " + installed + " is already installed. Setup will reinstall it.\n\n"
                      : "Ledger " + installed + " is installed. Setup will replace it with version " + Installer.Version + ".\n\n") +
                  "Your data isn't touched, and Ledger backs it up before any change to how it's stored.";
            layout.Controls.Add(_body, 1, 1);

            _progress.Width = 440;
            _progress.Height = 6;
            _progress.Visible = false;
            _progress.Margin = new Padding(0, 0, 0, 16);
            layout.Controls.Add(_progress, 1, 2);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            foreach (var b in new[] { _primary, _secondary })
            {
                b.AutoSize = true;
                b.MinimumSize = new Size(100, 32);
                b.Padding = new Padding(10, 0, 10, 0);
                b.Margin = new Padding(8, 0, 0, 0);
                b.FlatStyle = FlatStyle.System;
                buttons.Controls.Add(b);
            }
            _primary.Text = installed == null ? "Install" : installed == Installer.Version ? "Reinstall" : "Update";
            _secondary.Text = "Cancel";
            _primary.Click += OnPrimary;
            _secondary.Click += (s, e) => Close();
            AcceptButton = _primary;
            CancelButton = _secondary;
            layout.Controls.Add(buttons, 1, 3);

            Controls.Add(layout);
        }

        private void OnPrimary(object sender, EventArgs e)
        {
            if (_done)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Path.Combine(_options.InstallDir, "Ledger.exe")) { UseShellExecute = true, WorkingDirectory = _options.InstallDir });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Ledger couldn't be started: " + ex.Message, "Ledger", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                Close();
                return;
            }
            if (Program.IsLedgerRunning(_options.InstallDir))
            {
                MessageBox.Show(this, "Ledger is open. Close it, then choose " + _primary.Text + " again.", "Ledger", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _primary.Enabled = false;
            _secondary.Enabled = false;
            _progress.Visible = true;
            _body.Text = "Installing Ledger " + Installer.Version + "…";
            var ui = SynchronizationContext.Current;
            var worker = new Thread(() =>
            {
                Exception error = null;
                try
                {
                    Installer.Install(_options, p => ui.Post(_ => _progress.Value = Math.Min(100, Math.Max(0, p)), null));
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                ui.Post(_ => Finished(error), null);
            }) { IsBackground = true };
            worker.Start();
        }

        private void Finished(Exception error)
        {
            _progress.Visible = false;
            _secondary.Enabled = true;
            if (error != null)
            {
                _title.Text = "Ledger wasn't installed";
                _body.Text = "Something went wrong, and nothing was changed: " + error.Message +
                             "\n\nMake sure Ledger is closed, then try again.";
                _primary.Enabled = true;
                _primary.Text = "Try again";
                _secondary.Text = "Close";
                return;
            }
            _done = true;
            _title.Text = "Ledger is ready";
            _body.Text = "Ledger " + Installer.Version + " is installed. Open it from the desktop or the Start menu any time.\n\n" +
                         "To remove it later, use Windows Settings → Apps → Installed apps. Your data stays on this PC unless you delete it.";
            _primary.Text = "Open Ledger";
            _primary.Enabled = true;
            _secondary.Text = "Close";
            _primary.Focus();
        }
    }
#endif

#if UNINSTALLER
    internal static class Uninstaller
    {
        public static int Run(Options options)
        {
            var dir = Path.GetDirectoryName(Application.ExecutablePath);
            // Only ever remove a folder this installer created.
            if (!File.Exists(Path.Combine(dir, "Ledger.exe")) || !File.Exists(Path.Combine(dir, Program.InfoFile)))
            {
                return Program.Fail(options, "This doesn't look like a Ledger install folder, so nothing was removed.");
            }
            if (Program.IsLedgerRunning(dir))
            {
                return Program.Fail(options, "Ledger is open. Close it, then uninstall again.");
            }
            if (!options.Quiet)
            {
                var answer = MessageBox.Show(
                    "Remove Ledger from this PC?\n\nYour financial data stays where it is (" + Program.DataDir + "), so reinstalling picks up where you left off. " +
                    "If you no longer need it, delete that folder yourself afterwards.",
                    "Uninstall Ledger", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return 0;
            }

            var registered = false;
            foreach (var line in File.ReadAllLines(Path.Combine(dir, Program.InfoFile)))
            {
                if (line.StartsWith("shortcut=", StringComparison.Ordinal))
                {
                    var lnk = line.Substring(9);
                    if (lnk.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(lnk)) File.Delete(lnk);
                }
                else if (line == "registry=1")
                {
                    registered = true;
                }
            }
            if (registered)
            {
                Registry.CurrentUser.DeleteSubKeyTree(Program.RegistryKey, false);
            }

            // Everything but this running uninstaller now; the folder itself just after it exits.
            var self = Application.ExecutablePath;
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(file, self, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(file); } catch (Exception) { /* swept up below */ }
            }
            if (!options.Quiet)
            {
                MessageBox.Show("Ledger was removed. Your data is still in " + Program.DataDir + ".", "Uninstall Ledger",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + dir + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            return 0;
        }
    }
#endif
}
