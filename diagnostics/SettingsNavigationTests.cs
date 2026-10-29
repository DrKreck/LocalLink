// Native preview forms only. No user settings, network, filter or dependency setup.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using LocalLink;

internal static class SettingsNavigationTests
{
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static int checks;
    [STAThread]static int Main()
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            foreach(bool dark in new[]{true,false})foreach(bool minimum in new[]{true,false})Run(dark,minimum);
            Console.WriteLine("Settings navigation: "+checks+" native assertions passed. Preview forms only; no network, filter or user settings.");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    static void Run(bool dark,bool minimum)
    {
        using(var form=new MainForm(true,dark,!dark,null,false))
        {
            form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-32000,-32000);
            if(minimum)form.Size=form.MinimumSize;
            form.Show();Pump(form);((System.Windows.Forms.Timer)Value(form,"timer")).Stop();
            var shell=(AppShell)Value(form,"shell");
            var sections=(Dictionary<string,Panel>)Value(form,"settingsSections");
            var buttons=(Dictionary<string,Button>)Value(form,"settingsSectionButtons");
            Check(sections.Count==5&&buttons.Count==5,"five distinct sections with five native navigation buttons");
            Check((string)Value(form,"selectedSettingsSection")=="basic","settings start at automatic basic workflow");
            shell.OpenSettings();Pump(form);
            string[] ids={"basic","applications","profiles","updates","advanced"};
            foreach(string id in ids)
            {
                buttons[id].PerformClick();Pump(form);
                Check((string)Value(form,"selectedSettingsSection")==id,"navigation selects "+id);
                foreach(string candidate in ids)
                {
                    Check(sections[candidate].Visible==(candidate==id),"only selected page is visible: "+id+"/"+candidate);
                    Check(sections[candidate].Enabled==(candidate==id),"hidden page cannot receive input: "+id+"/"+candidate);
                    Check((string)buttons[candidate].Tag==(candidate==id?"nav_selected":"nav"),"selected visual state: "+id+"/"+candidate);
                }
                foreach(var action in buttons.Values)
                {
                    Check(action.Width>=110&&action.Height>=32,"navigation remains usable at minimum window size");
                    Check(action.Font.FontFamily.Name=="Segoe UI","navigation uses the application font");
                    Check(!String.IsNullOrWhiteSpace(action.AccessibleName),"navigation has accessible labels");
                }
            }
            Check(IsInside((Control)Value(form,"strategy"),sections["advanced"]),"raw strategy is only in Advanced");
            Check(IsInside((Control)Value(form,"endpoint"),sections["advanced"]),"raw VPN endpoint is only in Advanced");
            Check(IsInside((Control)Value(form,"log"),sections["advanced"]),"technical log is only in Advanced");
            Check(IsInside((Control)Value(form,"checkUpdate"),sections["updates"]),"application updater has its own section");
            Check(IsInside((Control)Value(form,"autoRecovery"),sections["basic"]),"connection recovery is in Basic, not the update page");
            Check(IsInside((Control)Value(form,"startupToggle"),sections["basic"]),"Windows startup is in Basic");
            Check(IsInside((Control)Value(form,"presetChoice"),sections["profiles"]),"saved presets are in Profiles");
            Check(IsInside((Control)Value(form,"rememberNetwork"),sections["profiles"]),"network binding is in Profiles");
            var services=(Dictionary<string,CheckBox>)Value(form,"selections");
            buttons["applications"].PerformClick();Pump(form);
            foreach(var pair in services)Check(pair.Value.Visible&&IsInside(pair.Value,sections["applications"]),"service choice is directly readable: "+pair.Key);
            Check(!((Control)Value(form,"publicLists")).Visible,"the expanded list switch is not duplicated in the service checklist");
            buttons["advanced"].PerformClick();Pump(form);
            var strategy=(ComboBox)Value(form,"strategy");strategy.SelectedIndex=3;
            var custom=(TextBox)Value(form,"custom");custom.Text="example.org";
            buttons["basic"].PerformClick();buttons["advanced"].PerformClick();Pump(form);
            Check(strategy.SelectedIndex==3&&custom.Text=="example.org","navigation does not reset manual drafts");
            var right=new KeyEventArgs(Keys.Right);Call(form,"SettingsNavigationKeyDown",buttons["advanced"],right);Pump(form);
            Check(right.Handled&&(string)Value(form,"selectedSettingsSection")=="basic","Right wraps to the first section");
            var end=new KeyEventArgs(Keys.End);Call(form,"SettingsNavigationKeyDown",buttons["basic"],end);Pump(form);
            Check(end.Handled&&(string)Value(form,"selectedSettingsSection")=="advanced","End selects Advanced");
            Call(form,"SetAdvancedSettings",false);Pump(form);
            Check(!((Panel)Value(form,"advancedSettings")).Visible&&(string)Value(form,"selectedSettingsSection")=="basic","legacy disclosure API returns to Basic");
            shell.CloseSettings();Pump(form);
            Check(!shell.SettingsOpen&&shell.MainContent.Visible,"back/Escape can return to the main connection view");
            Check(!((Engine)Value(form,"engine")).IsRunning&&Value(form,"operation")==null,"navigation never starts connection, tuning or installation");
            Check(Value(form,"tray")==null,"preview fixture owns no tray resources");
            form.Close();Application.DoEvents();
        }
    }
    static object Value(MainForm form,string name){return typeof(MainForm).GetField(name,Private).GetValue(form);}
    static object Call(MainForm form,string name,params object[] args){return typeof(MainForm).GetMethod(name,Private).Invoke(form,args);}
    static bool IsInside(Control value,Control ancestor){for(Control parent=value;parent!=null;parent=parent.Parent)if(Object.ReferenceEquals(parent,ancestor))return true;return false;}
    static void Pump(Form form){form.PerformLayout();Application.DoEvents();}
    static void Check(bool value,string message){if(!value)throw new Exception("FAIL: "+message);checks++;}
}
