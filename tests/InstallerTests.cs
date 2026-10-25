using Altium17365Setup;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

string temp = Path.Combine(Path.GetTempPath(), "365-setup-test-" + Guid.NewGuid());
Directory.CreateDirectory(temp);
string registry = Path.Combine(temp, "ExtensionsRegistry.xml"), target = Path.Combine(temp, InstallerCore.Id);
byte[] Archive(IEnumerable<string> names) {
    using var bytes = new MemoryStream();
    using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        foreach (string name in names) {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("portable Git fixture");
        }
    return bytes.ToArray();
}
string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
byte[] git = Archive(PortableGit.Required);
string Install(string root, IDictionary<string, byte[]> files, string version, Action<string,string,string>? commit = null) {
    using var stream = new MemoryStream(git);
    return InstallerCore.Install(root, files, version, stream, Hash(git), commit);
}
var payload = InstallerCore.Files.ToDictionary(name => name, name => Encoding.UTF8.GetBytes("new " + name));
void Require(bool condition, string description) {
    if (!condition) throw new Exception(description);
    Console.WriteLine("PASS " + description);
}
void Reject(Action action, string description) {
    try { action(); } catch { Console.WriteLine("PASS " + description); return; }
    throw new Exception(description);
}
try {
    File.WriteAllText(registry, "<Extensions><Item HRID='Other'><Path>C:\\Other</Path><Custom>keep</Custom></Item></Extensions>");
    string original = File.ReadAllText(registry);
    string backup = Install(temp, payload, "0.2.2.0");
    var installed = XDocument.Load(registry);
    var item = installed.Root!.Elements("Item").Single(x => (string?)x.Attribute("HRID") == InstallerCore.Id);
    Require(installed.Root.Elements("Item").Single(x => (string?)x.Attribute("HRID") == "Other").Element("Custom")!.Value == "keep", "preserves unrelated extensions and custom fields");
    Require(item.Element("Version")!.Value == "0.2.2.0" && item.Element("Path")!.Value == target, "registers installed version and exact plugin path");
    Require(!File.ReadAllBytes(registry).Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), "writes the AD17 registry as UTF-8 without a byte-order mark");
    Require(File.ReadAllText(Path.Combine(backup, "ExtensionsRegistry.xml")) == original, "backs up the original extension registry");
    Require(InstallerCore.Files.All(name => File.ReadAllBytes(Path.Combine(target, name)).SequenceEqual(payload[name])), "installs all embedded plugin files");
    Require(PortableGit.Required.All(name => File.Exists(Path.Combine(target, "MinGit", name))), "installs portable Git and its HTTPS certificates and license beside the plugin");

    File.WriteAllText(Path.Combine(target, "stale.dll"), "preserve in backup");
    item.Element("PlatformVersions")!.Element("DXP")!.SetAttributeValue("BuildNumber", "99.0.0.0");
    installed.Save(registry);
    backup = Install(temp, payload, "0.2.2.0");
    Require(!File.Exists(Path.Combine(target, "stale.dll")) && File.Exists(Path.Combine(backup, InstallerCore.Id, "stale.dll")), "updates replace stale files and preserve the previous plugin");
    installed = XDocument.Load(registry);
    item = installed.Root!.Elements("Item").Single(x => (string?)x.Attribute("HRID") == InstallerCore.Id);
    Require(item.Element("PlatformVersions")!.Element("DXP")!.Attribute("BuildNumber")!.Value == "1.0.5.5", "repairs AD17 platform minimums without duplicate registration");

    var culture = CultureInfo.CurrentCulture;
    try {
        CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
        Install(temp, payload, "0.2.2.0");
        item = XDocument.Load(registry).Root!.Elements("Item").Single(x => (string?)x.Attribute("HRID") == InstallerCore.Id);
        Require(!item.Element("DateInstalled")!.Value.Contains(','), "registration dates are independent of system locale");
    } finally { CultureInfo.CurrentCulture = culture; }

    File.WriteAllText(Path.Combine(target, "old.txt"), "original plugin");
    File.WriteAllText(Path.Combine(target, "MinGit", "old.txt"), "original Git");
    string beforeFailure = File.ReadAllText(registry);
    Reject(() => Install(temp, payload, "0.3.0.0", (source, destination, _) => {
        File.WriteAllText(destination, "partial write"); throw new IOException("simulated failure");
    }), "registry commit failure is surfaced");
    Require(File.ReadAllText(registry) == beforeFailure && File.ReadAllText(Path.Combine(target, "old.txt")) == "original plugin", "rollback restores the original registry and complete plugin directory");
    Require(File.ReadAllText(Path.Combine(target, "MinGit", "old.txt")) == "original Git", "failed updates preserve the previous portable Git too");

    var invalid = new Dictionary<string, byte[]>(payload); invalid.Remove(InstallerCore.Files[0]); invalid["../unsafe.dll"] = new byte[] { 1 };
    Reject(() => Install(temp, invalid, "0.2.2.0"), "rejects incomplete or unexpected payload files");
    Require(File.ReadAllText(registry) == beforeFailure, "invalid payload leaves the registry unchanged");
    File.WriteAllText(registry, "<Extensions><Item HRID='Altium17-365'/><Item HRID='Altium17-365'/></Extensions>");
    string duplicate = File.ReadAllText(registry);
    Reject(() => Install(temp, payload, "0.2.2.0"), "rejects duplicate plugin registration before installation");
    Require(File.ReadAllText(registry) == duplicate && File.Exists(Path.Combine(target, "old.txt")), "duplicate registration leaves existing files unchanged");
    File.WriteAllText(registry, "<WrongRoot/>");
    Reject(() => Install(temp, payload, "0.2.2.0"), "rejects an invalid extension registry");
    Directory.Delete(target, true);
    File.WriteAllText(registry, "<Extensions/>");
    Reject(() => Install(temp, payload, "0.2.2.0", (_, __, ___) => throw new IOException("simulated failure")), "fresh-install commit failure is surfaced");
    Require(!Directory.Exists(target) && File.ReadAllText(registry) == "<Extensions/>", "failed fresh install leaves no registered or installed plugin");
    Require(!Directory.EnumerateFiles(temp, "*.tmp").Any() && !Directory.EnumerateDirectories(temp, ".Altium17-365.*").Any(), "failed and successful installs clean up staging files");

    void RejectArchive(byte[] bytes, string hash, string message) {
        string before = File.ReadAllText(registry);
        using var stream = new MemoryStream(bytes);
        Reject(() => InstallerCore.Install(temp, payload, "0.6.0.0", stream, hash), message);
        Require(File.ReadAllText(registry) == before && !Directory.Exists(target), "invalid Git archive leaves no plugin or registry changes");
    }
    RejectArchive(git, new string('0',64), "rejects a corrupt Git archive before changing the installation");
    byte[] incomplete = Archive(new[] { "cmd/git.exe" });
    RejectArchive(incomplete, Hash(incomplete), "rejects Git without HTTPS support, certificates and licensing");
    foreach (string path in new[] { "../escape.txt", "cmd/../../escape.txt", "/escape.txt", @"C:\escape.txt", @"\\server\escape", "cmd/git.exe:extra", "cmd/../escape", "cmd./escape" }) {
        byte[] unsafeZip = Archive(PortableGit.Required.Concat(new[] { path }));
        RejectArchive(unsafeZip, Hash(unsafeZip), "rejects archive path " + path);
    }
    byte[] duplicatePaths = Archive(PortableGit.Required.Concat(new[] { "CMD/GIT.EXE" }));
    RejectArchive(duplicatePaths, Hash(duplicatePaths), "rejects case-insensitive duplicate archive paths");
    byte[] manyFiles = Archive(Enumerable.Range(0,4097).Select(i => "file" + i));
    RejectArchive(manyFiles, Hash(manyFiles), "rejects archives with excessive entries");
    using (var bytes = new MemoryStream()) {
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true)) {
            var link = zip.CreateEntry("link"); link.ExternalAttributes = (0xa000 | 0x1ff) << 16;
            using var writer = new StreamWriter(link.Open()); writer.Write("../escape");
        }
        RejectArchive(bytes.ToArray(), Hash(bytes.ToArray()), "rejects symbolic links in Git archives");
    }
    Require(!File.Exists(Path.Combine(temp,"escape.txt")), "archive paths never escape staging");
    Require(!Directory.EnumerateDirectories(temp, ".Altium17-365.*").Any(), "invalid Git archives clean up staging");
    Console.WriteLine("Portable Git extraction and installer checks passed.");
} finally { Directory.Delete(temp, true); }
