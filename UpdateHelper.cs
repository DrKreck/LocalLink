using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace LocalLink
{
    internal static class UpdateHelper
    {
        // The helper never elevates, invokes a shell, changes network settings,
        // or accepts arbitrary commands. It consumes one hash-bound local plan.
        [STAThread]
        static int Main(string[] args)
        {
            if (args == null || args.Length != 3 || args[0] != "--apply" || AppUpdateLauncher.IsElevated) return 2;
#if LOCALLINK_UPDATER_TESTS
            // The desktop broker inherits Explorer's environment, not the test
            // parent's. Only this synthetic build accepts its isolated root.
            Environment.SetEnvironmentVariable("LOCALLINK_UPDATE_FIXTURE_ROOT", Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(args[1]))));
#endif
            AppUpdatePlan plan = null; bool changed = false, parentExited = false;
            try
            {
                plan = AppUpdateInstaller.LoadPlan(args[1], args[2], AppUpdateClient.DefaultRoot);
                using (var transactionLock = new FileStream(Path.Combine(plan.Directory, "helper.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    AppUpdateHandoffControl.ThrowIfAborted(plan, args[2]);
                    Preflight(plan);
                    WaitForParent(plan, args[2]); parentExited = true;
                    AppUpdateHandoffControl.ThrowIfAborted(plan, args[2]);
                    AppUpdateFiles.ValidateApplication(Path.Combine(plan.Target, "LocalLink.exe"), plan.PreviousVersion);
                    AppUpdateInstaller.SavePending(plan, args[2]);
                    AppUpdateInstaller.ApplyFiles(plan,
#if LOCALLINK_UPDATER_TESTS
                        index => { if (index == 1 && Environment.GetEnvironmentVariable("LOCALLINK_UPDATE_TEST_INTERRUPT") == "1") { AppUpdateFiles.WriteNew(Path.Combine(plan.Directory, "fixture-after-first"), new byte[] { 1 }); Thread.Sleep(60000); } }
#else
                        null
#endif
                    ); changed = true;
                    if (LaunchAndConfirm(plan, args[1], args[2]))
                    {
                        // Health is confirmed: never replace files underneath the
                        // new live process just because rollback metadata failed.
                        changed=false;
                        AppUpdateInstaller.CommitHealthy(plan,args[2]);return 0;
                    }
                    AppUpdateInstaller.RestoreFiles(plan); changed = false;
                    StartPrevious(plan); return 3;
                }
            }
            catch (OperationCanceledException)
            {
                if (plan != null) try { AppUpdateInstaller.Mark(plan, "cancelled"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                return 4;
            }
            catch (Exception error)
            {
#if LOCALLINK_UPDATER_TESTS
                if (plan != null) try { File.WriteAllText(Path.Combine(plan.Directory, "fixture-helper-error.txt"), error.ToString()); } catch { }
#else
                GC.KeepAlive(error);
#endif
                if (plan != null)
                {
                    try { if (changed) AppUpdateInstaller.RestoreFiles(plan); if (parentExited) StartPrevious(plan); AppUpdateInstaller.Mark(plan, "failed"); }
                    catch { }
                }
                return 1;
            }
        }
        static void Preflight(AppUpdatePlan plan)
        {
            AppUpdateInstaller.VerifyFiles(Path.Combine(plan.Directory, "payload"), plan.Incoming);
            AppUpdateInstaller.VerifyFiles(Path.Combine(plan.Directory, "backup"), plan.Previous);
            AppUpdateInstaller.VerifyFiles(plan.Target, plan.Previous);
            string probe = Path.Combine(plan.Target, ".locallink-update-probe-" + plan.HealthToken);
            AppUpdateFiles.NoReparse(probe);
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            { stream.WriteByte(1); stream.Flush(true); }
        }
        static void WriteReady(AppUpdatePlan plan, string hash)
        {
            int pid = Process.GetCurrentProcess().Id;
            byte[] ready = AppUpdateFiles.Json(new { Schema = 1, PlanSha256 = hash, HealthToken = plan.HealthToken,
                ProcessId = pid, StartedUtcTicks = DesktopProcess.QueryStartTimeUtcTicks(pid) });
            string path = Path.Combine(plan.Directory, "helper-ready.json");
            AppUpdateFiles.WriteNew(path + ".tmp", ready); AppUpdateFiles.NoReparse(path); File.Move(path + ".tmp", path);
        }
        static void WaitForParent(AppUpdatePlan plan, string hash)
        {
            DesktopProcess.ObservedProcess parent;
            try { parent = DesktopProcess.Observe(plan.ParentId); }
            catch (Win32Exception error) { if (error.NativeErrorCode != 87) throw; RequireConfirmedExit(plan, hash); return; }
            using (parent)
            {
                if (parent.StartedUtcTicks != plan.ParentStartedUtcTicks) throw new IOException("Процесс приложения изменился.");
                string path = parent.ImagePath;
                if (!AppUpdateInstaller.SamePath(path, Path.Combine(plan.Target, "LocalLink.exe")) || AppUpdateFiles.HashFile(path) != plan.ParentSha256)
                    throw new IOException("Запущена другая копия приложения.");
                // Ready includes permission to observe this exact parent. An
                // elevated-parent query failure must leave the old runtime up.
                WriteReady(plan, hash);
                var clock = Stopwatch.StartNew();
                while (!parent.HasExited)
                {
                    AppUpdateHandoffControl.ThrowIfAborted(plan, hash);
                    if (clock.ElapsedMilliseconds >= 120000) throw new IOException("Приложение не завершилось для обновления.");
                    Thread.Sleep(100);
                }
            }
            RequireConfirmedExit(plan, hash);
        }
        static void RequireConfirmedExit(AppUpdatePlan plan, string hash)
        {
            AppUpdateHandoffControl.ThrowIfAborted(plan, hash);
            if (!AppUpdateHandoffControl.IsSet(plan, hash, "exit-confirmed"))
                throw new OperationCanceledException("Приложение завершилось без подтверждения обновления.");
        }
        static bool LaunchAndConfirm(AppUpdatePlan plan, string path, string hash)
        {
            string executable = Path.Combine(plan.Target, "LocalLink.exe");
            bool healthProtocol = AppUpdateHealth.SupportsStartupGate(plan.Version);
            if (AppUpdateFiles.HashFile(executable) != plan.Incoming.Single(value => value.Name == "LocalLink.exe").Sha256) throw new IOException("Приложение обновления повреждено.");
            using (var process = Process.Start(new ProcessStartInfo { FileName = executable, WorkingDirectory = plan.Target,
                Arguments = healthProtocol ? "--update-health " + AppUpdateLauncher.Quote(path) + " " + hash : "", UseShellExecute = false }))
            {
                if (process == null) return false;
                var deadline = Stopwatch.StartNew(); bool acknowledged = false;
                while (deadline.ElapsedMilliseconds < 45000)
                {
                    if (process.HasExited) return false;
                    string marker = Path.Combine(plan.Directory, "healthy");
                    if (!healthProtocol) acknowledged = process.MainWindowHandle != IntPtr.Zero;
                    else if (File.Exists(marker)) acknowledged = AppUpdateFiles.Utf8.GetString(AppUpdateFiles.Read(marker, 128)) == plan.HealthToken;
                    if (acknowledged && deadline.ElapsedMilliseconds >= 5000) return true;
                    Thread.Sleep(200);
                }
                // This is the exact process we launched, never another LocalLink.
                if (!process.HasExited) { process.Kill(); if (!process.WaitForExit(10000)) throw new IOException("Новая версия не завершилась для отката."); }
                return false;
            }
        }
        static void StartPrevious(AppUpdatePlan plan)
        {
            string executable = Path.Combine(plan.Target, "LocalLink.exe");
            if (AppUpdateFiles.HashFile(executable) != plan.Previous.Single(value => value.Name == "LocalLink.exe").Sha256) return;
            Process.Start(new ProcessStartInfo { FileName = executable, WorkingDirectory = plan.Target, UseShellExecute = false });
        }
    }
}
