// Native synthetic forms and own immutable list fixture only. No filter, network,
// dependency setup, user settings or operating-system settings are changed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using LocalLink;

internal static class DomainManagerUiTests
{
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    static int checks;

    [STAThread] static int Main()
    {
        try
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs error) { Console.Error.WriteLine("Unhandled native UI callback: " + error.Exception); Environment.Exit(2); };
            Control.CheckForIllegalCrossThreadCalls = true;
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string path = Fixture();
            foreach (string appearance in new[] { "dark", "light" })
                foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
                    CheckDialog(path, appearance, scale);
            CheckSearchAndSelection(path);
            CheckMainForm();
            Console.WriteLine("Domain manager UI: " + checks + " assertions passed; native synthetic forms only, no network, filters, dependency setup or user preference writes.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static void Check(bool value, string message)
    { if (!value) { Console.Error.WriteLine("ASSERTION FAILED: " + message); throw new Exception(message); } checks++; }
    static T Value<T>(object target, string field)
    { return (T)target.GetType().GetField(field, Private).GetValue(target); }
    static void Invoke(object target, string method,params object[] args)
    { target.GetType().GetMethod(method, Private).Invoke(target, args); }
    static void Wait(Func<bool> ready, string message)
    {
        var watch = Stopwatch.StartNew();
        do { PumpEvents(); if (ready()) { checks++; return; } Thread.Sleep(10); } while (watch.ElapsedMilliseconds < 12000);
        throw new Exception(message + " (timeout)");
    }
    static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { PumpEvents(); Thread.Sleep(5); }
    }
    static void Ready(DomainManagerForm form)
    { Wait(() => !Value<bool>(form, "loading") && !Value<System.Windows.Forms.Timer>(form, "debounce").Enabled, "search did not finish: query=" + Value<TextBox>(form, "search").Text + " offset=" + Value<int>(form, "offset") + " status=" + Value<Label>(form, "count").Text); }
    static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        { yield return child; foreach (Control nested in Descendants(child)) yield return nested; }
    }
    static void Show(Form form)
    {
        form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000);
        form.Show(); PumpEvents(); form.PerformLayout();
        // The fixture intentionally has no Application.Run. DoEvents restores
        // its caller's base context, so install the same STA continuation
        // context a normal WinForms message loop gives real click handlers.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
    }
    static void PumpEvents()
    {
        Application.DoEvents();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
    }
    static void Screenshot(Form form, string name)
    {
        string folder = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "screenshots")); Directory.CreateDirectory(folder);
        using (var bitmap = new Bitmap(form.Width, form.Height))
        { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(folder, name + ".png"), System.Drawing.Imaging.ImageFormat.Png); }
    }
    static bool IsInside(Control control, Control parent)
    { for (Control value = control; value != null; value = value.Parent) if (Object.ReferenceEquals(value, parent)) return true; return false; }

    static string Fixture()
    {
        string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lists"); Directory.CreateDirectory(folder);
        string name = "sites-domain-ui-" + Guid.NewGuid().ToString("N") + ".lst";
        string path = Path.Combine(folder, name);
        var rows = new SortedSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 451; i++) rows.Add("domain" + i.ToString("D4") + ".org");
        rows.Add("child.parent.org"); rows.Add("parent.org"); rows.Add("media.discord.com");
        var encoding = new UTF8Encoding(false, true);
        byte[] bytes = encoding.GetBytes(String.Join("\n", rows) + "\n");
        File.WriteAllBytes(path, bytes);
        string hash; using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        File.WriteAllText(path + ".json", "{\"FileName\":\"" + name + "\",\"Sha256\":\"" + hash + "\",\"DomainCount\":" + rows.Count + ",\"SourceCount\":0,\"RejectedLines\":0,\"Sources\":[],\"UpdatedUtc\":\"2026-10-28T00:00:00Z\"}", encoding);
        return path;
    }

    static AppSettings Settings()
    { return new AppSettings { UsePublicLists = true, CustomDomains = "custom.org", DisabledDomains = new List<string> { "parent.org" } }; }

    static void CheckDialog(string path, string appearance, float scale)
    {
        string label = appearance + " scale=" + scale;
        var original = Settings(); var palette = AppTheme.Read(appearance);
        using (var form = new DomainManagerForm(original, path, palette))
        {
            if (scale != 1f) form.Scale(new SizeF(scale, scale));
            Show(form); Ready(form);
            var controls = Descendants(form).Where(x => x.Visible).ToArray();
            Check(form.DialogResult == DialogResult.None, label + ": showing does not apply");
            Check(Value<ListView>(form, "domains").Items.Count <= DomainSelection.MaximumPageSize, label + ": bounded visible rows");
            Check(Value<TextBox>(form, "search").AccessibleName == "Поиск домена", label + ": accessible search");
            Check(Value<Dictionary<string, CheckBox>>(form, "categories").Count == DomainSelection.Categories().Count, label + ": category coverage");
            foreach (var control in controls)
            {
                Check(control.Width > 0 && control.Height > 0, label + ": non-empty " + control.GetType().Name);
                Check(control.Left >= -1 && control.Top >= -1 && control.Right <= control.Parent.ClientSize.Width + 1 && control.Bottom <= control.Parent.ClientSize.Height + 1,
                    label + ": contained " + control.GetType().Name + " " + control.Bounds);
                if (appearance == "dark" && !palette.HighContrast && control.BackColor.A != 0)
                    Check(control.BackColor.GetBrightness() < .7f, label + ": no white surface " + control.GetType().Name);
            }
            foreach (var button in controls.OfType<Button>())
            {
                Size needed = TextRenderer.MeasureText(button.Text, button.Font, new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                Check(needed.Width <= button.ClientSize.Width - 6 && needed.Height <= button.ClientSize.Height, label + ": caption fits " + button.Text);
                foreach (var other in controls.Where(x => x.Parent == button.Parent && !Object.ReferenceEquals(x, button)))
                    Check(!button.Bounds.IntersectsWith(other.Bounds), label + ": action does not overlap " + button.Text);
            }
            foreach (var checkbox in controls.OfType<CheckBox>())
            {
                Size needed = TextRenderer.MeasureText(checkbox.Text, checkbox.Font, new Size(Math.Max(1, checkbox.ClientSize.Width - 30), Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                Check(needed.Height <= checkbox.ClientSize.Height, label + ": category label fits " + checkbox.Text);
            }
            if (scale == 1f) Screenshot(form, "domain-manager-" + appearance);
            var categories = Value<Dictionary<string, CheckBox>>(form, "categories");
            categories["discord"].Checked = false;
            Check(original.Services.Contains("discord"), label + ": category draft does not mutate input");
            Check(!form.Selection.Services.Contains("discord"), label + ": draft records category change");
            var clone = form.Selection; clone.DisabledDomains.Clear(); clone.Services.Clear();
            Check(form.Selection.DisabledDomains.Contains("parent.org") && form.Selection.Services.Count > 0, label + ": Selection is defensive copy");
            var cancel = controls.OfType<Button>().Single(x => x.DialogResult == DialogResult.Cancel); cancel.PerformClick();
            Check(form.DialogResult == DialogResult.Cancel && original.Services.Contains("discord"), label + ": cancel preserves original");
            form.Close(); Pump(20);
        }
    }

    static void SelectFilter(DomainManagerForm form, string id)
    {
        var filter = Value<ComboBox>(form, "categoryFilter");
        for (int i = 0; i < filter.Items.Count; i++)
            if ((string)filter.Items[i].GetType().GetProperty("Id").GetValue(filter.Items[i], null) == id) { filter.SelectedIndex = i; Ready(form); return; }
        throw new Exception("missing category filter " + id);
    }
    static void Search(DomainManagerForm form, string text)
    { Value<TextBox>(form, "search").Text = text; Ready(form); }
    static ListViewItem SelectRow(DomainManagerForm form, string domain)
    {
        var rows = Value<ListView>(form, "domains");
        foreach (ListViewItem item in rows.Items) item.Selected = false;
        var result = rows.Items.Cast<ListViewItem>().Single(x => x.Text == domain); result.Selected = true; rows.Focus(); PumpEvents();
        return result;
    }

    static void CheckSearchAndSelection(string path)
    {
        var original = Settings();
        using (var form = new DomainManagerForm(original, path, AppTheme.Read("dark")))
        {
            try
            {
            Console.WriteLine("Workflow: initial form");
            Show(form); Ready(form); SelectFilter(form, "public"); Search(form, "domain");
            var rows = Value<ListView>(form, "domains"); var next = Value<Button>(form, "next"); var previous = Value<Button>(form, "previous");
            Check(rows.Items.Count == 200 && next.Enabled && !previous.Enabled, "first page 200 with next only: rows=" + rows.Items.Count + " next=" + next.Enabled + " previous=" + previous.Enabled + " status=" + Value<Label>(form, "count").Text);
            Check(rows.Items[0].Text == "domain0000.org", "first-page ordering");
            Console.WriteLine("Workflow: second page; UI thread=" + Thread.CurrentThread.ManagedThreadId + "; context=" + (SynchronizationContext.Current == null ? "null" : SynchronizationContext.Current.GetType().FullName)); next.PerformClick(); Ready(form); Check(rows.Items.Count == 200 && rows.Items[0].Text == "domain0200.org" && previous.Enabled, "second page");
            next.PerformClick(); Ready(form); Check(rows.Items.Count == 51 && !next.Enabled && rows.Items[0].Text == "domain0400.org", "last bounded page");
            previous.PerformClick(); Ready(form); Check(rows.Items[0].Text == "domain0200.org", "previous page");
            Console.WriteLine("Workflow: individual domain"); Search(form, "domain0007"); Check(rows.Items.Count == 1 && !previous.Enabled, "new query resets pagination");
            SelectRow(form, "domain0007.org"); Check(Value<Button>(form, "disable").Enabled, "active domain can be disabled");
            Value<Button>(form, "disable").PerformClick(); Ready(form);
            Check(form.Selection.DisabledDomains.Contains("domain0007.org") && !original.DisabledDomains.Contains("domain0007.org"), "individual exclusion remains draft");
            SelectRow(form, "domain0007.org"); Check(Value<Button>(form, "enable").Enabled, "explicit exclusion can be re-enabled");
            Value<Button>(form, "enable").PerformClick(); Ready(form); Check(!form.Selection.DisabledDomains.Contains("domain0007.org"), "individual restore");
            Console.WriteLine("Workflow: inherited domain"); Search(form, "child.parent"); SelectRow(form, "child.parent.org");
            Check(!Value<Button>(form, "enable").Enabled && !Value<Button>(form, "disable").Enabled, "inherited child cannot override parent rule");
            Search(form, "domain0007"); Value<Dictionary<string, CheckBox>>(form, "categories")["public"].Checked = false; Ready(form);
            SelectRow(form, "domain0007.org");
            Check(!Value<Button>(form, "enable").Enabled && !Value<Button>(form, "disable").Enabled, "disabled category cannot be overridden by a child");
            Check(rows.Items[0].SubItems[2].Text.Contains("Категория"), "disabled category visibly distinguished");
            var categories = Value<Dictionary<string, CheckBox>>(form, "categories"); categories["public"].Checked = true; Ready(form);
            Console.WriteLine("Workflow: all categories"); string[] exclusions = form.Selection.DisabledDomains.ToArray();
            Value<Button>(form, "disableAllCategories").PerformClick(); Ready(form);
            Check(categories.Values.All(x => !x.Checked), "all categories off button updates every checkbox");
            Check(form.Selection.Services.Count == 0 && !form.Selection.UsePublicLists && !form.Selection.UseCustomDomains, "all categories off updates draft selection");
            Check(form.Selection.DisabledDomains.SequenceEqual(exclusions), "all-off preserves individual domain exclusions");
            Value<Button>(form, "enableAllCategories").PerformClick(); Ready(form);
            Check(categories.Values.All(x => x.Checked), "all categories on button updates every checkbox");
            Check(form.Selection.Services.Count == ServiceCatalog.All().Count && form.Selection.UsePublicLists && form.Selection.UseCustomDomains, "all categories on updates draft selection");
            Check(form.Selection.DisabledDomains.SequenceEqual(exclusions), "all-on preserves individual domain exclusions");
            Value<TextBox>(form, "search").Text = "discarded-query"; Invoke(form, "StartSearch");
            Value<TextBox>(form, "search").Text = "domain0449"; Ready(form);
            Check(rows.Items.Count == 1 && rows.Items[0].Text == "domain0449.org", "latest asynchronous query wins");
            var apply = Descendants(form).OfType<Button>().Single(x => x.DialogResult == DialogResult.OK); apply.PerformClick();
            Check(form.DialogResult == DialogResult.OK && form.Selection.UsePublicLists, "explicit apply returns usable selection"); form.Close(); Pump(20);
            }
            catch (Exception error) { Console.Error.WriteLine("Domain workflow failure before disposal: " + error); throw; }
        }
    }

    static void CheckMainForm()
    {
        using (var form = new MainForm(true, true, false, null, false))
        {
            Show(form); Value<System.Windows.Forms.Timer>(form, "timer").Stop();
            var shell = Value<AppShell>(form, "shell"); shell.OpenSettings(); Pump(20);
            var ignore = Value<CheckBox>(form, "ignoreVpn"); var manage = Value<Button>(form, "manageDomains");
            Invoke(form,"SelectSettingsSection","advanced");Pump(20);
            Check(!ignore.Checked && ignore.Enabled && ignore.Visible, "VPN override starts unarmed and visible in settings");
            Invoke(form,"SelectSettingsSection","applications");Pump(20);
            Check(manage.Enabled && manage.Visible, "domain manager is directly discoverable in settings");
            var email = Value<Button>(form, "emailContact");
            Check(IsInside(email, shell.MainContent) && email.AccessibleName.Contains("kirill0063@gmail.com"), "email contact belongs to main page and names requested address");
            Invoke(form,"SelectSettingsSection","profiles");Pump(20);
            foreach (string name in new[] { "presetChoice", "savePreset", "savePresetAs" })
            {
                Check(IsInside(Value<Control>(form, name), shell.SettingsContent), "preset control belongs to settings: " + name);
                Check(Value<Control>(form,name).Visible,"preset control is visible on its own section: "+name);
            }
            Check(Value<ComboBox>(form,"presetChoice").Enabled,"preset selection is readable on its active section");
            Check(!Value<Button>(form,"savePreset").Enabled&&!Value<Button>(form,"savePresetAs").Enabled,"preview keeps preset-write actions disabled");
            Invoke(form,"SelectSettingsSection","applications");Pump(20);
            Screenshot(form, "domain-settings-dark");
            Invoke(form,"SelectSettingsSection","advanced");Pump(20);
            ignore.Checked = true;
            var captured = (AppSettings)form.GetType().GetMethod("CaptureSettings", Private).Invoke(form, null);
            Check(!typeof(AppSettings).GetFields().Any(x => x.Name.IndexOf("IgnoreVpn", StringComparison.OrdinalIgnoreCase) >= 0), "VPN override is never persisted as a preference");
            Check(captured.Services.Count > 0, "ordinary preferences still captured");
            form.GetType().GetField("busy", Private).SetValue(form, true); Invoke(form, "RefreshControls");
            Check(ignore.Visible&&!ignore.Enabled,"VPN override is disabled on its active section during an operation");
            Invoke(form,"SelectSettingsSection","applications");Pump(20);
            Check(manage.Visible&&!manage.Enabled,"domain manager is disabled on its active section during an operation");
            form.GetType().GetField("busy", Private).SetValue(form, false); Invoke(form, "RefreshControls");
            Value<ComboBox>(form, "mode").SelectedIndex = 1; Invoke(form, "RefreshControls");
            Check(ignore.Visible&&!ignore.Enabled,"VPN override is disabled on its active section in VPN helper mode");
            Invoke(form,"SelectSettingsSection","applications");Pump(20);
            Check(manage.Visible&&!manage.Enabled,"domain manager is disabled on its active section in VPN helper mode");
            form.GetType().GetField("exitRequested", Private).SetValue(form, true); form.Close(); Pump(20);
        }
    }
}
