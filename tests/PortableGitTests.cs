using Altium17Cloud;
using Altium17365Setup;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

internal static class PortableGitTests
{
    private static async Task<int> Main(string[] args)
    {
        try { await Run(args); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static async Task Run(string[] args)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new InvalidOperationException("Run the portable Windows Git check on Windows or Wine.");
        string propsFile = Path.GetFullPath(args.Single()), repo = Path.GetDirectoryName(Path.GetDirectoryName(propsFile));
        var props = XDocument.Load(propsFile).Root.Element("PropertyGroup");
        string version = props.Element("MinGitVersion").Value, hash = props.Element("MinGitSha256").Value;
        string archive = Path.Combine(repo, ".tools", "MinGit-" + version + "-32-bit.zip");
        string bundled = Path.Combine(Path.GetDirectoryName(typeof(ProcessGitRunner).Assembly.Location), "MinGit");
        string temp = Path.Combine(Path.GetTempPath(), "AD17 portable Git " + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(bundled)) throw new InvalidOperationException("The isolated test output already contains MinGit; refusing to replace it.");
        Directory.CreateDirectory(temp);
        try
        {
            using (var zip = File.OpenRead(archive)) PortableGit.Extract(zip, hash, bundled);
            var runner = new ProcessGitRunner();
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
            {
                async Task<string> Git(string folder, params string[] arguments)
                {
                    var command = new GitCommand { WorkingDirectory = folder };
                    command.Arguments.AddRange(new[] { "-c", "user.name=lazy-jubei", "-c", "user.email=323475146+lazy-jubei@users.noreply.github.com", "-c", "core.autocrlf=false" });
                    command.Environment["GIT_TERMINAL_PROMPT"] = "0";
                    command.Arguments.AddRange(arguments);
                    var result = await runner.RunAsync(command, timeout.Token);
                    if (result.ExitCode != 0) throw new Exception(string.Join(" ", arguments) + ": " + result.Error);
                    return result.Output;
                }
                string actual = await Git(temp, "--version");
                int revision = version.LastIndexOf('.');
                string expected = version.Substring(0, revision) + ".windows." + version.Substring(revision + 1);
                if (!actual.Contains(expected)) throw new Exception("The bundled Git was not selected: " + actual);
                string helpers = Path.GetFullPath((await Git(temp, "--exec-path")).Trim());
                if (!helpers.StartsWith(Path.GetFullPath(bundled) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Git is using helpers from a separate installation: " + helpers);
                Console.WriteLine("PASS automatic discovery uses bundled " + actual.Trim());
                await Git(temp, "init", "--bare", "--initial-branch=master", "origin.git");
                await Git(temp, "clone", "origin.git", "first copy");
                string first = Path.Combine(temp, "first copy"), second = Path.Combine(temp, "second copy");
                File.WriteAllText(Path.Combine(first, "test.txt"), "portable Git first save\n");
                await Git(first, "add", "test.txt");
                await Git(first, "commit", "--allow-empty-message", "-m", "");
                await Git(first, "push", "origin", "master");
                await Git(temp, "clone", "origin.git", "second copy");
                File.WriteAllText(Path.Combine(first, "test.txt"), "portable Git update\n");
                await Git(first, "commit", "-am", "", "--allow-empty-message");
                await Git(first, "push", "origin", "master");
                await Git(second, "pull", "--ff-only");
                if (File.ReadAllText(Path.Combine(second, "test.txt")) != "portable Git update\n") throw new Exception("Pull did not preserve file bytes.");
                Console.WriteLine("PASS clone, blank-message commit, push and pull in paths containing spaces");
                string remote = await Git(temp, "-c", "credential.helper=", "-c", "core.askPass=", "-c", "http.sslBackend=openssl", "-c", "http.sslCAInfo=" + Path.Combine(bundled, "mingw32/etc/ssl/certs/ca-bundle.crt"), "ls-remote", "https://github.com/lazy-jubei/Altium-17-365.git", "HEAD");
                if (!remote.Contains("HEAD")) throw new Exception("HTTPS did not return a remote revision.");
                Console.WriteLine("PASS HTTPS Git transport and bundled certificates");
            }
        }
        finally
        {
            if (Directory.Exists(bundled)) Directory.Delete(bundled, true);
            foreach (string file in Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(temp, true);
        }
    }
}
