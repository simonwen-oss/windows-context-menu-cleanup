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
            // --keep-open has to hold the window on EVERY exit path (a cancelled prompt, an
            // unresolved selection, a crash), not just on the success path, so wrap the whole
            // body and pause in finally.
            try
            {
                return Run();
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("UNEXPECTED ERROR: " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
            finally
            {
                PauseIfRequested(Has(Environment.GetCommandLineArgs(), "--keep-open"));
            }
        }

        private static int Run()
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
            bool keepOpen = Has(args, "--keep-open");
            bool autoElevate = Has(args, "--auto-elevate");
            _logPath = Value(args, "--log");
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

            if (Has(args, "--selftest"))
            {
                return SelfTest();
            }

            if (Has(args, "--stdin-info"))
            {
                // Diagnose "the prompt answered itself / exited immediately" reports.
                Console.WriteLine("Console.IsInputRedirected  : " + Console.IsInputRedirected);
                Console.WriteLine("Console.IsOutputRedirected : " + Console.IsOutputRedirected);
                Console.WriteLine("stdin is a console (TTY)   : " + (SafeIsTerminal() ? "yes" : "no"));
                Console.WriteLine("stdin type                 : " + DescribeStdin());
                string probe = Console.ReadLine();
                Console.WriteLine("first ReadLine() returned  : " + (probe == null ? "(null - no input available)" : "[" + probe + "]"));
                return 0;
            }

            // --runas <args>: re-launch elevated with exactly <args> and wait.
            if (runAs != null)
            {
                if (string.Equals(runAs, "exec", StringComparison.OrdinalIgnoreCase))
                {
                    return RelaunchElevated(BuildElevatedArgs(args, select, grid), args, keepOpen, false);
                }
                return RelaunchElevated(runAs, args, keepOpen, false);
            }

            bool elevated = IsElevated();
            Header(elevated);
            Log("=== start === elevated=" + elevated + " forceWrite=" + forceWrite +
                " dryRun=" + dryRun + " yes=" + yes + " listOnly=" + listOnly +
                " grid=" + grid + " select=" + Quote(select) + " keepOpen=" + keepOpen);

            List<Entry> entries = Collect();
            List<Group> groups = Group2(entries);

            int windowsCount = 0, thirdCount = 0;
            foreach (Group g in groups) { if (g.Windows) windowsCount++; else thirdCount++; }

            Console.WriteLine("Found " + groups.Count + " component(s): " +
                              thirdCount + " third-party, " + windowsCount + " Windows built-in.");

            // Assign the slot numbers ONCE, here. Both the listing and every selection
            // path share this map, so --select N, the interactive prompt and the elevated
            // child all agree on what "N" means.
            Dictionary<int, Group> indexToGroup = NumberGroups(groups);

            if (listOnly)
            {
                Print(groups, showWindows, indexToGroup);
                return 0;
            }

            // --- elevation, decided ONCE and BEFORE any selection ------------------
            // Asking for elevation after the user has already answered a prompt is how a run
            // ends in "Aborted." and looks like "my input did nothing" (a [y/N] prompt
            // defaults to N). Ask first instead; --yes or --force-write skip the question.
            bool needElevation = !elevated && !forceWrite && !dryRun;
            if (needElevation && autoElevate && !yes)
            {
                // --auto-elevate: never ask, just re-launch. Removes the prompt that can eat a
                // typed answer in a terminal whose input timing is awkward.
                Log("auto-elevating without asking");
                string autoArgs = BuildElevatedArgs(args, sel: select, grid: grid);
                if (!Has(args, "--keep-open")) autoArgs += " --keep-open";
                return RelaunchElevated(autoArgs, args, keepOpen, true);
            }
            if (needElevation && !yes && !autoElevate)
            {
                Console.WriteLine();
                Console.WriteLine("Elevation is required to remove keys under HKLM.");
                if (!string.IsNullOrEmpty(select))
                {
                    Console.WriteLine("Your selection (" + select + ") is kept - it will be applied after re-launching,");
                    Console.WriteLine("so you will NOT have to type it again.");
                }
                else
                {
                    Console.WriteLine("The elevated window will show the same list and ask for your selection.");
                }
                string e0 = ReadLineLogged("Re-launch elevated now? [Y/n] ");
                if (e0 == null) { NoInteractiveInput(); return 1; }
                string e0t = e0.Trim();
                if (e0t.Length == 0 || e0t.StartsWith("y", StringComparison.OrdinalIgnoreCase))
                {
                    string upArgs = BuildElevatedArgs(args, sel: select, grid: grid);
                    if (!Has(args, "--keep-open")) upArgs += " --keep-open";
                    return RelaunchElevated(upArgs, args, keepOpen, true);
                }
                Console.WriteLine();
                Console.WriteLine("Continuing WITHOUT elevation - listing only; removal cannot work.");
            }

            List<Group> chosen;
            List<int> groupsPicked = new List<int>();

            if (grid)
            {
                chosen = PickGrid(groups);
                if (chosen == null || chosen.Count == 0) { Console.WriteLine("Nothing selected."); return 0; }
                foreach (int k in indexToGroup.Keys)
                {
                    if (chosen.Contains(indexToGroup[k])) groupsPicked.Add(k);
                }
            }
            else if (select != null)
            {
                chosen = PickByNumbers(select, indexToGroup, groupsPicked);
                if (chosen.Count == 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("ERROR: --select " + Quote(select) + " matched no component.");
                    Console.WriteLine("       Run --list first to see the valid numbers.");
                    return 1;
                }
            }
            else
            {
                Print(groups, showWindows, indexToGroup);
                Console.WriteLine();
                Console.WriteLine("Enter the numbers to DELETE, separated by spaces or commas.");
                Console.WriteLine("  e.g.  1 4 7    or  2,3        (empty line = cancel, 'w' = show Windows built-ins)");
                Log("about to prompt for selection; groups=" + groups.Count +
                    " numbered=" + indexToGroup.Count + " elevated=" + elevated);
                string answer = ReadLineLogged("Selection: ");
                if (answer == null)
                {
                    // null means there is NO readable stdin (redirected or detached), which is
                    // not the same as the user pressing Enter - that would be "". Bail out
                    // loudly instead of pretending the user cancelled.
                    NoInteractiveInput();
                    return 1;
                }
                if (answer.Trim().Equals("w", StringComparison.OrdinalIgnoreCase))
                {
                    Print(groups, true, indexToGroup);
                    answer = ReadLineLogged("Selection: ");
                    if (answer == null) { NoInteractiveInput(); return 1; }
                }
                Log("answer=" + Quote(answer) + " trimmed=" + Quote(answer.Trim()));
                if (answer.Trim().Length == 0)
                {
                    Console.WriteLine("Cancelled.");
                    return 0;
                }
                chosen = PickByNumbers(answer, indexToGroup, groupsPicked);
                Log("parsed groups=" + chosen.Count + " numbers=" + JoinNumbers(groupsPicked));
                if (chosen.Count == 0)
                {
                    // Never let an unparsable answer look like a quiet cancel. This also
                    // catches a paste that raced ahead of the prompt and got eaten here.
                    Console.WriteLine();
                    Console.WriteLine("ERROR: no valid number was found in that answer.");
                    Console.WriteLine("       Input was: " + Quote(answer));
                    Console.WriteLine("       Expected numbers, e.g.  1 4 7  or  2,3  (empty line cancels).");
                    Console.WriteLine("       Nothing was changed.");
                    NoInteractiveInputHint();
                    return 1;
                }
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
                // Should be unreachable: elevation was decided before the selection, or --yes
                // was given. Kept as a guard so a future edit cannot silently skip it.
                Console.WriteLine();
                Console.WriteLine("ERROR: not elevated and the early elevation step did not run.");
                Console.WriteLine("       Re-run with --yes, or from an elevated prompt.");
                return 1;
            }

            if (!yes)
            {
                Console.WriteLine();
                Console.WriteLine(">>> Type YES (three letters, upper case) and press Enter to actually delete.");
                Console.WriteLine(">>> Anything else - including just Enter - cancels. Nothing has been changed yet.");
                string c = ReadLineLogged("Type YES to confirm: ");
                if (c == null) { NoInteractiveInput(); return 1; }
                if (c.Trim() != "YES")
                {
                    Console.WriteLine("Not confirmed (you typed " + Quote(c.Trim()) + ") - nothing was changed.");
                    Console.WriteLine("To skip this confirmation entirely, add --yes to the command line.");
                    return 0;
                }
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

            PauseIfRequested(false);   // the real pause happens in Main's finally
            return bad == 0 ? 0 : 2;
        }

        // An elevated child gets its own console window, which would vanish before the user
        // could read it. --keep-open (added automatically when we spawn such a window) holds
        // it until Enter.
        private static void PauseIfRequested(bool keepOpen)
        {
            if (!keepOpen) return;
            Console.WriteLine();
            Console.Write("Press Enter to close this window...");
            try { Console.ReadLine(); } catch { }
        }

        // Distinguish "no stdin available" from "the user pressed Enter". Silently treating
        // the former as a cancel is what made an interactive run look like it exited by
        // itself while the typed characters stayed in the keyboard buffer.
        private static void NoInteractiveInput()
        {
            Console.WriteLine();
            Console.WriteLine("ERROR: no interactive input is available (stdin is redirected or detached).");
            Console.WriteLine("       Your keystrokes did NOT reach this program.");
            NoInteractiveInputHint();
        }

        private static void NoInteractiveInputHint()
        {
            Console.WriteLine("       Use a non-interactive selection instead, for example:");
            Console.WriteLine("         CtxMenuCleaner.exe --list");
            Console.WriteLine("         CtxMenuCleaner.exe --select 4 --dry-run");
            Console.WriteLine("         CtxMenuCleaner.exe --select 4 --yes");
            Console.WriteLine("       Run --stdin-info to see what this process sees on stdin.");
        }

        private static string Quote(string s)
        {
            if (s == null) return "(null)";
            if (s.Length > 120) s = s.Substring(0, 120) + "...";
            return "\"" + s + "\"";
        }

        // ------------------------------------------------- interactive tracing

        private static string _logPath;

        // Appends a timestamped line to the --log file. Never throws: tracing must not be
        // able to break the run it is trying to explain.
        private static void Log(string message)
        {
            if (string.IsNullOrEmpty(_logPath)) return;
            try
            {
                File.AppendAllText(_logPath,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch { }
        }

        // Prompt + read in one place, recording exactly what ReadLine returned and what the
        // console looked like at that moment. This is the only way to see the interactive
        // path from an environment whose stdin is redirected.
        private static string ReadLineLogged(string prompt)
        {
            bool terminal = SafeIsTerminal();
            Console.Write(prompt);
            string v = Console.ReadLine();
            Log(prompt.Trim() + " -> " + Quote(v) +
                "   [stdinTerminal=" + terminal +
                " inputRedirected=" + Console.IsInputRedirected +
                " outputRedirected=" + Console.IsOutputRedirected + "]");
            return v;
        }

        // ------------------------------------------------------------ diagnostics

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        private static bool SafeIsTerminal()
        {
            try
            {
                IntPtr h = GetStdHandle(-10);   // STD_INPUT_HANDLE
                if (h == IntPtr.Zero || h == new IntPtr(-1)) return false;
                uint mode;
                return GetConsoleMode(h, out mode);
            }
            catch { return false; }
        }

        private static string DescribeStdin()
        {
            try
            {
                IntPtr h = GetStdHandle(-10);
                if (h == IntPtr.Zero) return "invalid handle";
                if (h == new IntPtr(-1)) return "no stdin handle";
                return "handle 0x" + h.ToInt64().ToString("X");
            }
            catch (Exception ex) { return "error: " + ex.Message; }
        }

        // Diagnostic: can this process write to the registry at all? Used to distinguish
        // "our logic picked the wrong view" from "this process token cannot write".
        //
        // SIDE EFFECT: this creates one throwaway key under
        // HKCU\SOFTWARE\Classes\*\shellex\ContextMenuHandlers and removes it again. Nothing
        // else is touched, and no delete of a real key is performed.
        private static int SelfTest()
        {
            Console.WriteLine("Note: this check creates one throwaway key under HKCU and removes it again.");
            Console.WriteLine();
            Console.WriteLine("=== identity ===");
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                Console.WriteLine("  Name      : " + id.Name);
                Console.WriteLine("  Token     : " + id.Token);
                Console.WriteLine("  IsSystem  : " + id.IsSystem);
                WindowsPrincipal p = new WindowsPrincipal(id);
                Console.WriteLine("  Admin     : " + p.IsInRole(WindowsBuiltInRole.Administrator));
            }
            catch (Exception ex) { Console.WriteLine("  identity failed: " + ex.Message); }

            Console.WriteLine();
            Console.WriteLine("=== process ===");
            Console.WriteLine("  64-bit    : " + (IntPtr.Size == 8));

            string rel = "*\\shellex\\ContextMenuHandlers";
            // Deliberately unusual so it cannot collide with a real extension. Written and
            // removed wholly inside this method (see the cleanup step at the end).
            string name = "    CtxMenuCleanerWriteProbe";
            string full = "SOFTWARE\\Classes\\" + rel + "\\" + name;

            foreach (string view in new string[] { "64", "32" })
            {
                Console.WriteLine();
                Console.WriteLine("=== view " + view + " ===");
                RegistryKey bk = null;
                try { bk = Base("HKCU", view); }
                catch (Exception ex) { Console.WriteLine("  OpenBaseKey failed: " + ex.Message); continue; }

                // 1. create a throwaway key
                try
                {
                    using (RegistryKey c = bk.CreateSubKey("SOFTWARE\\Classes\\" + rel))
                    using (RegistryKey k = c.CreateSubKey(name)) { }
                    Console.WriteLine("  create            : OK");
                }
                catch (Exception ex) { Console.WriteLine("  create            : " + ex.GetType().Name + " " + ex.Message); }

                // 2. writable open of the throwaway key
                try
                {
                    using (RegistryKey x = bk.OpenSubKey(full, true))
                    {
                        Console.WriteLine("  OpenSubKey(true)  : " + (x == null ? "NULL" : "OK"));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  OpenSubKey(true)  : " + ex.GetType().Name +
                                      " HR=0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                }

                // 3. writable open of an unrelated user key for comparison
                try
                {
                    using (RegistryKey x = bk.OpenSubKey("SOFTWARE\\Classes\\*", true))
                    {
                        Console.WriteLine("  * writable        : " + (x == null ? "NULL" : "OK"));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  * writable        : " + ex.GetType().Name +
                                      " HR=0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                }

                // 4. clean up
                try
                {
                    using (RegistryKey c = bk.OpenSubKey("SOFTWARE\\Classes\\" + rel, true))
                    {
                        if (c != null) { c.DeleteSubKeyTree(name, false); Console.WriteLine("  cleanup           : OK"); }
                    }
                }
                catch (Exception ex) { Console.WriteLine("  cleanup           : " + ex.Message); }

                bk.Close();
            }

            Console.WriteLine();
            Console.WriteLine("=== RegistryView enum values ===");
            Console.WriteLine("  Registry64 = 0x" + ((int)RegistryView.Registry64).ToString("X4"));
            Console.WriteLine("  Registry32 = 0x" + ((int)RegistryView.Registry32).ToString("X4"));
            Console.WriteLine("  RegistryHive.CurrentUser = 0x" + ((int)RegistryHive.CurrentUser).ToString("X8"));
            Console.WriteLine("  RegistryHive.LocalMachine = 0x" + ((int)RegistryHive.LocalMachine).ToString("X8"));
            return 0;
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
            bool trace = Environment.GetEnvironmentVariable("CTXMENU_TRACE") == "1";
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
                            if (container == null)
                            {
                                if (trace) Console.WriteLine("[trace] " + hive + "/" + view + " " + rel + ": container NULL");
                                continue;
                            }

                            string[] names;
                            try { names = container.GetSubKeyNames(); }
                            catch { container.Close(); continue; }
                            if (trace) Console.WriteLine("[trace] " + hive + "/" + view + " " + rel + ": " + names.Length + " subkeys");

                            foreach (string real in names)
                            {
                                RegistryKey child = null;
                                try { child = container.OpenSubKey(real); }
                                catch { }
                                if (child == null)
                                {
                                    if (trace) Console.WriteLine("[trace]   child NULL for [" + real + "]");
                                    continue;
                                }

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
            catch (Exception ex)
            {
                // Silent on purpose: for HKLM keys without elevation this is expected on
                // every single entry, and printing it would bury the real output. Use
                // CTXMENU_TRACE=1 when diagnosing why a writable view was not chosen.
                if (Environment.GetEnvironmentVariable("CTXMENU_TRACE") == "1")
                    Console.WriteLine("[trace]   IsWritable(" + sub + ") -> " + ex.GetType().Name +
                                      " HR=0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                return false;
            }
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
            catch (Exception ex) { error = "OpenBaseKey: " + ex.Message; return false; }

            string sub = "SOFTWARE\\Classes\\" + e.Rel;
            RegistryKey container = null;
            try { container = baseKey.OpenSubKey(sub, true); }
            catch (Exception ex)
            {
                error = "OpenSubKey(writable) on " + sub + ": " + ex.GetType().Name +
                        " HR=0x" + ex.HResult.ToString("X8") + " " + ex.Message;
                baseKey.Close();
                return false;
            }
            if (container == null)
            {
                error = "container not found or not writable: " + e.Hive + "\\" + sub +
                        " (view " + e.View + ", writable-probe=" + e.Writable + ")";
                baseKey.Close();
                return false;
            }

            try
            {
                container.DeleteSubKeyTree(e.RealName, false);
            }
            catch (Exception ex)
            {
                error = "DeleteSubKeyTree on [" + e.RealName + "]: " + ex.GetType().Name +
                        " HR=0x" + ex.HResult.ToString("X8") + " " + ex.Message;
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
            Console.WriteLine("  --selftest       report the process identity and whether it can write");
            Console.WriteLine("                   to HKCU at all, then exit (diagnostic)");
            Console.WriteLine("  --keep-open      wait for Enter before exiting (for windows that would");
            Console.WriteLine("                   otherwise close; added automatically when self-elevating)");
            Console.WriteLine("  --stdin-info     report whether stdin can actually be read, then exit");
            Console.WriteLine("                   (use when the interactive prompt seems to answer itself)");
            Console.WriteLine("  --auto-elevate   re-launch elevated without asking (avoids a prompt that");
            Console.WriteLine("                   can consume a typed answer on some terminals)");
            Console.WriteLine("  --log <file>     append a trace of prompts and answers to <file>");
            Console.WriteLine("  --force-write    skip the elevation guard; HKLM will still fail");
            Console.WriteLine("  --version        print the version");
            Console.WriteLine("  --help           this text");
            Console.WriteLine();
            Console.WriteLine("Both 32-bit and 64-bit registry views are handled by this one process.");
            Console.WriteLine("Windows built-ins are never selected by default; --select can still");
            Console.WriteLine("target them deliberately.");
        }

        // Assign slot numbers once. Third-party components come first so that "1" is the
        // first thing a user wants to act on; Windows built-ins are numbered after them so
        // an explicit number can still reach one.
        private static Dictionary<int, Group> NumberGroups(List<Group> groups)
        {
            Dictionary<int, Group> map = new Dictionary<int, Group>();
            int index = 0;
            foreach (Group g in groups) { if (!g.Windows) { index++; map[index] = g; g.Key = index.ToString(); } }
            foreach (Group g in groups) { if (g.Windows) { index++; map[index] = g; g.Key = index.ToString(); } }
            return map;
        }

        private static string JoinNumbers(List<int> numbers)
        {
            List<string> parts = new List<string>();
            numbers.Sort();
            foreach (int n in numbers) parts.Add(n.ToString());
            return string.Join(",", parts.ToArray());
        }

        private static void Print(List<Group> groups, bool showWindows, Dictionary<int, Group> indexToGroup)
        {
            Console.WriteLine();
            if (showWindows)
            {
                Console.WriteLine("=== Windows built-ins (NOT selected unless you name them) ===");
            }

            int shown = 0;
            foreach (Group g in groups)
            {
                if (g.Windows && !showWindows) continue;
                shown++;
                string tag = g.Windows ? " (built-in)" : "";
                Console.WriteLine("  [" + g.Key.PadLeft(3) + "] " + g.Label +
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
            Console.WriteLine("(" + shown + " slot(s) shown of " + indexToGroup.Count + " total. " +
                              (showWindows
                                  ? "Windows built-ins included.)"
                                  : "Windows built-ins are numbered too but not shown; --all shows them.)"));
        }

        // Resolve a selection spec against the shared numbering. Valid numbers are recorded
        // in pickedNumbers so an elevated re-launch can replay exactly the same selection.
        private static List<Group> PickByNumbers(string spec,
                                                 Dictionary<int, Group> indexToGroup,
                                                 List<int> pickedNumbers)
        {
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
                if (indexToGroup.TryGetValue(n, out g))
                {
                    if (!picked.Contains(g)) picked.Add(g);
                    if (!pickedNumbers.Contains(n)) pickedNumbers.Add(n);
                }
                else
                {
                    Console.WriteLine("Ignoring out-of-range selection: " + n);
                }
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

        private static int RelaunchElevated(string argumentString, string[] originalArgs,
                                            bool keepOpen, bool explainTwoWindows)
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
                if (explainTwoWindows)
                {
                    Console.WriteLine();
                    Console.WriteLine("Waiting for the elevated window to finish. Approve the UAC prompt,");
                    Console.WriteLine("then read and close that window to return here.");
                }
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

        // Build the command line for the elevated instance. `sel` carries the chosen slot
        // numbers so the child does not have to ask again; `keepOpen` makes the child wait
        // for Enter in its own window, otherwise the window closes before it can be read.
        private static string BuildElevatedArgs(string[] args, string sel, bool grid)
        {
            StringBuilder sb = new StringBuilder();
            if (grid) sb.Append("--grid");
            else if (!string.IsNullOrEmpty(sel)) sb.Append("--select ").Append(sel);
            if (Has(args, "--yes")) sb.Append(" --yes");
            if (Has(args, "--no-restart")) sb.Append(" --no-restart");
            if (Has(args, "--dry-run")) sb.Append(" --dry-run");
            if (Has(args, "--no-backup")) sb.Append(" --no-backup");
            if (Has(args, "--all")) sb.Append(" --all");
            string bd = Value(args, "--backup-dir");
            if (bd != null) sb.Append(" --backup-dir \"").Append(bd).Append("\"");
            if (Has(args, "--force-write")) sb.Append(" --force-write");
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
