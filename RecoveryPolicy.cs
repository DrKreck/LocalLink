using System;

namespace LocalLink
{
    // Pure scheduling policy. It never starts processes or switches a profile.
    // The caller retains an immutable copy of the user's last explicit start.
    public sealed class RecoveryPolicy
    {
        public const int MaximumAttempts = 3;
        public const int StableSeconds = 120;
        static readonly int[] Delays = { 5, 15, 45 };
        bool suspended, paused, networkAvailable = true, running, inFlight;
        DateTime stableSince = DateTime.MinValue;
        public bool DesiredRunning { get; private set; }
        public bool Pending { get; private set; }
        public bool Exhausted { get; private set; }
        public int Attempts { get; private set; }
        public DateTime NextAttemptUtc { get; private set; }
        public bool IsSuspended { get { return suspended; } }
        public bool IsPaused { get { return paused; } }
        public bool AttemptInFlight { get { return inFlight; } }

        public void UserStarted(DateTime now)
        {
            now = Utc(now);
            DesiredRunning = true; Attempts = 0; Exhausted = false; Pending = false; inFlight = false;
            running = true; stableSince = now; NextAttemptUtc = DateTime.MinValue;
        }
        public void UserStopped()
        {
            DesiredRunning = false; Pending = false; Exhausted = false; Attempts = 0; inFlight = false;
            running = false; stableSince = DateTime.MinValue; NextAttemptUtc = DateTime.MinValue;
        }
        public void Pause(bool value) { paused = value; }
        public void Suspend()
        {
            suspended = true; Pending = false; inFlight = false; running = false;
            stableSince = DateTime.MinValue; NextAttemptUtc = DateTime.MinValue;
        }
        public void Resume(DateTime now)
        {
            now = Utc(now);
            if (!suspended) return;
            suspended = false;
            Schedule(now);
        }
        public void SetNetworkAvailable(bool available, DateTime now)
        {
            now = Utc(now);
            bool restored = !networkAvailable && available;
            networkAvailable = available;
            if (!available) stableSince = DateTime.MinValue;
            if (restored && Pending) NextAttemptUtc = Later(NextAttemptUtc, Add(now, Delays[0]));
        }
        public void UnexpectedExit(DateTime now)
        {
            now = Utc(now);
            running = false; inFlight = false; stableSince = DateTime.MinValue;
            if (!Pending) Schedule(now);
        }
        public bool TryBeginAttempt(DateTime now)
        {
            now = Utc(now);
            if (!DesiredRunning || !Pending || suspended || paused || !networkAvailable || inFlight || Exhausted || now < NextAttemptUtc) return false;
            if (Attempts >= MaximumAttempts) { Exhausted = true; Pending = false; return false; }
            Attempts++; Pending = false; inFlight = true; running = false; stableSince = DateTime.MinValue;
            return true;
        }
        public void AttemptFailed(DateTime now)
        {
            now = Utc(now);
            if (!inFlight) return;
            inFlight = false; running = false; Schedule(now);
        }
        public void MarkRunning(DateTime now)
        {
            now = Utc(now);
            if (!DesiredRunning || suspended) return;
            running = true; inFlight = false; Pending = false; stableSince = now; NextAttemptUtc = DateTime.MinValue;
        }
        // A stable process only resets the crash budget; this is not a service
        // availability test and cannot establish that voice/game traffic works.
        public void ObserveHealthy(DateTime now)
        {
            now = Utc(now);
            if (!DesiredRunning || !running || suspended || inFlight || Pending || !networkAvailable) return;
            if (stableSince == DateTime.MinValue || now < stableSince) { stableSince = now; return; }
            if ((now - stableSince).TotalSeconds >= StableSeconds) { Attempts = 0; Exhausted = false; }
        }
        void Schedule(DateTime now)
        {
            if (!DesiredRunning || suspended || Exhausted) return;
            if (Attempts >= MaximumAttempts) { Exhausted = true; Pending = false; NextAttemptUtc = DateTime.MinValue; return; }
            Pending = true; NextAttemptUtc = Add(now, Delays[Attempts]);
        }
        static DateTime Utc(DateTime now)
        {
            if (now.Kind != DateTimeKind.Utc) throw new ArgumentException("Recovery clock must be UTC.", "now");
            return now;
        }
        static DateTime Add(DateTime now, int seconds)
        { return now > DateTime.MaxValue.AddSeconds(-seconds) ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc) : now.AddSeconds(seconds); }
        static DateTime Later(DateTime a, DateTime b) { return a > b ? a : b; }
    }
}
