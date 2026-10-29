// Synthetic application used ONLY by Test-AppUpdates.ps1 in its private fixture.
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
[assembly: AssemblyProduct("LocalLink")]
#if OLD_UPDATE_FIXTURE
[assembly: AssemblyVersion("0.8.5.0")]
[assembly: AssemblyFileVersion("0.8.5.0")]
#else
[assembly: AssemblyVersion("0.9.0.0")]
[assembly: AssemblyFileVersion("0.9.0.0")]
#endif
namespace LocalLink
{
    internal static class AppUpdateFixture
    {
        [STAThread]
        static int Main(string[] args)
        {
            try
            {
#if BAD_UPDATE_FIXTURE
                return 7;
#endif
                if (args.Length == 3 && (args[0] == "--fixture-update" || args[0] == "--fixture-desktop-update"))
                {
                    if (args[0] == "--fixture-desktop-update") Environment.SetEnvironmentVariable("LOCALLINK_UPDATE_TEST_DESKTOP", "1");
                    AppUpdateLauncher.LaunchApply(new StagedAppUpdate { Directory = args[1], ArchiveSha256 = args[2], Version = "0.9.0" }); return 0;
                }
                if (args.Length == 3 && (args[0] == "--fixture-cancel-update" || args[0] == "--fixture-dispose-update" || args[0] == "--fixture-unconfirmed-update" || args[0] == "--fixture-revoke-update"))
                {
                    var staged = new StagedAppUpdate { Directory = args[1], ArchiveSha256 = args[2], Version = "0.9.0" };
                    var handoff = AppUpdateLauncher.LaunchApplyAsync(staged, CancellationToken.None).GetAwaiter().GetResult();
                    if (args[0] == "--fixture-cancel-update") { handoff.Cancel(); handoff.Dispose(); }
                    else if (args[0] == "--fixture-revoke-update") { handoff.ConfirmExit(); handoff.Cancel(); handoff.Dispose(); }
                    else if (args[0] == "--fixture-dispose-update") handoff.Dispose();
                    // The final branch deliberately exits without disposal or
                    // confirmation, simulating a parent dying after readiness.
                    return 0;
                }
                if (args.Length == 1 && args[0] == "--fixture-rollback")
                {
                    var rollback = AppUpdateLauncher.FindRollback();
                    if (rollback == null) return 8;
                    AppUpdateLauncher.LaunchRollback(rollback); return 0;
                }
                Application.EnableVisualStyles();
                using (var form = new Form { Text = "LocalLink updater fixture", Width = 360, Height = 140, ShowInTaskbar = true,
                    StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-32000,-32000) })
                {
                    form.Shown += delegate { if (AppUpdateHealth.IsHealthLaunch(args)) AppUpdateHealth.Acknowledge(args); };
                    Application.Run(form);
                }
                return 0;
            }
            catch (Exception error) { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixture-error.txt"), error.ToString()); return 9; }
        }
    }
}
