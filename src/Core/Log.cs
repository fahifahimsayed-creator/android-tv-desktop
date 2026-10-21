using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace AndroidTVDesktop.Core
{
    /// <summary>
    /// Thread-safe file log with size-based rotation (app.log, app.1.log ... app.N.log).
    /// Identical consecutive messages are collapsed. Never logs account data: callers pass only
    /// window/process/geometry facts.
    /// </summary>
    public static class Log
    {
        static readonly object sync = new object();
        static string path;
        static long maxBytes = 1024 * 1024;
        static int keepFiles = 3;
        static string last;
        static int repeats;
        static readonly LinkedList<string> recent = new LinkedList<string>();

        public static string Directory { get; private set; }
        public static string FilePath => path;
        public static event Action<string> LineWritten;

        public static void Init(string directory, long maxFileBytes = 1024 * 1024, int keep = 3)
        {
            lock (sync)
            {
                System.IO.Directory.CreateDirectory(directory);
                Directory = directory;
                path = Path.Combine(directory, "app.log");
                maxBytes = Math.Max(16 * 1024, maxFileBytes);
                keepFiles = Math.Max(1, keep);
                last = null; repeats = 0;
            }
        }

        public static void Info(string msg) => Write("INFO ", msg);
        public static void Warn(string msg) => Write("WARN ", msg);
        public static void Error(string msg) => Write("ERROR", msg);
        public static void Error(string msg, Exception ex) => Write("ERROR", msg + ": " + ex);

        static void Write(string level, string msg)
        {
            string line;
            lock (sync)
            {
                if (msg == last) { repeats++; return; }
                var sb = new StringBuilder();
                if (repeats > 0) sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} INFO  (previous message repeated {repeats} more times)");
                line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {msg}";
                sb.AppendLine(line);
                last = msg; repeats = 0;
                recent.AddLast(line);
                while (recent.Count > 500) recent.RemoveFirst();
                if (path != null)
                {
                    try
                    {
                        RotateIfNeeded();
                        File.AppendAllText(path, sb.ToString());
                    }
                    catch { /* logging must never break the app */ }
                }
            }
            try { LineWritten?.Invoke(line); } catch { }
            Debug.WriteLine(line);
        }

        static void RotateIfNeeded()
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < maxBytes) return;
            string Name(int i) => Path.Combine(Directory, $"app.{i}.log");
            if (File.Exists(Name(keepFiles))) File.Delete(Name(keepFiles));
            for (int i = keepFiles - 1; i >= 1; i--)
                if (File.Exists(Name(i))) File.Move(Name(i), Name(i + 1));
            File.Move(path, Name(1));
        }

        public static string[] Recent(int max = 200)
        {
            lock (sync) return recent.Skip(Math.Max(0, recent.Count - max)).ToArray();
        }

        public static IEnumerable<string> AllLogFiles()
        {
            if (Directory == null) yield break;
            if (File.Exists(path)) yield return path;
            for (int i = 1; i <= keepFiles; i++)
            {
                var p = Path.Combine(Directory, $"app.{i}.log");
                if (File.Exists(p)) yield return p;
            }
        }
    }
}
