using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
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
            Console.WriteLine("Dependency UI: " + checks + " assertions passed; isolated confirmation forms only, no network, settings, filter or downloaded process.");
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
}
