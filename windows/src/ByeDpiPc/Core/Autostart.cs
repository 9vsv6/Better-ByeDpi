using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;

namespace ByeDpiPc.Core;

/// <summary>
/// Sign-in autostart through Task Scheduler. A "highest privileges" task is the only way to
/// start an admin app at logon without a UAC prompt (a Run-key entry would be blocked).
/// Defined via XML because plain schtasks flags would leave the 72-hour run limit and the
/// "don't start on battery" condition switched on.
/// </summary>
public static class Autostart
{
    private const string TaskName = "ByeDPI-PC";

    public static bool IsEnabled() => Schtasks($"/Query /TN \"{TaskName}\"", logErrors: false) == 0;

    public static bool Set(bool enabled)
    {
        if (!enabled) return Schtasks($"/Delete /TN \"{TaskName}\" /F") == 0;

        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var exe = SecurityElement.Escape(Environment.ProcessPath!);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{exe}</Command><Arguments>--autostart</Arguments></Exec>
              </Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), "byedpi-pc-task.xml");
        File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
        try
        {
            return Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F") == 0;
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static int Schtasks(string args, bool logErrors = true)
    {
        using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && logErrors) Log.Write($"[autostart] schtasks {args}: {err.Trim()} {stdout.Result.Trim()}");
        return p.ExitCode;
    }
}
