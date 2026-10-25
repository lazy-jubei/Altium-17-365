using Altium17Cloud;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

static class Program
{
    [STAThread] static int Main(string[] args)
    {
        string self = Process.GetCurrentProcess().MainModule.FileName;
        if (args.Length > 0 && args[0] == "--child") { Console.WriteLine(Process.GetCurrentProcess().Id); Thread.Sleep(3000); return 0; }
        if (args.Length > 0 && args[0] == "--parent")
        {
            Thread.Sleep(200); // Give the runner time to assign its process job.
            var child = Process.Start(new ProcessStartInfo(self, "--child") { UseShellExecute = false, CreateNoWindow = true });
            File.WriteAllText(args[1], child.Id.ToString());
            Thread.Sleep(30000); return 0;
        }
        int exit = 1;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
        {
            string temp = Path.Combine(Path.GetTempPath(), "365-git-ui-" + Guid.NewGuid());
            Directory.CreateDirectory(temp);
            try
            {
                int ticks = 0; double worstGap = 0, last = 0;
                var watch = Stopwatch.StartNew();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                timer.Tick += (_, __) => { var now = watch.Elapsed.TotalMilliseconds; worstGap = Math.Max(worstGap, now - last); last = now; ticks++; };
                timer.Start();
                var command = new GitCommand { WorkingDirectory = temp }; command.Arguments.Add("--child");
                var result = await new ProcessGitRunner(self).RunAsync(command, CancellationToken.None);
                timer.Stop();
                if (result.ExitCode != 0 || int.Parse(result.Output.Trim()) == Process.GetCurrentProcess().Id || ticks < 50 || worstGap > 500)
                    throw new Exception("UI dispatcher stalled during child-process execution.");
                Console.WriteLine("PASS separate process; dispatcher ticks=" + ticks + "; worst gap=" + worstGap.ToString("F1") + "ms during 3-second operation");
                var pidFile = Path.Combine(temp, "child.pid");
                command.Arguments.Clear(); command.Arguments.Add("--parent"); command.Arguments.Add(pidFile);
                using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    var pending = new ProcessGitRunner(self).RunAsync(command, cancel.Token);
                    while (!File.Exists(pidFile)) { cancel.Token.ThrowIfCancellationRequested(); await Task.Delay(30); }
                    int childPid = int.Parse(File.ReadAllText(pidFile)); cancel.Cancel();
                    bool cancelled = false;
                    try { await pending; } catch (OperationCanceledException) { cancelled = true; }
                    bool stopped;
                    try { stopped = Process.GetProcessById(childPid).HasExited; } catch (ArgumentException) { stopped = true; }
                    if (!cancelled || !stopped) throw new Exception("Cancellation left a child process running.");
                    Console.WriteLine("PASS cancellation stops the Git process tree and releases its pipes");
                }
                exit = 0;
            }
            catch (Exception e) { Console.Error.WriteLine("FAIL " + e.Message); }
            finally { Directory.Delete(temp, true); Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); }
        }));
        Dispatcher.Run(); return exit;
    }
}
