using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace BerthMenu.Services
{
    /// <summary>Settings → Startup → Start as Admin, without a UAC prompt every time.
    ///
    /// Restarting as administrator the usual way ("runas") makes Windows ask for
    /// permission every time. A Task Scheduler task set to "Run with highest
    /// privileges" doesn't: once it exists, BerthMenu can start it from a normal,
    /// non-admin copy and the new copy comes up as admin with no prompt. Creating
    /// the task is the only step that needs admin rights, so Windows asks once
    /// (when Start as Admin is saved), or not at all if BerthMenu is already
    /// running as admin then.
    ///
    /// The task has no trigger of its own: sign-in still goes through the normal
    /// Run key entry (AutostartService), and that copy hands over to the task
    /// (App.RestartAsAdministrator). Its action is this .exe with
    /// --elevated-relaunch, so the admin copy waits for the old one to close.</summary>
    public static class AdminTaskService
    {
        private const string FolderPath = @"\BerthMenu";
        private const string TaskLeafName = "Start as admin";
        private const string TaskPath = @"\BerthMenu\Start as admin"; // for schtasks /TN
        private const string OldTaskPath = @"\StartDock\Start as admin"; // before the rename
        private const string RelaunchArg = "--elevated-relaunch";

        /// <summary>True if the task exists and starts this copy's .exe (it won't
        /// after BerthMenu is installed somewhere else — then it's set up again).</summary>
        public static bool IsSetUp()
        {
            try
            {
                dynamic? task = GetTask();
                if (task == null)
                    return false;
                string path = ((string)task.Definition.Actions.Item(1).Path ?? "").Trim().Trim('"');
                return string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Starts the task: a new copy of BerthMenu, as admin. False if the
        /// task isn't there or Windows wouldn't start it.</summary>
        public static bool Run()
        {
            try
            {
                dynamic? task = GetTask();
                if (task == null)
                    return false;
                task.Run(null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Creates the task, or replaces it (for a new .exe path). Needs
        /// admin rights: with <paramref name="elevated"/> it just happens; otherwise
        /// Windows asks first. False if it didn't work or the prompt was declined.</summary>
        public static bool Create(bool elevated)
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return false;

            string xmlPath = Path.Combine(Path.GetTempPath(), "BerthMenu-admin-task.xml");
            try
            {
                // Task Scheduler expects the XML as UTF-16, matching its declaration.
                File.WriteAllText(xmlPath, BuildTaskXml(exe), Encoding.Unicode);
                return RunSchtasks($"/Create /TN \"{TaskPath}\" /XML \"{xmlPath}\" /F", elevated);
            }
            catch
            {
                return false;
            }
            finally
            {
                try { File.Delete(xmlPath); } catch { /* temp file, fine to leave */ }
            }
        }

        /// <summary>Removes the task from before the rename (StartDock), which points
        /// at the old .exe. Needs admin rights, like Delete.</summary>
        public static void DeleteOldTask()
        {
            try
            {
                RunSchtasks($"/Delete /TN \"{OldTaskPath}\" /F", elevated: true);
            }
            catch
            {
                // Harmless if it stays.
            }
        }

        /// <summary>Removes the task when Start as Admin is turned off. Only works
        /// from an admin copy; otherwise the task just stays unused.</summary>
        public static void Delete()
        {
            try
            {
                if (GetTask() != null)
                    RunSchtasks($"/Delete /TN \"{TaskPath}\" /F", elevated: true);
            }
            catch
            {
                // Harmless if it stays: nothing starts it while Start as Admin is off.
            }
        }

        /// <summary>The registered task through Task Scheduler's own COM API (no
        /// console output to decode), or null if there isn't one.</summary>
        private static dynamic? GetTask()
        {
            Type? type = Type.GetTypeFromProgID("Schedule.Service");
            if (type == null)
                return null;
            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            try
            {
                dynamic folder = service.GetFolder(FolderPath);
                return folder.GetTask(TaskLeafName);
            }
            catch
            {
                return null; // no folder or no task
            }
        }

        private static bool RunSchtasks(string arguments, bool elevated)
        {
            var psi = elevated
                ? new ProcessStartInfo("schtasks.exe", arguments) { UseShellExecute = false, CreateNoWindow = true }
                : new ProcessStartInfo("schtasks.exe", arguments) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            try
            {
                using var process = Process.Start(psi);
                if (process == null)
                    return false;
                process.WaitForExit(15000);
                return process.HasExited && process.ExitCode == 0;
            }
            catch
            {
                return false; // most likely the UAC prompt was declined
            }
        }

        private static string BuildTaskXml(string exe)
        {
            string user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            string dir = Path.GetDirectoryName(exe) ?? "";
            static string X(string s) => SecurityElement.Escape(s) ?? "";

            // Priority 4 = normal (the default, 7, would run BerthMenu at low
            // priority). Parallel: a new copy always starts; BerthMenu's own
            // single-instance check sorts out the rest. No time limit, no
            // battery or idle conditions.
            return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>Starts BerthMenu as administrator without asking each time (BerthMenu Settings, Startup, Start as Admin).</Description>
  </RegistrationInfo>
  <Principals>
    <Principal id=""Author"">
      <UserId>{X(user)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>""{X(exe)}""</Command>
      <Arguments>{RelaunchArg}</Arguments>
      <WorkingDirectory>{X(dir)}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";
        }
    }
}
