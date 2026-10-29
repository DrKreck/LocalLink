using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using LocalLink;

internal static class TroubleshootingTests
{
    static int assertions;
    const string Secret="SENTINEL_SECRET_TOKEN_OR_PRIVATE_HOST_709";
    static readonly JavaScriptSerializer Parser=new JavaScriptSerializer {MaxJsonLength=8*1024*1024,RecursionLimit=64};
    [STAThread] static int Main()
    {
        string fixture=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"troubleshooting-fixtures",Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(fixture);Cases();Lifecycle();Privacy();Bounded();Archive(fixture);Ui(fixture);
            Console.WriteLine("Troubleshooting: "+assertions+" offline assertions passed. Fixtures retained: "+fixture);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);Console.Error.WriteLine("Fixture retained: "+fixture);return 1;}
    }
    static void Cases()
    {
        var all=Troubleshooting.Cases();Check(all.Count==10,"ten specific workflows");Check(all.Select(x=>x.Id).Distinct().Count()==all.Count,"case identifiers unique");
        foreach(var item in all){Check(item.Name.Length>0&&item.Instruction.Length>0,"case readable");Check(ServiceCatalog.All().Any(x=>x.Id==item.ServiceId),"case maps to known service");Check(Troubleshooting.Case(item.Id).Name==item.Name,"known case roundtrip");}
        Throws(delegate{Troubleshooting.Case(Secret);},"unknown case rejected");
    }
    static TroubleshootingState State() { return new TroubleshootingState {FilterRunning=true,Strategy="disorder",DiscordStrategy="",Mode=0}; }
    static CheckRow Row(string key,bool? passed,string code)
    {return new CheckRow {ServiceId="discord",Key=key,Passed=passed,DiagnosticCode=code,Milliseconds=23,Name=Secret,Detail=Secret,State=Secret};}
    static void Lifecycle()
    {
        DateTime now=new DateTime(2026,10,28,7,0,0,DateTimeKind.Utc);var state=State();var session=new TroubleshootingSession("discord-voice",state,now);
        state.Strategy="fake-disorder";Check(session.Before.Strategy=="disorder","capture copies state");
        Throws(delegate{session.Finish("works",null,false,State(),now);},"cannot claim app checked before reproduction");
        session.SetBaseline(new[]{Row("discord.web.tls",true,"ok")},false);session.BeginReproduction(now.AddMinutes(1));
        Throws(delegate{session.SetBaseline(null,false);},"baseline cannot be replaced after reproduce");Throws(delegate{session.BeginReproduction();},"cannot begin twice");
        Throws(delegate{session.Finish(Secret,null,false,State(),now);},"arbitrary outcome rejected");
        session.Finish("still-fails",new[]{Row("discord.web.tls",true,"ok")},false,State(),now.AddMinutes(2));
        Check(session.Outcome=="still-fails"&&!session.EnvironmentChanged,"reported outcome not inferred from website");
        Check(session.Summary().Contains("проблема сохранилась")&&session.Summary().Contains("не проверка звука"),"green public nodes not called voice success");
        Throws(delegate{session.Finish("works",null,false,State());},"cannot finish twice");
        var changed=new TroubleshootingSession("discord-startup",State(),now);changed.BeginReproduction(now);var next=State();next.FilterRunning=false;
        changed.Finish("not-checked",null,true,next,now.AddHours(2));Check(changed.EnvironmentChanged&&changed.EndedUtc.Value==now.AddMinutes(20),"changed state and time cap recorded");
        Check(changed.Summary().Contains("лимиту времени"),"timeout not called block");
        var backwards=new TroubleshootingSession("discord-startup",State(),now);backwards.BeginReproduction(now.AddMinutes(2));backwards.Finish("works",null,false,State(),now);
        Check(backwards.EndedUtc==backwards.ReproduceStartedUtc,"clock regression cannot reverse case interval");
        var cancel=new TroubleshootingSession("youtube-video",null,now);cancel.Finish("cancelled",null,false,null,now);Check(cancel.Summary().Contains("прервана"),"baseline cancellation exportable");
        foreach(string code in new[]{"dns-failed","certificate-or-tls","timeout"})
        {
            var failure=new TroubleshootingSession("discord-startup",State(),now);failure.BeginReproduction(now);
            failure.Finish("still-fails",new[]{Row(code=="timeout"?"discord.desktop.api.v4.tcp":"discord.desktop.api.v4.dns",false,code)},false,State(),now);
            Check(!failure.Summary().Contains("блокировка")&&!failure.Summary().Contains("вирус"),"failure does not invent root cause");
        }
    }
    static void Privacy()
    {
        var session=new TroubleshootingSession("discord-upload",new TroubleshootingState {Strategy=Secret,DiscordStrategy=Secret,Mode=99});
        var rows=new[]{Row("discord.upload.tls",true,"ok"),Row(Secret,false,Secret),Row("discord.web.tls",false,Secret),Row("discord.upload.tls",false,"ok"),
            new CheckRow {ServiceId=Secret,Key="discord.web.http",Passed=false,Detail=Secret},Row("discord.desktop.api.v4.tcp",false,"timeout")};
        rows[2].Milliseconds=Int64.MaxValue;session.SetBaseline(rows,false);session.BeginReproduction();session.Finish("still-fails",rows,false,State());
        Check(session.Baseline.Count==3,"unknown, other service and duplicate probe ignored");Check(session.Baseline[1].Milliseconds==120000,"numeric probe duration bounded");
        var copy=session.Baseline[0];copy.Code=Secret;copy.Key=Secret;
        session.Before.Strategy=Secret;session.After.DiscordStrategy=Secret;
        string json=DiagnosticFiles.Json(session.Document());Check(!json.Contains(Secret),"raw text excluded and public objects resanitized");
        Check(!json.Contains("203.0.113.")&&!json.Contains("Host")&&!json.Contains("Detail"),"no endpoint payload or detail in case document");
        var game=new TroubleshootingSession("dcs-connect",State());game.SetBaseline(new[]{new CheckRow {ServiceId="dcs",Key="dcs.web.tls",Passed=true}},false);game.BeginReproduction();game.Finish("still-fails",null,false,State());Check(game.Baseline.Count==1,"DCS fixed probe preserved");
    }
    static void Bounded()
    {
        var item=Troubleshooting.Case("discord-startup");int calls=0;
        var result=Troubleshooting.RunBoundedAsync(item,null,CancellationToken.None,delegate(TroubleshootingCase c,IProgress<CheckRow> p,CancellationToken t)
        {calls++;return Task.FromResult(new List<CheckRow>{Row("discord.web.tls",true,"ok")});},100).GetAwaiter().GetResult();
        Check(calls==1&&!result.TimedOut&&result.Rows.Count==1,"single offline injected probe completes");
        var stuck=new TaskCompletionSource<List<CheckRow>>();var timer=Stopwatch.StartNew();
        result=Troubleshooting.RunBoundedAsync(item,null,CancellationToken.None,delegate(TroubleshootingCase c,IProgress<CheckRow> progress,CancellationToken token)
        { progress.Report(Row("discord.desktop.api.v4.dns",false,"dns-failed"));progress.Report(Row(Secret,false,Secret));return stuck.Task;},30).GetAwaiter().GetResult();
        Check(result.TimedOut&&timer.ElapsedMilliseconds<1500,"noncooperative runner has bounded wait");
        Check(result.Rows.Count==1&&result.Rows[0].Key=="discord.desktop.api.v4.dns"&&result.Rows[0].Detail==null,"timeout retains sanitized partial observations");stuck.SetResult(new List<CheckRow>());
        using(var cancel=new CancellationTokenSource())
        {
            cancel.Cancel();Throws(delegate{Troubleshooting.RunBoundedAsync(item,null,cancel.Token,delegate{ return Task.FromCanceled<List<CheckRow>>(cancel.Token);},100).GetAwaiter().GetResult();},"user cancellation preserved");
        }
    }
    static void Archive(string fixture)
    {
        var session=new TroubleshootingSession("discord-upload",State());session.SetBaseline(new[]{Row("discord.upload.tls",true,"ok")},false);session.BeginReproduction();session.Finish("still-fails",new[]{Row("discord.web.tls",true,"ok")},false,State());
        using(var history=DiagnosticHistory.Disabled())
        {
            var state=new BugReportSnapshot {AppVersion="0.9.0",Strategy="disorder",Checks=new List<BugReportCheck>()};
            Action<string> standard=delegate(string path){BugReport.Export(path,history,state);};string destination=Path.Combine(fixture,"case.zip");
            Check(Troubleshooting.Export(destination,session,standard)==destination,"complete report saved");
            using(var archive=ZipFile.OpenRead(destination))
            {
                Check(archive.Entries.Count==6,"standard report plus two scenario files");
                foreach(string name in new[]{"SUMMARY.txt","report.json","history.json","manifest.json","case.json","CASE-SUMMARY.txt"})Check(archive.GetEntry(name)!=null,"report entry "+name);
                Dictionary<string,object> manifest;using(var reader=new StreamReader(archive.GetEntry("manifest.json").Open()))manifest=(Dictionary<string,object>)Parser.DeserializeObject(reader.ReadToEnd());
                var entries=(object[])manifest["Files"];Check(entries.Length==5,"manifest covers all payload files");
                foreach(Dictionary<string,object> record in entries)
                {
                    var entry=archive.GetEntry((string)record["Path"]);using(var input=entry.Open())using(var sha=SHA256.Create())
                    {string hash=BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant();Check(hash==(string)record["SHA256"],"manifest hash "+entry.FullName);Check(entry.Length==Convert.ToInt64(record["Bytes"]),"manifest size "+entry.FullName);}
                }
                foreach(var entry in archive.Entries)using(var reader=new StreamReader(entry.Open()))Check(!reader.ReadToEnd().Contains(Secret),"secret absent "+entry.FullName);
            }
            byte[] before=File.ReadAllBytes(destination);Throws(delegate{Troubleshooting.Export(destination,session,standard);},"existing file not overwritten");Check(before.SequenceEqual(File.ReadAllBytes(destination)),"existing report unchanged");
            Throws(delegate{Troubleshooting.Export(Path.Combine(fixture,"bad.txt"),session,standard);},"nonZIP rejected");
            Throws(delegate{Troubleshooting.Export(Path.Combine(fixture,"unfinished.zip"),new TroubleshootingSession("discord-voice",State()),standard);},"unfinished session not exported");
            string bad=Path.Combine(fixture,"invalid-callback.zip");Throws(delegate{Troubleshooting.Export(bad,session,delegate(string path){File.WriteAllText(path,"not a zip");});},"invalid callback archive rejected");Check(!File.Exists(bad),"invalid report not committed");
            Check(Directory.GetFiles(fixture,".locallink-case-*").Length==0,"own partial artifacts removed on failure");
        }
    }
    static void Ui(string fixture)
    {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);int probes=0,exports=0;
        foreach(string appearance in new[]{"dark","light"})
        {
            var theme=AppTheme.Read(appearance);
            using(var form=new TroubleshootingForm(theme,delegate{exports++;},State,delegate(TroubleshootingCase c,IProgress<CheckRow> progress,CancellationToken token)
            {probes++;if(progress!=null)progress.Report(Row("discord.web.tls",true,"ok"));return Task.FromResult(new TroubleshootingRun {Rows=new List<CheckRow>{Row("discord.web.tls",true,"ok")}});}))
            {
                form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-32000,-32000);form.Show();Application.DoEvents();
                Check(probes==0&&exports==0,"construction never probes or exports");
                var primary=Children(form).OfType<Button>().First(x=>x.AccessibleName=="Начать");var export=Children(form).OfType<Button>().First(x=>x.AccessibleName=="Сохранить отчёт");
                Check(primary.Enabled&&!export.Enabled,"initial actions correct");primary.PerformClick();Application.DoEvents();
                Check(form.SessionActive&&primary.Text=="Завершить"&&!export.Enabled,"baseline transitions to reproduce");
                primary.PerformClick();Application.DoEvents();Check(!form.SessionActive&&export.Enabled&&primary.Text=="Новая проверка","finish enables report");
                using(var bitmap=new Bitmap(form.ClientSize.Width,form.ClientSize.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.ClientSize));bitmap.Save(Path.Combine(fixture,"wizard-"+appearance+".png"));}
                Check(form.BackColor==theme.Background,"theme matches app");
                primary.PerformClick();Application.DoEvents();Check(!form.SessionActive&&!export.Enabled&&primary.Text=="Начать","new case resets without running a probe");form.Close();
            }
            Check(probes==2,"only explicit start and finish run probes");probes=0;
        }
    }
    static IEnumerable<Control> Children(Control parent)
    {foreach(Control control in parent.Controls){yield return control;foreach(var child in Children(control))yield return child;}}
    static void Check(bool condition,string label){assertions++;if(!condition)throw new Exception("FAIL: "+label);}
    static void Throws(Action action,string label){bool thrown=false;try{action();}catch(Exception){thrown=true;}Check(thrown,label);}
}
