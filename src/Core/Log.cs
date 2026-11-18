using System;
using System.IO;
using System.Text;

namespace Simplemize
{
    /// <summary>Plain text log in the data folder. Everything Simplemize changes is written here.</summary>
    public sealed class Log
    {
        readonly string file;
        readonly object gate = new object();
        public event Action<string> Line;

        public Log(string file)
        {
            this.file = file;
        }

        public string FilePath => file;

        public void Info(string message) => Write("    ", message);
        public void Warn(string message) => Write("WARN", message);
        public void Error(string message) => Write("FAIL", message);

        void Write(string level, string message)
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + level + "  " + message;
            lock (gate)
            {
                try
                {
                    if (file != null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(file));
                        File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
                    }
                }
                catch { /* logging must never break a run */ }
            }
            Line?.Invoke(line);
        }
    }
}
