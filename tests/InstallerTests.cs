using Altium17365Setup;
using System.Globalization;
using System.Text;
using System.Xml.Linq;

string temp = Path.Combine(Path.GetTempPath(), "365-setup-test-" + Guid.NewGuid());
Directory.CreateDirectory(temp);
string registry = Path.Combine(temp, "ExtensionsRegistry.xml"), target = Path.Combine(temp, InstallerCore.Id);
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
    string backup = InstallerCore.Install(temp, payload, "0.2.2.0");
    var installed = XDocument.Load(registry);
    var item = installed.Root!.Elements("Item").Single(x => (string?)x.Attribute("HRID") == InstallerCore.Id);
    Require(installed.Root.Elements("Item").Single(x => (string?)x.Attribute("HRID") == "Other").Element("Custom")!.Value == "keep", "preserves unrelated extensions and custom fields");
    Require(item.Element("Version")!.Value == "0.2.2.0" && item.Element("Path")!.Value == target, "registers installed version and exact plugin path");
    Require(!File.ReadAllBytes(registry).Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), "writes the AD17 registry as UTF-8 without a byte-order mark");
    Require(File.ReadAllText(Path.Combine(backup, "ExtensionsRegistry.xml")) == original, "backs up the original extension registry");
    Require(InstallerCore.Files.All(name => File.ReadAllBytes(Path.Combine(target, name)).SequenceEqual(payload[name])), "installs all embedded plugin files");

    File.WriteAllText(Path.Combine(target, "stale.dll"), "preserve in backup");
    item.Element("PlatformVersions")!.Element("DXP")!.SetAttributeValue("BuildNumber", "99.0.0.0");
    installed.Save(registry);
    backup = InstallerCore.Install(temp, payload, "0.2.2.0");
    Require(!File.Exists(Path.Combine(target, "stale.dll")) && File.Exists(Path.Combine(backup, InstallerCore.Id, "stale.dll")), "updates replace stale files and preserve the previous plugin");
    installed = XDocument.Load(registry);
    item = installed.Root!.Elements("Item").Single(x => (string?)x.Attribute("HRID") == InstallerCore.Id);
    Require(item.Element("PlatformVersions")!.Element("DXP")!.Attribute("BuildNumber")!.Value == "1.0.5.5", "repairs AD17 platform minimums without duplicate registration");

    var culture = CultureInfo.CurrentCulture;
    try {
        CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
        InstallerCore.Install(temp, payload, "0.2.2.0");
        item = XDocument.Load(registry).Root!.Elements("Item").Single(x => (string?)x.Attribute("HRID") == InstallerCore.Id);
        Require(!item.Element("DateInstalled")!.Value.Contains(','), "registration dates are independent of system locale");
    } finally { CultureInfo.CurrentCulture = culture; }

    File.WriteAllText(Path.Combine(target, "old.txt"), "original plugin");
    string beforeFailure = File.ReadAllText(registry);
    Reject(() => InstallerCore.Install(temp, payload, "0.3.0.0", (source, destination, _) => {
        File.WriteAllText(destination, "partial write"); throw new IOException("simulated failure");
    }), "registry commit failure is surfaced");
    Require(File.ReadAllText(registry) == beforeFailure && File.ReadAllText(Path.Combine(target, "old.txt")) == "original plugin", "rollback restores the original registry and complete plugin directory");

    var invalid = new Dictionary<string, byte[]>(payload); invalid.Remove(InstallerCore.Files[0]); invalid["../unsafe.dll"] = new byte[] { 1 };
    Reject(() => InstallerCore.Install(temp, invalid, "0.2.2.0"), "rejects incomplete or unexpected payload files");
    Require(File.ReadAllText(registry) == beforeFailure, "invalid payload leaves the registry unchanged");
    File.WriteAllText(registry, "<Extensions><Item HRID='Altium17-365'/><Item HRID='Altium17-365'/></Extensions>");
    string duplicate = File.ReadAllText(registry);
    Reject(() => InstallerCore.Install(temp, payload, "0.2.2.0"), "rejects duplicate plugin registration before installation");
    Require(File.ReadAllText(registry) == duplicate && File.Exists(Path.Combine(target, "old.txt")), "duplicate registration leaves existing files unchanged");
    File.WriteAllText(registry, "<WrongRoot/>");
    Reject(() => InstallerCore.Install(temp, payload, "0.2.2.0"), "rejects an invalid extension registry");
    Directory.Delete(target, true);
    File.WriteAllText(registry, "<Extensions/>");
    Reject(() => InstallerCore.Install(temp, payload, "0.2.2.0", (_, __, ___) => throw new IOException("simulated failure")), "fresh-install commit failure is surfaced");
    Require(!Directory.Exists(target) && File.ReadAllText(registry) == "<Extensions/>", "failed fresh install leaves no registered or installed plugin");
    Require(!Directory.EnumerateFiles(temp, "*.tmp").Any() && !Directory.EnumerateDirectories(temp, ".Altium17-365.*").Any(), "failed and successful installs clean up staging files");
} finally { Directory.Delete(temp, true); }
