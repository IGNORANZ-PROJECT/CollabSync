#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Ignoranz.CollabSync
{
    public class CollabSyncDoctorIssue
    {
        public string code = "";
        public string title = "";
        public string detail = "";
        public string fix = "";
    }

    public class CollabSyncDoctorReport
    {
        public bool running;
        public bool success;
        public string summary = "";
        public string startedAtLocal = "";
        public string finishedAtLocal = "";
        public List<string> lines = new();
        public List<CollabSyncDoctorIssue> issues = new();

        public string ToClipboardText()
        {
            var builder = new StringBuilder();
            builder.AppendLine("CollabSync Doctor");

            if (!string.IsNullOrEmpty(summary))
                builder.AppendLine(CollabSyncLocalization.F("Summary: {0}", "概要: {0}", summary));
            if (!string.IsNullOrEmpty(startedAtLocal))
                builder.AppendLine(CollabSyncLocalization.F("Started: {0}", "開始: {0}", startedAtLocal));
            if (!string.IsNullOrEmpty(finishedAtLocal))
                builder.AppendLine(CollabSyncLocalization.F("Finished: {0}", "終了: {0}", finishedAtLocal));

            builder.AppendLine();
            builder.AppendLine(CollabSyncLocalization.T("Log", "ログ"));
            foreach (var line in lines)
                builder.AppendLine(line);

            if (issues != null && issues.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine(CollabSyncLocalization.T("Suggested fixes", "解決方法"));
                foreach (var issue in issues)
                {
                    builder.Append("- ").AppendLine(issue.title);
                    if (!string.IsNullOrEmpty(issue.detail))
                        builder.Append("  ").AppendLine(issue.detail);
                    if (!string.IsNullOrEmpty(issue.fix))
                        builder.Append("  ").AppendLine(issue.fix);
                }
            }

            return builder.ToString();
        }
    }

    public class CollabSyncDoctor
    {
        public static CollabSyncDoctorReport LatestReport { get; private set; }
        public static event Action<CollabSyncDoctorReport> OnReportChanged;

        public static void Run() => _ = RunAsync();

        public static async Task<CollabSyncDoctorReport> RunAsync()
        {
            var report = new CollabSyncDoctorReport
            {
                running = true,
                startedAtLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            Publish(report);

            Log(report, L("=== CollabSync Doctor (Local JSON) ===", "=== CollabSync Doctor（ローカル JSON）==="));
            try
            {
                var cfg = CollabSyncConfig.LoadOrCreate();
                if (cfg == null)
                {
                    Log(report, L("[NG] CollabSyncConfig could not be loaded/created.", "[NG] CollabSyncConfig を読み込みまたは作成できませんでした。"), true);
                    AddIssue(
                        report,
                        "config-missing",
                        L("Config asset is missing or unreadable.", "設定アセットが見つからないか、読み込めません。"),
                        L("CollabSync could not open its config asset.", "CollabSync の設定アセットを開けませんでした。"),
                        L("Reimport the CollabSync folder, then reopen Tools > CollabSync. If the asset is still missing, recreate it from Runtime/Resources/CollabSyncConfig.asset.",
                          "CollabSync フォルダを再インポートしてから Tools > CollabSync を開き直してください。まだ無い場合は Runtime/Resources/CollabSyncConfig.asset を作り直してください。"));
                    report.success = false;
                    report.summary = L("Doctor failed", "Doctor 失敗");
                    return FinalizeReport(report);
                }

                if (!cfg.TryGetResolvedJsonPath(out var resolvedPath, out var statusOrError))
                {
                    Log(report, L("Storage resolve", "共有設定の解決"), false, statusOrError);
                    AddIssue(
                        report,
                        "storage-resolve",
                        L("Shared JSON path is invalid.", "共有 JSON パスが無効です。"),
                        statusOrError,
                        ResolveStorageFix(statusOrError));
                    report.success = false;
                    report.summary = L("Doctor failed", "Doctor 失敗");
                    return FinalizeReport(report);
                }

                var okPath = EnsureJsonFile(resolvedPath, out var ensureError);
                Log(report, L("Local JSON file", "ローカル JSON ファイル"), okPath, resolvedPath);
                if (!okPath)
                {
                    AddIssue(
                        report,
                        "json-create",
                        L("Shared JSON file could not be created or repaired.", "共有 JSON ファイルを作成または修復できませんでした。"),
                        ensureError,
                        ResolveFileAccessFix(ensureError, resolvedPath));
                    report.success = false;
                    report.summary = L("Doctor failed", "Doctor 失敗");
                    return FinalizeReport(report);
                }

                var okRW = VerifyReadWrite(resolvedPath, out var readWriteError);
                Log(report, L("Local JSON read/write", "ローカル JSON の読み書き"), okRW);
                if (!okRW)
                {
                    AddIssue(
                        report,
                        "json-readwrite",
                        L("Shared JSON could not be read or written safely.", "共有 JSON を安全に読み書きできませんでした。"),
                        readWriteError,
                        ResolveFileAccessFix(readWriteError, resolvedPath));
                    report.success = false;
                    report.summary = L("Doctor failed", "Doctor 失敗");
                    return FinalizeReport(report);
                }

                var backend = new LocalJsonBackend(resolvedPath);
                var doc = await backend.LoadOnceAsync() ?? new CollabStateDocument();
                CollabSyncEvents.RaiseDocUpdate(doc);
                Log(report, L("[OK] Broadcast latest snapshot.", "[OK] 最新スナップショットを配信しました。"));
                report.success = true;
                report.summary = L("Doctor passed", "Doctor 成功");
            }
            catch (Exception ex)
            {
                var detail = ex.ToString();
                Log(report, L("[NG] Doctor error:\n", "[NG] Doctor エラー:\n") + detail, true);
                AddIssue(
                    report,
                    "doctor-exception",
                    L("Doctor stopped because of an unexpected exception.", "予期しない例外で Doctor が停止しました。"),
                    FirstLine(detail),
                    L("Copy the result and inspect the stack trace. In many cases, reopening Unity or moving the shared JSON to a simpler writable folder resolves editor-side issues.",
                      "結果をコピーしてスタックトレースを確認してください。Unity の再起動、または共有 JSON をより単純な書き込み可能フォルダへ移すと解消することがあります。"));
                report.success = false;
                report.summary = L("Doctor failed", "Doctor 失敗");
            }
            finally
            {
                Log(report, L("=== End of CollabSync Doctor ===", "=== CollabSync Doctor 終了 ==="));
            }

            return FinalizeReport(report);
        }

        static bool EnsureJsonFile(string path, out string error)
        {
            error = "";
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    path = "ProjectSettings/CollabSyncLocal.json";

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                if (!File.Exists(path))
                {
                    var init = new CollabStateDocument { updatedAt = TimeUtil.NowMs() };
                    File.WriteAllText(path, Json(init), Encoding.UTF8);
                }
                else
                {
                    try
                    {
                        var txt = File.ReadAllText(path, Encoding.UTF8);
                        var d = JsonUtility.FromJson<CollabStateDocument>(txt);
                        if (d == null)
                            File.WriteAllText(path, Json(new CollabStateDocument { updatedAt = TimeUtil.NowMs() }), Encoding.UTF8);
                    }
                    catch
                    {
                        File.WriteAllText(path, Json(new CollabStateDocument { updatedAt = TimeUtil.NowMs() }), Encoding.UTF8);
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        static bool VerifyReadWrite(string path, out string error)
        {
            error = "";
            try
            {
                var txt = File.ReadAllText(path, Encoding.UTF8);
                var doc = JsonUtility.FromJson<CollabStateDocument>(txt) ?? new CollabStateDocument();
                doc.memos ??= new List<MemoItem>();

                var tempId = Guid.NewGuid().ToString("N");
                var tempMemo = new MemoItem
                {
                    id = tempId,
                    text = L("Doctor temp", "Doctor 一時メモ"),
                    author = SystemInfo.deviceName,
                    createdAt = TimeUtil.NowMs(),
                    pinned = false,
                };
                TryMarkReadGeneric(tempMemo, SystemInfo.deviceName);

                doc.memos.Add(tempMemo);
                doc.updatedAt = TimeUtil.NowMs();
                File.WriteAllText(path, Json(doc), Encoding.UTF8);

                var txt2 = File.ReadAllText(path, Encoding.UTF8);
                var doc2 = JsonUtility.FromJson<CollabStateDocument>(txt2) ?? new CollabStateDocument();
                doc2.memos ??= new List<MemoItem>();
                var found = doc2.memos.Exists(m => m.id == tempId);

                doc2.memos.RemoveAll(m => m.id == tempId);
                doc2.updatedAt = TimeUtil.NowMs();
                File.WriteAllText(path, Json(doc2), Encoding.UTF8);

                if (!found)
                {
                    error = L("The temporary doctor memo was not found after writing the file.", "一時メモを書き込んだ後に再読込で見つかりませんでした。");
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        static void TryMarkReadGeneric(object memo, string user)
        {
            if (memo == null || string.IsNullOrEmpty(user)) return;
            var t = memo.GetType();

            var fReadBy = t.GetField("readBy", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fReadBy != null)
            {
                if (fReadBy.GetValue(memo) is not Dictionary<string, bool> dict)
                {
                    dict = new Dictionary<string, bool>();
                    fReadBy.SetValue(memo, dict);
                }
                if (!dict.ContainsKey(user)) dict[user] = true;
                return;
            }

            var fReadByUsers = t.GetField("readByUsers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fReadByUsers != null)
            {
                if (fReadByUsers.GetValue(memo) is not List<string> list)
                {
                    list = new List<string>();
                    fReadByUsers.SetValue(memo, list);
                }
                if (!list.Contains(user)) list.Add(user);
            }
        }

        static string Json(CollabStateDocument d)
        {
#if UNITY_2021_2_OR_NEWER
            return JsonUtility.ToJson(d, true);
#else
            return JsonUtility.ToJson(d);
#endif
        }

        static CollabSyncDoctorReport FinalizeReport(CollabSyncDoctorReport report)
        {
            report.running = false;
            report.finishedAtLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            Publish(report);
            return report;
        }

        static void Publish(CollabSyncDoctorReport report)
        {
            LatestReport = report;
            OnReportChanged?.Invoke(report);
        }

        static void AddIssue(CollabSyncDoctorReport report, string code, string title, string detail, string fix)
        {
            report.issues ??= new List<CollabSyncDoctorIssue>();
            if (report.issues.Exists(x => x.code == code))
                return;

            report.issues.Add(new CollabSyncDoctorIssue
            {
                code = code ?? "",
                title = title ?? "",
                detail = detail ?? "",
                fix = fix ?? ""
            });
            Publish(report);
        }

        static void Log(CollabSyncDoctorReport report, string message, bool isError = false)
        {
            report.lines.Add(message);
            Publish(report);
        }

        static void Log(CollabSyncDoctorReport report, string label, bool ok, string extra = null)
        {
            var message = (ok ? "[OK] " : "[NG] ") + label + (extra != null ? $"  ({extra})" : "");
            Log(report, message, !ok);
        }

        static string ResolveStorageFix(string detail)
        {
            if (!string.IsNullOrEmpty(detail) && detail.IndexOf("URL", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return L(
                    "Use Settings > JSON Path > Choose... and pick a real local or network file path. Do not paste a OneDrive or web URL.",
                    "Settings > JSON パス > 選択... から、実際のローカルまたはネットワーク上のファイルを選んでください。OneDrive や Web の URL は使えません。");
            }

            return L(
                "Open Settings and choose a reachable shared JSON file. Prefer a simple writable folder on OneDrive, SMB, or another shared drive.",
                "Settings を開き、到達できる共有 JSON ファイルを選び直してください。OneDrive、SMB 共有、共有ドライブ上の単純な書き込み可能フォルダを推奨します。");
        }

        static string ResolveFileAccessFix(string detail, string path)
        {
            var pathText = string.IsNullOrEmpty(path)
                ? ""
                : L($" Target: {path}", $" 対象: {path}");

            if (!string.IsNullOrEmpty(detail) &&
                (detail.IndexOf("access", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 detail.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 detail.IndexOf("permission", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return L(
                    "The folder is not writable or the file is read-only. Check file permissions, remove read-only flags, and move the JSON to a writable shared folder if needed." + pathText,
                    "フォルダに書き込み権限がないか、ファイルが読み取り専用です。権限と読み取り専用属性を確認し、必要なら書き込み可能な共有フォルダへ移してください。" + pathText);
            }

            if (!string.IsNullOrEmpty(detail) &&
                (detail.IndexOf("used by another process", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 detail.IndexOf("sharing violation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 detail.IndexOf("lock", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return L(
                    "Another app is holding the file. Wait a moment, close editors or sync tools that may be locking it, then run Doctor again." + pathText,
                    "別のアプリがファイルを掴んでいます。少し待つか、ロックしていそうなエディタや同期ツールを閉じてから、Doctor を再実行してください。" + pathText);
            }

            if (!string.IsNullOrEmpty(detail) &&
                (detail.IndexOf("directory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 detail.IndexOf("path", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 detail.IndexOf("find", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return L(
                    "The path is invalid or the shared folder is not mounted. Re-select the JSON file from Settings and confirm the shared drive is available." + pathText,
                    "パスが不正か、共有フォルダがマウントされていません。Settings から JSON を選び直し、共有ドライブが利用可能か確認してください。" + pathText);
            }

            return L(
                "Re-select the shared JSON path from Settings, then run Doctor again. If the problem persists, restore the latest valid file from .collabsync-backups." + pathText,
                "Settings から共有 JSON パスを選び直して、Doctor を再実行してください。直らない場合は .collabsync-backups の最新正常ファイルを確認してください。" + pathText);
        }

        static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            using var reader = new StringReader(text);
            return reader.ReadLine() ?? "";
        }

        static string L(string english, string japanese)
        {
            return CollabSyncLocalization.T(english, japanese);
        }
    }
}
#endif
