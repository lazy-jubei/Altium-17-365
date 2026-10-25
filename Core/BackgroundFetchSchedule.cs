using System;
using System.Collections.Generic;

namespace Altium17Cloud
{
    public sealed class BackgroundFetchSchedule
    {
        public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
        private readonly Dictionary<string, DateTime> attempts = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool TryStart(string folder, DateTime now)
        {
            if (running.Contains(folder) || (attempts.TryGetValue(folder, out var last) && now - last < Interval)) return false;
            attempts[folder] = now; running.Add(folder); return true;
        }
        public void Complete(string folder) => running.Remove(folder);
        public void Clear() { attempts.Clear(); running.Clear(); }
    }
}
