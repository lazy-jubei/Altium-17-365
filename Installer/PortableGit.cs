using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

namespace Altium17365Setup
{
    internal static class PortableGit
    {
        internal static readonly string[] Required = { "cmd/git.exe", "mingw32/bin/git-remote-https.exe", "mingw32/etc/ssl/certs/ca-bundle.crt", "LICENSE.txt" };

        internal static void Extract(Stream source, string expectedHash, string destination)
        {
            if (source == null || !source.CanSeek || expectedHash == null || expectedHash.Length != 64)
                throw new InvalidOperationException("The bundled Git archive is missing.");
            source.Position = 0;
            using (var sha = SHA256.Create())
                if (!string.Equals(BitConverter.ToString(sha.ComputeHash(source)).Replace("-", ""), expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The bundled Git archive failed its integrity check.");
            source.Position = 0;
            string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            using (var zip = new ZipArchive(source, ZipArchiveMode.Read, true))
            {
                if (zip.Entries.Count == 0 || zip.Entries.Count > 4096 || zip.Entries.Sum(e => e.Length) > 512L * 1024 * 1024)
                    throw new InvalidDataException("Invalid bundled Git archive size.");
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zip.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/'), relative = name.TrimEnd('/');
                    if (relative.Length == 0 || name.StartsWith("/") || relative.Split('/').Any(p =>
                        p.Length == 0 || p == "." || p == ".." || p.EndsWith(".") || p.EndsWith(" ") || p.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|', '\0' }) >= 0) ||
                        ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || !paths.Add(relative))
                        throw new InvalidDataException("Invalid bundled Git archive path.");
                    string path = Path.GetFullPath(Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar)));
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid bundled Git archive path.");
                    if (name.EndsWith("/")) Directory.CreateDirectory(path);
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        using (var input = entry.Open())
                        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) input.CopyTo(output);
                    }
                }
                if (Required.Any(p => !File.Exists(Path.Combine(destination, p)) || new FileInfo(Path.Combine(destination, p)).Length == 0))
                    throw new InvalidDataException("The bundled Git archive is incomplete.");
            }
        }
    }
}
