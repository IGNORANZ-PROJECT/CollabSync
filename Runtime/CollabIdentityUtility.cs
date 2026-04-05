using System;
using System.Collections.Generic;

namespace Ignoranz.CollabSync
{
    public static class CollabIdentityUtility
    {
        public static string Normalize(string value)
        {
            return (value ?? "").Trim();
        }

        public static bool Matches(string currentId, string currentName, string storedId, string storedName)
        {
            currentId = Normalize(currentId);
            currentName = Normalize(currentName);
            storedId = Normalize(storedId);
            storedName = Normalize(storedName);

            if (!string.IsNullOrEmpty(storedId))
                return !string.IsNullOrEmpty(currentId) && string.Equals(currentId, storedId, StringComparison.Ordinal);

            if (!string.IsNullOrEmpty(storedName) && !string.IsNullOrEmpty(currentName))
                return string.Equals(storedName, currentName, StringComparison.Ordinal);

            return false;
        }

        public static string DisplayName(string storedId, string storedName)
        {
            storedName = Normalize(storedName);
            if (!string.IsNullOrEmpty(storedName))
                return storedName;

            storedId = Normalize(storedId);
            return storedId;
        }

        public static void EnsureReadBy(MemoItem memo)
        {
            if (memo == null)
                return;

            memo.readByUsers ??= new List<string>();
            memo.readByUserIds ??= new List<string>();
        }

        public static bool HasRead(MemoItem memo, string currentId, string currentName)
        {
            if (memo == null)
                return false;

            EnsureReadBy(memo);

            currentId = Normalize(currentId);
            currentName = Normalize(currentName);
            if (!string.IsNullOrEmpty(currentId) && memo.readByUserIds.Contains(currentId))
                return true;

            return !string.IsNullOrEmpty(currentName) && memo.readByUsers.Contains(currentName);
        }

        public static bool AddReadMarker(MemoItem memo, string currentId, string currentName)
        {
            if (memo == null)
                return false;

            EnsureReadBy(memo);

            bool changed = false;
            currentId = Normalize(currentId);
            currentName = Normalize(currentName);

            if (!string.IsNullOrEmpty(currentId) && !memo.readByUserIds.Contains(currentId))
            {
                memo.readByUserIds.Add(currentId);
                changed = true;
            }

            if (!string.IsNullOrEmpty(currentName) && !memo.readByUsers.Contains(currentName))
            {
                memo.readByUsers.Add(currentName);
                changed = true;
            }

            return changed;
        }
    }
}
