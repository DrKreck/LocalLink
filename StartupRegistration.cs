using System;
using System.IO;
using Microsoft.Win32;

namespace LocalLink
{
    internal enum StartupRegistrationState
    {
        Disabled,
        Enabled,
        DifferentLocation,
        ForeignEntry
    }

    // Values are never expanded. A REG_EXPAND_SZ entry or an arbitrary command
    // with the same name is not assumed to belong to this application.
    internal sealed class StartupRegistryValue
    {
        internal readonly object Value;
        internal readonly RegistryValueKind Kind;
        internal StartupRegistryValue(object value, RegistryValueKind kind)
        {
            Value = value; Kind = kind;
        }
    }

    internal interface IStartupRegistry
    {
        StartupRegistryValue Read();
        void Write(string command);
        void Remove();
    }

    internal sealed class CurrentUserStartupRegistry : IStartupRegistry
    {
        internal const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal const string ValueName = "LocalLink";

        public StartupRegistryValue Read()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, false))
            {
                if (key == null) return null;
                object value = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                return value == null ? null : new StartupRegistryValue(value, key.GetValueKind(ValueName));
            }
        }

        public void Write(string command)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                if (key == null) throw new InvalidOperationException("Cannot open the current-user startup key.");
                key.SetValue(ValueName, command, RegistryValueKind.String);
            }
        }

        public void Remove()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, true))
            {
                if (key != null) key.DeleteValue(ValueName, false);
            }
        }
    }

    // Registering startup does not start the app, driver, filter, VPN or Telegram.
    // The GUI handles --startup by entering the tray WITHOUT enabling the filter.
    internal sealed class StartupRegistration
    {
        internal const string StartupArgument = "--startup";
        internal const int MaximumCommandLength = 260;
        readonly IStartupRegistry registry;
        readonly string executable;
        internal readonly string Command;

        internal StartupRegistration(string executablePath)
            : this(executablePath, new CurrentUserStartupRegistry()) { }

        internal StartupRegistration(string executablePath, IStartupRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException("registry");
            this.registry = registry;
            executable = NormalizeExecutable(executablePath);
            Command = BuildCommand(executable);
        }

        internal StartupRegistrationState GetState()
        {
            StartupRegistryValue value = registry.Read();
            if (value == null) return StartupRegistrationState.Disabled;
            string registeredPath;
            if (!TryReadOwnedCommand(value, out registeredPath)) return StartupRegistrationState.ForeignEntry;
            return String.Equals(executable, registeredPath, StringComparison.OrdinalIgnoreCase)
                ? StartupRegistrationState.Enabled : StartupRegistrationState.DifferentLocation;
        }

        internal void Enable()
        {
            // Reread at click time; never trust the last UI status snapshot.
            StartupRegistryValue value = registry.Read();
            string registeredPath;
            if (value != null && !TryReadOwnedCommand(value, out registeredPath))
                throw new InvalidOperationException("The LocalLink startup entry contains an unfamiliar command; it was not changed.");
            if (value != null && String.Equals((string)value.Value, Command, StringComparison.Ordinal)) return;
            registry.Write(Command);
        }

        internal void Disable()
        {
            StartupRegistryValue value = registry.Read();
            if (value == null) return;
            string registeredPath;
            if (!TryReadOwnedCommand(value, out registeredPath))
                throw new InvalidOperationException("The LocalLink startup entry contains an unfamiliar command; it was not removed.");
            // Only this one known value is removed. Other values and the key stay.
            registry.Remove();
        }

        internal static string BuildCommand(string executablePath)
        {
            string command = "\"" + NormalizeExecutable(executablePath) + "\" " + StartupArgument;
            // Windows Run/RunOnce command values are limited to 260 characters.
            if (command.Length > MaximumCommandLength)
                throw new ArgumentException("The startup command is too long. Move LocalLink to a shorter folder path.", "executablePath");
            return command;
        }

        internal static bool TryReadOwnedCommand(StartupRegistryValue value, out string path)
        {
            path = null;
            if (value == null || value.Kind != RegistryValueKind.String) return false;
            string text = value.Value as string;
            if (text == null || text.Length < StartupArgument.Length + 3 || !text.StartsWith("\"", StringComparison.Ordinal) ||
                !text.EndsWith("\" " + StartupArgument, StringComparison.Ordinal)) return false;
            string candidate = text.Substring(1, text.Length - StartupArgument.Length - 3);
            try
            {
                string normalized = NormalizeExecutable(candidate);
                if (!String.Equals(text, BuildCommand(normalized), StringComparison.OrdinalIgnoreCase)) return false;
                path = normalized; return true;
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
        }

        static string NormalizeExecutable(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || path.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0)
                throw new ArgumentException("An absolute LocalLink.exe path is required.", "path");
            // Path.IsPathRooted alone accepts drive-relative C:folder and root-
            // relative \folder. Neither is suitable for a login-time command.
            bool driveAbsolute = path.Length >= 3 && Char.IsLetter(path[0]) && path[1] == ':' &&
                (path[2] == '\\' || path[2] == '/');
            bool uncAbsolute = path.StartsWith(@"\\", StringComparison.Ordinal) &&
                !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal);
            if (!driveAbsolute && !uncAbsolute) throw new ArgumentException("An absolute LocalLink.exe path is required.", "path");
            string full = Path.GetFullPath(path);
            if (!String.Equals(Path.GetFileName(full), "LocalLink.exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only the LocalLink executable can be registered.", "path");
            if (uncAbsolute && full.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries).Length < 3)
                throw new ArgumentException("A complete UNC executable path is required.", "path");
            return full;
        }
    }
}
