using System;
using System.IO;

namespace NetBoxSync.Utilities;

public static class SyncLogger
{
    private static readonly string LogFilePath = "sync_log.txt";

    public static void Info(string message) => Log("INFO", message);
    public static void Warning(string message) => Log("WARNING", message);
    public static void Error(string message) => Log("ERROR", message);

    private static void Log(string level, string message)
    {
        string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
        
        if (level == "ERROR")
            Console.Error.WriteLine(logLine);
        else
            Console.WriteLine(logLine);

        try
        {
            File.AppendAllText(LogFilePath, logLine + Environment.NewLine);
        }
        catch 
        {
            // Ignore file write errors to prevent crashing
        }
    }
}
