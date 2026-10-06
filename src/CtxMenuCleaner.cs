// CtxMenuCleaner - list and remove Windows Explorer context-menu entries.
//
// Compiled with the .NET Framework C# 5 compiler that ships with Windows
// (C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe), so no SDK and no
// PowerShell are required to build or to run it.
//
// Why a native executable instead of the PowerShell scripts:
//   PowerShell 5.1 cannot read the 32-bit registry view and the 64-bit view from one
//   process (RegistryKey.OpenBaseKey does not exist there), which forced a fragile
//   child-process + JSON workaround. C# has OpenBaseKey, so both views are read
//   directly and there is no PowerShell version dependency at all.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CtxMenuCleaner
{
    internal sealed class Entry
    {
        public string Hive;          // HKLM / HKCU
        public string ClassKey;      // *, Directory, Directory\Background, ...
        public string Kind;          // shell | shellex\ContextMenuHandlers
        public string Rel;           // ClassKey + "\" + Kind
        public string RealName;      // true registry key name (may be space-padded)
        public string RawKey;        // full path for reg.exe
        public string View;          // 32 | 64
        public string Target;        // resolved DLL or command line
        public string Shape;         // verb | com
        public bool Padded;
        public string Logical;       // hive + rel + name: identifies the key across views
        public bool Writable;        // can this view actually delete the key?
    }

    internal sealed class Group
    {
        public string Key;
        public string Label;
        public string Detail;
        public bool Windows;
        public List<Entry> Entries = new List<Entry>();
    }

    internal static class Program
    {
        private const string Product = "CtxMenuCleaner";
        private const string Version = "1.0.0";

        private static readonly string[] ClassKeys = new string[]
        {
            "*", "AllFilesystemObjects", "Directory", @"Directory\Background",
            "Folder", "Drive", "DesktopBackground", "lnkfile", "exefile"
        };

        private static readonly string[] Kinds = new string[]
        {
            "shell", @"shellex\ContextMenuHandlers"
        };

        // ---------------------------------------------------------------- main

        private static int Main()
        {
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch { /* console may not support it; not fatal */ }

            string[] args = Environment.GetCommandLineArgs();

            bool listOnly = Has(args, "--list") || Has(args, "-l");
            bool grid = Has(args, "--grid") || Has(args, "-g");
            bool dryRun = Has(args, "--dry-run");
            bool noRestart = Has(args, "--no-restart");
            bool yes = Has(args, "--yes") || Has(args, "-y");
            bool noBackup = Has(args, "--no-backup");
            bool showWindows = Has(args, "--all");
            bool forceWrite = Has(args, "--force-write");
            string backupDir = Value(args, "--backup-dir");
            string select = Value(args, "--select");
            string runAs = Value(args, "--runas");

            if (Has(args, "--help") || Has(args, "-h") || Has(args, "/?"))
            {
                Usage();
                return 0;
            }

            if (Has(args, "--version"))
            {
                Console.WriteLine(Product + " " + Version);
                return 0;
            }

            // --runas <args>: re-launch elevated and forward <args>. The literal token
            // "exec" means "arguments arrive below", which is not the case; treat any
            // caller that only passed the marker as a request to rebuild from argv.
            if (runAs != null)
            {
                if (string.Equals(runAs, "exec", StringComparison.OrdinalIgnoreCase))
                {
                    return RelaunchElevated(RebuildArgs(args, select, grid), args);
                }
                return RelaunchElevated(runAs, args);
            }

            bool elevated = IsElevated();
            Header(elevated);

            List<Entry> entries = Collect();
            List<Group> groups = Group2(entries);

            int windowsCount = 0, thirdCount = 0;
            foreach (Group g in groups) { if (g.Windows) windowsCount++; else thirdCount++; }

            Console.WriteLine("Found " + groups.Count + " component(s): " +
                              thirdCount + " third-party, " + windowsCount + " Windows built-in.");

            if (listOnly)
            {
                Print(groups, showWindows);
                return 0;
            }

            List<Group> chosen;

            if (grid)
            {
                chosen = PickGrid(groups);
                if (chosen == null || chosen.Count == 0) { Console.WriteLine("Nothing selected."); return 0; }
            }
            else if (select != null)
            {
                chosen = PickByNumbers(groups, select);
                if (chosen.Count == 0) { Console.WriteLine("Nothing selected."); return 0; }
            }
            else
            {
                Print(groups, showWindows);
                Console.WriteLine();
                Console.WriteLine("Enter the numbers to DELETE, separated by spaces or commas.");
                Console.WriteLine("  e.g.  1 4 7    or  2,3        (empty = cancel, 'w' = show Windows built-ins)");
                Console.Write("Selection: ");
                string answer = Console.ReadLine();
                if (answer != null && answer.Trim().Equals("w", StringComparison.OrdinalIgnoreCase))
                {
                    Print(groups, true);
                    Console.Write("Selection: ");
                    answer = Console.ReadLine();
                }
                if (string.IsNullOrEmpty(answer) || answer.Trim().Length == 0)
                {
                    Console.WriteLine("Cancelled.");
                    return 0;
                }
                chosen = PickByNumbers(groups, answer);
                if (chosen.Count == 0) { Console.WriteLine("Nothing selected."); return 0; }
            }

            List<Entry> victims = new List<Entry>();
            bool hitWindows = false;
            foreach (Group g in chosen)
            {
                if (g.Windows) { hitWindows = true; }
                victims.AddRange(g.Entries);
            }

            Console.WriteLine();
            Console.WriteLine("About to remove " + victims.Count + " registry key(s) from " + chosen.Count + " component(s).");
            if (hitWindows)
            {
                Console.WriteLine("WARNING: your selection includes a Windows built-in component.");
                Console.WriteLine("         Removing it can break Explorer. Continue only if you are sure.");
            }

            if (dryRun)
            {
                Console.WriteLine("Dry run: nothing was deleted.");
                return 0;
            }

            if (!elevated && !forceWrite)
            {
                Console.WriteLine();
                Console.WriteLine("Administrator rights are required (HKLM keys cannot be removed otherwise).");
                Console.WriteLine("Will run: " + SelfPath() + " " + RebuildArgs(args, select, grid));
                Console.Write("Re-launch elevated now? [y/N]: ");
                string a = Console.ReadLine();
                if (a != null && a.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase))
                {
                    return RelaunchElevated(RebuildArgs(args, select, grid), args);
                }
                Console.WriteLine("Aborted.");
                return 1;
            }

            if (!yes)
            {
                Console.Write("Type YES to confirm: ");
                string c = Console.ReadLine();
                if (c == null || c.Trim() != "YES") { Console.WriteLine("Aborted."); return 0; }
            }

            if (backupDir == null || backupDir.Length == 0)
            {
                backupDir = Path.Combine(Environment.CurrentDirectory,
                    "context-menu-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            }

            int ok = 0, bad = 0;
            foreach (Entry e in victims)
            {
                Console.WriteLine();
                Console.WriteLine("Deleting " + e.Hive + "\\" + e.Rel + "\\" + e.RealName);
                Console.WriteLine("        view " + e.View + "-bit   raw: " + e.RawKey + "   shape=" + e.Shape + "   target=" + (e.Target == null ? "(none)" : e.Target));
                if (!noBackup)
                {
                    string file = Export(e, backupDir);
                    if (file != null) Console.WriteLine("        backup -> " + file);
                }
                string err;
                if (DeleteKey(e, out err))
                {
                    Console.WriteLine("        -> DELETED");
                    ok++;
                }
                else
                {
                    Console.WriteLine("        -> FAILED: " + err);
                    bad++;
                }
            }

            Console.WriteLine();
            Console.WriteLine("Deleted " + ok + ", failed " + bad + ".");
            if (!noBackup) Console.WriteLine("Backups in " + backupDir);

            if (!noRestart)
            {
                Console.WriteLine();
                Console.WriteLine("Restarting Explorer so the menu refreshes...");
                RestartExplorer();
            }

            return bad == 0 ? 0 : 2;
        }

        // ------------------------------------------------------------ registry

        private static RegistryKey Base(string hive, string view)
        {
            RegistryHive h = hive == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            RegistryView v = view == "32" ? RegistryView.Registry32 : RegistryView.Registry64;
            return RegistryKey.OpenBaseKey(h, v);
        }

        private static List<Entry> Collect()
        {
            List<Entry> all = new List<Entry>();
            string[] hives = new string[] { "HKCU", "HKLM" };
            string[] views = new string[] { "64", "32" };

            foreach (string hive in hives)
            {
                foreach (string view in views)
                {
                    RegistryKey baseKey;
                    try { baseKey = Base(hive, view); }
                    catch { continue; }
                    if (baseKey == null) continue;

                    foreach (string cls in ClassKeys)
                    {
                        foreach (string kind in Kinds)
                        {
                            string rel = cls + "\\" + kind;
                            RegistryKey container = null;
                            try { container = baseKey.OpenSubKey("SOFTWARE\\Classes\\" + rel); }
                            catch { }
                            if (container == null) continue;

                            string[] names;
                            try { names = container.GetSubKeyNames(); }
                            catch { container.Close(); continue; }

                            foreach (string real in names)
                            {
                                RegistryKey child = null;
                                try { child = container.OpenSubKey(real); }
                                catch { }
                                if (child == null) continue;

                                Entry e = new Entry();
                                e.Hive = hive;
                                e.View = view;
                                e.ClassKey = cls;
                                e.Kind = kind;
                                e.Rel = rel;
                                e.RealName = real;
                                e.RawKey = hive + "\\SOFTWARE\\Classes\\" + rel + "\\" + real;
                                e.Padded = real != real.Trim();
                                e.Logical = hive + "|" + rel + "|" + real;
                                e.Writable = IsWritable(baseKey, "SOFTWARE\\Classes\\" + rel + "\\" + real);

                                string def = null;
                                try { object o = child.GetValue(null); if (o != null) def = o.ToString(); }
                                catch { }

                                if (kind == "shell")
                                {
                                    e.Shape = "verb";
                                    RegistryKey cmd = null;
                                    try { cmd = child.OpenSubKey("command"); } catch { }
                                    if (cmd != null)
                                    {
                                        try { object o = cmd.GetValue(null); if (o != null) e.Target = o.ToString(); }
                                        catch { }
                                        cmd.Close();
                                    }
                                }
                                else
                                {
                                    e.Shape = "com";
                                    if (def != null && def.StartsWith("{"))
                                    {
                                        string dll = ResolveClsid(baseKey, def);
                                        if (dll != null) e.Target = dll;
                                    }
                                    if (e.Target == null) e.Target = def;
                                }

                                child.Close();
                                all.Add(e);
                            }
                            container.Close();
                        }
                    }
                    baseKey.Close();
                }
            }
            return Dedupe(all);
        }

        private static string ResolveClsid(RegistryKey baseKey, string clsid)
        {
            RegistryKey k = null;
            try { k = baseKey.OpenSubKey("SOFTWARE\\Classes\\CLSID\\" + clsid + "\\InProcServer32"); }
            catch { }
            if (k == null) return null;
            string result = null;
            try { object o = k.GetValue(null); if (o != null) result = o.ToString(); }
            catch { }
            k.Close();
            return result;
        }

        // HKCU\SOFTWARE\Classes is a single store that BOTH registry views see, so the
        // same key is discovered twice. Only one of the two views can delete it (on a
        // 64-bit OS the 32-bit view of that store is readable but not writable), and
        // deleting through the read-only view throws SecurityException. Probe for write
        // access here so the caller can pick the usable entry.
        private static bool IsWritable(RegistryKey baseKey, string sub)
        {
            try
            {
                RegistryKey k = baseKey.OpenSubKey(sub, true);
                if (k == null) return false;
                k.Close();
                return true;
            }
            catch { return false; }
        }

        // Collapse entries that denote the same underlying key, preferring writable ones.
        private static List<Entry> Dedupe(List<Entry> all)
        {
            List<Entry> result = new List<Entry>();
            Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (Entry e in all)
            {
                int at;
                if (!index.TryGetValue(e.Logical, out at))
                {
                    index[e.Logical] = result.Count;
                    result.Add(e);
                    continue;
                }
                Entry kept = result[at];
                if (!kept.Writable && e.Writable) result[at] = e;
                else if (kept.Writable == e.Writable && e.View == "64" && kept.View != "64") result[at] = e;
            }
            return result;
        }

        private static bool DeleteKey(Entry e, out string error)
        {
            error = null;
            RegistryKey baseKey;
            try { baseKey = Base(e.Hive, e.View); }
            catch (Exception ex) { error = ex.Message; return false; }

            string sub = "SOFTWARE\\Classes\\" + e.Rel;
            RegistryKey container = null;
            try { container = baseKey.OpenSubKey(sub, true); }
            catch (Exception ex) { error = ex.Message; baseKey.Close(); return false; }
            if (container == null) { error = "container not found"; baseKey.Close(); return false; }

            try
            {
                container.DeleteSubKeyTree(e.RealName, false);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                container.Close();
                baseKey.Close();
                return false;
            }
            container.Close();

            // verify
            RegistryKey check = null;
            try { check = baseKey.OpenSubKey(sub + "\\" + e.RealName); }
            catch { }
            bool gone = check == null;
            if (check != null) check.Close();
            baseKey.Close();

            if (!gone) { error = "still present after delete"; return false; }
            return true;
        }

        private static string Export(Entry e, string backupDir)
        {
            try { Directory.CreateDirectory(backupDir); }
            catch { return null; }

            string safe = Sanitize(e.Hive + "_" + e.ClassKey + "_" + e.Kind + "_" + e.RealName);
            string file = Path.Combine(backupDir, safe + ".reg");
            string regView = e.View == "32" ? "/reg:32" : "/reg:64";

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("reg.exe");
                psi.Arguments = "export \"" + e.RawKey + "\" \"" + file + "\" /y " + regView;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Process p = Process.Start(psi);
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
            }
            catch { return null; }

            return File.Exists(file) ? file : null;
        }

        private static string Sanitize(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-') sb.Append(c);
                else sb.Append('_');
            }
            return sb.ToString();
        }

        // -------------------------------------------------------- classifying

        private static bool IsWindowsTarget(string t)
        {
            if (t == null || t.Trim().Length == 0) return true;   // unknown -> treat as system
            string s = t.Trim();

            if (s.StartsWith("{")) return true;                   // unresolved CLSID
            if (s.StartsWith("@")) return true;                   // module-relative, e.g. @shell32.dll,-8506
            if (s.StartsWith("\"") && s.Length > 1)
            {
                int q = s.IndexOf('"', 1);
                if (q > 0) s = s.Substring(1, q - 1);
            }
            else
            {
                int sp2 = s.IndexOf(' ');
                if (sp2 > 0) s = s.Substring(0, sp2);
            }

            if (s.Length == 0) return true;
            if (s.IndexOf("%SystemRoot%", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (s.IndexOf("%windir%", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            string lower = s.ToLowerInvariant();
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant();
            if (lower.StartsWith(winDir)) return true;

            // Component shipped by Windows or its own vendors, but living elsewhere.
            string[] known =
            {
                "\\program files\\windows defender\\",
                "\\program files\\windowsapps\\",
                "\\program files\\nvidia corporation\\",
                "\\program files\\microsoft onedrive\\",
                "\\program files (x86)\\windows defender\\",
                "\\program files (x86)\\nvidia corporation\\"
            };
            foreach (string k in known) if (lower.IndexOf(k) >= 0) return true;

            if (s.IndexOf('\\') < 0) return true;                 // bare name -> resolved from System32

            string leaf = Path.GetFileName(lower);
            string[] builtinExe =
            {
                "cmd.exe", "powershell.exe", "pwsh.exe", "explorer.exe", "control.exe",
                "rundll32.exe", "cscript.exe", "wscript.exe", "bdeunlock.exe",
                "bitlockerwizard.exe", "bitlockerwizardelev.exe", "bdechangepin.exe"
            };
            foreach (string b in builtinExe) if (leaf == b) return true;

            return false;
        }

        // ------------------------------------------------------------- groups

        private static string GroupKey(Entry e)
        {
            if (e.Target != null && e.Target.Trim().Length > 0)
            {
                if (e.Shape == "verb") return "verb:" + e.Target.Trim() + "|" + e.RealName.Trim();
                return "dll:" + e.Target;
            }
            return "name:" + e.RealName.Trim();
        }

        private static string GroupLabel(Entry e)
        {
            if (e.Target != null && e.Target.Trim().Length > 0)
            {
                if (e.Shape == "verb")
                {
                    string exe = FirstToken(e.Target);
                    return Path.GetFileName(exe) + "  [" + e.RealName.Trim() + "]";
                }
                string leaf = Path.GetFileName(e.Target);
                return leaf.Length > 0 ? leaf : e.Target;
            }
            return "[" + e.RealName.Trim() + "]";
        }

        private static string FirstToken(string cmd)
        {
            string s = cmd.Trim();
            if (s.StartsWith("\""))
            {
                int q = s.IndexOf('"', 1);
                if (q > 0) return s.Substring(1, q - 1);
            }
            int sp = s.IndexOf(' ');
            return sp > 0 ? s.Substring(0, sp) : s;
        }

        private static List<Group> Group2(List<Entry> entries)
        {
            List<Group> groups = new List<Group>();
            Dictionary<string, Group> map = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);

            foreach (Entry e in entries)
            {
                string k = GroupKey(e);
                Group g;
                if (!map.TryGetValue(k, out g))
                {
                    g = new Group();
                    g.Key = k;
                    g.Label = GroupLabel(e);
                    g.Windows = IsWindowsTarget(e.Target);
                    if (e.Target != null && e.Target.Trim().Length > 0)
                        g.Detail = e.Shape == "verb" ? ("runs: " + e.Target) : ("from: " + SafeDir(e.Target));
                    else
                        g.Detail = null;
                    map[k] = g;
                    groups.Add(g);
                }
                g.Entries.Add(e);
                if (g.Windows && !IsWindowsTarget(e.Target)) g.Windows = false;
            }

            // third-party first (that is what users want to act on), then Windows
            List<Group> ordered = new List<Group>();
            foreach (Group g in groups) if (!g.Windows) ordered.Add(g);
            foreach (Group g in groups) if (g.Windows) ordered.Add(g);
            return ordered;
        }

        private static string SafeDir(string path)
        {
            try
            {
                string d = Path.GetDirectoryName(path);
                return d == null ? path : d;
            }
            catch { return path; }
        }

        // -------------------------------------------------------------- output

        private static void Header(bool elevated)
        {
            Console.WriteLine();
            Console.WriteLine("==========================================================");
            Console.WriteLine(" " + Product + " " + Version +
                              "    64-bit process: " + (IntPtr.Size == 8) +
                              "    Elevated: " + elevated);
            Console.WriteLine("==========================================================");
        }

        private static void Usage()
        {
            Console.WriteLine(Product + " " + Version + " - clean up Explorer right-click menu entries");
            Console.WriteLine();
            Console.WriteLine("  CtxMenuCleaner.exe                     interactive: list, then pick numbers");
            Console.WriteLine("  CtxMenuCleaner.exe --list              list only (no admin needed)");
            Console.WriteLine("  CtxMenuCleaner.exe --all               include Windows built-ins in the list");
            Console.WriteLine("  CtxMenuCleaner.exe --grid              pick in a searchable window");
            Console.WriteLine("  CtxMenuCleaner.exe --select 3,7        remove those numbers");
            Console.WriteLine("  CtxMenuCleaner.exe --dry-run           show what would happen");
            Console.WriteLine();
            Console.WriteLine("  --yes            skip the YES confirmation");
            Console.WriteLine("  --no-backup      do not export .reg backups (not recommended)");
            Console.WriteLine("  --backup-dir D   where to write backups");
            Console.WriteLine("  --no-restart     do not restart Explorer afterwards");
            Console.WriteLine("  --show-elevate   show the elevated command instead of running it");
            Console.WriteLine("  --version        print the version");
            Console.WriteLine("  --help           this text");
            Console.WriteLine();
            Console.WriteLine("Both 32-bit and 64-bit registry views are handled by this one process.");
            Console.WriteLine("Windows built-ins are never selected by default; --select can still");
            Console.WriteLine("target them deliberately.");
        }

        private static void Print(List<Group> groups, bool showWindows)
        {
            int index = 0;
            Console.WriteLine();
            if (showWindows)
            {
                Console.WriteLine("=== Windows built-ins (NOT selected unless you name them) ===");
            }

            foreach (Group g in groups)
            {
                if (g.Windows && !showWindows) continue;
                index++;
                g.Key = index.ToString();
                string tag = g.Windows ? " (built-in)" : "";
                Console.WriteLine("  [" + index.ToString().PadLeft(3) + "] " + g.Label +
                                  "   (" + g.Entries.Count + " registration(s))" + tag);
                if (g.Detail != null) Console.WriteLine("        " + g.Detail);
                foreach (Entry e in g.Entries)
                {
                    string shape = e.Shape == "verb" ? "static verb" : "COM handler ";
                    string pad = e.Padded ? "   [padded key name]" : "";
                    Console.WriteLine("        " + e.ClassKey.PadRight(24) + shape + "  " +
                                      e.View + "-bit " + e.Hive + pad);
                }
            }
            Console.WriteLine();
            if (showWindows)
            {
                Console.WriteLine("(" + index + " slot(s) listed; Windows built-ins included.)");
            }
            else
            {
                Console.WriteLine("(" + index + " slot(s) listed. Windows built-ins are hidden unless --all is used.)");
            }
        }

        private static List<Group> PickByNumbers(List<Group> groups, string spec)
        {
            // Re-index exactly like Print() does.
            int index = 0;
            Dictionary<int, Group> byNum = new Dictionary<int, Group>();
            foreach (Group g in groups)
            {
                // only third-party groups are numbered unless the user asked for --all
                if (g.Windows) continue;
                index++;
                byNum[index] = g;
            }
            // number the Windows groups after the third-party ones so an explicit
            // number can still reach them once --all was printed
            foreach (Group g in groups)
            {
                if (!g.Windows) continue;
                index++;
                byNum[index] = g;
            }

            List<Group> picked = new List<Group>();
            string[] parts = spec.Split(new char[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string p in parts)
            {
                int n;
                if (!int.TryParse(p, out n))
                {
                    Console.WriteLine("Ignoring unparsable selection: " + p);
                    continue;
                }
                Group g;
                if (byNum.TryGetValue(n, out g)) picked.Add(g);
                else Console.WriteLine("Ignoring out-of-range selection: " + n);
            }
            return picked;
        }

        private static List<Group> PickGrid(List<Group> groups)
        {
            try
            {
                ListView view = new ListView();
                view.View = View.Details;
                view.CheckBoxes = true;
                view.FullRowSelect = true;
                view.GridLines = true;
                view.Width = 900;
                view.Height = 520;
                view.Columns.Add("Component", 260);
                view.Columns.Add("Kind", 110);
                view.Columns.Add("Registrations", 90);
                view.Columns.Add("Source", 420);

                foreach (Group g in groups)
                {
                    string kind = g.Windows ? "Windows" : "third-party";
                    ListViewItem it = new ListViewItem(new string[]
                    {
                        g.Label, kind, g.Entries.Count.ToString(), g.Detail == null ? "" : g.Detail
                    });
                    it.Tag = g;
                    view.Items.Add(it);
                }

                Button ok = new Button();
                ok.Text = "Delete checked";
                ok.DialogResult = DialogResult.OK;
                ok.Top = 530;
                ok.Left = 700;
                ok.Width = 120;

                Button cancel = new Button();
                cancel.Text = "Cancel";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.Top = 530;
                cancel.Left = 560;
                cancel.Width = 120;

                Form form = new Form();
                form.Text = Product + " - tick the components to remove";
                form.Width = 940;
                form.Height = 610;
                form.StartPosition = FormStartPosition.CenterScreen;
                view.Left = 10; view.Top = 10;
                form.Controls.Add(view);
                form.Controls.Add(ok);
                form.Controls.Add(cancel);
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                DialogResult r = form.ShowDialog();
                if (r != DialogResult.OK) return null;

                List<Group> picked = new List<Group>();
                foreach (ListViewItem it in view.CheckedItems) picked.Add((Group)it.Tag);
                return picked;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Could not open the graphical picker: " + ex.Message);
                return null;
            }
        }

        // ---------------------------------------------------------- elevation

        private static bool IsElevated()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                WindowsPrincipal p = new WindowsPrincipal(id);
                return p.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static int RelaunchElevated(string argumentString, string[] originalArgs)
        {
            if (Has(originalArgs, "--show-elevate"))
            {
                Console.WriteLine("Would re-launch with:");
                Console.WriteLine("  exe     : " + SelfPath());
                Console.WriteLine("  args    : " + argumentString);
                Console.WriteLine("  verb    : runas");
                return 0;
            }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(SelfPath());
                psi.Arguments = argumentString;
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process p = Process.Start(psi);
                p.WaitForExit();
                return p.ExitCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Elevation failed or was declined: " + ex.Message);
                return 1;
            }
        }

        // Prefer the executable's own path; MainModule can be unreadable or carry the
        // non-ASCII characters of the folder it sits in.
        private static string SelfPath()
        {
            try
            {
                string p = Process.GetCurrentProcess().MainModule.FileName;
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            }
            catch { }
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Product + ".exe");
        }

        private static string RebuildArgs(string[] args, string select, bool grid)
        {
            StringBuilder sb = new StringBuilder();
            if (grid) sb.Append("--grid");
            else if (select != null) sb.Append("--select ").Append(select);
            if (Has(args, "--yes")) sb.Append(" --yes");
            if (Has(args, "--no-restart")) sb.Append(" --no-restart");
            if (Has(args, "--dry-run")) sb.Append(" --dry-run");
            if (Has(args, "--no-backup")) sb.Append(" --no-backup");
            if (Has(args, "--all")) sb.Append(" --all");
            string bd = Value(args, "--backup-dir");
            if (bd != null) sb.Append(" --backup-dir \"").Append(bd).Append("\"");
            return sb.ToString().Trim();
        }

        // ------------------------------------------------------------ helpers

        private static void RestartExplorer()
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName("explorer"))
                {
                    try { p.Kill(); } catch { }
                }
                System.Threading.Thread.Sleep(2000);

                bool running = Process.GetProcessesByName("explorer").Length > 0;
                if (!running)
                {
                    string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    Process.Start(Path.Combine(winDir, "explorer.exe"));
                    System.Threading.Thread.Sleep(2000);
                }

                Process[] now = Process.GetProcessesByName("explorer");
                if (now.Length > 0) Console.WriteLine("Explorer is running (pid " + now[0].Id + ").");
                else Console.WriteLine("WARNING: Explorer is not running; start it from Task Manager.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Could not restart Explorer automatically: " + ex.Message);
                Console.WriteLine("Start it manually with: explorer.exe");
            }
        }

        private static bool Has(string[] args, string flag)
        {
            foreach (string a in args)
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string Value(string[] args, string flag)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) return args[i + 1];
                    return null;
                }
                if (args[i].StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
                {
                    return args[i].Substring(flag.Length + 1);
                }
            }
            return null;
        }
    }
}
