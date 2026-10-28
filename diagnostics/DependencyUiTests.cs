using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using LocalLink;

internal static class DependencyUiTests
{
    static int checks;

    [STAThread] static int Main()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            foreach (string appearance in new[] { "dark", "light" })
                foreach (bool retry in new[] { false, true })
                    foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
                        CheckPrompt(appearance, retry, scale);
            CheckPayloadIsolation();
            Console.WriteLine("Dependency UI: " + checks + " assertions passed; isolated confirmation forms and payload fixtures only, no network, user settings, filter or downloaded process.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); checks++; }

    static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control control in root.Controls)
        {
            yield return control;
            foreach (Control nested in Descendants(control)) yield return nested;
        }
    }

    static void CheckPrompt(string appearance, bool retry, float scale)
    {
        var palette = AppTheme.Read(appearance);
        string label = appearance + " retry=" + retry + " scale=" + scale;
        using (var prompt = new SetupPrompt(palette, retry))
        {
            prompt.StartPosition = FormStartPosition.Manual;
            prompt.Location = new Point(-30000, -30000);
            prompt.ShowInTaskbar = false;
            if (scale != 1f) prompt.Scale(new SizeF(scale, scale));
            prompt.Show();
            prompt.PerformLayout();
            Application.DoEvents();
            var controls = Descendants(prompt).ToArray();
            var buttons = controls.OfType<Button>().ToArray();
            Check(buttons.Length == 2, label + ": exactly two choices");
            var install = buttons.SingleOrDefault(b => b.AccessibleName == "Установить компоненты");
            var later = buttons.SingleOrDefault(b => b.AccessibleName == "Не сейчас");
            Check(install != null && later != null, label + ": accessible install and defer actions");
            Check(install.DialogResult == DialogResult.Yes && later.DialogResult == DialogResult.No, label + ": consent uses explicit Yes/No");
            Check(Object.ReferenceEquals(prompt.AcceptButton, install), label + ": Enter confirms installation");
            Check(Object.ReferenceEquals(prompt.CancelButton, later), label + ": Escape defers installation");
            Check(install.Enabled && later.Enabled && install.Visible && later.Visible, label + ": both choices are usable");
            Check(!install.Bounds.IntersectsWith(later.Bounds) || install.Parent != later.Parent, label + ": action buttons do not overlap");

            var labels = controls.OfType<Label>().Where(c => c.Visible).ToArray();
            string copy = String.Join(" ", labels.Select(c => c.Text));
            Check(copy.Length > 10 && copy.Length <= 340, label + ": explanation stays short");
            Check(copy.Contains("Сетевой модуль нужен для всей работы LocalLink."), label + ": network module is required for the entire app");
            Check(copy.Contains("Модуль Telegram — дополнительный."), label + ": Telegram module is clearly additional");
            Check(copy.Contains("Подписал специально для Kenkyusha"), label + ": requested caption is present");
            foreach (string unwanted in new[] { "PowerShell", "Setup-Engine", "README", "Без регистрации", "не является VPN", "не гарантирует" })
                Check(copy.IndexOf(unwanted, StringComparison.OrdinalIgnoreCase) < 0, label + ": no manual setup or warning wall: " + unwanted);

            foreach (Control control in controls.Where(c => c.Visible))
            {
                Check(control.Width > 0 && control.Height > 0, label + ": non-empty control " + control.GetType().Name);
                Check(control.Left >= -1 && control.Top >= -1 && control.Right <= control.Parent.ClientSize.Width + 1 && control.Bottom <= control.Parent.ClientSize.Height + 1,
                    label + ": control contained in parent: " + control.GetType().Name + " " + control.Bounds);
                if (appearance == "dark" && !palette.HighContrast && control.BackColor.A != 0)
                    Check(control.BackColor.GetBrightness() < .7f, label + ": no white control surface: " + control.GetType().Name);
            }
            foreach (Button button in buttons)
            {
                Size required = TextRenderer.MeasureText(button.Text, button.Font, new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                Check(required.Width <= button.ClientSize.Width - 6 && required.Height <= button.ClientSize.Height,
                    label + ": button caption fits: " + button.Text);
            }
            foreach (Label text in labels)
            {
                Size required = TextRenderer.MeasureText(text.Text, text.Font, new Size(Math.Max(1, text.ClientSize.Width), Int32.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding);
                Check(required.Height <= text.ClientSize.Height + 4, label + ": explanation fits without clipping");
            }
            for (int i = 0; i < labels.Length; i++)
                foreach (Control other in controls.Where(c => c.Visible && !Object.ReferenceEquals(c, labels[i]) && c.Parent == labels[i].Parent))
                    Check(!labels[i].Bounds.IntersectsWith(other.Bounds), label + ": explanation and caption do not overlap controls");
            Check(prompt.DialogResult == DialogResult.None, label + ": showing the dialog never implies consent");
            later.PerformClick();
            Check(prompt.DialogResult == DialogResult.No, label + ": defer remains non-installing");
            prompt.Close();
        }
        using (var prompt = new SetupPrompt(palette, retry))
        {
            prompt.StartPosition = FormStartPosition.Manual;
            prompt.Location = new Point(-30000, -30000);
            prompt.ShowInTaskbar = false;
            prompt.Show();
            Application.DoEvents();
            var install = Descendants(prompt).OfType<Button>().Single(b => b.AccessibleName == "Установить компоненты");
            install.PerformClick();
            Check(prompt.DialogResult == DialogResult.Yes, label + ": install explicitly confirms consent");
            prompt.Close();
        }
    }

    static string Prepare(string folder, byte[] launcher, byte[] script, byte[] config)
    {
        var method = typeof(BootstrapPayload).GetMethod("PrepareResources", BindingFlags.NonPublic | BindingFlags.Static);
        Check(method != null, "payload test seam exists");
        try { return (string)method.Invoke(null, new object[] { folder, launcher, script, config }); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    static void CheckPayloadIsolation()
    {
        string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bootstrap-payload-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        byte[] first = Encoding.UTF8.GetBytes("SYNTHETIC LAUNCHER ONE - NEVER EXECUTE"), second = Encoding.UTF8.GetBytes("SYNTHETIC LAUNCHER TWO - NEVER EXECUTE");
        byte[] script = Encoding.UTF8.GetBytes("# Synthetic script; never execute"), config = Encoding.UTF8.GetBytes("<configuration/>");
        string oldAlias = Path.Combine(folder, "LocalLink.Telegram.exe"), oldAliasConfig = oldAlias + ".config";
        File.WriteAllBytes(oldAlias, first); File.WriteAllBytes(oldAliasConfig, config);
        string state = Path.Combine(folder, "telegram-state.json"); File.WriteAllText(state, "SYNTHETIC STATE");
        string path = Prepare(folder, first, script, config);
        Check(path != oldAlias && Path.GetFileName(path).StartsWith("LocalLink.Telegram-", StringComparison.Ordinal), "content identity does not use or overwrite legacy alias");
        Check(Path.GetFileName(path).Length == "LocalLink.Telegram-".Length + 16 + ".exe".Length, "payload name has bounded content identity");
        Check(File.ReadAllBytes(path).SequenceEqual(first), "prepared launcher bytes exact");
        Check(File.ReadAllBytes(path + ".config").SequenceEqual(config), "configuration follows actual executable name");
        Check(File.ReadAllBytes(Path.Combine(folder, "bridge.py")).SequenceEqual(script), "script prepared beside runtime-relative launcher");
        DateTime written = File.GetLastWriteTimeUtc(path), configWritten = File.GetLastWriteTimeUtc(path + ".config");
        int files = Directory.GetFiles(folder).Length;
        Check(Prepare(folder, first, script, config) == path, "same component content returns same launcher");
        Check(File.GetLastWriteTimeUtc(path) == written && File.GetLastWriteTimeUtc(path + ".config") == configWritten, "unchanged files are not rewritten");
        Check(Directory.GetFiles(folder).Length == files && Directory.GetFiles(folder, "*.previous-*").Length == 0, "repeated preparation creates no extra payloads or backups");
        string next;
        using (var legacyLock = new FileStream(oldAlias, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var activeLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var configLock = new FileStream(path + ".config", FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Check(Prepare(folder, first, script, config) == path, "identical locked launcher remains reusable");
            next = Prepare(folder, second, script, config);
            Check(next != path && File.ReadAllBytes(next).SequenceEqual(second), "new launcher prepares without replacing active executable");
            Check(File.ReadAllBytes(path).SequenceEqual(first) && File.ReadAllBytes(oldAlias).SequenceEqual(first), "active and legacy launcher bytes untouched");
            byte[] changedConfig = Encoding.UTF8.GetBytes("<configuration><startup/></configuration>");
            string withConfig = Prepare(folder, first, script, changedConfig);
            Check(withConfig != path && File.ReadAllBytes(withConfig + ".config").SequenceEqual(changedConfig), "config-only update gets separate companion identity");
            Check(File.ReadAllBytes(path + ".config").SequenceEqual(config), "active old configuration never overwritten");
        }
        File.WriteAllBytes(next, new byte[] { 0 });
        Check(Prepare(folder, second, script, config) == next && File.ReadAllBytes(next).SequenceEqual(second), "corrupt unlocked payload repaired from embedded bytes");
        Check(Directory.GetFiles(folder, Path.GetFileName(next) + ".previous-*").Length == 1, "corrupt prior file retained for rollback");
        File.Delete(next + ".config"); Prepare(folder, second, script, config);
        Check(File.ReadAllBytes(next + ".config").SequenceEqual(config), "missing companion configuration repaired");
        Check(File.ReadAllText(state) == "SYNTHETIC STATE", "component preparation does not touch Telegram user state");
        Check(Directory.GetFiles(folder, "*.new-*").Length == 0, "no unfinished payload stages remain");
    }
}
