// Own settings-storage fixtures only. No user preferences, network, or deletes.
// Compile against LocalLink.exe; internal fixture seams are called by reflection.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using LocalLink;

internal static class SettingsStorageTests
{
    static int assertions;
    static readonly Encoding Utf8=new UTF8Encoding(false);
    static readonly MethodInfo LoadMethod=typeof(AppSettings).GetMethod("LoadFromPath",BindingFlags.NonPublic|BindingFlags.Static);
    static readonly MethodInfo SaveMethod=typeof(AppSettings).GetMethod("SaveToPath",BindingFlags.NonPublic|BindingFlags.Instance);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.U1)]
    static extern bool CreateSymbolicLink(string link,string target,int flags);

    static int Main()
    {
        string fixture=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"settings-test-fixtures",Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(fixture);
            Check(LoadMethod!=null&&SaveMethod!=null,"workspace storage seams are present");
            string path=Path.Combine(fixture,"settings-v3.json");
            string legacy=Path.Combine(fixture,"settings-v2.json");
            Check(Load(path,legacy).Appearance=="system","missing preferences use defaults without creating a file");
            Check(!File.Exists(path),"load does not write primary");
            File.WriteAllText(legacy,"{\"Strategy\":\"disorder\",\"Mode\":1}",Utf8);
            string legacyOriginal=File.ReadAllText(legacy,Utf8);
            Check(Load(path,legacy).Strategy=="disorder"&&Load(path,legacy).Appearance=="system","v2 migration retains preferences and default appearance");

            Save(new AppSettings{Appearance="light",Strategy="split"},path,null);
            Check(File.Exists(path)&&Load(path,legacy).Appearance=="light","initial atomic save loads normally");
            Check(Directory.GetFiles(fixture,"settings-v3.json.backup-*").Length==0,"initial save does not manufacture a backup");
            string first=File.ReadAllText(path,Utf8);
            int unchangedStages=Directory.GetFiles(fixture,"settings-v3.json.staged-*").Length;
            int unchangedBackups=Directory.GetFiles(fixture,"settings-v3.json.backup-*").Length;
            bool unchangedHookCalled=false;
            Save(new AppSettings{Appearance="light",Strategy="split"},path,delegate(string stage){unchangedHookCalled=true;});
            Save(new AppSettings{Appearance="light",Strategy="split"},path,null);
            Check(File.ReadAllText(path,Utf8)==first,"unchanged save preserves exact primary bytes");
            Check(Directory.GetFiles(fixture,"settings-v3.json.backup-*").Length==unchangedBackups,"unchanged saves do not create backups");
            Check(Directory.GetFiles(fixture,"settings-v3.json.staged-*").Length==unchangedStages&&!unchangedHookCalled,"unchanged saves do not stage or invoke before-commit hook");
            Save(new AppSettings{Appearance="dark",Strategy="disorder"},path,null);
            Check(Load(path,legacy).Appearance=="dark"&&Load(path,legacy).Strategy=="disorder","update commits new appearance and strategy");
            string[] backups=Directory.GetFiles(fixture,"settings-v3.json.backup-*");
            Check(backups.Length==1&&File.ReadAllText(backups[0],Utf8)==first,"atomic update preserves preceding exact bytes");
            string second=File.ReadAllText(path,Utf8);
            Save(new AppSettings{Appearance="system",Strategy="fake-split"},path,null);
            backups=Directory.GetFiles(fixture,"settings-v3.json.backup-*");
            Check(backups.Length==2&&HasContents(backups,first)&&HasContents(backups,second),"successive saves retain older backups");

            File.WriteAllText(path,"{broken-json",Utf8);
            Check(Load(path,legacy).Appearance=="dark"&&Load(path,legacy).Strategy=="disorder"&&Load(path,legacy).RecoveredFromBackup,"damaged primary recovers newest valid backup and reports source");
            Check(File.ReadAllText(path,Utf8)=="{broken-json","backup recovery is read-only and preserves damaged primary");
            string invalidBackup=path+".backup-9999999999999999999-invalid";
            File.WriteAllText(invalidBackup,"null",Utf8);
            Check(Load(path,legacy).Appearance=="dark","malformed newest backup is skipped");
            Save(new AppSettings{Appearance="light"},path,null);
            Check(Load(path,legacy).Appearance=="light"&&!Load(path,legacy).RecoveredFromBackup,"a subsequent save can replace damaged primary safely");
            Check(File.ReadAllText(path,Utf8).IndexOf("RecoveredFromBackup",StringComparison.Ordinal)<0,"recovery status is not serialized as a preference");
            Check(File.ReadAllText(legacy,Utf8)==legacyOriginal,"v2 source remains unchanged");

            string preserved=File.ReadAllText(path,Utf8);
            int stagesBefore=Directory.GetFiles(fixture,"settings-v3.json.staged-*").Length;
            int backupsBefore=Directory.GetFiles(fixture,"settings-v3.json.backup-*").Length;
            bool failed=false;
            try{Save(new AppSettings{Appearance="dark"},path,delegate(string stage){throw new IOException("synthetic before-commit failure");});}
            catch(IOException){failed=true;}
            Check(failed,"before-commit write failure is surfaced");
            Check(File.ReadAllText(path,Utf8)==preserved,"failed commit retains previous exact primary bytes");
            Check(Directory.GetFiles(fixture,"settings-v3.json.backup-*").Length==backupsBefore,"failed precommit does not alter backups");
            Check(Directory.GetFiles(fixture,"settings-v3.json.staged-*").Length==stagesBefore+1,"failed stage is retained, not deleted");
            Check(Load(path,legacy).Appearance=="light","uncommitted stages are not used for recovery");
            stagesBefore=Directory.GetFiles(fixture,"settings-v3.json.staged-*").Length;
            bool lockedFailure=false;
            using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                try{Save(new AppSettings{Appearance="dark"},path,null);}
                catch(IOException){lockedFailure=true;}
            }
            Check(lockedFailure&&File.ReadAllText(path,Utf8)==preserved,"atomic replace failure on locked fixture preserves primary");
            Check(Directory.GetFiles(fixture,"settings-v3.json.staged-*").Length==stagesBefore+1,"failed atomic replace retains its stage");

            string recoveryPath=Path.Combine(fixture,"recovery-depth.json");
            for(int i=0;i<48;i++)
                File.WriteAllText(recoveryPath+".backup-"+i.ToString("D19")+"-fixture",i==17?"{\"Appearance\":\"dark\"}":"{bad",Utf8);
            Check(Load(recoveryPath,null).Appearance=="dark"&&Load(recoveryPath,null).RecoveredFromBackup,"recovery examines at least the latest 32 candidates");
            File.WriteAllText(recoveryPath+".backup-"+17.ToString("D19")+"-fixture","{bad",Utf8);
            File.WriteAllText(recoveryPath+".backup-"+0.ToString("D19")+"-fixture","{\"Appearance\":\"dark\"}",Utf8);
            Check(Load(recoveryPath,null).Appearance=="system","recovery reads at most the latest 32 candidates");

            string legacyFallbackPath=Path.Combine(fixture,"legacy-fallback.json");
            File.WriteAllText(legacyFallbackPath,"{broken",Utf8);
            Check(Load(legacyFallbackPath,legacy).Strategy=="disorder","damaged primary with no backup still safely migrates v2");

            string boundPath=Path.Combine(fixture,"bounded.json");
            string valid="{\"Appearance\":\"dark\"}";
            File.WriteAllText(boundPath,valid+new string(' ',16384-Utf8.GetByteCount(valid)),Utf8);
            Check(new FileInfo(boundPath).Length==16384&&Load(boundPath,null).Appearance=="dark","exact 16384-byte JSON is accepted");
            File.WriteAllText(boundPath,valid+new string(' ',16385-Utf8.GetByteCount(valid)),Utf8);
            Check(Load(boundPath,null).Appearance=="system","16385-byte primary is rejected");
            File.WriteAllText(boundPath,"{\"Appearance\":\"dark\",\"Ignored\":\""+new string('\u0430',8300)+"\"}",Utf8);
            Check(Load(boundPath,null).Appearance=="system","UTF-8 byte bound applies to multibyte JSON");
            File.WriteAllBytes(boundPath,new byte[]{0x7B,0x22,0xFF,0x22,0x3A,0x31,0x7D});
            Check(Load(boundPath,null).Appearance=="system","invalid UTF-8 is rejected");
            File.WriteAllText(boundPath,"null",Utf8);
            Check(Load(boundPath,null).Appearance=="system","null JSON cannot become trusted settings");
            bool sizeRejected=false;
            try{Save(new AppSettings{Appearance="dark"},path,delegate(string stage){File.AppendAllText(stage,new string(' ',17000),Utf8);});}
            catch(IOException){sizeRejected=true;}
            Check(sizeRejected&&File.ReadAllText(path,Utf8)==preserved,"oversized staged content is rejected before atomic commit");

            string normalizePath=Path.Combine(fixture,"normalized.json");
            Save(new AppSettings{Appearance="wrong",Mode=9,Port=0,Strategy="bad",Services=new List<string>{"discord","discord","bad"},CustomDomains="Example.org\nexample.org"},normalizePath,null);
            AppSettings normalized=Load(normalizePath,null);
            Check(normalized.Appearance=="system"&&normalized.Mode==0&&normalized.Port==443&&normalized.Strategy=="split","save/load consistently normalize bounded preferences");
            Check(normalized.Services.Count==1&&normalized.Services[0]=="discord"&&normalized.CustomDomains=="example.org","services and domains normalize consistently");
            int normalizedBackups=Directory.GetFiles(fixture,"normalized.json.backup-*").Length;
            Save(new AppSettings{Appearance="wrong",Mode=9,Port=0,Strategy="bad",Services=new List<string>{"discord","discord","bad"},CustomDomains="Example.org\nexample.org"},normalizePath,null);
            Check(Directory.GetFiles(fixture,"normalized.json.backup-*").Length==normalizedBackups,"deduplication compares bytes after normalization and serialization");
            string equivalentPath=Path.Combine(fixture,"equivalent-json.json");
            string equivalent="{\"Appearance\":\"light\",\"Strategy\":\"split\"}";
            File.WriteAllText(equivalentPath,equivalent,Utf8);
            Save(new AppSettings{Appearance="light",Strategy="split"},equivalentPath,null);
            string[] equivalentBackups=Directory.GetFiles(fixture,"equivalent-json.json.backup-*");
            Check(equivalentBackups.Length==1&&File.ReadAllText(equivalentBackups[0],Utf8)==equivalent,"semantically equivalent but different bytes still make an atomic revision");
            string oversizedPath=Path.Combine(fixture,"oversized-primary.json");
            string oversized=new string(' ',17000);
            File.WriteAllText(oversizedPath,oversized,Utf8);
            Save(new AppSettings{Appearance="light"},oversizedPath,null);
            string[] oversizedBackups=Directory.GetFiles(fixture,"oversized-primary.json.backup-*");
            Check(Load(oversizedPath,null).Appearance=="light"&&oversizedBackups.Length==1&&new FileInfo(oversizedBackups[0]).Length==17000,"oversized existing primary is not read for dedup and is preserved during repair");
            string bomPath=Path.Combine(fixture,"bom.json");
            File.WriteAllText(bomPath,"{\"Appearance\":\"dark\"}",new UTF8Encoding(true));
            Check(Load(bomPath,null).Appearance=="dark","UTF-8 BOM legacy files load safely");

            string linked=Path.Combine(fixture,"reparse-directory");
            if(CreateSymbolicLink(linked,fixture,3)||CreateSymbolicLink(linked,fixture,1))
            {
                string linkedPath=Path.Combine(linked,"settings-v3.json");
                bool linkRejected=false;
                try{Save(new AppSettings{Appearance="dark"},linkedPath,null);}
                catch(IOException){linkRejected=true;}
                Check(linkRejected&&File.ReadAllText(path,Utf8)==preserved,"reparse ancestor prevents saving or changing target");
                Check(Load(linkedPath,null).Appearance=="system","reparse ancestor prevents loading target preferences");
            }
            else Console.WriteLine("SKIP: fixture symlink unavailable; no privileges or Windows settings changed.");
            Console.WriteLine("PASS: "+assertions+" settings-storage assertions. Fixture files retained: "+fixture);
            return 0;
        }
        catch(Exception e){Console.Error.WriteLine("FAIL: "+e);Console.Error.WriteLine("Fixture files retained: "+fixture);return 1;}
    }
    static AppSettings Load(string path,string legacy)
    {
        try{return (AppSettings)LoadMethod.Invoke(null,new object[]{path,legacy});}
        catch(TargetInvocationException e){throw e.InnerException;}
    }
    static void Save(AppSettings settings,string path,Action<string> beforeCommit)
    {
        try{SaveMethod.Invoke(settings,new object[]{path,beforeCommit});}
        catch(TargetInvocationException e){throw e.InnerException;}
    }
    static bool HasContents(string[] paths,string expected)
    {
        foreach(string path in paths)if(File.ReadAllText(path,Utf8)==expected)return true;
        return false;
    }
    static void Check(bool value,string message)
    {
        if(!value)throw new InvalidOperationException(message);
        assertions++;Console.WriteLine("PASS: "+message);
    }
}
