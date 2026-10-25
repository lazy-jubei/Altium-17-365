using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Altium17Cloud
{
    internal sealed class SavedSignIn
    {
        public string RefreshToken { get; set; }
        public string Region { get; set; }
        public bool ConnectOnStartup { get; set; }
    }
    internal sealed class SignInStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Altium17365/sign-in/v1");
        private readonly string path;
        private string lastSaved;
        internal SignInStore(string path) { this.path = path; }
        internal SavedSignIn Read()
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > 65536) return null;
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                try
                {
                    string json = Encoding.UTF8.GetString(plain);
                    var saved = JsonConvert.DeserializeObject<SavedSignIn>(json);
                    if (string.IsNullOrWhiteSpace(saved?.RefreshToken) || saved.RefreshToken.Length > 32768 || !WorkspaceApi.Regions.Any(r => r.Value == saved.Region)) return null;
                    lastSaved = json;
                    return saved;
                }
                finally { Array.Clear(plain, 0, plain.Length); }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is CryptographicException || e is JsonException || e is ArgumentException) { return null; }
        }
        internal void Save(SavedSignIn saved)
        {
            string json = JsonConvert.SerializeObject(saved);
            if (json == lastSaved && File.Exists(path)) return;
            byte[] plain = Encoding.UTF8.GetBytes(json), encrypted;
            try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
            finally { Array.Clear(plain, 0, plain.Length); }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(encrypted, 0, encrypted.Length); output.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                lastSaved = json;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        internal void Clear() { File.Delete(path); lastSaved = null; }
    }
}
