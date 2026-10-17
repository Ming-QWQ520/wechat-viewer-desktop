using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace WeChatViewer.Desktop.Services;

/// <summary>Run bundled native helper tools (same pattern as the original project).</summary>
public static class NativeTools
{
    /// <summary>tools/ directory next to the app executable.</summary>
    public static string ToolsDir => Path.Combine(AppContext.BaseDirectory, "tools");

    /// <summary>Runtime data directory (keys, decrypted dbs, media cache).</summary>
    public static string DataDir { get; } = Path.Combine(AppContext.BaseDirectory, "data");

    public static string WeChatKeysExe => Path.Combine(ToolsDir, "wechat-keys.exe");
    public static string ImgCheckExe => Path.Combine(ToolsDir, "imgcheck.exe");
    public static string SilkDecExe => Path.Combine(ToolsDir, "silkdec.exe");

    public static bool HasTool(string path) => File.Exists(path);

    /// <summary>Open a file or URL with the default system handler.</summary>
    public static void ShellOpen(string pathOrUrl)
    {
        try
        {
            Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true });
        }
        catch
        {
            // Ignore: missing handler or blocked by policy.
        }
    }

    /// <summary>Reveal a file in Explorer.</summary>
    public static void ShowInExplorer(string filePath)
    {
        try
        {
            Process.Start("explorer.exe", $"/select,\"{filePath}\"");
        }
        catch { }
    }

    public class ToolResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; } = "";
        public bool Success => ExitCode == 0;
    }

    public static ToolResult RunTool(string exePath, string arguments, int timeoutMs = 600000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments ?? "",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = ToolsDir,
        };
        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(true); } catch { }
            return new ToolResult { ExitCode = -1, Output = output + "\n[timeout]" };
        }
        return new ToolResult { ExitCode = process.ExitCode, Output = output.ToString() };
    }

    /// <summary>Run the async key extraction so the UI thread is never blocked.</summary>
    public static System.Threading.Tasks.Task<ToolResult> RunToolAsync(string exePath, string arguments, int timeoutMs = 600000)
        => System.Threading.Tasks.Task.Run(() => RunTool(exePath, arguments, timeoutMs));

    /// <summary>imgcheck.exe full-decode validation: "OK\t<path>\t<w>\t<h>" on success.</summary>
    public static bool ImageCheck(string tempFile)
    {
        if (!HasTool(ImgCheckExe)) return true; // tool missing: skip validation (degrades gracefully)
        var result = RunTool(ImgCheckExe, $"\"{tempFile}\"", 20000);
        return result.Output.StartsWith("OK\t");
    }
}
