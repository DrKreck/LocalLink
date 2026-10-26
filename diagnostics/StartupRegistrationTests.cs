// Pure fake-registry tests: no real registry, network, process or driver actions.
using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace LocalLink
{
    internal static class StartupRegistrationTests
    {
        static int assertions;
        static int Main()
        {
            try
            {
                var fake = new FakeRegistry();
                var unrelated = new StartupRegistryValue("unrelated command", RegistryValueKind.String);
                fake.Values["AnotherProgram"] = unrelated;
                var registration = new StartupRegistration(@"C:\Apps\Моя папка\LocalLink.exe", fake);
                Check(registration.Command == "\"C:\\Apps\\Моя папка\\LocalLink.exe\" --startup", "space and Unicode path is quoted");
                Check(registration.GetState() == StartupRegistrationState.Disabled, "initial state is disabled");
                registration.Disable();
                Check(fake.Removes == 0, "disabling absent registration is a no-op");
                registration.Enable();
                Check(registration.GetState() == StartupRegistrationState.Enabled, "explicit registration is detected");
                Check(fake.Writes == 1 && fake.Current.Kind == RegistryValueKind.String, "literal REG_SZ command written once");
                registration.Enable();
                Check(fake.Writes == 1, "repeated enable is idempotent");
                Check(Object.ReferenceEquals(fake.Values["AnotherProgram"], unrelated), "unrelated value preserved on enable");
                registration.Disable(); registration.Disable();
                Check(fake.Removes == 1 && registration.GetState() == StartupRegistrationState.Disabled, "repeated disable is idempotent");
                Check(Object.ReferenceEquals(fake.Values["AnotherProgram"], unrelated), "unrelated value preserved on disable");

                fake.Current = new StartupRegistryValue("\"D:\\Old portable folder\\LocalLink.exe\" --startup", RegistryValueKind.String);
                Check(registration.GetState() == StartupRegistrationState.DifferentLocation, "older portable location is distinguished");
                registration.Enable();
                Check(registration.GetState() == StartupRegistrationState.Enabled, "explicit enable updates owned stale location");
                fake.Current = new StartupRegistryValue("\"D:\\Old portable folder\\LocalLink.exe\" --startup", RegistryValueKind.String);
                registration.Disable();
                Check(registration.GetState() == StartupRegistrationState.Disabled, "explicit disable removes only owned older entry");

                string[] unsafeCommands = {
                    "calc.exe", "\" --startup", "\"\" --startup", "C:\\Apps\\LocalLink.exe --startup", "\"C:\\Apps\\other.exe\" --startup",
                    "\"C:\\Apps\\LocalLink.exe\" --startup --extra", "\"C:\\Apps\\LocalLink.exe\"",
                    "\"C:Apps\\LocalLink.exe\" --startup", "\"\\Apps\\LocalLink.exe\" --startup",
                    "\"C:\\Apps\\LocalLink.exe\" --startup\r\n", "\"C:\\Apps\\x\"y\\LocalLink.exe\" --startup"
                };
                foreach (string command in unsafeCommands)
                {
                    var value = new StartupRegistryValue(command, RegistryValueKind.String);
                    fake.Current = value;
                    Check(registration.GetState() == StartupRegistrationState.ForeignEntry, "unrecognized command is not owned");
                    ExpectRejected(delegate { registration.Enable(); }, "foreign entry cannot be overwritten");
                    ExpectRejected(delegate { registration.Disable(); }, "foreign entry cannot be deleted");
                    Check(Object.ReferenceEquals(fake.Current, value), "foreign entry preserved exactly");
                }
                fake.Current = new StartupRegistryValue(registration.Command, RegistryValueKind.ExpandString);
                Check(registration.GetState() == StartupRegistrationState.ForeignEntry, "expandable strings are not expanded or assumed owned");
                ExpectRejected(delegate { registration.Disable(); }, "expandable value is preserved");
                fake.Current = new StartupRegistryValue(123, RegistryValueKind.DWord);
                ExpectRejected(delegate { registration.Enable(); }, "non-string value is preserved");
                fake.Current = new StartupRegistryValue("\"c:\\apps\\моя папка\\locallink.EXE\" --startup", RegistryValueKind.String);
                Check(registration.GetState() == StartupRegistrationState.Enabled, "Windows path comparison is case-insensitive");
                Check(StartupRegistration.BuildCommand(@"C:\Apps\100% portable\LocalLink.exe") ==
                    "\"C:\\Apps\\100% portable\\LocalLink.exe\" --startup", "percent-containing path stays literal");
                Check(StartupRegistration.BuildCommand(@"\\server\share\folder\LocalLink.exe") ==
                    "\"\\\\server\\share\\folder\\LocalLink.exe\" --startup", "complete UNC path is quoted");
                string prefix = @"C:\", suffix = @"\LocalLink.exe";
                string maximumPath = prefix + new string('a', 248 - prefix.Length - suffix.Length) + suffix;
                Check(StartupRegistration.BuildCommand(maximumPath).Length == 260, "260-character Run command is accepted");
                string oversizedPath = prefix + new string('a', 249 - prefix.Length - suffix.Length) + suffix;
                ExpectRejected(delegate { StartupRegistration.BuildCommand(oversizedPath); }, "261-character Run command is rejected");
                fake.Current = new StartupRegistryValue("\"" + oversizedPath + "\" --startup", RegistryValueKind.String);
                Check(registration.GetState() == StartupRegistrationState.ForeignEntry, "oversized existing Run command is not assumed owned");
                foreach (string path in new[] { "LocalLink.exe", @"C:LocalLink.exe", @"\folder\LocalLink.exe", @"C:\Apps\other.exe", "C:\\bad\"name\\LocalLink.exe", @"\\server\LocalLink.exe" })
                    ExpectRejected(delegate { StartupRegistration.BuildCommand(path); }, "invalid executable path rejected");
                fake.Current = null;
                registration.GetState();
                var racedValue = new StartupRegistryValue("other owner", RegistryValueKind.String);
                fake.Current = racedValue;
                ExpectRejected(delegate { registration.Enable(); }, "enable rereads instead of trusting cached status");
                Check(Object.ReferenceEquals(fake.Current, racedValue), "changed value remains untouched");
                Console.WriteLine("Startup registration: " + assertions + " assertions passed; fake registry only.");
                return 0;
            }
            catch (Exception error)
            {
                Console.WriteLine("Startup registration test failed: " + error.GetType().Name + " " + error.Message);
                return 1;
            }
        }

        static void Check(bool result, string label)
        {
            assertions++;
            if (!result) throw new InvalidOperationException(label);
            Console.WriteLine("PASS " + label);
        }

        static void ExpectRejected(Action action, string label)
        {
            bool rejected = false;
            try { action(); }
            catch (ArgumentException) { rejected = true; }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, label);
        }

        sealed class FakeRegistry : IStartupRegistry
        {
            internal readonly Dictionary<string, StartupRegistryValue> Values = new Dictionary<string, StartupRegistryValue>();
            internal int Writes, Removes;
            internal StartupRegistryValue Current
            {
                get { StartupRegistryValue value; return Values.TryGetValue(CurrentUserStartupRegistry.ValueName, out value) ? value : null; }
                set { if (value == null) Values.Remove(CurrentUserStartupRegistry.ValueName); else Values[CurrentUserStartupRegistry.ValueName] = value; }
            }
            public StartupRegistryValue Read() { return Current; }
            public void Write(string command) { Writes++; Current = new StartupRegistryValue(command, RegistryValueKind.String); }
            public void Remove() { Removes++; Values.Remove(CurrentUserStartupRegistry.ValueName); }
        }
    }
}
