using System.IO;

namespace Altium17Cloud
{
    public static class ProjectWorkingCopy
    {
        public static string FindRoot(string projectFile)
        {
            if (string.IsNullOrWhiteSpace(projectFile) || !Path.IsPathRooted(projectFile)) return null;
            string parent = Path.GetDirectoryName(Path.GetFullPath(projectFile));
            if (string.IsNullOrEmpty(parent)) return null;
            var directory = new DirectoryInfo(parent);
            while (directory != null)
            {
                string git = Path.Combine(directory.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git)) return WorkingCopyPath.Root(directory.FullName);
                directory = directory.Parent;
            }
            return null;
        }
    }
}
