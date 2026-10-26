// Own WinForms component integration test, no real network/filter or input injection.
// Uses a benign waiting child in Engine's existing process job. Preferences never save.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using LocalLink;

internal static class TrayLifecycleTests
{
    static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    static int assertions;
    [STAThread] static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--benign-wait") { Thread.Sleep(60000); return 0; }
        if (args.Length == 1 && args[0] == "--benign-exit") { Thread.Sleep(150); return 7; }
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        bool owns;
        using (var mutex = new Mutex(true, @"Local\LocalLink.TrayFixture." + Guid.NewGuid().ToString("N"), out owns))
        {
            if (!owns) throw new InvalidOperationException("Fixture mutex collision.");
            try
            {
                using (var restore = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LocalLink.RestoreFixture." + Guid.NewGuid().ToString("N")))
                using (var form = new MainForm(false, false, false, restore, false))
                {
                    Check(restore != null, "isolated restore signal created");
                    form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-32000, -32000); form.ShowInTaskbar = false;
                    // Keep fixture UI off screen and suppress a test-only notification.
                    Field("trayNoticeShown").SetValue(form, true);
                    form.Show(); Application.DoEvents();
                    var engine = (Engine)Field("engine").GetValue(form);
                    var deck = (AppShell)Field("shell").GetValue(form);
                    var tray = (NotifyIcon)Field("tray").GetValue(form);
                    Check(tray != null && tray.Visible, "tray icon exists");
                    LaunchBenign(engine, "--benign-exit");
                    PumpUntil(delegate { return !engine.IsRunning; }, 4000);
                    typeof(MainForm).GetMethod("InspectEngineExit", Private).Invoke(form, null); Refresh(form);
                    Check(!engine.IsRunning && (bool)Field("engineFailure").GetValue(form), "unexpected own child exit marks failure without restart");
                    Check(((Label)Field("status").GetValue(form)).Text == "Обработка остановлена", "unexpected exit visible in main status");
                    Check(((Label)Field("status").GetValue(form)).ForeColor == AppTheme.Read("system").Error, "unexpected exit uses error color");
                    Check(((TextBoxBase)Field("log").GetValue(form)).Text.Contains("код 7"), "safe exit code retained in local UI log");
                    Check(engine.TakeUnexpectedExit() == null, "exit notification consumed once by UI");
                    typeof(MainForm).GetMethod("StopOperation", Private).Invoke(form, null); Refresh(form);
                    Check(!(bool)Field("engineFailure").GetValue(form) && ((Label)Field("status").GetValue(form)).Text == "Фильтр выключен", "explicit Stop dismisses failure");
                    LaunchBenign(engine, "--benign-exit");
                    // Observe the exit without pumping the UI timer, then Stop.
                    var stopClock = Stopwatch.StartNew();
                    while (engine.IsRunning && stopClock.ElapsedMilliseconds < 4000) Thread.Sleep(20);
                    typeof(MainForm).GetMethod("StopOperation", Private).Invoke(form, null);
                    typeof(MainForm).GetMethod("InspectEngineExit", Private).Invoke(form, null);
                    Check(!(bool)Field("engineFailure").GetValue(form), "Stop does not reclassify pending exit on next timer tick");
                    LaunchBenign(engine, "--benign-exit");
                    stopClock.Restart();
                    while (engine.IsRunning && stopClock.ElapsedMilliseconds < 4000) Thread.Sleep(20);
                    typeof(MainForm).GetMethod("ResetEngineFailureForNewStart", Private).Invoke(form, null);
                    typeof(MainForm).GetMethod("Begin", Private).Invoke(form, new object[] { "Подготовка" });
                    var retryOperation = (CancellationTokenSource)Field("operation").GetValue(form);
                    PumpUntil(delegate { return retryOperation.IsCancellationRequested; }, 500);
                    Check(!retryOperation.IsCancellationRequested && !(bool)Field("engineFailure").GetValue(form), "previous child exit never cancels a new operation on timer tick");
                    typeof(MainForm).GetMethod("End", Private).Invoke(form, null);
                    LaunchBenign(engine); Refresh(form);
                    var themeChild = (Process)typeof(Engine).GetField("child", Private).GetValue(engine);
                    int themePid = themeChild.Id;
                    deck.OpenSettings(); Application.DoEvents();
                    Check(deck.SettingsOpen && !deck.MainContent.Enabled, "settings page opens while own benign child is running");
                    var settingsEscape = new KeyEventArgs(Keys.Escape);
                    typeof(Form).GetMethod("OnKeyDown", Private).Invoke(form, new object[] { settingsEscape });
                    Check(!deck.SettingsOpen && settingsEscape.Handled, "Escape leaves settings page");
                    Check(engine.IsRunning && ((Process)typeof(Engine).GetField("child", Private).GetValue(engine)).Id == themePid,
                        "leaving settings preserves exact owned child");
                    string[] appearanceValues = { "system", "light", "dark" };
                    for (int index = 0; index < appearanceValues.Length; index++)
                    {
                        deck.Appearance.SelectedIndex = index; Application.DoEvents();
                        Check(form.BackColor == AppTheme.Read(appearanceValues[index]).Background, "manual appearance applied: " + appearanceValues[index]);
                        var captured = (AppSettings)typeof(MainForm).GetMethod("CaptureSettings", Private).Invoke(form, null);
                        Check(captured.Appearance == appearanceValues[index], "appearance captured without disk writes: " + appearanceValues[index]);
                        Check(engine.IsRunning && ((Process)typeof(Engine).GetField("child", Private).GetValue(engine)).Id == themePid, "theme switch preserves own child: " + appearanceValues[index]);
                    }
                    if (!SystemInformation.HighContrast)
                    {
                        Check(AppTheme.Read("dark").Background != AppTheme.Read("light").Background, "explicit light and dark palettes differ");
                        foreach (string value in new[] { "light", "dark" })
                        {
                            var palette = AppTheme.Read(value);
                            Color[] surfaces = { palette.Background, palette.Surface, palette.Card, palette.Input, palette.Hover, palette.Selected, palette.Pressed };
                            for (int surface = 0; surface < surfaces.Length; surface++)
                            {
                                Check(AppTheme.ContrastRatio(palette.Text, surfaces[surface]) >= 4.5, "text contrast: " + value + " surface " + surface);
                                Check(AppTheme.ContrastRatio(palette.Muted, surfaces[surface]) >= 4.5, "secondary contrast: " + value + " surface " + surface);
                                Check(AppTheme.ContrastRatio(palette.ControlBorder, surfaces[surface]) >= 3, "control outline contrast: " + value + " surface " + surface);
                            }
                            Check(AppTheme.ContrastRatio(palette.Accent, palette.Card) >= 3 && AppTheme.ContrastRatio(palette.Accent, palette.Selected) >= 3 && AppTheme.ContrastRatio(palette.Accent, palette.Pressed) >= 3, "focus accent contrast: " + value);
                            Check(tray.ContextMenuStrip.BackColor == AppTheme.Read(appearanceValues[2]).Surface, "tray palette follows last selected theme: " + value);
                        }
                    }
                    var inMemorySettings = (AppSettings)Field("settings").GetValue(form);
                    inMemorySettings.Strategy = "split";
                    ((ComboBox)Field("strategy").GetValue(form)).SelectedIndex = 2;
                    deck.Appearance.SelectedIndex = 1; Application.DoEvents();
                    Check(inMemorySettings.Strategy == "split" && inMemorySettings.Appearance == "light", "appearance-only change preserves network preference drafts");
                    deck.TrayAction.PerformClick(); Application.DoEvents();
                    Check(!form.Visible && !form.IsDisposed, "tray button hides instead of exiting");
                    Check(engine.IsRunning, "tray button preserves own benign child");
                    restore.Set();
                    PumpUntil(delegate { return form.Visible; }, 4000);
                    Check(form.Visible, "isolated injected restore signal shows own fixture window");
                    Check(engine.IsRunning, "isolated restore preserves own benign child");
                    form.Close(); Application.DoEvents();
                    Check(!form.Visible && !form.IsDisposed, "window close hides instead of exiting");
                    Check(engine.IsRunning, "window close preserves own benign child");
                    var key = new KeyEventArgs(Keys.Escape);
                    typeof(Form).GetMethod("OnKeyDown", Private).Invoke(form, new object[] { key });
                    Check(key.Handled && key.SuppressKeyPress && engine.IsRunning, "Escape preserves processing");
                    FindItem(tray.ContextMenuStrip, "Остановить обработку").PerformClick(); Application.DoEvents();
                    Check(!engine.IsRunning && !form.IsDisposed, "tray Stop stops only child and keeps UI");
                    LaunchBenign(engine); Refresh(form);
                    var owned = (Process)typeof(Engine).GetField("child", Private).GetValue(engine);
                    using (var witness = Process.GetProcessById(owned.Id))
                    {
                        FindItem(tray.ContextMenuStrip, "Выход").PerformClick(); Application.DoEvents();
                        Check(form.IsDisposed, "tray Exit disposes window");
                        Check(witness.HasExited || witness.WaitForExit(3000), "tray Exit terminates own benign child");
                    }
                }
                Console.WriteLine("Tray lifecycle: " + assertions + " assertions passed. Only unique fixture mutex/event and own child processes; no production restore signal, filter/network or preferences. Production duplicate-launch path not exercised.");
                return 0;
            }
            catch (Exception error) { Console.WriteLine("Tray test failed: " + error.GetType().Name); return 1; }
            finally { mutex.ReleaseMutex(); }
        }
    }
    static FieldInfo Field(string name) { return typeof(MainForm).GetField(name, Private); }
    static void Refresh(MainForm form) { typeof(MainForm).GetMethod("RefreshControls", Private).Invoke(form, null); }
    static void LaunchBenign(Engine engine, string mode = "--benign-wait")
    {
        typeof(Engine).GetMethod("LaunchOwnedProcess", Private).Invoke(engine, new object[] {
            Assembly.GetExecutingAssembly().Location, new List<string> { mode }, AppDomain.CurrentDomain.BaseDirectory });
    }
    static ToolStripItem FindItem(ContextMenuStrip menu, string text)
    {
        foreach (ToolStripItem item in menu.Items) if (item.Text == text) return item;
        throw new InvalidOperationException();
    }
    static void PumpUntil(Func<bool> condition, int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(20); }
    }
    static void Check(bool result, string label)
    {
        assertions++; if (!result) throw new InvalidOperationException(); Console.WriteLine("PASS " + label);
    }
}
