// Hidden WinForms fixture only. No real filter, network, process launch,
// startup registration mutation, or preference read/write is requested.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace LocalLink
{
    internal static class StartupTrayTests
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        static int assertions;

        [STAThread] static int Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                using (var form = new MainForm(false, false, false, null, false, true))
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-32000, -32000);
                    form.ShowInTaskbar = false;
                    Field("trayNoticeShown").SetValue(form, true);
                    var engine = (Engine)Field("engine").GetValue(form);
                    var tray = (NotifyIcon)Field("tray").GetValue(form);
                    Check(tray != null && tray.Visible, "startup fixture owns a visible tray icon");
                    Check(!(bool)Field("persistPreferences").GetValue(form), "test fixture cannot save preferences");
                    Check(!engine.IsRunning, "constructor does not start processing");

                    form.Show();
                    PumpUntil(delegate { return !form.Visible; }, 2000);
                    Check(!form.Visible && !form.ShowInTaskbar && !form.IsDisposed, "first startup show enters tray only");
                    Check(!engine.IsRunning && NoChild(engine), "startup creates no filter child");
                    Check(!(bool)Field("busy").GetValue(form) && Field("operation").GetValue(form) == null,
                        "startup performs no diagnostic or background operation");

                    Invoke(form, "RestoreWindow");
                    Application.DoEvents();
                    Check(form.Visible && form.ShowInTaskbar, "explicit restore shows the existing window");
                    Check(!engine.IsRunning && NoChild(engine), "restoring the window cannot enable processing");
                    PumpUntil(delegate { return false; }, 120);
                    Check(form.Visible, "startup hide is one-time and does not rehide restored window");

                    form.Close(); Application.DoEvents();
                    Check(!form.Visible && !form.IsDisposed && tray.Visible, "ordinary close preserves tray application");
                    Check(!engine.IsRunning && NoChild(engine), "ordinary close does not create a filter");
                    Invoke(form, "RestoreWindow"); Application.DoEvents();
                    Check(form.Visible, "window restores after an ordinary close");

                    FindItem(tray.ContextMenuStrip, "Выход").PerformClick(); Application.DoEvents();
                    Check(form.IsDisposed, "explicit tray Exit cleanly disposes startup window");
                    Check((bool)Field("resourcesDisposed").GetValue(form) && Field("tray").GetValue(form) == null,
                        "explicit exit releases owned tray resources");
                }
                Console.WriteLine("Startup tray: " + assertions + " assertions passed; no registry writes, preferences, real filter or network.");
                return 0;
            }
            catch (Exception error)
            {
                Console.WriteLine("Startup tray test failed: " + error.GetType().Name + " " + error.Message);
                return 1;
            }
        }

        static bool NoChild(Engine engine)
        {
            return typeof(Engine).GetField("child", Private).GetValue(engine) == null;
        }
        static FieldInfo Field(string name) { return typeof(MainForm).GetField(name, Private); }
        static void Invoke(MainForm form, string name) { typeof(MainForm).GetMethod(name, Private).Invoke(form, null); }
        static ToolStripItem FindItem(ContextMenuStrip menu, string text)
        {
            foreach (ToolStripItem item in menu.Items) if (item.Text == text) return item;
            throw new InvalidOperationException("Tray command missing.");
        }
        static void PumpUntil(Func<bool> condition, int milliseconds)
        {
            var clock = Stopwatch.StartNew();
            while (!condition() && clock.ElapsedMilliseconds < milliseconds)
            {
                Application.DoEvents(); Thread.Sleep(15);
            }
        }
        static void Check(bool result, string label)
        {
            assertions++;
            if (!result) throw new InvalidOperationException(label);
            Console.WriteLine("PASS " + label);
        }
    }
}
