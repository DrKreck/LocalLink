// Own WinForms layout regression test. Preview forms only: no filter, tray,
// network actions, user preference writes, or changes to the OS display scale.
// Control.Scale plus explicit point-font scaling is a geometric simulation;
// it is NOT a native per-monitor-DPI or visual glyph-rendering certification.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using LocalLink;

internal static class TypographyLayoutTests
{
    static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    static readonly List<string> failures = new List<string>();
    static int assertions, fixtures, pages;

    [STAThread] static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Console.WriteLine("Typography/layout: isolated preview controls; geometric/font scale simulation, NOT native DPI.");
        foreach (bool dark in new[] { false, true })
            foreach (bool minimum in new[] { false, true })
                foreach (float factor in new[] { 1f, 1.25f, 1.5f, 2f })
                    RunFixture(dark, minimum, factor);
        Console.WriteLine("Typography/layout: " + assertions + " assertions, " + fixtures + " fixtures, " + pages + " page passes, " + failures.Count + " failures.");
        int limit = Math.Min(80, failures.Count);
        for (int index = 0; index < limit; index++) Console.WriteLine("FAIL " + failures[index]);
        if (failures.Count > limit) Console.WriteLine("Further failures omitted: " + (failures.Count - limit));
        Console.WriteLine("No real filter/network, production UI, OS-DPI changes, or preference writes.");
        return failures.Count == 0 ? 0 : 1;
    }

    static void RunFixture(bool dark, bool minimum, float factor)
    {
        string fixture = (dark ? "dark" : "light") + "/" + (minimum ? "minimum" : "normal") + "/" + (int)(factor * 100) + "%";
        var createdFonts = new List<Font>();
        try
        {
            using (var form = new MainForm(true, dark, !dark, null, false))
            {
                fixtures++;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-32000, -32000);
                form.ShowInTaskbar = false;
                if (minimum) form.Size = form.MinimumSize;
                form.Show(); Application.DoEvents();
                // Disable automatic DPI/font scaling for this fixture only. First
                // scale geometry/styles/padding once, then scale explicit fonts once.
                // This avoids changing any screen's actual Windows DPI settings.
                form.AutoScaleMode = AutoScaleMode.None;
                var controls = Flatten(form);
                var fonts = new Dictionary<Control, Font>();
                foreach (var control in controls) fonts[control] = control.Font;
                form.SuspendLayout();
                form.Scale(new SizeF(factor, factor));
                foreach (var control in controls)
                {
                    Font source = fonts[control];
                    var font = new Font(source.FontFamily, source.SizeInPoints * factor, source.Style, GraphicsUnit.Point);
                    createdFonts.Add(font); control.Font = font;
                }
                form.ResumeLayout(true); form.PerformLayout(); Application.DoEvents();
                var engine = (Engine)Field("engine").GetValue(form);
                Check(!engine.IsRunning, fixture + ": preview engine remains stopped");
                Check(Field("tray").GetValue(form) == null, fixture + ": preview has no tray icon");
                foreach (var control in controls)
                {
                    bool icon = (control.Name == "SettingsAction" || control.Name == "CloseSettings") && String.Equals(control.Font.FontFamily.Name, "Segoe MDL2 Assets", StringComparison.OrdinalIgnoreCase);
                    Check(icon || String.Equals(control.Font.FontFamily.Name, "Segoe UI", StringComparison.OrdinalIgnoreCase), fixture + ": content font or settings icon font " + Name(control));
                }
                var shell = (AppShell)Field("shell").GetValue(form);
                shell.CloseSettings();
                CheckPage(form, engine, fixture + "/main");
                shell.OpenSettings();
                CheckPage(form, engine, fixture + "/settings");
                foreach(string section in new[]{"applications","profiles","updates"})
                {
                    typeof(MainForm).GetMethod("SelectSettingsSection",Private).Invoke(form,new object[]{section});
                    CheckPage(form,engine,fixture+"/settings-"+section);
                }
                typeof(MainForm).GetMethod("SetAdvancedSettings", Private).Invoke(form, new object[] { true });
                Check(((Panel)Field("advancedSettings").GetValue(form)).Visible, fixture + ": advanced settings expanded");
                CheckPage(form, engine, fixture + "/settings-expanded");
                var showDetails = (CheckBox)Field("showDetails").GetValue(form);
                showDetails.Checked = true;
                Check(showDetails.Checked, fixture + ": diagnostic details expanded");
                CheckPage(form, engine, fixture + "/settings-details");
                shell.CloseSettings();
                CheckPage(form, engine, fixture + "/main-details");
            }
        }
        catch (Exception error) { Check(false, fixture + ": fixture exception " + error.GetType().Name + " " + error.Message); }
        finally { foreach (var font in createdFonts) font.Dispose(); }
    }

    static void CheckPage(MainForm form, Engine engine, string scope)
    {
        form.PerformLayout(); Application.DoEvents();
        pages++;
        using (var graphics = form.CreateGraphics())
        {
            foreach (var control in Flatten(form))
            {
                if (!control.Visible || control.Width <= 0 || control.Height <= 0) continue;
                CheckText(control, graphics, scope);
                CheckChildren(control, scope);
            }
        }
        Check(!engine.IsRunning, scope + ": no engine launched by layout/settings/disclosure");
    }

    static void CheckText(Control control, Graphics graphics, string scope)
    {
        if (String.IsNullOrWhiteSpace(control.Text)) return;
        var label = control as Label;
        if (label != null)
        {
            int width = Math.Max(1, label.ClientSize.Width - label.Padding.Horizontal);
            int height = Math.Max(0, label.ClientSize.Height - label.Padding.Vertical);
            Size needed = TextRenderer.MeasureText(graphics, label.Text, label.Font, new Size(width, Int32.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            Check(needed.Height <= height + 2, scope + ": label height " + Name(control) + " needs=" + needed.Height + " available=" + height);
            // Headings, sidebar captions, and card-field labels are not intended to
            // hide their own names. Paragraph labels are allowed to wrap normally.
            if (label.AutoEllipsis || IsCardHeading(label))
            {
                Size line = TextRenderer.MeasureText(graphics, label.Text, label.Font, Size.Empty,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                Check(line.Width <= width + 2, scope + ": heading width " + Name(control) + " needs=" + line.Width + " available=" + width);
            }
        }
        var check = control as ThemedCheckBox;
        if (check != null)
        {
            Size preferred = check.GetPreferredSize(new Size(check.ClientSize.Width, 0));
            Check(preferred.Height <= check.ClientSize.Height + 2, scope + ": checkbox preferred height " + Name(control) + " needs=" + preferred.Height + " available=" + check.ClientSize.Height);
            Check(preferred.Width <= check.ClientSize.Width + 2, scope + ": checkbox preferred width " + Name(control) + " needs=" + preferred.Width + " available=" + check.ClientSize.Width);
        }
        var button = control as ThemedButton;
        if (button != null)
        {
            bool navigation = (button.Tag as string) == "nav" || (button.Tag as string) == "nav_selected";
            int width = Math.Max(1, button.ClientSize.Width - (navigation ? 30 : 12));
            Size needed = TextRenderer.MeasureText(graphics, button.Text, button.Font, Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            Check(needed.Height <= button.ClientSize.Height + 2, scope + ": button height " + Name(control));
            Check(needed.Width <= width + 2, scope + ": button width " + Name(control) + " needs=" + needed.Width + " available=" + width);
        }
        var combo = control as ThemedComboBox;
        if (combo != null)
        {
            Check(combo.ItemHeight >= combo.Font.Height, scope + ": combo item height " + Name(control));
            Check(combo.ClientSize.Height >= combo.Font.Height + 2, scope + ": combo face height " + Name(control));
        }
    }

    static bool IsCardHeading(Label label)
    {
        var cell = label.Parent as TableLayoutPanel;
        return cell != null && cell.Parent is ThemedPanel && cell.GetRow(label) == 0;
    }

    static void CheckChildren(Control container, string scope)
    {
        if (!(container is TableLayoutPanel) && !(container is FlowLayoutPanel) && !(container is ThemedPanel)) return;
        var scrollable = container as ScrollableControl;
        if (scrollable != null && scrollable.AutoScroll) return;
        Rectangle bounds = container.DisplayRectangle;
        bounds.Inflate(2, 2); // Tolerate one-pixel fractional scaling/rounding edges.
        var visible = new List<Control>();
        foreach (Control child in container.Controls)
        {
            if (!child.Visible || child.Width <= 0 || child.Height <= 0) continue;
            visible.Add(child);
            Check(bounds.Contains(child.Bounds), scope + ": child bounds " + Name(child) + " in " + Name(container) + " child=" + child.Bounds + " parent=" + container.DisplayRectangle);
        }
        for (int left = 0; left < visible.Count; left++)
            for (int right = left + 1; right < visible.Count; right++)
            {
                Rectangle intersection = Rectangle.Intersect(visible[left].Bounds, visible[right].Bounds);
                Check(intersection.Width <= 2 || intersection.Height <= 2, scope + ": sibling overlap " + Name(visible[left]) + " / " + Name(visible[right]));
            }
    }

    static List<Control> Flatten(Control root)
    {
        var result = new List<Control>(); AddControls(root, result); return result;
    }
    static void AddControls(Control root, List<Control> output)
    {
        output.Add(root); foreach (Control child in root.Controls) AddControls(child, output);
    }
    static FieldInfo Field(string name) { return typeof(MainForm).GetField(name, Private); }
    static string Name(Control control)
    {
        string text = String.IsNullOrWhiteSpace(control.Text) ? "" : " '" + control.Text.Replace('\r', ' ').Replace('\n', ' ') + "'";
        if (text.Length > 100) text = text.Substring(0, 100) + "...";
        return control.GetType().Name + text;
    }
    static void Check(bool result, string message)
    {
        assertions++; if (!result) failures.Add(message);
    }
}
