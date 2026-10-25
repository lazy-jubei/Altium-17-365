using Altium17Cloud;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

internal static class SignInStoreTests
{
    private const string Fixture = "fixture-refresh-secret-that-must-not-be-written-in-plain-text";
    private static int Main(string[] args)
    {
        try { Run(args); return 0; } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static void Run(string[] args)
    {
        if (args.Length == 2 && args[0] == "--read")
        { if (new SignInStore(args[1]).Read()?.RefreshToken != Fixture) throw new Exception("Cross-process credential read failed."); return; }
        string root = Path.Combine(Path.GetTempPath(), "AD17 sign-in " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int checks = 0;
        void Require(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
        try
        {
            string path = Path.Combine(root, "sign-in.dat"); var store = new SignInStore(path);
            Require(store.Read() == null, "missing credentials leave the user signed out");
            var saved = new SavedSignIn { RefreshToken = Fixture, Region = WorkspaceApi.Regions[0].Value, ConnectOnStartup = true };
            store.Save(saved);
            byte[] encrypted = File.ReadAllBytes(path);
            Require(!Encoding.UTF8.GetString(encrypted).Contains(Fixture), "refresh credential is absent from the stored ciphertext");
            Require(new SignInStore(path).Read()?.RefreshToken == Fixture, "another store instance decrypts the saved sign-in");
            using (var child = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--read " + ProcessGitRunner.Quote(path)) { UseShellExecute = false, CreateNoWindow = true }))
            {
                if (!child.WaitForExit(15000)) { child.Kill(); throw new Exception("Cross-process credential read timed out."); }
                Require(child.ExitCode == 0, "remembered sign-in survives a process restart");
            }
            saved.ConnectOnStartup = false; store.Save(saved);
            Require(new SignInStore(path).Read()?.ConnectOnStartup == false, "disconnect preference survives without erasing the credential");
            saved.RefreshToken = "rotated-fixture-refresh"; store.Save(saved);
            Require(new SignInStore(path).Read()?.RefreshToken == saved.RefreshToken, "rotated credentials replace the previous saved value");
            encrypted = File.ReadAllBytes(path); encrypted[encrypted.Length / 2] ^= 0xff; File.WriteAllBytes(path, encrypted);
            Require(new SignInStore(path).Read() == null, "corrupt ciphertext fails closed without exposing credentials");
            File.WriteAllBytes(path, new byte[65537]);
            Require(new SignInStore(path).Read() == null, "oversized saved credentials are ignored");
            store.Clear(); Require(!File.Exists(path) && store.Read() == null, "Log out deletes the saved credential");
            store.Clear(); Require(!Directory.EnumerateFiles(root, "*.tmp").Any(), "credential updates leave no temporary plaintext or staging files");
            Console.WriteLine(checks + " saved sign-in checks passed.");
        }
        finally { Directory.Delete(root, true); }
    }
}
