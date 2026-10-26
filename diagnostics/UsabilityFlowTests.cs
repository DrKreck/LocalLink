// Synthetic controls and own report fixtures only. No real filter, network or user preference writes.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using LocalLink;

public static class UsabilityFlowTests
{
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    static readonly List<string> failures = new List<string>();
    static int assertions, fixtures, states;

    [STAThread] public static int Main()
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        CheckReportIntegration();
        CheckPrimaryCaptions();
        foreach (bool dark in new[] { false, true })
            foreach (bool minimum in new[] { false, true }) RunFixture(dark, minimum);
        Console.WriteLine("Usability flow: " + assertions + " assertions, " + fixtures + " preview fixtures, " + states + " settings view states, " + failures.Count + " failures.");
        for (int index = 0; index < Math.Min(80, failures.Count); index++) Console.WriteLine("FAIL " + failures[index]);
        Console.WriteLine("Own report-fixture tray only; no filter child, network, user preference writes or OS setting changes.");
        return failures.Count == 0 ? 0 : 1;
    }
    static void CheckPrimaryCaptions()
    {
        var method = typeof(MainForm).GetMethod("PrimaryCaption", BindingFlags.NonPublic | BindingFlags.Static);
        Check(method != null, "primary caption mapping exists");
        if (method == null) return;
        string[] captions = { "Включить", "Выключить", "Отменить", "Остановить" };
        bool[][] values = { new[] { false,false }, new[] { false,true }, new[] { true,false }, new[] { true,true } };
        for (int i=0;i<values.Length;i++)
            Check((string)method.Invoke(null,new object[]{values[i][0],values[i][1]}) == captions[i], "primary caption state " + i);
    }
    static void RunFixture(bool dark, bool minimum)
    {
        string scope=(dark?"dark":"light")+"/"+(minimum?"minimum":"normal");
        try
        {
            using(var form=new MainForm(true,dark,!dark,null,false))
            {
                fixtures++; form.ShowInTaskbar=false; form.StartPosition=FormStartPosition.Manual;
                form.Location=new Point(-32000,-32000); if(minimum)form.Size=form.MinimumSize;
                form.Show(); Pump(form); ((Timer)Value(form,"timer")).Stop();
                var shell=(AppShell)Value(form,"shell");
                Check((bool)Value(form,"preview") && !(bool)Value(form,"persistPreferences"),scope+": isolated preview");
                Check(Value(form,"tray")==null,scope+": no tray");
                Check(!shell.SettingsOpen,scope+": settings closed initially");
                Check(shell.MainContent.Enabled && shell.MainContent.Visible,scope+": main enabled initially");
                foreach(string name in new[]{"status","summary","primary","check","bugReport","contact","results"})
                    Check(IsInside((Control)Value(form,name),shell.MainContent),scope+": main ownership "+name);
                foreach(string name in new[]{"publicLists","strategy","discordStrategy","mode","custom","showDetails","log","tune","tuneDiscord","exportTuning","telegramBridge","startupToggle","startupRemove"})
                {
                    var control=(Control)Value(form,name);
                    Check(IsInside(control,shell.SettingsContent) && !control.Visible,scope+": setting hidden from main "+name);
                }
                foreach(var pair in (Dictionary<string,CheckBox>)Value(form,"selections"))
                    Check(IsInside(pair.Value,shell.SettingsContent) && !pair.Value.Visible,scope+": service only in settings "+pair.Key);
                CheckMainActions(form,scope+"/initial");
                CheckBusy(form,scope);
                CheckLongSummary(form,scope);
                CheckRowsAndSettings(form,shell,scope);
                CheckAdvancedAndDraft(form,shell,scope);
                CheckBugReport(form,scope);
                CheckQuiet(form,scope+"/final"); form.Close(); Application.DoEvents();
            }
        }
        catch(Exception error)
        {
            var invocation=error as TargetInvocationException;
            if(invocation!=null && invocation.InnerException!=null)error=invocation.InnerException;
            Check(false,scope+": "+error.GetType().Name+" "+error.Message);
        }
    }
    static void CheckReportIntegration()
    {
        string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"bug-report-ui-fixtures",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        using(var history=new DiagnosticHistory(Path.Combine(root,"history"),true))
        using(var form=new MainForm(false,true,false,null,false,false,history))
        {
            Field("exitRequested").SetValue(form,true);form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-32000,-32000);
            form.Show();((Timer)Value(form,"timer")).Stop();Application.DoEvents();
            var action=(Button)Value(form,"bugReport");Check(action.Enabled,"report: action available on ordinary isolated window");
            Field("busy").SetValue(form,true);Field("activeOperation").SetValue(form,"check");Invoke(form,"RefreshControls");
            Check(action.Enabled,"report: export remains available during active operation");
            Check(((Button)Value(form,"contact")).Enabled,"contact: link remains available during active operation");
            typeof(MainForm).GetMethod("RecordFailure",Private).Invoke(form,new object[]{"check",new IOException("SENTINEL_UI_PRIVATE_MESSAGE")});
            var state=(BugReportSnapshot)typeof(MainForm).GetMethod("CaptureBugReport",Private).Invoke(form,null);
            string destination=Path.Combine(root,"ui-report.zip");BugReport.Export(destination,history,state);
            Check(File.Exists(destination),"report: UI snapshot exports one local ZIP");
            Check(state.Busy&&state.Operation=="check"&&(bool)Value(form,"busy"),"report: export never cancels operation");
            Check(history.Snapshot().Events.Exists(x=>x.Code=="caught_error"&&x.Error!=null),"report: caught UI errors reach persistent history");
            Check(!(bool)Value(form,"persistPreferences"),"report: integration fixture cannot write preferences");
            var engine=(Engine)Value(form,"engine");Check(!engine.IsRunning&&typeof(Engine).GetField("child",Private).GetValue(engine)==null,"report: no driver or child is started");
            Field("busy").SetValue(form,false);form.Close();Application.DoEvents();
        }
    }
    static void CheckMainActions(MainForm form,string scope)
    {
        foreach(string name in new[]{"primary","check","bugReport","contact","results"})
        {
            var control=(Control)Value(form,name);
            Check(control.Visible && control.Width>0 && control.Height>0,scope+": visible "+name);
            Check(form.ClientRectangle.Contains(InForm(form,control)),scope+": in window "+name);
        }
        var primary=(Button)Value(form,"primary"); var check=(Button)Value(form,"check");
        Rectangle overlap=Rectangle.Intersect(InForm(form,primary),InForm(form,check));
        Check(overlap.Width<=1 || overlap.Height<=1,scope+": main actions separate");
        Check(primary.Text=="Включить",scope+": short primary action");
        Check(!String.IsNullOrWhiteSpace(check.Text),scope+": check action labelled");
    }
    static void CheckBugReport(MainForm form,string scope)
    {
        var button=(Button)Value(form,"bugReport");
        Check(button.AccessibleName=="Создать баг-репорт",scope+": labelled report action");
        var contact=(Button)Value(form,"contact");
        Check(contact.Text=="@AresC404"&&contact.AccessibleName=="Открыть Telegram @AresC404",scope+": labelled contact action");
        Check(contact.Parent==button.Parent&&contact.Left>=button.Right,scope+": contact sits next to report without overlap");
        Check(!contact.Enabled,scope+": preview cannot launch Telegram");
        Check(!button.Enabled,scope+": preview cannot open report dialog");
        var history=(DiagnosticHistory)Value(form,"history");
        Check(!history.PersistenceAvailable&&history.Snapshot().Events.Count==0,scope+": preview creates no diagnostic history");
        var snapshot=(BugReportSnapshot)typeof(MainForm).GetMethod("CaptureBugReport",Private).Invoke(form,null);
        Check(snapshot.AppVersion=="0.8.1.0",scope+": report has current version");
        Check(snapshot.Mode>=0&&snapshot.Mode<=2,scope+": report configuration is captured");
        Check(!snapshot.FilterRunning,scope+": report cannot start a filter");
        Check(Value(form,"tray")==null&&!((Engine)Value(form,"engine")).IsRunning,scope+": report capture has no live side effects");
        Field("busy").SetValue(form,true);
        try
        {
            Invoke(form,"RefreshControls");
            snapshot=(BugReportSnapshot)typeof(MainForm).GetMethod("CaptureBugReport",Private).Invoke(form,null);
            Check(snapshot.Busy,scope+": report captures active operation without cancelling it");
            Check((bool)Value(form,"busy"),scope+": snapshot preserves busy state");
        }
        finally{Field("busy").SetValue(form,false);Invoke(form,"RefreshControls");}
    }
    static void CheckBusy(MainForm form,string scope)
    {
        Field("busy").SetValue(form,true);
        try
        {
            Invoke(form,"RefreshControls"); Pump(form);
            Check(((Button)Value(form,"primary")).Text=="Отменить",scope+": busy cancellation");
            Check(((Button)Value(form,"primary")).Enabled,scope+": cancel remains enabled");
            Check(!((Button)Value(form,"check")).Enabled,scope+": no concurrent diagnostics");
            CheckQuiet(form,scope+"/busy");
        }
        finally { Field("busy").SetValue(form,false); Invoke(form,"RefreshControls"); Pump(form); }
        CheckMainActions(form,scope+"/idle");
    }
    static void CheckLongSummary(MainForm form,string scope)
    {
        var summary=(Label)Value(form,"summary");
        string text="Synthetic "; for(int i=0;i<50;i++)text+="complete diagnostic message ";
        summary.Text=text; Invoke(form,"RefreshControls"); Pump(form);
        Check(summary.Text==text && summary.AccessibleDescription==text,scope+": long message preserved and accessible");
        var shell=(AppShell)Value(form,"shell"); shell.OpenSettings(); Pump(form); shell.CloseSettings(); Pump(form);
        Check(summary.Text==text && summary.AccessibleDescription==text,scope+": settings view preserves stored error");
        summary.Text=""; Invoke(form,"RefreshControls"); form.ShowElevatedRestartNotice(); Pump(form);
        Check(summary.Text.Contains("Включить") && summary.Text.Contains("не включ"),scope+": restart needs explicit activation");
        CheckQuiet(form,scope+"/message");
    }
    static void CheckRowsAndSettings(MainForm form,AppShell shell,string scope)
    {
        Invoke(form,"ResetRows");
        var details=(CheckBox)Value(form,"showDetails"); details.Checked=false;
        string[] all={"discord.desktop.api.v4.dns","discord.desktop.api.v4.tcp","discord.desktop.api.v4.tls","discord.desktop.api.v4","discord.desktop.cdn.v4.tls","web.tls"};
        string[] compact={"discord.desktop.api.v4","discord.desktop.cdn.v4.tls","web.tls"};
        foreach(string key in all)Invoke(form,"AddRow",new CheckRow{Key=key,Name="Synthetic "+key,State="Доступен",Passed=true,Milliseconds=42,Detail="Fixture "+key});
        var rows=(Dictionary<string,ListViewItem>)Value(form,"rows"); var results=(ListView)Value(form,"results");
        var snapshot=new RowsSnapshot(rows); snapshot.CheckOrder(rows,results,compact,scope+"/compact");
        var main=shell.MainContent; var drawer=shell.SettingsContent;
        for(int cycle=0;cycle<3;cycle++)
        {
            shell.SettingsAction.PerformClick(); Pump(form); states++;
            Check(shell.SettingsOpen && shell.SettingsContent.Visible,scope+": opens settings page "+cycle);
            Check(!shell.MainContent.Enabled || !shell.MainContent.Visible,scope+": main unavailable while settings open "+cycle);
            Control page=NamedAncestor(shell.SettingsContent,"SettingsPage");
            Check(page!=null && page.Name=="SettingsPage",scope+": fullscreen settings container "+cycle);
            Check(page!=null && IsInside(shell.Appearance,page),scope+": theme is part of settings page "+cycle);
            if(page!=null)Check(InForm(form,page)==InForm(form,shell.MainContent),scope+": settings fills complete body "+cycle);
            Check(Object.ReferenceEquals(main,shell.MainContent)&&Object.ReferenceEquals(drawer,shell.SettingsContent),scope+": hosts not replaced "+cycle);
            snapshot.CheckUnchanged(rows,scope+"/settings-open"+cycle);
            details.Checked=true; Pump(form);
            snapshot.CheckOrder(rows,results,all,scope+"/details-open"+cycle);
            snapshot.CheckUnchanged(rows,scope+"/details-open"+cycle);
            details.Checked=false; Pump(form);
            snapshot.CheckOrder(rows,results,compact,scope+"/details-close"+cycle);
            Check(SendShellKey(shell,Keys.Escape),scope+": Escape handled "+cycle);
            Pump(form); states++;
            Check(!shell.SettingsOpen && shell.MainContent.Enabled,scope+": Escape returns to main "+cycle);
            Check(shell.SettingsAction.ContainsFocus,scope+": focus restored "+cycle);
            snapshot.CheckUnchanged(rows,scope+"/settings-close"+cycle);
            CheckMainActions(form,scope+"/settings-close"+cycle); CheckQuiet(form,scope+"/drawer"+cycle);
        }
        shell.OpenSettings(); Pump(form);
        Check(shell.Appearance.Focus(),scope+": settings theme accepts focus");
        Control drawerRoot=CommonAncestor(shell.Appearance,shell.SettingsContent);
        Check(drawerRoot!=null && drawerRoot!=shell,scope+": settings focus boundary located");
        for(int i=0;i<20;i++)
        {
            Check(SendShellKey(shell,i%2==0?Keys.Tab:Keys.Shift|Keys.Tab),scope+": tab handled "+i);
            Pump(form); var focused=Focused(form);
            Check(focused!=null && focused.Visible && focused.FindForm()==form,scope+": focus remains in visible application "+i);
            Check(!shell.MainContent.ContainsFocus,scope+": hidden main cannot receive Tab "+i);
        }
        shell.CloseSettings(); Pump(form);
        CheckMainActions(form,scope+"/after-keyboard");
    }
    static void CheckAdvancedAndDraft(MainForm form,AppShell shell,string scope)
    {
        shell.OpenSettings(); Pump(form);
        Invoke(form,"SetAdvancedSettings",true); Pump(form);
        Check(((Panel)Value(form,"advancedSettings")).Visible,scope+": advanced settings disclose");
        ((ComboBox)Value(form,"mode")).SelectedIndex=1;
        ((TextBox)Value(form,"endpoint")).Text="198.51.100.42";
        ((NumericUpDown)Value(form,"port")).Value=51820;
        ((TextBox)Value(form,"custom")).Text="example.org\r\nvideo.example.org";
        ((ComboBox)Value(form,"strategy")).SelectedIndex=3;
        var snapshot=new DraftSnapshot(form); var original=Value(form,"settings");
        for(int i=0;i<3;i++)
        {
            shell.CloseSettings(); Pump(form); states++;
            snapshot.CheckUnchanged(form,scope+"/draft-close"+i);
            shell.OpenSettings(); Pump(form); states++;
            snapshot.CheckUnchanged(form,scope+"/draft-open"+i);
            Check(Object.ReferenceEquals(original,Value(form,"settings")),scope+": preferences object preserved "+i);
        }
        Invoke(form,"SetAdvancedSettings",false); Pump(form);
        Check(!((Panel)Value(form,"advancedSettings")).Visible,scope+": advanced hides");
        shell.CloseSettings(); Pump(form); CheckQuiet(form,scope+"/draft");
    }
    static bool SendShellKey(AppShell shell,Keys keys)
    {
        var method=typeof(AppShell).GetMethod("ProcessCmdKey",Private);
        if(method==null)throw new InvalidOperationException("Shell keyboard handler missing.");
        object[] args={new Message(),keys}; return (bool)method.Invoke(shell,args);
    }
    static Control CommonAncestor(Control left,Control right)
    {
        for(Control node=left;node!=null;node=node.Parent)if(node==right||IsInside(right,node))return node;
        return null;
    }
    static Control NamedAncestor(Control control,string name)
    {
        for(Control node=control;node!=null;node=node.Parent)if(node.Name==name)return node;
        return null;
    }
    static Control Focused(Control control)
    {
        foreach(Control child in control.Controls)if(child.ContainsFocus)return Focused(child);
        return control.Focused?control:null;
    }
    static void CheckQuiet(MainForm form,string scope)
    {
        var engine=(Engine)Value(form,"engine");
        Check(!engine.IsRunning,scope+": engine stopped");
        Check(typeof(Engine).GetField("child",Private).GetValue(engine)==null,scope+": no child");
        Check((IntPtr)typeof(Engine).GetField("job",Private).GetValue(engine)==IntPtr.Zero,scope+": no job");
        Check(Value(form,"operation")==null,scope+": no operation");
        Check(!(bool)Value(form,"persistPreferences"),scope+": no preference writes");
    }
    sealed class DraftSnapshot
    {
        readonly Dictionary<Control, string> values = new Dictionary<Control, string>();
        public DraftSnapshot(MainForm form)
        {
            foreach (Control control in Flatten(form))
                if (control is ComboBox || control is CheckBox || control is NumericUpDown || control is TextBox)
                    values.Add(control, DraftValue(control));
        }
        public void CheckUnchanged(MainForm form, string scope)
        {
            foreach (var pair in values)
                Check(!pair.Key.IsDisposed && IsInside(pair.Key, form) && DraftValue(pair.Key) == pair.Value,
                    scope + ": draft value/object preserved " + pair.Key.GetType().Name);
        }
        static string DraftValue(Control control)
        {
            var combo = control as ComboBox;
            if (combo != null) return combo.SelectedIndex + "|" + combo.Text + "|" + combo.Items.Count;
            var box = control as CheckBox;
            if (box != null) return box.Checked.ToString();
            var number = control as NumericUpDown;
            if (number != null) return number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return control.Text;
        }
    }

    sealed class RowsSnapshot
    {
        readonly Dictionary<string, ListViewItem> references = new Dictionary<string, ListViewItem>();
        readonly Dictionary<string, string> contents = new Dictionary<string, string>();
        readonly Dictionary<string, object> tags = new Dictionary<string, object>();
        readonly Dictionary<string, string> tagContents = new Dictionary<string, string>();
        public RowsSnapshot(Dictionary<string, ListViewItem> rows)
        {
            foreach (var pair in rows)
            {
                references.Add(pair.Key, pair.Value); contents.Add(pair.Key, RowValue(pair.Value)); tags.Add(pair.Key, pair.Value.Tag);
                tagContents.Add(pair.Key, CheckRowValue((CheckRow)pair.Value.Tag));
            }
        }
        public void CheckUnchanged(Dictionary<string, ListViewItem> rows, string scope)
        {
            Check(rows.Count == references.Count, scope + ": underlying row count preserved");
            foreach (var pair in references)
            {
                ListViewItem actual;
                bool found = rows.TryGetValue(pair.Key, out actual);
                Check(found && Object.ReferenceEquals(actual, pair.Value), scope + ": row/item object preserved " + pair.Key);
                Check(found && Object.ReferenceEquals(actual.Tag, tags[pair.Key]) && RowValue(actual) == contents[pair.Key], scope + ": row data preserved " + pair.Key);
                Check(found && CheckRowValue((CheckRow)actual.Tag) == tagContents[pair.Key], scope + ": underlying CheckRow data preserved " + pair.Key);
            }
        }
        public void CheckOrder(Dictionary<string, ListViewItem> rows, ListView results, string[] keys, string scope)
        {
            Check(results.Items.Count == keys.Length, scope + ": intended visible subset count");
            for (int index = 0; index < keys.Length; index++)
                Check(index < results.Items.Count && Object.ReferenceEquals(results.Items[index], rows[keys[index]]), scope + ": visible subset order/object " + keys[index]);
        }
        static string RowValue(ListViewItem item)
        {
            string value = item.ToolTipText;
            foreach (ListViewItem.ListViewSubItem cell in item.SubItems) value += "\n" + cell.Text;
            return value;
        }
        static string CheckRowValue(CheckRow row)
        {
            return row.Key + "\n" + row.ServiceId + "\n" + row.Name + "\n" + row.State + "\n" + row.Detail + "\n" + row.DiagnosticCode + "\n" +
                (row.Passed.HasValue ? row.Passed.Value.ToString() : "null") + "\n" + row.Milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    static List<Control> Flatten(Control control)
    {
        var controls = new List<Control>(); Add(control, controls); return controls;
    }
    static void Add(Control control, List<Control> controls)
    { controls.Add(control); foreach (Control child in control.Controls) Add(child, controls); }
    static bool IsInside(Control control, Control ancestor)
    { for (Control parent = control.Parent; parent != null; parent = parent.Parent) if (parent == ancestor) return true; return false; }
    static Rectangle InForm(MainForm form, Control control)
    { return new Rectangle(form.PointToClient(control.PointToScreen(Point.Empty)), control.Size); }
    static void Pump(MainForm form) { form.PerformLayout(); Application.DoEvents(); }
    static FieldInfo Field(string name)
    {
        FieldInfo field = typeof(MainForm).GetField(name, Private);
        if (field == null) throw new InvalidOperationException("Required MainForm field missing: " + name);
        return field;
    }
    static object Value(MainForm form, string name) { return Field(name).GetValue(form); }
    static void Invoke(MainForm form, string name, params object[] arguments)
    {
        MethodInfo method = typeof(MainForm).GetMethod(name, Private);
        if (method == null) throw new InvalidOperationException("Required MainForm method missing: " + name);
        method.Invoke(form, arguments);
    }
    static void Check(bool result, string message)
    { assertions++; if (!result) failures.Add(message); }
}
