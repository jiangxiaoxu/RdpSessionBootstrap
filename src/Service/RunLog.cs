using System.Globalization;
using System.Text;

public sealed class RunLog
{
    public string LogPath { get; }

    public RunLog(string directory, DateTimeOffset? startedAt = null)
    {
        DateTimeOffset started = startedAt ?? DateTimeOffset.UtcNow;
        Directory.CreateDirectory(directory);
        string timestamp = started.UtcDateTime.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture);
        LogPath = Path.Combine(directory, $"service-{timestamp}-{Guid.NewGuid():N}.log");
        using (new FileStream(LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
        RemoveExpiredLogs(directory, started.UtcDateTime.AddDays(-7));
    }

    public void Write(string message)
    {
        try
        {
            File.AppendAllText(LogPath, DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine, Encoding.UTF8);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    void RemoveExpiredLogs(string directory, DateTime cutoffUtc)
    {
        string[] files;
        try { files = Directory.GetFiles(directory); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Write("log cleanup failed: " + e.Message);
            return;
        }
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            bool owned = name.Equals("service.log", StringComparison.OrdinalIgnoreCase)
                || (name.StartsWith("service-", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase));
            if (!owned || file.Equals(LogPath, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoffUtc) File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Write("log cleanup failed for " + name + ": " + e.Message);
            }
        }
    }
}
