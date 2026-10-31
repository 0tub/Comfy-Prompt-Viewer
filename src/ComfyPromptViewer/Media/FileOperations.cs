using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Tmds.DBus.Protocol;

namespace ComfyPromptViewer;

// The only code that changes or reveals source files. Deleting is the one write the app ever makes to them,
// and only on an explicit user command. Each platform's mechanism lives here, not in the window.
internal static class FileOperations
{
    public static bool DeleteIsRecoverable => OperatingSystem.IsWindows();

    // Windows sends to the Recycle Bin; elsewhere the delete is permanent. A path that is already gone counts
    // as deleted. Returns the paths that are gone and how many could not be deleted.
    public static (List<string> Deleted, int FailedCount) Delete(IReadOnlyList<string> paths)
    {
        var deleted = new List<string>(paths.Count);
        var failedCount = 0;
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    if (OperatingSystem.IsWindows())
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                            path,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    }
                    else
                    {
                        File.Delete(path);
                    }
                }

                deleted.Add(path);
            }
            // Cancelling the Windows shell error dialog surfaces as OperationCanceledException.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or OperationCanceledException)
            {
                failedCount++;
                DebugLog.Write($"Could not delete image '{path}': {ex.Message}");
            }
        }

        return (deleted, failedCount);
    }

    // Opens the file manager with the file selected. launcher opens the containing folder where selecting is
    // not available.
    public static void Reveal(string filePath, ILauncher? launcher)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                RevealOnWindows(filePath);
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = $"-R \"{filePath}\"",
                    UseShellExecute = true
                });
            }
            else
            {
                DebugLog.Observe(RevealOnFreedesktopAsync(filePath, launcher), $"Reveal {filePath}");
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to open file manager for {filePath}: {ex.Message}");
        }
    }

    private static void RevealOnWindows(string filePath)
    {
        var windowsPath = Path.GetFullPath(filePath);
        var success = false;
        try
        {
            var hr = SHParseDisplayName(windowsPath, IntPtr.Zero, out var pidl, 0, out _);
            if (hr == 0 && pidl != IntPtr.Zero)
            {
                try
                {
                    success = SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0) == 0;
                }
                finally
                {
                    ILFree(pidl);
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to select image in Explorer shell API for {filePath}: {ex.Message}");
        }

        if (!success)
        {
            Process.Start("explorer.exe", $"/select,\"{windowsPath}\"");
        }
    }

    // org.freedesktop.FileManager1.ShowItems opens the folder with the file selected; Dolphin, Nautilus, Nemo,
    // Thunar and Caja implement it. Without that service, open the containing folder instead.
    private static async Task RevealOnFreedesktopAsync(string filePath, ILauncher? launcher)
    {
        filePath = Path.GetFullPath(filePath);
        try
        {
            using var connection = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("No session bus"));
            await connection.ConnectAsync();
            MessageBuffer message;
            using (var writer = connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader("org.freedesktop.FileManager1", "/org/freedesktop/FileManager1",
                    "org.freedesktop.FileManager1", "ShowItems", "ass", MessageFlags.None);
                writer.WriteArray(new[] { new Uri(filePath).AbsoluteUri });
                writer.WriteString(""); // startup id
                message = writer.CreateMessage();
            }
            await connection.CallMethodAsync(message);
            return;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"FileManager1.ShowItems failed for {filePath}, opening the folder: {ex.Message}");
        }

        if (Path.GetDirectoryName(filePath) is { } directory && launcher is not null)
        {
            await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(directory));
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr pidlFolder,
        uint cidl,
        IntPtr apidl,
        uint dwFlags);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);
}
