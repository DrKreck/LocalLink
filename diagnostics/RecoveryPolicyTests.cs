using System;

namespace LocalLink
{
    internal static class RecoveryPolicyTests
    {
        static int checks;
        static int Main()
        {
            try
            {
                DateTime t = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc);
                var policy = new RecoveryPolicy();
                policy.UnexpectedExit(t);
                Check(!policy.DesiredRunning && !policy.Pending && !policy.TryBeginAttempt(t.AddMinutes(10)), "no auto-start before explicit user start");
                policy.UserStarted(t); policy.UnexpectedExit(t);
                Check(policy.Pending && policy.NextAttemptUtc == t.AddSeconds(5) && policy.Attempts == 0, "first crash schedules 5-second retry without starting anything");
                Check(!policy.TryBeginAttempt(t.AddSeconds(4)), "retry waits for backoff");
                policy.UnexpectedExit(t.AddSeconds(1));
                Check(policy.NextAttemptUtc == t.AddSeconds(5), "repeated exit observation cannot continuously postpone retry");
                Check(policy.TryBeginAttempt(t.AddSeconds(5)) && policy.Attempts == 1 && policy.AttemptInFlight, "first retry claims one budget slot");
                Check(!policy.TryBeginAttempt(t.AddMinutes(1)), "in-flight retry cannot overlap another attempt");
                policy.AttemptFailed(t.AddSeconds(6));
                Check(policy.Pending && policy.NextAttemptUtc == t.AddSeconds(21), "failed first retry backs off 15 seconds");
                Check(policy.TryBeginAttempt(t.AddSeconds(21)) && policy.Attempts == 2, "second retry claims second budget slot");
                policy.AttemptFailed(t.AddSeconds(22));
                Check(policy.NextAttemptUtc == t.AddSeconds(67), "failed second retry backs off 45 seconds");
                Check(policy.TryBeginAttempt(t.AddSeconds(67)) && policy.Attempts == 3, "third retry is allowed");
                policy.AttemptFailed(t.AddSeconds(68));
                Check(policy.Exhausted && !policy.Pending && !policy.TryBeginAttempt(t.AddDays(1)), "third failure stops automatic retries indefinitely");
                policy.UnexpectedExit(t.AddDays(1)); policy.Resume(t.AddDays(1));
                Check(policy.Exhausted && !policy.Pending, "extra crash or spurious resume cannot reset exhausted budget");
                policy.UserStarted(t);
                Check(policy.DesiredRunning && policy.Attempts == 0 && !policy.Exhausted, "explicit user start resets budget");
                policy.UnexpectedExit(t); policy.UserStopped();
                Check(!policy.Pending && !policy.DesiredRunning && !policy.TryBeginAttempt(t.AddDays(1)), "user stop cancels scheduled recovery");
                policy.UserStarted(t); policy.Suspend(); policy.UnexpectedExit(t);
                Check(policy.IsSuspended && !policy.Pending && !policy.TryBeginAttempt(t.AddDays(1)), "no retry during suspend");
                policy.Resume(t.AddSeconds(20));
                Check(!policy.IsSuspended && policy.Pending && policy.NextAttemptUtc == t.AddSeconds(25), "resume schedules same requested session after grace interval");
                policy.MarkRunning(t.AddSeconds(20));
                Check(!policy.Pending && !policy.TryBeginAttempt(t.AddDays(1)), "surviving module after resume cancels unnecessary restart");
                policy.Suspend(); policy.UserStopped(); policy.Resume(t.AddSeconds(30));
                Check(!policy.DesiredRunning && !policy.Pending, "user stop during suspend prevents resume restart");
                policy.UserStarted(t);policy.UnexpectedExit(t);policy.TryBeginAttempt(t.AddSeconds(5));policy.Suspend();
                policy.Resume(t.AddSeconds(6));policy.MarkRunning(t.AddSeconds(6));
                policy.UnexpectedExit(t.AddSeconds(7));
                Check(policy.Pending&&policy.DesiredRunning,"late cancelled recovery stop after Resume/MarkRunning schedules another bounded retry");
                policy.UserStopped();policy.UnexpectedExit(t.AddSeconds(8));
                Check(!policy.Pending&&!policy.DesiredRunning,"late cancelled stop cannot override a manual stop");
                policy.UserStarted(t); policy.UnexpectedExit(t); policy.Pause(true);
                Check(policy.IsPaused && !policy.TryBeginAttempt(t.AddMinutes(5)) && policy.Attempts == 0, "update, tuning or diagnostic pause consumes no retries");
                policy.Pause(false);
                Check(policy.TryBeginAttempt(t.AddMinutes(5)), "pending recovery can continue after operation completes");
                policy.AttemptFailed(t.AddMinutes(5)); policy.SetNetworkAvailable(false, t.AddMinutes(5));
                Check(!policy.TryBeginAttempt(t.AddMinutes(10)) && policy.Attempts == 1, "offline waits without burning budget");
                policy.SetNetworkAvailable(true, t.AddMinutes(10));
                Check(policy.NextAttemptUtc == t.AddMinutes(10).AddSeconds(5), "network return adds a grace interval");
                Check(!policy.TryBeginAttempt(t.AddMinutes(10).AddSeconds(4)) && policy.TryBeginAttempt(t.AddMinutes(10).AddSeconds(5)), "network grace interval is enforced");
                policy.MarkRunning(t.AddMinutes(10).AddSeconds(5));
                policy.ObserveHealthy(t.AddMinutes(12));
                Check(policy.Attempts == 2, "brief stability does not erase crash history");
                policy.ObserveHealthy(t.AddMinutes(12).AddSeconds(5));
                Check(policy.Attempts == 0, "120-second process stability resets crash budget");
                policy.UnexpectedExit(t.AddMinutes(13));
                Check(policy.NextAttemptUtc == t.AddMinutes(13).AddSeconds(5), "new crash after stable period uses initial delay");
                policy.UserStopped(); policy.MarkRunning(t);
                Check(!policy.DesiredRunning && !policy.Pending, "late success callback cannot rearm user-stopped session");
                policy.UserStarted(t); policy.UnexpectedExit(t); policy.TryBeginAttempt(t.AddSeconds(5)); policy.UserStopped(); policy.AttemptFailed(t.AddSeconds(6));
                Check(!policy.DesiredRunning && !policy.Pending, "late failure callback cannot rearm user-stopped session");
                policy.UserStarted(t); policy.UnexpectedExit(t); policy.TryBeginAttempt(t.AddSeconds(5)); policy.MarkRunning(t.AddSeconds(6));
                policy.ObserveHealthy(t.AddSeconds(1)); policy.ObserveHealthy(t.AddSeconds(120));
                Check(policy.Attempts == 1, "backward clock jump restarts stability interval instead of early budget reset");
                policy.ObserveHealthy(t.AddSeconds(121));
                Check(policy.Attempts == 0, "clock-safe stable interval eventually resets budget");
                policy.UserStarted(t); policy.UnexpectedExit(DateTime.SpecifyKind(DateTime.MaxValue.AddSeconds(-2), DateTimeKind.Utc));
                Check(policy.NextAttemptUtc == DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), "scheduling cannot overflow clock range");
                Expect<ArgumentException>(() => new RecoveryPolicy().UserStarted(DateTime.SpecifyKind(t, DateTimeKind.Unspecified)), "ambiguous local clock rejected");
                Console.WriteLine("Recovery policy: " + checks + " assertions passed; no processes, drivers or network settings touched.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        static void Check(bool value, string text) { checks++; if (!value) throw new Exception(text); }
        static void Expect<T>(Action action, string text) where T : Exception
        { checks++; try { action(); } catch (T) { return; } throw new Exception(text); }
    }
}
