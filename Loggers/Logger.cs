using Akiyama.Log.Configuration;
using Akiyama.Log.Levels;
using Akiyama.Log.Types;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Akiyama.Log.Loggers
{
    public class Logger
    {

        private readonly List<string> LineCache = new List<string>();

        private readonly List<Logger> _children = new List<Logger>();
        /// <summary>
        /// Returns a read-only list of Children for this <see cref="Logger"/>, if any.
        /// </summary>
        public ReadOnlyCollection<Logger> Children
        {
            get
            {
                return this._children.AsReadOnly();
            }
        }

        public Logger Parent { get; internal set; }
        public bool IsChild { get { return Parent != null; } }


        /* Configurable Variables */
        public string Name { get; private set; } = string.Empty;
        public LoggerType Type { get; private set; }
        public int CacheSize { get; private set; } = 100;
        public int CacheBypassLength { get; private set; } = 500;
        public LogLevel Level { get; private set; } = LogLevel.INFO;
        public string LineFormat { get; private set; } = "[<time>] [<level>] <message>";
        public LogFormatter Formatter { get; private set; } = LogFormatter.DEFAULT;
        public string DateTimeFormat { get; private set; } = @"yyyy-MM-dd HH\:mm\:ss";
        public string OutputDirectory { get; private set; }
        /// <summary>
        /// The full path for this <see cref="Logger"/>, including file name.
        /// </summary>
        public string LogPath { get; private set; }
        public int MaxCycledFiles { get; private set; } = 5;
        public bool ConsoleOutputDisabled { get; private set; }
        public bool DebugOutputDisabled { get; private set; } = false;
        public Encoding Encoding { get; private set; } = Encoding.Unicode;

        public Logger(string name, string outputDirectory = "./logs/", LogLevel level = LogLevel.INFO, int maxOldFiles = 5, LoggerType type = LoggerType.REALTIME,
            LogFormatter formatter = LogFormatter.DEFAULT, int cacheSize = 100, int cacheBypassLength = 500, string lineFormat = "[<time>] [<level>]: <message>",
            string datetimeFormat = @"yyyy-MM-dd HH\:mm\:ss", bool consoleOutputDisabled = false, bool debugOutputDisabled = false)
        {
            File.WriteAllText(Path.Combine(outputDirectory, $"test.log"), "Hello World");
            this.Name = name;
            this.OutputDirectory = outputDirectory;
            this.Level = level;
            this.Type = type;
            this.Formatter = formatter;
            this.CacheSize = cacheSize;
            this.CacheBypassLength = cacheBypassLength;
            this.LineFormat = lineFormat;
            this.DateTimeFormat = datetimeFormat;
            this.ConsoleOutputDisabled = consoleOutputDisabled;
            this.DebugOutputDisabled = debugOutputDisabled;
            this.MaxCycledFiles = maxOldFiles;
            this.UpdateLogPath();
            this.MakeDirectories();
            this.TrySetUpColourConsole();
            this.CycleFiles();
        }

        internal void MakeDirectories()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.OutputDirectory));
        }

        private void CycleFiles()
        {
            if (MaxCycledFiles == 0)
            {
                if (File.Exists(LogPath))
                    File.Delete(LogPath);
            }
            else if (MaxCycledFiles == 1)
            {
                if (File.Exists(Path.Combine(OutputDirectory, $"{Path.GetFileNameWithoutExtension(LogPath)}.previous{Path.GetExtension(LogPath)}")))
                {
                    File.Delete(Path.Combine(OutputDirectory, $"{Path.GetFileNameWithoutExtension(LogPath)}.previous{Path.GetExtension(LogPath)}"));
                }
                if (File.Exists(LogPath))
                    File.Move(LogPath, Path.Combine(OutputDirectory, $"{Path.GetFileNameWithoutExtension(LogPath)}.previous{Path.GetExtension(LogPath)}"));
            }
            else
            {
                for (int x = MaxCycledFiles; x > 0; x--)
                {
                    string fileName = Path.Combine(OutputDirectory, $"{Path.GetFileNameWithoutExtension(LogPath)}.{x}{Path.GetExtension(LogPath)}");
                    if (x == MaxCycledFiles)
                    {
                        if (File.Exists(fileName))
                            File.Delete(fileName);
                        continue;
                    }

                    if (!File.Exists(fileName))
                        continue;
                    if (File.Exists(fileName))
                    {
                        string newPath = Path.Combine(OutputDirectory, $"{Path.GetFileNameWithoutExtension(LogPath)}.{x + 1}{Path.GetExtension(LogPath)}");
                        File.Move(fileName, newPath);
                    }

                }
                if (File.Exists(LogPath))
                {
                    File.Move(LogPath, Path.Combine(OutputDirectory, $"{Path.GetFileNameWithoutExtension(LogPath)}.1{Path.GetExtension(LogPath)}"));
                }
            }
        }

        private void TrySetUpColourConsole()
        {
            if (Formatter != LogFormatter.COLOUR) return;

            var handle = GetStdHandle(STD_OUTPUT_HANDLE);
            bool ready = true;
            if (!GetConsoleMode(handle, out uint mode)) { Formatter = LogFormatter.DEFAULT; ready = false; }
            mode |= ENABLE_VIRTUAL_TERMINAL_PROCESSING;
            if (!SetConsoleMode(handle, mode)) { Formatter = LogFormatter.DEFAULT; ready = false; }
            if (!ready) this.LogInternal("Couldn't set up console to allow colouring.");

            // Is there even a way to detect this shit on *nix?
        }
        private const int STD_OUTPUT_HANDLE = -11;
        private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 4;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);
        [DllImport("kernel32.dll")]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        /// <summary>
        /// Creates a child of this <see cref="Logger"/>, using this Logger's config as a base.
        /// </summary>
        /// <param name="childName">The Name of the child <see cref="Logger"/>.</param>
        /// <returns></returns>
        public Logger CreateChild(string childName)
        {
            Logger child = this.MemberwiseClone() as Logger;
            child.SetName(childName, out _, renameFile: true, silent: true);
            _children.Add(child);
            child.UpdateLogPath();
            child.MakeDirectories();
            child.TrySetUpColourConsole();
            child.CycleFiles();
            child.Parent = this;
            return child;
        }

        public void SetName(string newName, out string oldName, bool renameFile = false, bool silent = false)
        {
            oldName = this.Name;
            if (renameFile && !this.IsChild)
            {
                string oldPath = this.LogPath;
                this.UpdateLogPath();
                try
                {
                    if (File.Exists(this.LogPath))
                    {
                        File.Move(oldPath, this.LogPath);
                    }
                }
                catch
                {
                    this.Name = oldName;
                    this.LogPath = oldPath;
                    throw;
                }

            }
            this.Name = newName;
            if (!silent)
                this.LogInternal($"Logger name was changed to '{this.Name}' (was '{oldName}').");
        }

        internal void UpdateLogPath()
        {
            this.LogPath = Path.Combine(this.OutputDirectory, $"{this.Name}.log");
        }

        public void SetLevel(LogLevel level)
        {
            this.Level = level;
            this.LogInternal($"Log level changed to '{level}'.");
        }

        private void WriteCachedLinesToFile()
        {
            if (this.LineCache.Count > 0)
            {
                List<string> cache = this.LineCache.ToList();
                this.LineCache.Clear();
                using (StreamWriter w = File.AppendText(LogPath))
                {
                    w.Write(string.Join("\n", cache) + "\n");
                }
                cache.Clear();
            }
        }

        internal void AddLineToCache(string line, int rawStringLength)
        {
            // This is very similar to the old version (because it stills works for what we're doing here)
            this.LineCache.Add(line);
            if (Type == Types.LoggerType.REALTIME || this.LineCache.Count >= CacheSize || rawStringLength >= CacheBypassLength)
            {
                this.WriteCachedLinesToFile();
            }
        }

        internal void LogInternal(string message, params object[] fillers) => Log(message, (LogLevel)999, fillers);
        public void Info(string message, params object[] fillers) => Log(message, LogLevel.INFO, fillers);
        public void Log(string message, params object[] fillers) => Log(message, LogLevel.INFO, fillers);
        public void Warning(string message, params object[] fillers) => Log(message, LogLevel.WARNING, fillers);
        public void Debug(string message, params object[] fillers) => Log(message, LogLevel.DEBUG, fillers);
        public void Error(string message, Exception exception, params object[] fillers) => Error($"{message}\n{exception.Message}\n{exception.StackTrace}", fillers);
        public void Error(Exception exception) => Error($"{exception.Message}\n{exception.StackTrace}", new object[0]);
        public void Error(string message, params object[] fillers) => Log(message, LogLevel.ERROR, fillers);
        /// <summary>
        /// Logs a message.
        /// </summary>
        /// <param name="str">The <see cref="String"/> to log.</param>
        /// <param name="level">The <see cref="LogLevel"/> to log this message as.</param>
        /// <param name="fillers"><c>(optional)</c> The values you wish to use for string formatting. See <see href="https://learn.microsoft.com/en-us/dotnet/api/system.string.format?view=netframework-4.7.2"/>.</param>
        protected void Log(string str, LogLevel level, params object[] fillers)
        {

            if (level < Level) { return; }

            string msg = string.Format(str, fillers);
            string prefix = NameFormat(level);

            string time = DateTime.Now.ToString(DateTimeFormat);

            string line = LineFormat.Replace("<time>", time)
                                                .Replace("<level>", prefix)
                                                .Replace("<name>", Name)
                                                .Replace("<message>", msg);
            //line = string.Format("[{0}] [{1}] {2}: {3}", time, prefix.PadRight(7, ' '), this.Config.Name, msg);

            if (!DebugOutputDisabled && Debugger.IsAttached) { System.Diagnostics.Debug.WriteLine(Regex.Replace(line, "\u001b\\[\\d{1,2}m", "")); }
            if (!ConsoleOutputDisabled) { Console.WriteLine(line); }


            if (Formatter == LogFormatter.COLOUR) { line = Regex.Replace(line, "\u001b\\[\\d{1,2}m", ""); }
            //if (IsChild && ShareFileWithParent) { this.Parent.AddLineToCache(line, msg.Length); }
            //else { this.AddLineToCache(line, msg.Length); }
            AddLineToCache(line, msg.Length);

        }


        private string NameFormat(LogLevel level) // This is horrible lol
        {
            string prefix = level.ToString().ToUpper();
            if (Formatter == LogFormatter.DEFAULT)
            {
                switch (level)
                {
                    case LogLevel.ALWAYS:
                    case LogLevel.VERBOSE:
                    case LogLevel.INFO:
                        prefix = "INFO";
                        break;
                    case LogLevel.WARNING:
                        prefix = "WARNING";
                        break;
                    case LogLevel.ERROR:
                        prefix = "ERROR";
                        break;
                    case LogLevel.DEBUG:
                        prefix = "DEBUG";
                        break;
                    default:
                        prefix = "-------";
                        break;
                }
            }
            else if (Formatter == LogFormatter.EMOJI)
            {
                switch (level)
                {
                    case LogLevel.ALWAYS:
                    case LogLevel.VERBOSE:
                        return "💭";
                    case LogLevel.INFO:
                        return "ℹ️";
                    case LogLevel.WARNING:
                        return "⚠️";
                    case LogLevel.ERROR:
                        return "❌";
                    case LogLevel.DEBUG:
                        return "🔵";
                    default:
                        return "--";
                }
            }
            else if (Formatter == LogFormatter.COLOUR)
            {
                switch (level)
                {
                    case LogLevel.ALWAYS:
                    case LogLevel.VERBOSE:
                    case LogLevel.INFO:
                        return $"\x1b[34m{prefix,-7}\x1b[0m";
                    case LogLevel.WARNING:
                        return $"\x1b[33m{prefix,-7}\x1b[0m";
                    case LogLevel.ERROR:
                        return $"\x1b[31m{prefix,-7}\x1b[0m";
                    case LogLevel.DEBUG:
                        return $"\x1b[35m{prefix,-7}\x1b[0m";
                    default:
                        return "-------";
                }
            }
            return prefix.PadRight(7, ' ');
        }

    }

}
