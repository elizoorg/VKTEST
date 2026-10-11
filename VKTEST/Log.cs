using System;
using System.Collections.Generic;
using System.Text;

namespace BmwBenchmarkAutomator;

public static class Log
{
    private static readonly object Sync = new();

    public static void Plain(string m) { lock (Sync) Console.WriteLine(m); }
    public static void Info(string m) { lock (Sync) Console.WriteLine("  " + m); }

    public static void Success(string m) { Colored(ConsoleColor.Green, "  [OK] " + m); }
    public static void Warn(string m) { Colored(ConsoleColor.Yellow, "  [!] " + m); }
    public static void Error(string m) { Colored(ConsoleColor.Red, "  [X] " + m); }

    public static void Section(string title)
    {
        lock (Sync)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"════════ {title} ════════");
            Console.ResetColor();
        }
    }

    private static void Colored(ConsoleColor c, string m)
    {
        lock (Sync)
        {
            Console.ForegroundColor = c;
            Console.WriteLine(m);
            Console.ResetColor();
        }
    }
}