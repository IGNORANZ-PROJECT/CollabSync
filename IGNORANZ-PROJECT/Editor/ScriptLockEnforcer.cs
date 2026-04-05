using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Ignoranz.CollabSync
{
    /// <summary>
    /// .cs ファイルを read-only にして「ロック」を強制するエンフォーサー
    /// - アセット/フォルダロック → 対象 .cs を read-only
    /// - オブジェクトロック（obj:{GlobalObjectId}） → その GameObject に付いている MonoBehaviour の .cs を read-only
    /// </summary>
    [InitializeOnLoad]
    public static class ScriptLockEnforcer
    {
        static ScriptLockEnforcer()
        {
            CollabSyncEvents.OnDocUpdate += OnDocUpdate;
        }

        static string _lastLocksKey;

        private static void OnDocUpdate(CollabStateDocument doc)
        {
            try
            {
                ApplyLocksToScripts(doc);
            }
            catch (Exception ex)
            {
                Debug.LogError("[CollabSync] ApplyLocksToScripts error: " + ex);
            }
        }

        private static void ApplyLocksToScripts(CollabStateDocument _doc)
        {
            if (_doc == null) return;

            var meId = CollabSyncUser.UserId;
            var meName = CollabSyncUser.UserName;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string projectRoot = Directory.GetCurrentDirectory();

            // 1) 有効ロック抽出（自分以外）
            var active = _doc.locks
                .Where(l => l.ttlMs <= 0 || now - l.createdAt <= l.ttlMs)
                .Where(l => !CollabIdentityUtility.Matches(meId, meName, l.ownerId, l.owner))
                .ToList();

            // 2) 差分判定キー（ロック一覧が同じなら処理スキップ）
            string locksKey = string.Join("|",
                active.OrderBy(l => l.assetPath)
                      .Select(l => $"{l.assetPath}@{l.ownerId}:{l.owner}:{l.ttlMs}:{l.createdAt}"));
            if (_lastLocksKey == locksKey) return;
            _lastLocksKey = locksKey;

            // 3) .cs インデックス構築
            CsIndex.EnsureBuilt();

            // 4) ロック対象の .cs フルパス集合を作る
            var lockedCs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var l in active)
            {
                if (string.IsNullOrEmpty(l.assetPath)) continue;

                // 4-1) オブジェクトロック（obj:{GID}）
                if (l.assetPath.StartsWith("obj:", StringComparison.Ordinal))
                {
                    var key = l.assetPath.Substring(4);
                    if (GlobalObjectId.TryParse(key, out var gid))
                    {
                        var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid) as GameObject;
                        if (obj)
                        {
                            foreach (var mb in obj.GetComponents<MonoBehaviour>())
                            {
                                if (!mb) continue; // Missing Script
                                var ms = MonoScript.FromMonoBehaviour(mb);
                                if (!ms) continue;
                                var ap = AssetDatabase.GetAssetPath(ms);
                                if (ap.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                                {
                                    lockedCs.Add(ToFullPath(projectRoot, ap));
                                }
                            }
                        }
                    }
                    continue;
                }

                // 4-2) フォルダロック（末尾 /）
                if (l.assetPath.EndsWith("/"))
                {
                    string folder = l.assetPath;
                    foreach (var ap in CsIndex.AllCsAssetPaths)
                    {
                        if (ap.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                            lockedCs.Add(ToFullPath(projectRoot, ap));
                    }
                    continue;
                }

                // 4-3) 単一ファイルロック（.cs）
                if (l.assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    lockedCs.Add(ToFullPath(projectRoot, l.assetPath));
                }
            }

            // 5) read-only の付け外し
            foreach (var ap in CsIndex.AllCsAssetPaths)
            {
                string full = ToFullPath(projectRoot, ap);
                bool shouldLock = lockedCs.Contains(full);

                try
                {
                    var attr = File.GetAttributes(full);
                    bool isReadOnly = (attr & FileAttributes.ReadOnly) != 0;

                    if (shouldLock && !isReadOnly)
                    {
                        File.SetAttributes(full, attr | FileAttributes.ReadOnly);
                    }
                    else if (!shouldLock && isReadOnly)
                    {
                        File.SetAttributes(full, attr & ~FileAttributes.ReadOnly);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CollabSync] Failed to change lock for {ap}: {ex.Message}");
                }
            }
        }

        private static string ToFullPath(string projectRoot, string assetPath)
        {
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }
    }

    /// <summary>
    /// プロジェクト内の全 .cs ファイルインデックスをキャッシュ
    /// </summary>
    static class CsIndex
    {
        static bool _built;
        static bool _dirty;
        static List<string> _allCsAssetPaths;

        public static IReadOnlyList<string> AllCsAssetPaths
            => _allCsAssetPaths ?? (IReadOnlyList<string>)Array.Empty<string>();

        class CsIndexPostprocessor : AssetPostprocessor
        {
            static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
            {
                if (imported.Any(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) ||
                    deleted.Any(p  => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) ||
                    moved.Any(p    => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) ||
                    movedFrom.Any(p=> p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
                {
                    _dirty = true;
                }
            }
        }

        public static void EnsureBuilt()
        {
            if (_built && !_dirty) return;
            _allCsAssetPaths = BuildAllCs();
            _built = true;
            _dirty = false;
        }

        static List<string> BuildAllCs()
        {
            var guids = AssetDatabase.FindAssets("t:Script", new[] { "Assets" });
            var list = new List<string>(guids.Length);
            foreach (var g in guids)
            {
                var ap = AssetDatabase.GUIDToAssetPath(g);
                if (!string.IsNullOrEmpty(ap) && ap.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    list.Add(ap);
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }
    }
}
