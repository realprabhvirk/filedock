using System.IO;
using FileDock.Models;
using Microsoft.Win32;

namespace FileDock.Services;

/// <summary>Loads and saves <c>settings.json</c>.</summary>
public sealed class SettingsService
{
    private readonly string _file;

    public SettingsService(string file)
    {
        _file = file;
    }

    public AppSettings Current { get; private set; } = new();

    public LoadStatus LoadStatus { get; private set; }

    public void Load()
    {
        LoadResult<AppSettings> result = JsonStore.Load<AppSettings>(_file);
        Current = result.Value;
        Current.Normalize();
        LoadStatus = result.Status;
    }

    public void Save()
    {
        try
        {
            JsonStore.Save(_file, Current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not save settings", ex);
        }
    }
}

/// <summary>
/// "Start with Windows" through the standard per-user Run key. Only FileDock's own value is
/// ever read or written; no other startup entries are touched.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "FileDock";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string)
                return false;

            // Task Manager disables entries by flagging them here (odd first byte = disabled).
            using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } flags || (flags[0] & 1) == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn("Could not read the startup setting", ex);
            return false;
        }
    }

    public static bool TrySet(bool enabled)
    {
        try
        {
            using (RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled)
                {
                    string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path");
                    run.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
                }
                else
                {
                    run.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }

            // Clear a "disabled in Task Manager" flag for our own entry so the choice made here wins.
            using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException
                                       or IOException or InvalidOperationException)
        {
            Log.Error("Could not change the startup setting", ex);
            return false;
        }
    }
}
