using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class TelegramAutoSetupTests
{
    static int checks;
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    [STAThread] static int Main()
    {
        string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tg-auto",Guid.NewGuid().ToString("N").Substring(0,12));
        Directory.CreateDirectory(root);
        try
        {
            bool automatic,selfTest;
            Check(TelegramBridgeWindow.TryParseArguments(new string[0],out automatic,out selfTest)&&!automatic&&!selfTest,"empty arguments preserve manual module mode");
            Check(TelegramBridgeWindow.TryParseArguments(new[]{"--auto-setup"},out automatic,out selfTest)&&automatic&&!selfTest,"exact auto setup argument recognized");
            Check(TelegramBridgeWindow.TryParseArguments(new[]{"--self-test"},out automatic,out selfTest)&&!automatic&&selfTest,"existing self-test argument recognized");
            foreach(string[] invalid in new[]{null,new[]{"--AUTO-SETUP"},new[]{"--start"},new[]{"tg://proxy?server=remote"},new[]{"--auto-setup","extra"},new[]{"--self-test","--auto-setup"},new[]{"--auto-setup --execute"}})
                Check(!TelegramBridgeWindow.TryParseArguments(invalid,out automatic,out selfTest)&&!automatic&&!selfTest,"unknown or mixed arguments rejected without launch");
            byte[] key=Enumerable.Range(0,16).Select(x=>(byte)x).ToArray();int launches=0;
            Check(TelegramBridgeWindow.TryOfferAutomaticProxy(root,key,()=>launches++),"first ready module offers its own proxy");
            string marker=Path.Combine(root,"proxy-link-offered-v1.json");string initial=File.ReadAllText(marker);
            Check(launches==1&&File.Exists(marker),"offer marker is committed before fake external handoff completes");
            Check(initial.Contains("\"Offered\":true")&&initial.Contains("SecretSha256"),"marker records an offer rather than acceptance");
            Check(!initial.Contains(BitConverter.ToString(key).Replace("-","").ToLowerInvariant())&&!initial.Contains("tg://")&&!initial.Contains("Accepted")&&!initial.Contains("Enabled"),"marker stores no proxy secret, URI or invented approval");
            Check(!TelegramBridgeWindow.TryOfferAutomaticProxy(root,key,()=>launches++)&&launches==1,"same secret cannot repeat automatic proxy offer");
            Check(File.ReadAllText(marker)==initial&&Directory.GetFiles(root,"*.backup-*").Length==0,"skipped duplicate does not rewrite metadata");
            byte[] second=Enumerable.Repeat((byte)0x55,16).ToArray();
            Check(TelegramBridgeWindow.TryOfferAutomaticProxy(root,second,()=>launches++)&&launches==2,"a genuinely changed own secret can offer reconfiguration");
            Check(Directory.GetFiles(root,"proxy-link-offered-v1.json.backup-*").Length==1&&File.ReadAllText(Directory.GetFiles(root,"proxy-link-offered-v1.json.backup-*")[0])==initial,"changed-secret marker preserves preceding exact metadata");
            string failed=Path.Combine(root,"failed");Directory.CreateDirectory(failed);
            Expect<InvalidOperationException>(()=>TelegramBridgeWindow.TryOfferAutomaticProxy(failed,key,()=>{throw new InvalidOperationException("synthetic shell failure");}),"external launch failure surfaces to status handler");
            Check(!TelegramBridgeWindow.TryOfferAutomaticProxy(failed,key,()=>launches++),"failed external handoff does not create repeated automatic prompts after reboot");
            Check(launches==2,"all repeated/error cases perform no additional external action");
            Expect<ArgumentException>(()=>TelegramBridgeWindow.TryOfferAutomaticProxy(root,new byte[15],()=>launches++),"invalid owned secret length rejected");
            Expect<ArgumentException>(()=>TelegramBridgeWindow.TryOfferAutomaticProxy(root,null,()=>launches++),"missing owned secret rejected");
            Expect<ArgumentNullException>(()=>TelegramBridgeWindow.TryOfferAutomaticProxy(root,key,null),"missing launch callback rejected");
            foreach(string bad in new[]{"relative",@"\\host\share\module",root+":ads"})
                Expect<ArgumentException>(()=>TelegramBridgeWindow.TryOfferAutomaticProxy(bad,key,()=>launches++),"nonlocal marker path rejected");
            Expect<IOException>(()=>TelegramBridgeWindow.TryOfferAutomaticProxy(Path.Combine(root,"absent"),key,()=>launches++),"missing component folder not created silently");
            File.WriteAllText(marker,"{}",new UTF8Encoding(false));
            Expect<IOException>(()=>TelegramBridgeWindow.TryOfferAutomaticProxy(root,key,()=>launches++),"unknown marker schema fails closed");
            Check(File.ReadAllText(marker)=="{}"&&launches==2,"malformed marker is not overwritten and launches nothing");
            File.WriteAllBytes(marker,new byte[513]);
            Expect<IOException>(()=>TelegramBridgeWindow.TryOfferAutomaticProxy(root,key,()=>launches++),"oversized marker rejected before parsing");
            Check(launches==2,"invalid metadata never opens a proxy URI");
            var uri=(string)typeof(TelegramBridgeWindow).GetMethod("BuildProxyUri",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{key});
            Check(uri=="tg://proxy?server=127.0.0.1&port=1443&secret=000102030405060708090a0b0c0d0e0f","official deep link is restricted to own loopback port and exact owned secret");
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(var form=new TelegramBridgeWindow(true))
            {
                Check(!form.Visible&&!form.ShowInTaskbar,"automatic module constructor stays hidden and out of taskbar");
                Check(!(bool)Value(form,"startupStarted")&&Value(form,"child")==null&&Value(form,"secret")==null,"constructing test form performs no runtime start, DPAPI read or network enumeration");
                Check(!((System.Windows.Forms.Timer)Value(form,"networkTimer")).Enabled,"constructor does not arm recovery timer");
            }
            using(var form=new TelegramBridgeWindow())Check(form.ShowInTaskbar&&!form.Visible,"manual module mode retains normal visible-window policy");
            Console.WriteLine("Telegram automatic setup: "+checks+" checks passed. Synthetic marker/callbacks and unshown owned forms only; no real Telegram, proxies, sessions or network touched.");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    static object Value(TelegramBridgeWindow form,string field){return typeof(TelegramBridgeWindow).GetField(field,Private).GetValue(form);}
    static void Check(bool value,string text){if(!value)throw new InvalidOperationException("FAIL: "+text);checks++;}
    static void Expect<T>(Action action,string text) where T:Exception
    {try{action();}catch(T){checks++;return;}throw new InvalidOperationException("FAIL: "+text);}
}
