using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace BerthMenu.Services
{
    /// <summary>
    /// "Pin to BerthMenu" in File Explorer's right-click menu (AppConfig.ExplorerPinMenu,
    /// off by default — Settings → Startup).
    ///
    /// The menu item: a per-user shell verb on every file (HKCU\Software\Classes\*\shell)
    /// and folder (…\Directory\shell) that runs <c>BerthMenu.exe --pin "path"</c>. Only
    /// the current user's registry is touched, so no admin rights are needed. On
    /// Windows 11 it's in the classic menu ("Show more options", or Shift+right-click);
    /// Windows 11's new short menu only lists apps packaged a special way.
    ///
    /// Getting the path to the BerthMenu that's already running: that second
    /// BerthMenu.exe sends it over a named pipe (<see cref="TrySendToRunning"/>) and
    /// exits; the running one listens (<see cref="StartListening"/>) and pins it. If
    /// BerthMenu wasn't running, the new one starts normally and pins it itself.
    /// The installer's uninstall removes the menu item too.
    /// </summary>
    public static class ExplorerMenuService
    {
        public const string PinArgument = "--pin";

        private const string VerbKey = "BerthMenuPin";
        private static readonly string[] Roots =
        {
            @"Software\Classes\*\shell\" + VerbKey,
            @"Software\Classes\Directory\shell\" + VerbKey,
        };

        private static string PipeName => "BerthMenu-Pin-" + Environment.UserName;

        /// <summary>Adds or removes the menu item. Re-run at every start so it points
        /// at wherever BerthMenu.exe is now (after an update to a new folder, say).</summary>
        public static void SetEnabled(bool enabled)
        {
            string? exe = Environment.ProcessPath;
            foreach (string path in Roots)
            {
                try
                {
                    if (!enabled || string.IsNullOrEmpty(exe))
                    {
                        Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
                        continue;
                    }
                    using var verb = Registry.CurrentUser.CreateSubKey(path);
                    verb.SetValue("MUIVerb", "Pin to BerthMenu");
                    verb.SetValue("Icon", $"\"{exe}\",0");
                    using var command = verb.CreateSubKey("command");
                    command.SetValue(null, $"\"{exe}\" {PinArgument} \"%1\"");
                }
                catch
                {
                    // A locked-down registry — the menu item just isn't there.
                }
            }
        }

        /// <summary>The path after --pin on the command line, or null.</summary>
        public static string? PinPathFromArgs(string[] args)
        {
            int i = Array.IndexOf(args, PinArgument);
            return i >= 0 && i + 1 < args.Length && args[i + 1].Length > 0 ? args[i + 1] : null;
        }

        /// <summary>Hands <paramref name="path"/> to the BerthMenu that's already
        /// running. False if none answered (then this one carries on starting).</summary>
        public static bool TrySendToRunning(string path)
        {
            // A few tries: when several files are pinned at once, Explorer starts one
            // BerthMenu.exe each, and the first may still be starting up.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                    pipe.Connect(1000);
                    using var writer = new StreamWriter(pipe);
                    writer.WriteLine(path);
                    writer.Flush();
                    return true;
                }
                catch
                {
                    Thread.Sleep(300);
                }
            }
            return false;
        }

        private static CancellationTokenSource? _listening;

        /// <summary>The running BerthMenu: waits for paths from later BerthMenu.exe
        /// launches and hands each one to <paramref name="onPath"/> (on a background
        /// thread — the caller moves it to the UI).</summary>
        public static void StartListening(Action<string> onPath)
        {
            if (_listening != null)
                return;
            _listening = new CancellationTokenSource();
            var token = _listening.Token;
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var server = CreateServer();
                        await server.WaitForConnectionAsync(token);
                        using var reader = new StreamReader(server);
                        string? line;
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            if (line.Length > 0)
                                onPath(line);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch
                    {
                        try { await Task.Delay(1000, token); } catch { return; }
                    }
                }
            });
        }

        public static void StopListening() => _listening?.Cancel();

        /// <summary>A pipe the current user can always write to — also when this
        /// BerthMenu runs as admin and File Explorer doesn't.</summary>
        private static NamedPipeServerStream CreateServer()
        {
            try
            {
                var security = new PipeSecurity();
                var me = System.Security.Principal.WindowsIdentity.GetCurrent().User;
                if (me != null)
                {
                    security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                        System.Security.AccessControl.AccessControlType.Allow));
                    return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                }
            }
            catch
            {
                // Fall back to the default permissions below.
            }
            return new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }
    }
}
