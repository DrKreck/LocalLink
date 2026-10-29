using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using LocalLink;

internal static class UpdatePromptTests
{
    static int checks;
    [STAThread] static int Main()
    {
        try
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            foreach(bool dark in new[]{true,false})
            {
                foreach(var choice in new[]{UpdatePromptChoice.Update,UpdatePromptChoice.Later,UpdatePromptChoice.IgnoreVersion})Buttons(dark,choice);
                Dismiss(dark);
                foreach(float scale in new[]{1.25f,1.5f,2f})Scaled(dark,scale);
            }
            Throws<ArgumentException>(()=>new UpdatePromptForm(AppTheme.Read("dark"),"0.9.1\n"),"prompt refuses injected version text");
            Console.WriteLine("Update prompt UI: "+checks+" native assertions passed; no network, updater or preferences.");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    static void Buttons(bool dark,UpdatePromptChoice choice)
    {
        using(var form=new UpdatePromptForm(AppTheme.Read(dark?"dark":"light"),"0.9.1"))
        {
            form.Location=new Point(-32000,-32000);form.StartPosition=FormStartPosition.Manual;form.Show();Application.DoEvents();
            Check(form.Choice==UpdatePromptChoice.Later,"no installation decision on opening prompt");
            Check(form.OfferedVersion=="0.9.1"&&form.Text=="Обновление LocalLink","prompt identifies application and version");
            Button update=Button(form,"UpdateNow"),later=Button(form,"UpdateLater"),ignore=Button(form,"IgnoreVersion");
            Check(update.Text=="Обновить"&&later.Text=="Позже"&&ignore.Text=="Не напоминать","three compact choices have requested copy");
            Check(form.AcceptButton==later&&form.CancelButton==later,"Enter and Escape never silently accept installation");
            foreach(Button button in new[]{update,later,ignore})
            {
                Check(button.Visible&&button.Enabled&&button.Height>=32&&button.Width>=100,"themed button is visible and large enough");
                Check(button.AccessibleName==button.Text,"choice has readable accessibility label");
                Check(button.Left>=0&&button.Right<=button.Parent.ClientSize.Width+1,"buttons fit their container");
            }
            Check(ignore.Left>=later.Right&&later.Left>=update.Right,"buttons do not overlap");
            string name=choice==UpdatePromptChoice.Update?"UpdateNow":choice==UpdatePromptChoice.Later?"UpdateLater":"IgnoreVersion";
            Button(form,name).PerformClick();Application.DoEvents();
            Check(form.Choice==choice,"button returns only its explicit enum decision");
            Check(form.DialogResult==(choice==UpdatePromptChoice.Update?DialogResult.OK:choice==UpdatePromptChoice.Later?DialogResult.Cancel:DialogResult.Ignore),"dialog result matches explicit choice");
        }
    }
    static void Dismiss(bool dark)
    {
        using(var form=new UpdatePromptForm(AppTheme.Read(dark?"dark":"light"),"0.9.1"))
        {
            form.Location=new Point(-32000,-32000);form.StartPosition=FormStartPosition.Manual;form.Show();Application.DoEvents();form.Close();
            Check(form.Choice==UpdatePromptChoice.Later,"closing window is a deferral, not installation or permanent mute");
        }
    }
    static void Scaled(bool dark,float scale)
    {
        var fonts=new List<Font>();
        try
        {
            using(var form=new UpdatePromptForm(AppTheme.Read(dark?"dark":"light"),"65535.65535.65535"))
            {
                form.Location=new Point(-32000,-32000);form.StartPosition=FormStartPosition.Manual;form.Show();Application.DoEvents();
                form.AutoScaleMode=AutoScaleMode.None;
                var controls=Flatten(form).ToArray();var original=controls.ToDictionary(x=>x,x=>x.Font);
                form.SuspendLayout();form.Scale(new SizeF(scale,scale));
                foreach(Control control in controls)
                {Font prior=original[control];var font=new Font(prior.FontFamily,prior.SizeInPoints*scale,prior.Style,GraphicsUnit.Point);fonts.Add(font);control.Font=font;}
                form.ResumeLayout(true);Application.DoEvents();
                foreach(string name in new[]{"UpdateNow","UpdateLater","IgnoreVersion"})
                {
                    var button=Button(form,name);
                    Check(button.Width>=TextRenderer.MeasureText(button.Text,button.Font).Width+8,"scaled prompt choice text fits");
                    Check(button.Height>=Math.Ceiling(32*scale),"scaled prompt choice retains useful height");
                    Check(button.Right<=button.Parent.ClientSize.Width+1,"scaled prompt choice stays inside its container");
                }
            }
        }
        finally{foreach(var font in fonts)font.Dispose();}
    }
    static IEnumerable<Control> Flatten(Control parent)
    {yield return parent;foreach(Control child in parent.Controls)foreach(Control item in Flatten(child))yield return item;}
    static Button Button(Control root,string name)
    {return root.Controls.Find(name,true).OfType<Button>().Single();}
    static void Check(bool value,string description){checks++;if(!value)throw new Exception(description);}
    static void Throws<T>(Action action,string description) where T:Exception
    {bool thrown=false;try{action();}catch(T){thrown=true;}Check(thrown,description);}
}
