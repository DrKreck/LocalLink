// Native forms and per-AppDomain fixtures only; no filter, network requests,
// dependency setup, real user preferences, or startup registration changes.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using LocalLink;

internal static class PresetUiTests
{
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    static int checks;
    [STAThread] static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--isolated") return Run();
            // Keep nested transactional preset paths below Framework MAX_PATH
            // even when the checked-out project has a deep Windows path.
            string fixture = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pui", Guid.NewGuid().ToString("N").Substring(0,12));
            Directory.CreateDirectory(fixture);
            var domain = AppDomain.CreateDomain("preset-ui", null, new AppDomainSetup { ApplicationBase = fixture, ConfigurationFile = Assembly.GetExecutingAssembly().Location + ".config" });
            try { return domain.ExecuteAssembly(Assembly.GetExecutingAssembly().Location, new[] { "--isolated" }); }
            finally { AppDomain.Unload(domain); }
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    static int Run()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        using (var form = new MainForm(false, false, false, null, false))
        {
            form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-32000, -32000);
            form.Show(); Application.DoEvents(); ((System.Windows.Forms.Timer)Value(form, "timer")).Stop();
            Check(!(bool)Value(form, "preview") && !(bool)Value(form, "persistPreferences"), "native non-preview path with real preference writes disabled");
            var engine = (Engine)Value(form, "engine"); Check(!engine.IsRunning, "fixture engine never starts");
            var store = (PresetStore)Value(form, "presetStore");
            var first = new AppSettings { Services = new List<string> { "youtube", "discord" }, Mode = 1, Strategy = "fake-disorder", DiscordStrategy = "sni-split",
                UsePublicLists = true, UseCustomDomains = false, DisabledDomains = new List<string> { "off.example.org" }, CustomDomains = "custom.example.org",
                Quic = true, DiscordVoice = false, TelegramNative = true, Endpoint = "203.0.113.5", Port = 8443, Appearance = "dark" };
            var second = new AppSettings { Services = new List<string> { "steam", "telegram" }, Mode = 0, Strategy = "disorder", DiscordStrategy = "",
                UsePublicLists = false, UseCustomDomains = true, DisabledDomains = new List<string> { "another.example.org" }, CustomDomains = "second.example.org",
                Quic = false, DiscordVoice = true, TelegramNative = false, Endpoint = "203.0.113.6", Port = 7443, Appearance = "light" };
            string one = store.Save("Дом", first), two = store.Save("Другая сеть", second);
            Call(form, "RefreshPresets", "");
            var choice = (ComboBox)Value(form, "presetChoice");
            Check(choice.Items.Count == 3, "own saved presets listed with custom-settings choice");
            Field("busy").SetValue(form, true); Call(form, "RefreshControls");
            Check(!choice.Enabled && !((Button)Value(form, "savePreset")).Enabled && !((Button)Value(form, "savePresetAs")).Enabled,
                "preset actions unavailable during active operation");
            Field("busy").SetValue(form, false); Call(form, "RefreshControls");
            Select(choice, one); AssertCapture(form, first, one);
            Color dark = form.BackColor;
            ((CheckBox)Value(form, "ignoreVpn")).Checked = true;
            Field("preparedExclusions").SetValue(form, "fixture-old-exclusions");
            Select(choice, two); AssertCapture(form, second, two);
            Check(form.BackColor != dark, "preset switch applies dark and light palettes immediately");
            Check(!(bool)((CheckBox)Value(form, "ignoreVpn")).Checked && (string)Value(form, "preparedExclusions") == "", "loading preset clears per-run VPN override and stale exclusion snapshot");
            ((CheckBox)Value(form, "quic")).Checked = true;
            Call(form, "SavePreset", false);
            Check(store.Load(two).Quic && ((PresetEntry)choice.SelectedItem).Id == two, "native Save writes current choices to selected preset, retaining selection");
            bool promptUsed = false;
            using (var timer = new System.Windows.Forms.Timer { Interval = 20 })
            {
                timer.Tick += delegate
                {
                    var prompt = Application.OpenForms.Cast<Form>().FirstOrDefault(x => x.Text == "Сохранить пресет");
                    if (prompt == null) return;
                    timer.Stop(); var name = prompt.Controls.OfType<TextBox>().Single(); name.Text = "Сеть для друга";
                    promptUsed = true; ((Button)prompt.AcceptButton).PerformClick();
                };
                timer.Start(); Call(form, "SavePreset", true);
            }
            string savedAs = ((PresetEntry)choice.SelectedItem).Id;
            Check(promptUsed && store.List().Count == 3 && savedAs != one && savedAs != two && store.List().Any(x => x.Id == savedAs && x.Name == "Сеть для друга"), "Save as uses native naming prompt and creates separate selected snapshot");
            var captured = (AppSettings)Call(form, "CaptureSettings");
            string preferences = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings-fixture.json");
            typeof(AppSettings).GetMethod("SaveToPath", Private).Invoke(captured, new object[] { preferences, null });
            var restored = (AppSettings)typeof(AppSettings).GetMethod("LoadFromPath", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { preferences, null });
            Check(restored.SelectedPresetId == savedAs && restored.Quic && restored.Services.OrderBy(x => x).SequenceEqual(second.Services.OrderBy(x => x)), "captured last-session preset ID and edited options survive native preference serialization");
            string corrupt = store.Save("Повреждённый", new AppSettings());
            Call(form, "RefreshPresets", savedAs);
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "presets-preview", corrupt + ".json"), "{corrupt");
            Select(choice, corrupt);
            Check(((AppSettings)Call(form, "CaptureSettings")).SelectedPresetId == savedAs, "failed load cannot label unchanged settings as broken preset");
            Check(!engine.IsRunning && Field("operation").GetValue(form) == null, "preset workflows never start network work");
            Field("exitRequested").SetValue(form, true); form.Close(); Application.DoEvents();
        }
        using (var preview = new MainForm(true, true, false, null, false))
        {
            var store = (PresetStore)Value(preview, "presetStore"); int count = store.List().Count;
            Call(preview, "SavePreset", false); Call(preview, "SavePreset", true);
            Check(store.List().Count == count && !((Button)Value(preview, "savePreset")).Enabled && !((Button)Value(preview, "savePresetAs")).Enabled,
                "preview disables both save paths without writing fixtures");
        }
        Console.WriteLine("Preset native UI: " + checks + " assertions passed. Own tray/fixtures only; no user preferences, filter or network.");
        return 0;
    }
    static void AssertCapture(MainForm form, AppSettings expected, string id)
    {
        var actual = (AppSettings)Call(form, "CaptureSettings");
        Check(actual.SelectedPresetId == id && actual.Services.OrderBy(x => x).SequenceEqual(expected.Services.OrderBy(x => x)), "selected categories and preset ID reach native controls");
        Check(actual.Mode == expected.Mode && actual.Strategy == expected.Strategy && actual.DiscordStrategy == expected.DiscordStrategy, "mode and both TLS methods applied");
        Check(actual.CustomDomains == expected.CustomDomains && actual.UseCustomDomains == expected.UseCustomDomains && actual.UsePublicLists == expected.UsePublicLists && actual.DisabledDomains.SequenceEqual(expected.DisabledDomains), "custom/public categories and exact exclusions applied");
        Check(actual.Quic == expected.Quic && actual.DiscordVoice == expected.DiscordVoice && actual.TelegramNative == expected.TelegramNative, "protocol options applied");
        Check(actual.Endpoint == expected.Endpoint && actual.Port == expected.Port && actual.Appearance == expected.Appearance, "endpoint, port and theme applied");
    }
    static void Select(ComboBox choice, string id)
    { for (int i = 0; i < choice.Items.Count; i++) if (((PresetEntry)choice.Items[i]).Id == id) { choice.SelectedIndex = i; Application.DoEvents(); return; } throw new Exception("Preset not listed: " + id); }
    static FieldInfo Field(string name) { return typeof(MainForm).GetField(name, Private); }
    static object Value(MainForm form, string name) { return Field(name).GetValue(form); }
    static object Call(MainForm form, string name, params object[] args)
    { try { return typeof(MainForm).GetMethod(name, Private).Invoke(form, args); } catch (TargetInvocationException error) { throw error.InnerException; } }
    static void Check(bool value, string text) { checks++; if (!value) throw new Exception(text); }
}
