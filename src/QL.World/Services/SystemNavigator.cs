using System.Diagnostics;
using System.IO;

namespace QL.World.Services;

/// <summary>Shell integration without any WPF dependency.</summary>
public static class SystemNavigator
{
    /// <summary>Opens a file with the OS default association.</summary>
    public static bool OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Opens Explorer with the file selected.</summary>
    public static bool RevealFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
