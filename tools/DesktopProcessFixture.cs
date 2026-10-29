using System;
using System.IO;
using System.Security.Principal;
using System.Windows.Forms;

namespace LocalLink
{
    internal static class DesktopProcessFixture
    {
        [STAThread]static int Main(string[] args)
        {
            if(args.Length!=3||args[0]!="--apply")return 2;
            string plan=Path.GetFullPath(args[1]);
            if(Path.GetFileName(plan)!="plan.json"||Path.GetDirectoryName(plan)!=AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'))return 3;
            if(DesktopProcessTests.Hash(File.ReadAllBytes(plan))!=args[2])return 4;
            using(var identity=WindowsIdentity.GetCurrent())
                if(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))return 5;
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(plan),"fixture-ready.txt"),"standard-user");return 0;
        }
    }
}
