#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Experimental.SceneManagement;
using UnityEngine;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Collections.Generic;
using Ignoranz.CollabSync;

public class CollabSyncWindow : EditorWindow
{
    private enum NewMemoLinkMode
    {
        None = 0,
        CurrentSelection = 1,
        ManualTarget = 2
    }

    private sealed class SummaryCardInfo
    {
        public string title = "";
        public string value = "";
        public string detail = "";
        public MessageType tone;
        public Action onValueClick;
        public string valueTooltip = "";
    }

    private sealed class ActionButtonInfo
    {
        public string label = "";
        public string tooltip = "";
        public bool enabled = true;
        public Action onClick;
    }

    private enum LockReleaseOutcome
    {
        None = 0,
        Released = 1,
        Retained = 2
    }

    private sealed class SelectionTargetInfo
    {
        public string displayName = "";
        public string assetPath = "";
        public string context = "";
        public string preferredLockKey = "";
        public string objectLockKey = "";
        public bool isFolder;

        public bool HasTarget =>
            !string.IsNullOrEmpty(assetPath) || !string.IsNullOrEmpty(preferredLockKey);
    }

    private sealed class BackupSnapshotInfo
    {
        public string fullPath = "";
        public string fileName = "";
        public string timestampLocal = "";
        public long sizeBytes;
    }

    private sealed class KnownUserInfo
    {
        public string userId = "";
        public string displayName = "";
        public bool isOnline;
    }

    private CollabSyncConfig _cfg;
    private ICollabBackend _backend;
    private CollabStateDocument _doc = new();

    private Vector2 _scrollOverview;
    private Vector2 _scrollActivity;
    private Vector2 _scrollMemo;
    private Vector2 _scrollSettings;
    private Vector2 _scrollDoctor;

    private string _newMemo = "";
    private string _newMemoLinkTarget = "";
    private string _memoSearch = "";
    private string _lockSearch = "";
    private double _lastUiRepaint;

    private bool _memoFilterUnread;
    private bool _memoFilterPinned;
    private bool _memoFilterSelection;
    private NewMemoLinkMode _newMemoLinkMode = NewMemoLinkMode.CurrentSelection;
    private bool _usersSectionExpanded = true;
    private bool _deletedUsersSectionExpanded;
    private bool _overviewOnlineExpanded;
    private bool _overviewRelatedLocksOnly;
    private int _lockFilter;

    private HashSet<string> _knownMemoIds = new();
    private readonly HashSet<string> _expandedUserHistories = new();
    private double _lastNotifyAt;

    private string _backendStatus = "";
    private int _tab; // 0: Overview, 1: Details, 2: Memos, 3: Settings
    private string _backendError = "";
    private bool _doctorRunning;
    private CollabSyncDoctorReport _doctorReport;
    private string _adminUserIdInput = "";
    private readonly List<string> _pendingMemoAlertIds = new();
    private readonly HashSet<string> _dismissedMemoAlertIds = new();
    private readonly List<EditingPresence> _cachedAlivePresences = new();
    private readonly List<LockItem> _cachedActiveLocks = new();
    private readonly List<KnownUserInfo> _cachedKnownUsers = new();
    private readonly List<KnownUserInfo> _cachedAdminUsers = new();
    private readonly Dictionary<string, EditingPresence> _cachedPresenceByUserKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<LockItem>> _cachedLocksByUserKey = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cachedAdminUserKeys = new(StringComparer.Ordinal);
    private long _derivedDocUpdatedAt = long.MinValue;
    private long _derivedTimeBucket = long.MinValue;
    private string _derivedIdentityKey = "";

    private GUIStyle _cardValueStyle;
    private GUIStyle _cardValueButtonStyle;
    private GUIStyle _cardDetailStyle;
    private GUIStyle _memoMetaStyle;
    private GUIStyle _memoBodyStyle;
    private GUIStyle _memoPreviewStyle;

    private const long PresenceAliveWindowMs = 20_000;
    private const long DerivedDataRefreshWindowMs = 500;
    private static readonly Regex MemoMarkdownFenceRegex = new(@"```(?:[^\n`]*)\n?([\s\S]*?)```", RegexOptions.Compiled);
    private static readonly Regex MemoMarkdownInlineCodeRegex = new(@"`([^`\n]+)`", RegexOptions.Compiled);
    private static readonly Regex MemoMarkdownLinkRegex = new(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);
    private static readonly Regex MemoMarkdownBoldRegex = new(@"(\*\*|__)(.+?)\1", RegexOptions.Compiled);
    private static readonly Regex MemoMarkdownItalicRegex = new(@"(?<!\*)\*(?!\s)(.+?)(?<!\s)\*(?!\*)|(?<!_)_(?!\s)(.+?)(?<!\s)_(?!_)", RegexOptions.Compiled);

    [MenuItem("Tools/CollabSync", priority = 20)]
    public static void Open()
    {
        var window = GetWindow<CollabSyncWindow>("CollabSync");
        window.titleContent = new GUIContent("CollabSync");
        window.Show();
        window.Focus();
    }

    public static void OpenSettingsTab()
    {
        var window = GetWindow<CollabSyncWindow>("CollabSync");
        window._tab = 3;
        window.titleContent = new GUIContent("CollabSync");
        window.Show();
        window.Focus();
    }

    private void OnEnable()
    {
        _cfg = CollabSyncConfig.LoadOrCreate();
        NormalizeStoredJsonPathIfNeeded();
        _doctorReport = CollabSyncDoctor.LatestReport;
        titleContent = new GUIContent("CollabSync");

        _doc = NormalizeWindowDoc(_doc);
        _doc = NormalizeWindowDoc(CollabSyncEvents.Latest ?? new CollabStateDocument());
        InvalidateDerivedData();
        foreach (var m in _doc.memos)
        {
            if (!string.IsNullOrEmpty(m.id))
                _knownMemoIds.Add(m.id);
        }

        BuildBackendSafe();
        CollabSyncEvents.OnDocUpdate += OnDocUpdate;
        _backendStatus = CollabSyncBackendUtility.FormatStorageLabel(_cfg);
    }

    private void OnDisable()
    {
        CollabSyncEvents.OnDocUpdate -= OnDocUpdate;
    }

    private void OnSelectionChange()
    {
        Repaint();
    }

    private void BuildBackendSafe()
    {
        try
        {
            NormalizeStoredJsonPathIfNeeded();

            if (!CollabSyncBackendUtility.TryCreateBackend(_cfg, out _backend, out var resolvedPath, out var statusOrError))
            {
                _backend = null;
                _backendError = statusOrError;
                _backendStatus = L("Shared JSON: unresolved", "共有 JSON: 未解決");
                return;
            }

            _backendError = "";
            _backendStatus = LF("Shared JSON: {0}", "共有 JSON: {0}", resolvedPath);
            RefreshSnapshotAsync();
        }
        catch (Exception ex)
        {
            Debug.LogError("[CollabSync] Backend build error: " + ex.Message);
            _backend = null;
            _backendError = ex.Message;
        }
    }

    private async void RefreshSnapshotAsync()
    {
        if (_backend == null)
            return;

        try
        {
            var doc = await _backend.LoadOnceAsync();
            OnDocUpdate(doc ?? new CollabStateDocument());
        }
        catch (Exception ex)
        {
            _backendError = ex.Message;
            Repaint();
        }
    }

    private void OnDocUpdate(CollabStateDocument doc)
    {
        _doc = NormalizeWindowDoc(doc);
        InvalidateDerivedData();
        PruneMemoAlerts();

        if (NotifyOnNewMemo && _doc.memos != null)
        {
            bool anyNotified = false;
            foreach (var m in _doc.memos)
            {
                if (string.IsNullOrEmpty(m.id)) continue;
                if (_knownMemoIds.Contains(m.id)) continue;

                _knownMemoIds.Add(m.id);
                if (!IsCurrentUser(m))
                {
                    QueueMemoAlert(m);
                    anyNotified = true;

                    var now = EditorApplication.timeSinceStartup;
                    if (BeepOnNewMemo && now - _lastNotifyAt > 0.5)
                    {
                        EditorApplication.Beep();
                        _lastNotifyAt = now;
                    }
                }
            }

            if (!anyNotified)
            {
                var ids = _doc.memos.Where(x => !string.IsNullOrEmpty(x.id)).Select(x => x.id);
                _knownMemoIds.IntersectWith(ids);
            }
        }

        var nowt = EditorApplication.timeSinceStartup;
        if (nowt - _lastUiRepaint > 0.5)
        {
            _lastUiRepaint = nowt;
            Repaint();
        }
    }

    private void OnGUI()
    {
        EnsureStyles();

        if (_cfg == null)
        {
            EditorGUILayout.HelpBox(L("Config not found.", "設定ファイルが見つかりません。"), MessageType.Error);
            return;
        }

        using (new GUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            if (GUILayout.Toggle(_tab == 0, ToolbarLabel("Overview", "概要", "Main", "概要"), EditorStyles.toolbarButton)) _tab = 0;
            if (GUILayout.Toggle(_tab == 1, ToolbarLabel("Details", "詳細", "More", "詳細"), EditorStyles.toolbarButton)) _tab = 1;
            if (GUILayout.Toggle(_tab == 2, ToolbarLabel("Memos", "メモ", "Memos", "メモ"), EditorStyles.toolbarButton)) _tab = 2;
            if (GUILayout.Toggle(_tab == 3, ToolbarLabel("Settings", "設定", "Prefs", "設定"), EditorStyles.toolbarButton)) _tab = 3;
            GUILayout.FlexibleSpace();
            if (!IsCompactLayout(700f))
                GUILayout.Label(new GUIContent(TruncateMiddle(_backendStatus, 64), _backendStatus), EditorStyles.miniLabel);

            using (new EditorGUI.DisabledScope(_backend == null))
            {
                if (GUILayout.Button(ToolbarLabel("Refresh", "更新", "Sync", "更新"), EditorStyles.toolbarButton, GUILayout.Width(IsCompactLayout(520f) ? 60f : 80f)))
                    RefreshSnapshotAsync();
            }
        }

        DrawMemoAlertBar();

        if (_tab != 3 && _tab != 0 && _backend == null)
        {
            EditorGUILayout.HelpBox(
                string.IsNullOrEmpty(_backendError)
                    ? L("Backend is not ready. Open Settings and configure storage.",
                        "バックエンドを使用できません。Settings を開いて共有設定を確認してください。")
                    : _backendError,
                MessageType.Error);
            DrawActionButtons(
                new ActionButtonInfo
                {
                    label = L("Go To Settings", "Settings を開く"),
                    onClick = () => _tab = 3
                },
                new ActionButtonInfo
                {
                    label = L("Retry", "再試行"),
                    onClick = BuildBackendSafe
                });
            return;
        }

        if (_tab == 0) DrawOverview();
        else if (_tab == 1) DrawActivity();
        else if (_tab == 2) DrawMemos();
        else DrawSettings();
    }

    private void DrawOverview()
    {
        var alive = GetAlivePresences();
        var activeLocks = GetActiveLocks();
        var workSelection = GetOverviewWorkTarget(alive);
        var unreadCount = (_doc?.memos ?? new List<MemoItem>()).Count(MemoIsUnread);
        var overviewMemos = (_doc?.memos ?? new List<MemoItem>())
            .Where(m => m.pinned || MemoIsUnread(m))
            .OrderByDescending(m => m.pinned)
            .ThenByDescending(MemoIsUnread)
            .ThenByDescending(m => m.createdAt)
            .Take(6)
            .ToList();
        var onlineCount = alive.Select(p => UserKey(p.userId, p.user)).Distinct().Count();
        var overviewLocks = _overviewRelatedLocksOnly && workSelection.HasTarget
            ? activeLocks.Where(l => DoesLockAffectSelection(l, workSelection)).ToList()
            : activeLocks;

        _scrollOverview = EditorGUILayout.BeginScrollView(_scrollOverview);

        EditorGUILayout.LabelField(L("Overview", "概要"), EditorStyles.boldLabel);

        DrawOverviewHeaderCards(
            new SummaryCardInfo
            {
                title = L("Online", "オンライン"),
                value = onlineCount.ToString(),
                detail = _backend == null || !string.IsNullOrEmpty(_backendError)
                    ? L("Connection issue. Check Settings.", "接続異常。Settings を確認してください。")
                    : _overviewOnlineExpanded
                        ? L("Click the panel to hide active users.", "パネルをクリックして一覧を閉じます。")
                        : L("Click the panel to show active users.", "パネルをクリックして一覧を表示します。"),
                tone = _backend == null || !string.IsNullOrEmpty(_backendError) ? MessageType.Warning : MessageType.Info,
                onValueClick = () => _overviewOnlineExpanded = !_overviewOnlineExpanded,
                valueTooltip = L("Show who is editing now", "現在編集中のユーザーを表示")
            },
            new SummaryCardInfo
            {
                title = L("Unread Memos", "未読メモ"),
                value = unreadCount.ToString(),
                detail = overviewMemos.Count == 0 ? L("No unread memo", "未読なし") : LF("{0} shown below", "{0} 件を下に表示", overviewMemos.Count),
                tone = unreadCount == 0 ? MessageType.Info : MessageType.Warning
            },
            alive,
            CurrentUserId);

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField(L("Unread / Pinned Memos", "未読 / ピン留めメモ"), EditorStyles.boldLabel);
        if (overviewMemos.Count == 0)
        {
            EditorGUILayout.HelpBox(L("No unread or pinned memo.", "未読またはピン留めメモはありません。"), MessageType.Info);
        }
        else
        {
            foreach (var memo in overviewMemos)
            {
                using (new GUILayout.VerticalScope("box"))
                {
                    var isUnread = MemoIsUnread(memo);
                    var tags = new List<string>();
                    if (memo.pinned) tags.Add("📌");
                    if (isUnread) tags.Add(L("Unread", "未読"));
                    var tagText = tags.Count == 0 ? "" : "  [" + string.Join(" / ", tags) + "]";
                    GUILayout.Label($"{DisplayUser(memo.authorId, memo.author)}  {UnixToLocal(memo.createdAt)}{tagText}", _memoMetaStyle);
                    GUILayout.Label(RenderMemoMarkdown(memo.text), _memoBodyStyle);

                    if (isUnread)
                    {
                        DrawActionButtons(
                            new ActionButtonInfo
                            {
                                label = L("Mark as read", "既読にする"),
                                enabled = _backend != null && !string.IsNullOrEmpty(memo.id),
                                onClick = () => MarkMemoAsReadAsync(memo)
                            });
                    }
                }
            }
        }

        EditorGUILayout.Space(10);
        var activeLocksLabel = L("Active Locks", "ロック中一覧");
        using (new GUILayout.HorizontalScope())
        {
            var labelWidth = EditorStyles.boldLabel.CalcSize(new GUIContent(activeLocksLabel)).x + 8f;
            GUILayout.Label(activeLocksLabel, EditorStyles.boldLabel, GUILayout.Width(labelWidth));
            GUILayout.Space(6f);
            using (new EditorGUI.DisabledScope(!workSelection.HasTarget))
            {
                _overviewRelatedLocksOnly = EditorGUILayout.ToggleLeft(
                    L("Only related to my work", "自分の作業に関連のみ"),
                    _overviewRelatedLocksOnly,
                    GUILayout.Width(IsCompactLayout(620f) ? 180f : 210f));
            }
        }

        if (_overviewRelatedLocksOnly && !workSelection.HasTarget)
        {
            EditorGUILayout.HelpBox(
                L("Open or select a target first to filter by your current work.",
                  "自分の作業に関連するロックだけに絞るには、先に対象を開くか選択してください。"),
                MessageType.Info);
        }

        if (overviewLocks.Count == 0)
        {
            EditorGUILayout.HelpBox(
                _overviewRelatedLocksOnly
                    ? L("No active lock affects your current work.", "現在の作業に影響するロックはありません。")
                    : L("No active lock.", "現在のロックはありません。"),
                MessageType.Info);
        }
        else
        {
            foreach (var lockItem in overviewLocks)
            {
                using (new GUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.LabelField(FormatLockTargetLabel(lockItem.assetPath), EditorStyles.boldLabel);
                    DrawOverviewLine(L("Owner", "所有者"), DisplayUser(lockItem.ownerId, lockItem.owner) ?? L("(unknown)", "(不明)"));
                    DrawOverviewLine(L("State", "状態"), FormatLockState(lockItem), GetLockStateTooltip(lockItem));
                    var gitSummary = FormatLockGitSummary(lockItem);
                    if (!string.IsNullOrEmpty(gitSummary))
                        DrawOverviewLine(L("Git", "Git"), gitSummary, GetLockGitTooltip(lockItem));
                    if (!string.IsNullOrEmpty(lockItem.reason))
                        DrawOverviewLine(L("Reason", "理由"), FormatLockReason(lockItem.reason));
                    if (lockItem.retainedAt > 0)
                        DrawOverviewLine(L("Retained Since", "保持開始"), UnixToLocal(lockItem.retainedAt), GetLockStateTooltip(lockItem));

                    var isMine = IsCurrentUser(lockItem);
                    var primaryLabel = GetLockPrimaryActionLabel(lockItem, isMine);
                    var primaryTooltip = GetLockPrimaryActionTooltip(lockItem, isMine);
                    var canUsePrimaryAction = CanUseLockPrimaryAction(lockItem, isMine);
                    DrawActionButtons(
                        new ActionButtonInfo
                        {
                            label = primaryLabel,
                            tooltip = primaryTooltip,
                            enabled = _backend != null && canUsePrimaryAction,
                            onClick = () =>
                            {
                                if (isMine)
                                    UnlockSelectionLocksAsync(new List<LockItem> { lockItem });
                                else
                                    RequestUnlockAsync(lockItem);
                            }
                        });
                }
            }
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawActivity()
    {
        var selection = GetCurrentSelectionInfo();
        var alive = GetAlivePresences();
        var activeLocks = GetActiveLocks();

        _scrollActivity = EditorGUILayout.BeginScrollView(_scrollActivity);
        DrawDashboard(selection, alive, activeLocks);
        EditorGUILayout.Space(10);
        DrawSelectionStatus(selection, alive, activeLocks);
        EditorGUILayout.Space(10);
        DrawUsersSection(alive, activeLocks);
        EditorGUILayout.Space(10);
        DrawLockManager(selection, activeLocks);
        EditorGUILayout.EndScrollView();
    }

    private void DrawDashboard(SelectionTargetInfo selection, List<EditingPresence> alive, List<LockItem> activeLocks)
    {
        EditorGUILayout.LabelField(L("Shared Status", "共有状態"), EditorStyles.boldLabel);

        var blockingCount = activeLocks.Count(l => !IsCurrentUser(l));
        var ownLockCount = activeLocks.Count(IsCurrentUser);
        var onlineNames = alive
            .GroupBy(p => UserKey(p.userId, p.user))
            .Select(group => DisplayUser(group.First().userId, group.First().user))
            .Where(x => !string.IsNullOrEmpty(x))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var connectionType = _backend == null ? MessageType.Warning : MessageType.Info;
        var lastSync = _doc.updatedAt > 0
            ? LF("Last sync {0}", "最終同期 {0}", FormatUnixToLocal(_doc.updatedAt))
            : L("No shared snapshot yet", "共有スナップショット未取得");

        DrawSummaryCards(
            new SummaryCardInfo
            {
                title = L("Connection", "接続"),
                value = _backend == null ? L("Needs setup", "要設定") : L("Healthy", "正常"),
                detail = lastSync,
                tone = connectionType
            },
            new SummaryCardInfo
            {
                title = L("Online", "オンライン"),
                value = onlineNames.Length.ToString(),
                detail = onlineNames.Length == 0 ? L("Nobody active", "アクティブなし") : FormatNames(onlineNames, 3),
                tone = onlineNames.Length == 0 ? MessageType.None : MessageType.Info
            },
            new SummaryCardInfo
            {
                title = L("Locks", "ロック"),
                value = blockingCount.ToString(),
                detail = LF("{0} mine, {1} total", "{0} 件自分, 合計 {1} 件", ownLockCount, activeLocks.Count),
                tone = blockingCount == 0 ? MessageType.Info : MessageType.Warning
            });
    }

    private void DrawSummaryCards(params SummaryCardInfo[] cards)
    {
        if (cards == null || cards.Length == 0)
            return;

        int columnCount = IsCompactLayout(620f) ? 1 : 2;
        float viewWidth = Mathf.Max(220f, position.width - 28f);
        float spacing = 8f;
        float cardWidth = columnCount == 1
            ? viewWidth
            : Mathf.Max(220f, (viewWidth - spacing) / columnCount);

        for (int i = 0; i < cards.Length; i += columnCount)
        {
            using (new GUILayout.HorizontalScope())
            {
                for (int column = 0; column < columnCount; column++)
                {
                    int index = i + column;
                    if (index >= cards.Length)
                    {
                        GUILayout.FlexibleSpace();
                        continue;
                    }

                    DrawSummaryCard(cards[index], cardWidth);
                    if (column < columnCount - 1)
                        GUILayout.Space(spacing);
                }
            }
        }
    }

    private void DrawOverviewHeaderCards(SummaryCardInfo onlineCard, SummaryCardInfo unreadCard, List<EditingPresence> alive, string me)
    {
        int columnCount = IsCompactLayout(620f) ? 1 : 2;
        float viewWidth = Mathf.Max(220f, position.width - 28f);
        float spacing = 8f;
        float cardWidth = columnCount == 1
            ? viewWidth
            : Mathf.Max(220f, (viewWidth - spacing) / columnCount);

        if (columnCount == 1)
        {
            DrawSummaryCard(onlineCard, cardWidth);
            if (_overviewOnlineExpanded)
                DrawOverviewOnlineExpansion(alive, cardWidth);

            GUILayout.Space(spacing);
            DrawSummaryCard(unreadCard, cardWidth);
            return;
        }

        using (new GUILayout.HorizontalScope())
        {
            DrawSummaryCard(onlineCard, cardWidth);
            GUILayout.Space(spacing);
            DrawSummaryCard(unreadCard, cardWidth);
        }

        if (_overviewOnlineExpanded)
        {
            using (new GUILayout.HorizontalScope())
            {
                DrawOverviewOnlineExpansion(alive, cardWidth);
                GUILayout.Space(spacing);
                GUILayout.FlexibleSpace();
            }
        }
    }

    private void DrawOverviewOnlineExpansion(List<EditingPresence> alive, float width)
    {
        using (new GUILayout.VerticalScope("box", GUILayout.Width(width)))
        {
            EditorGUILayout.LabelField(L("Who Is Editing", "編集中メンバー"), EditorStyles.boldLabel);

            if (alive.Count == 0)
            {
                EditorGUILayout.LabelField(L("Nobody is online right now.", "現在オンラインのユーザーはいません。"), _cardDetailStyle);
                return;
            }

            foreach (var presence in alive.OrderBy(p => DisplayUser(p.userId, p.user), StringComparer.OrdinalIgnoreCase))
            {
                EditorGUILayout.Space(2);
                var presenceName = DisplayUser(presence.userId, presence.user);
                var label = IsCurrentUser(presence)
                    ? LF("{0} ({1})", "{0}（{1}）", presenceName, L("You", "自分"))
                    : presenceName;
                EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
                DrawOverviewLine(L("Editing", "編集中"), FormatPresenceTarget(presence));
                if (!string.IsNullOrEmpty(presence.assetPath))
                    DrawOverviewLine(L("Path", "パス"), TruncateMiddle(presence.assetPath, IsCompactLayout(620f) ? 40 : 78));
            }
        }
    }

    private void DrawSummaryCard(SummaryCardInfo card, float width)
    {
        using (new GUILayout.VerticalScope("box", GUILayout.Width(width), GUILayout.MinHeight(78)))
        {
            var markerRect = GUILayoutUtility.GetRect(1f, 4f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(markerRect, ToneColor(card.tone));

            var previousColor = GUI.contentColor;
            EditorGUILayout.LabelField(card.title, EditorStyles.miniBoldLabel);
            GUI.contentColor = card.tone == MessageType.None ? previousColor : ToneColor(card.tone);
            EditorGUILayout.LabelField(card.value, _cardValueStyle);
            GUI.contentColor = previousColor;
            EditorGUILayout.LabelField(card.detail, _cardDetailStyle);
        }

        if (card.onValueClick != null)
        {
            var cardRect = GUILayoutUtility.GetLastRect();
            var tooltip = string.IsNullOrEmpty(card.valueTooltip) ? card.value : card.valueTooltip;
            EditorGUIUtility.AddCursorRect(cardRect, MouseCursor.Link);
            if (GUI.Button(cardRect, new GUIContent("", tooltip), GUIStyle.none))
                card.onValueClick.Invoke();
        }
    }

    private void DrawSelectionStatus(SelectionTargetInfo selection, List<EditingPresence> alive, List<LockItem> activeLocks)
    {
        EditorGUILayout.LabelField(L("Selection Status", "選択中ターゲット"), EditorStyles.boldLabel);

        if (!selection.HasTarget)
        {
            EditorGUILayout.HelpBox(
                L("Select an asset, folder, scene, prefab, or GameObject to see its collaboration state.",
                  "アセット、フォルダ、シーン、Prefab、GameObject を選ぶと、その状態を確認できます。"),
                MessageType.Info);
            return;
        }

        var selectionLocks = GetLocksForSelection(selection, activeLocks);
        var mineLocks = selectionLocks.Where(IsCurrentUser).ToList();
        var otherLocks = selectionLocks.Where(l => !IsCurrentUser(l)).ToList();
        var editors = GetEditorsForSelection(selection, alive);
        var peerEditors = editors.Where(p => !IsCurrentUser(p)).ToList();
        var selfEditing = editors.Any(IsCurrentUser);
        var relatedMemoCount = (_doc?.memos ?? new List<MemoItem>()).Count(m => DoesMemoMatchSelection(m, selection));

        var summaryType = otherLocks.Count > 0 ? MessageType.Error : peerEditors.Count > 0 ? MessageType.Warning : MessageType.Info;
        var summaryText = otherLocks.Count > 0
            ? LF("{0} teammate lock(s) affect this target.", "{0} 件の他ユーザーロックがこの対象に影響しています。", otherLocks.Count)
            : peerEditors.Count > 0
                ? LF("{0} teammate(s) are editing this target.", "{0} 人がこの対象を編集中です。", peerEditors.Count)
                : selfEditing
                    ? L("You are editing this target.", "自分がこの対象を編集中です。")
                    : L("No active conflict detected for the current target.", "現在の対象にアクティブな競合はありません。");

        using (new GUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField(selection.displayName, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(L("Context", "コンテキスト"), selection.context);
            if (!string.IsNullOrEmpty(selection.assetPath))
            {
                if (IsCompactLayout(620f))
                    EditorGUILayout.LabelField(selection.assetPath, _cardDetailStyle);
                else
                    EditorGUILayout.SelectableLabel(selection.assetPath, EditorStyles.textField, GUILayout.Height(34));
            }
            if (!string.IsNullOrEmpty(selection.objectLockKey))
                EditorGUILayout.LabelField(L("Lock Scope", "ロックスコープ"), L("Object-specific lock", "オブジェクト単位ロック"));

            EditorGUILayout.HelpBox(summaryText, summaryType);

            if (peerEditors.Count > 0)
            {
                EditorGUILayout.LabelField(L("Teammates Editing", "編集中の他ユーザー"),
                    string.Join(", ",
                        peerEditors
                            .GroupBy(p => UserKey(p.userId, p.user))
                            .Select(group => DisplayUser(group.First().userId, group.First().user))
                            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
            }
            else if (selfEditing)
            {
                DrawOverviewLine(L("Your Status", "自分の状態"), L("Editing this target", "この対象を編集中"));
            }

            if (selectionLocks.Count > 0)
            {
                EditorGUILayout.LabelField(L("Active Locks", "有効なロック"), EditorStyles.miniBoldLabel);
                foreach (var lockItem in selectionLocks.Take(3))
                {
                    var ownerPrefix = IsCurrentUser(lockItem)
                        ? L("Mine", "自分")
                        : DisplayUser(lockItem.ownerId, lockItem.owner);
                    EditorGUILayout.LabelField($"• {ownerPrefix}: {TruncateMiddle(lockItem.assetPath, 56)}");
                }
            }

            EditorGUILayout.LabelField(L("Related Memos", "関連メモ"), relatedMemoCount.ToString());

            DrawActionButtons(CreateSelectionActionButtons(selection, mineLocks));
        }
    }

    private void DrawUsersSection(List<EditingPresence> alive, List<LockItem> activeLocks)
    {
        var users = GetKnownUsers(alive, activeLocks);
        var deletedUsers = IsCurrentUserRootAdmin() ? GetDeletedUsers() : new List<KnownUserInfo>();
        var onlineCount = users.Count(user => user.isOnline);
        var now = TimeUtil.NowMs();

        using (new GUILayout.VerticalScope("box"))
        {
            _usersSectionExpanded = EditorGUILayout.Foldout(
                _usersSectionExpanded,
                LF("Users ({0} online / {1} total)", "ユーザー一覧（オンライン {0} / 合計 {1}）", onlineCount, users.Count),
                true,
                EditorStyles.foldoutHeader);

            if (!_usersSectionExpanded)
                return;
        }

        if (users.Count == 0)
        {
            EditorGUILayout.HelpBox(L("No user information yet.", "まだユーザー情報はありません。"), MessageType.Info);
        }
        else
        {
            foreach (var user in users)
            {
                var userKey = UserKey(user.userId, user.displayName);
                _cachedPresenceByUserKey.TryGetValue(userKey, out var currentPresence);
                var userLocks = _cachedLocksByUserKey.TryGetValue(userKey, out var cachedLocks)
                    ? cachedLocks
                    : new List<LockItem>();
                var historyKey = UserKey(user.userId, user.displayName);
                bool expanded = _expandedUserHistories.Contains(historyKey);
                bool isRootAdmin = CollabIdentityUtility.Matches(user.userId, user.displayName, GetRootAdminUserId(), GetRootAdminUserName());
                bool isAdmin = _cachedAdminUserKeys.Contains(userKey);
                bool canDeleteUser = IsCurrentUserRootAdmin() &&
                                     !isRootAdmin &&
                                     !IsCurrentUser(user.userId, user.displayName) &&
                                     !string.IsNullOrEmpty(user.userId);

                using (new GUILayout.VerticalScope("box"))
                {
                    var title = isRootAdmin
                        ? LF("{0} [{1}]", "{0} [{1}]", user.displayName, L("Root Admin", "Root管理者"))
                        : isAdmin
                            ? LF("{0} [{1}]", "{0} [{1}]", user.displayName, L("Admin", "管理者"))
                            : user.displayName;
                    EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                    if (!string.IsNullOrEmpty(user.userId))
                        DrawOverviewLine(L("User ID", "ユーザーID"), user.userId);
                    DrawOverviewLine(
                        L("Status", "状態"),
                        user.isOnline
                            ? L("Online", "オンライン")
                            : L("Offline", "オフライン"));

                    if (currentPresence != null)
                    {
                        var targetLabel = string.IsNullOrEmpty(currentPresence.assetPath)
                            ? currentPresence.context ?? ""
                            : $"{currentPresence.context} / {TruncateMiddle(currentPresence.assetPath, IsCompactLayout(620f) ? 44 : 72)}";
                        DrawOverviewLine(L("Current Work", "現在の作業"), targetLabel);
                        DrawOverviewLine(L("Last Beat", "最終ハートビート"), LF("{0:0.0}s ago", "{0:0.0}秒前", (now - currentPresence.heartbeat) / 1000f));
                    }

                    DrawOverviewLine(L("Locks", "ロック"), userLocks.Count.ToString());
                    if (!IsWorkHistoryEnabled())
                    {
                        DrawOverviewLine(L("Work History", "作業履歴"), L("Disabled by admin", "管理者により無効"));
                    }
                    else
                    {
                        DrawActionButtons(
                            new ActionButtonInfo
                            {
                                label = expanded ? L("Hide Work History", "作業履歴を閉じる") : L("Work History", "作業履歴"),
                                onClick = () =>
                                {
                                    if (expanded) _expandedUserHistories.Remove(historyKey);
                                    else _expandedUserHistories.Add(historyKey);
                                }
                            });

                        if (expanded)
                            DrawUserHistoryList(user.userId, user.displayName);
                    }

                    if (canDeleteUser)
                    {
                        DrawActionButtons(
                            new ActionButtonInfo
                            {
                                label = L("Delete User", "ユーザーを削除"),
                                tooltip = L("Remove this user from the active list and block the User ID until a Root Admin restores it.",
                                            "このユーザーをアクティブ一覧から外し、Root管理者が復活するまでその User ID の再書き込みをブロックします。"),
                                enabled = _backend != null,
                                onClick = () => DeleteUserAsync(user.userId, user.displayName)
                            });
                    }
                }
            }
        }

        if (IsCurrentUserRootAdmin())
            DrawDeletedUsersSection(deletedUsers);
    }

    private void DrawDeletedUsersSection(List<KnownUserInfo> deletedUsers)
    {
        using (new GUILayout.VerticalScope("box"))
        {
            _deletedUsersSectionExpanded = EditorGUILayout.Foldout(
                _deletedUsersSectionExpanded,
                LF("Deleted Users ({0})", "削除済みユーザー一覧（{0}）", deletedUsers?.Count ?? 0),
                true,
                EditorStyles.foldoutHeader);

            if (!_deletedUsersSectionExpanded)
                return;
        }

        EditorGUILayout.HelpBox(
            L("Only Root Admin can see this list and restore blocked users. Restoring a user removes the block and allows that User ID to write again.",
              "この一覧は Root管理者だけが表示・復活できます。復活すると削除済みブロックが解除され、その User ID で再び書き込めるようになります。"),
            MessageType.Info);

        if (deletedUsers == null || deletedUsers.Count == 0)
        {
            EditorGUILayout.HelpBox(L("No deleted users.", "削除済みユーザーはいません。"), MessageType.Info);
            return;
        }

        foreach (var user in deletedUsers)
        {
            using (new GUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField(user.displayName, EditorStyles.boldLabel);
                if (!string.IsNullOrEmpty(user.userId))
                    DrawOverviewLine(L("User ID", "ユーザーID"), user.userId);
                DrawOverviewLine(L("Status", "状態"), L("Deleted / Blocked", "削除済み / ブロック中"));

                DrawActionButtons(
                    new ActionButtonInfo
                    {
                        label = L("Restore User", "ユーザーを復活"),
                        tooltip = L("Remove this user from the deleted list and allow the User ID to join again.",
                                    "このユーザーを削除済み一覧から外し、その User ID で再参加できるようにします。"),
                        enabled = _backend != null,
                        onClick = () => RestoreUserAsync(user.userId, user.displayName)
                    });
            }
        }
    }

    private void DrawLockManager(SelectionTargetInfo selection, List<LockItem> activeLocks)
    {
        EditorGUILayout.LabelField(L("Lock Manager", "ロック管理"), EditorStyles.boldLabel);
        var canForceUnlock = IsCurrentUserAdmin();

        _lockFilter = GUILayout.Toolbar(
            _lockFilter,
            new[]
            {
                L("All", "すべて"),
                L("Mine", "自分"),
                L("Blocking Me", "他ユーザー")
            });

        _lockSearch = EditorGUILayout.TextField(L("Search", "検索"), _lockSearch);

        DrawActionButtons(
            new ActionButtonInfo
            {
                label = L("Unlock All Mine", "自分のロックを全解除"),
                enabled = _backend != null && activeLocks.Any(IsCurrentUser),
                onClick = () =>
                {
                    if (EditorUtility.DisplayDialog(
                            "CollabSync",
                            L("Release all locks owned by you?", "自分が所有するロックをすべて解除しますか？"),
                            L("Release", "解除"),
                            L("Cancel", "キャンセル")))
                    {
                        UnlockAllMineAsync(activeLocks);
                    }
                }
            },
            new ActionButtonInfo
            {
                label = L("Refresh Snapshot", "状態を更新"),
                onClick = RefreshSnapshotAsync
            });

        var filtered = activeLocks.Where(LockMatchesFilter).ToList();
        if (filtered.Count == 0)
        {
            EditorGUILayout.HelpBox(
                L("No locks match the current filter.", "現在の条件に一致するロックはありません。"),
                MessageType.Info);
            return;
        }

        if (canForceUnlock)
        {
            EditorGUILayout.HelpBox(
                L("As an admin, you can force unlock teammate locks from this Details tab only.",
                  "管理者はこの Details タブからのみ、他ユーザーのロックを強制解除できます。"),
                MessageType.Info);
        }

        foreach (var lockItem in filtered)
        {
            using (new GUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField(TruncateMiddle(lockItem.assetPath ?? L("(unknown)", "(不明)"), 72), EditorStyles.boldLabel);
                EditorGUILayout.LabelField(L("Owner", "所有者"), DisplayUser(lockItem.ownerId, lockItem.owner) ?? L("(unknown)", "(不明)"));
                DrawDetailLine(L("State", "状態"), FormatLockState(lockItem), GetLockStateTooltip(lockItem));
                var gitSummary = FormatLockGitSummary(lockItem);
                if (!string.IsNullOrEmpty(gitSummary))
                    DrawDetailLine(L("Git", "Git"), gitSummary, GetLockGitTooltip(lockItem));
                if (!string.IsNullOrEmpty(lockItem.reason))
                    EditorGUILayout.LabelField(L("Reason", "理由"), FormatLockReason(lockItem.reason));
                EditorGUILayout.LabelField(L("Expires", "期限"), FormatLockExpiry(lockItem));
                if (lockItem.retainedAt > 0)
                    DrawDetailLine(L("Retained Since", "保持開始"), UnixToLocal(lockItem.retainedAt), GetLockStateTooltip(lockItem));

                var retainedDetail = FormatRetainedLockDetail(lockItem);
                if (!string.IsNullOrEmpty(retainedDetail))
                    EditorGUILayout.HelpBox(retainedDetail, MessageType.Info);

                if (selection.HasTarget && DoesLockAffectSelection(lockItem, selection))
                    EditorGUILayout.HelpBox(L("This lock affects the current selection.", "このロックは現在の選択中ターゲットに影響します。"), MessageType.Warning);

                var isMine = IsCurrentUser(lockItem);
                var primaryLabel = GetLockPrimaryActionLabel(lockItem, isMine);
                var primaryTooltip = GetLockPrimaryActionTooltip(lockItem, isMine);
                var canUsePrimaryAction = CanUseLockPrimaryAction(lockItem, isMine);
                var buttons = new List<ActionButtonInfo>
                {
                    new ActionButtonInfo
                    {
                        label = L("Ping", "表示"),
                        tooltip = L("Ping the asset in Project or select the related target.", "Project で対象を表示、または関連ターゲットを選択します。"),
                        enabled = IsProjectRelativePath(lockItem.assetPath),
                        onClick = () => PingAssetPath(lockItem.assetPath)
                    },
                    new ActionButtonInfo
                    {
                        label = primaryLabel,
                        tooltip = primaryTooltip,
                        enabled = _backend != null && canUsePrimaryAction,
                        onClick = () =>
                        {
                            if (isMine)
                                UnlockSelectionLocksAsync(new List<LockItem> { lockItem });
                            else
                                RequestUnlockAsync(lockItem);
                        }
                    }
                };

                if (canForceUnlock && !isMine)
                {
                    buttons.Add(new ActionButtonInfo
                    {
                        label = L("Force Unlock", "強制解除"),
                        tooltip = L(
                            "Admin only. Bypasses retained-lock safety and removes the teammate lock immediately.",
                            "管理者専用です。保持ロックの安全確認を無視して、他ユーザーのロックを即時に削除します。"),
                        enabled = _backend != null,
                        onClick = () => ForceUnlockAsync(lockItem)
                    });
                }

                DrawActionButtons(buttons.ToArray());
            }
        }
    }

    private void DrawMemos()
    {
        var selection = GetCurrentSelectionInfo();
        var selectedMemoPath = selection.assetPath;

        EditorGUILayout.LabelField(L("Team Memos", "チームメモ"), EditorStyles.boldLabel);
        using (new GUILayout.VerticalScope("box"))
        {
            _newMemo = EditorGUILayout.TextArea(_newMemo ?? "", GUILayout.MinHeight(56));
            var availableLinkModes = GetAvailableNewMemoLinkModes(selectedMemoPath);
            var currentLinkModeIndex = Array.IndexOf(availableLinkModes, _newMemoLinkMode);
            if (currentLinkModeIndex < 0)
            {
                _newMemoLinkMode = string.IsNullOrWhiteSpace(_newMemoLinkTarget)
                    ? NewMemoLinkMode.None
                    : NewMemoLinkMode.ManualTarget;
                currentLinkModeIndex = Array.IndexOf(availableLinkModes, _newMemoLinkMode);
            }
            if (currentLinkModeIndex < 0)
                currentLinkModeIndex = 0;

            var nextLinkModeIndex = EditorGUILayout.Popup(
                L("Link target", "紐付け先"),
                currentLinkModeIndex,
                availableLinkModes.Select(GetNewMemoLinkModeLabel).ToArray());
            _newMemoLinkMode = availableLinkModes[Mathf.Clamp(nextLinkModeIndex, 0, availableLinkModes.Length - 1)];

            if (_newMemoLinkMode == NewMemoLinkMode.ManualTarget)
            {
                _newMemoLinkTarget = EditorGUILayout.TextField(
                    L("URL / Asset Path", "URL / アセットパス"),
                    _newMemoLinkTarget ?? "");
            }

            var newMemoLinkedTarget = ResolveNewMemoLinkTarget(selectedMemoPath);
            var canAddMemo = !string.IsNullOrWhiteSpace(_newMemo)
                && _backend != null
                && (_newMemoLinkMode != NewMemoLinkMode.ManualTarget || !string.IsNullOrWhiteSpace(newMemoLinkedTarget));

            if (IsCompactLayout())
            {
                DrawActionButtons(
                    new ActionButtonInfo
                    {
                        label = L("Add Memo", "メモを追加"),
                        enabled = canAddMemo,
                        onClick = () => _ = CreateMemoAsync(_newMemo, newMemoLinkedTarget)
                    });
            }
            else
            {
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();

                    using (new EditorGUI.DisabledScope(!canAddMemo))
                    {
                        if (GUILayout.Button(L("Add Memo", "メモを追加"), GUILayout.MinWidth(100)))
                            _ = CreateMemoAsync(_newMemo, newMemoLinkedTarget);
                    }
                }
            }

            if (!string.IsNullOrEmpty(newMemoLinkedTarget))
                EditorGUILayout.LabelField(L("Linked to", "紐付け先"), newMemoLinkedTarget, _cardDetailStyle);
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField(L("Filters", "フィルタ"), EditorStyles.boldLabel);
        _memoSearch = EditorGUILayout.TextField(L("Search", "検索"), _memoSearch);
        if (IsCompactLayout())
        {
            _memoFilterUnread = EditorGUILayout.ToggleLeft(L("Unread only", "未読のみ"), _memoFilterUnread);
            _memoFilterPinned = EditorGUILayout.ToggleLeft(L("Pinned only", "ピン留めのみ"), _memoFilterPinned);
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(selectedMemoPath)))
                _memoFilterSelection = EditorGUILayout.ToggleLeft(L("Related to selection", "選択に関連"), _memoFilterSelection);
        }
        else
        {
            using (new GUILayout.HorizontalScope())
            {
                _memoFilterUnread = EditorGUILayout.ToggleLeft(L("Unread only", "未読のみ"), _memoFilterUnread, GUILayout.Width(110));
                _memoFilterPinned = EditorGUILayout.ToggleLeft(L("Pinned only", "ピン留めのみ"), _memoFilterPinned, GUILayout.Width(120));
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(selectedMemoPath)))
                {
                    _memoFilterSelection = EditorGUILayout.ToggleLeft(L("Related to selection", "選択に関連"), _memoFilterSelection, GUILayout.Width(150));
                }
            }
        }

        if (_memoFilterSelection && string.IsNullOrEmpty(selectedMemoPath))
        {
            EditorGUILayout.HelpBox(
                L("Select something first to filter linked memos.", "紐付けメモで絞り込むには先に対象を選択してください。"),
                MessageType.Info);
        }

        _scrollMemo = EditorGUILayout.BeginScrollView(_scrollMemo);
        var memos = (_doc?.memos ?? new List<MemoItem>()).Where(m => MatchesMemoFilters(m, selection)).ToList();

        if (memos.Count == 0)
        {
            EditorGUILayout.HelpBox(
                L("No memos match the current filter.", "現在の条件に一致するメモはありません。"),
                MessageType.Info);
        }

        foreach (var memo in memos)
        {
            using (new GUILayout.VerticalScope("box"))
            {
                using (new GUILayout.HorizontalScope())
                {
                    var pinned = GUILayout.Toggle(memo.pinned, "📌", GUILayout.Width(36));
                    if (pinned != memo.pinned)
                    {
                        memo.pinned = pinned;
                        _ = _backend.UpsertMemoAsync(memo);
                    }

                    GUILayout.Label($"{DisplayUser(memo.authorId, memo.author)}  {UnixToLocal(memo.createdAt)}", _memoMetaStyle);
                    GUILayout.FlexibleSpace();

                    if (!string.IsNullOrEmpty(memo.assetPath))
                        GUILayout.Label(L("[Linked]", "[関連]"), _memoMetaStyle);
                }

                if (!string.IsNullOrEmpty(memo.assetPath))
                    EditorGUILayout.LabelField(L("Linked to", "紐付け先"), memo.assetPath, _cardDetailStyle);

                GUILayout.Label(RenderMemoMarkdown(memo.text), _memoBodyStyle);
                EditorGUILayout.Space(3);

                var isAdmin = IsCurrentUserAdmin();
                var canDelete = IsCurrentUser(memo);
                var canForceDelete = isAdmin && !canDelete;
                var readers = GetMemoReaderDisplayNames(memo);
                var meRead = CollabIdentityUtility.HasRead(memo, CurrentUserId, CurrentUserName);

                GUILayout.Label(L("Read by: ", "既読: ") + (readers.Length == 0 ? L("none", "なし") : string.Join(", ", readers)), _memoMetaStyle);
                DrawActionButtons(
                    new ActionButtonInfo
                    {
                        label = L("Mark as read", "既読にする"),
                        enabled = !meRead && !string.IsNullOrEmpty(memo.id),
                        onClick = () => _ = _backend.MarkMemoReadAsync(memo.id, CurrentUserId, CurrentUserName)
                    },
                    new ActionButtonInfo
                    {
                        label = L("Ping", "表示"),
                        enabled = !string.IsNullOrEmpty(memo.assetPath) && IsProjectRelativePath(memo.assetPath),
                        onClick = () => PingAssetPath(memo.assetPath)
                    },
                    new ActionButtonInfo
                    {
                        label = L("Open Link", "リンクを開く"),
                        enabled = IsWebUrl(memo.assetPath),
                        onClick = () => OpenExternalLink(memo.assetPath)
                    },
                    new ActionButtonInfo
                    {
                        label = L("Delete", "削除"),
                        enabled = canDelete,
                        onClick = () =>
                        {
                            DeleteMemoAsync(memo, false);
                        }
                    },
                    new ActionButtonInfo
                    {
                        label = L("Force Delete", "強制削除"),
                        enabled = canForceDelete,
                        onClick = () => DeleteMemoAsync(memo, true)
                    });
            }
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawSettings()
    {
        _scrollSettings = EditorGUILayout.BeginScrollView(_scrollSettings);

        EditorGUILayout.LabelField(L("User", "ユーザー"), EditorStyles.boldLabel);
        var name = CurrentUserName;
        var newName = EditorGUILayout.DelayedTextField(L("Your Name", "表示名"), name);
        if (!string.Equals(newName, name, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(newName))
            CollabSyncUser.UserName = newName;
        EditorGUILayout.LabelField(L("Your User ID", "自分のユーザーID"));
        EditorGUILayout.SelectableLabel(CurrentUserId, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight + 4));
        EditorGUILayout.HelpBox(
            L("User ID is issued once per local user and kept in protected local storage. It is shown here for reference and cannot be edited from CollabSync.",
              "User ID は各ローカルユーザーごとに一度発行され、保護されたローカル保存領域で固定されます。ここでは確認のみでき、CollabSync から直接変更はできません。"),
            MessageType.Info);

        EditorGUILayout.Space(8);
        DrawLanguageSection();

        EditorGUILayout.Space(8);
        DrawAdminSection();

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField(L("Shared State File", "共有状態ファイル"), EditorStyles.boldLabel);
        EditorGUILayout.LabelField(L("JSON Path", "JSON パス"));
        var nextJsonPath = EditorGUILayout.DelayedTextField(_cfg.localJsonPath);
        if (_cfg != null && !string.Equals(nextJsonPath, _cfg.localJsonPath, StringComparison.Ordinal))
            ApplyJsonPathSelection(nextJsonPath);
        DrawActionButtons(
            new ActionButtonInfo
            {
                label = L("Choose...", "選択..."),
                onClick = ChooseJsonPath
            },
            new ActionButtonInfo
            {
                label = L("New...", "新規..."),
                onClick = CreateJsonPath
            });
        EditorGUILayout.HelpBox(
            L("Point everyone to the same shared JSON file. Use Choose... for an existing file, or New... to create one. CollabSync automatically handles backup rotation and write conflict avoidance for the shared file.",
              "全員が同じ共有 JSON ファイルを指定してください。既存ファイルは「選択...」、新規作成は「新規...」を使います。バックアップ整理と共有ファイル自体の競合回避は CollabSync が自動で処理します。"),
            MessageType.Info);

        var protectSharedStateToggleContent = new GUIContent(
            L("Encrypt shared state file", "共有状態ファイルを暗号化"),
            L(
                "When enabled, the shared state file is stored in protected format. When disabled, it is written as plain JSON.",
                "有効にすると共有状態ファイルは保護形式で保存されます。無効にすると平文 JSON として保存されます。"));
        var canEditSharedStateProtection = CanEditSharedStateProtectionSetting();
        using (new EditorGUI.DisabledScope(!canEditSharedStateProtection))
        {
            var nextProtectSharedStateFile = EditorGUILayout.ToggleLeft(protectSharedStateToggleContent, _cfg.protectSharedStateFile);
            if (_cfg != null && nextProtectSharedStateFile != _cfg.protectSharedStateFile)
                ApplySharedStateProtectionSelection(nextProtectSharedStateFile);
        }

        if (!canEditSharedStateProtection)
        {
            EditorGUILayout.HelpBox(
                L("Only the Root Admin can change shared state file encryption.",
                  "共有状態ファイルの暗号化を切り替えられるのは Root管理者のみです。"),
                MessageType.Info);
        }

        EditorGUILayout.HelpBox(
            _cfg != null && _cfg.protectSharedStateFile
                ? L(
                    "ON: The shared state file is encrypted and tamper-detected. Direct text editing is not expected to work.",
                    "ON: 共有状態ファイルは暗号化と改ざん検知付きで保存されます。テキスト直接編集は前提にしていません。")
                : L(
                    "OFF: The shared state file is stored as plain JSON. This is easier to inspect manually, but direct editing can break synchronization.",
                    "OFF: 共有状態ファイルは平文 JSON で保存されます。中身は見やすくなりますが、直接編集で同期を壊しやすくなります。"),
            _cfg != null && _cfg.protectSharedStateFile ? MessageType.Info : MessageType.Warning);

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField(L("Resolved Local JSON", "解決後の JSON パス"), EditorStyles.boldLabel);
        var ok = _cfg.TryGetResolvedJsonPath(out var resolvedPath, out var statusOrError);
        EditorGUILayout.SelectableLabel(ok ? resolvedPath : statusOrError, EditorStyles.textField, GUILayout.Height(36));

        if (!string.IsNullOrEmpty(_backendError))
            EditorGUILayout.HelpBox(_backendError, MessageType.Warning);

        DrawActionButtons(
            new ActionButtonInfo
            {
                label = L("Reveal", "表示"),
                enabled = ok,
                onClick = () =>
                {
                    if (File.Exists(resolvedPath))
                    {
                        EditorUtility.RevealInFinder(resolvedPath);
                    }
                    else if (Directory.Exists(Path.GetDirectoryName(resolvedPath) ?? ""))
                    {
                        EditorUtility.RevealInFinder(Path.GetDirectoryName(resolvedPath));
                    }
                    else
                    {
                        EditorUtility.DisplayDialog(
                            "CollabSync",
                            LF("Path not found:\n{0}", "パスが見つかりません:\n{0}", resolvedPath),
                            L("OK", "OK"));
                    }
                }
            },
            new ActionButtonInfo
            {
                label = L("Reconnect", "再接続"),
                onClick = BuildBackendSafe
            });

        EditorGUILayout.Space(10);
        DrawBackupSection(ok, resolvedPath);

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField(L("Notifications", "通知"), EditorStyles.boldLabel);
        var nextNotifyOnNewMemo = EditorGUILayout.ToggleLeft(L("Show memo alert bar", "新しいメモを通知バー表示"), _cfg.notifyOnNewMemo);
        if (_cfg != null && nextNotifyOnNewMemo != _cfg.notifyOnNewMemo)
        {
            _cfg.notifyOnNewMemo = nextNotifyOnNewMemo;
            CollabSyncConfig.SaveEditorAsset(_cfg);
        }

        var nextBeepOnNewMemo = EditorGUILayout.ToggleLeft(L("Beep on new memo", "新しいメモでビープ音"), _cfg.beepOnNewMemo);
        if (_cfg != null && nextBeepOnNewMemo != _cfg.beepOnNewMemo)
        {
            _cfg.beepOnNewMemo = nextBeepOnNewMemo;
            CollabSyncConfig.SaveEditorAsset(_cfg);
        }

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField(L("Git-aware Locks", "Git連動ロック"), EditorStyles.boldLabel);
        var retainedLocksToggleContent = new GUIContent(
            L("Keep retained locks until merged", "マージされるまで保持ロックを残す"),
            L(
                "When enabled, manual locks can stay retained until the protected branch contains the lock owner's commit.",
                "有効にすると、手動ロックは保護ブランチに所有者のコミットが入るまで保持状態のまま残ります。"));
        var nextEnableGitAwareRetainedLocks = EditorGUILayout.ToggleLeft(retainedLocksToggleContent, _cfg.enableGitAwareRetainedLocks);
        if (_cfg != null && nextEnableGitAwareRetainedLocks != _cfg.enableGitAwareRetainedLocks)
        {
            _cfg.enableGitAwareRetainedLocks = nextEnableGitAwareRetainedLocks;
            CollabSyncConfig.SaveEditorAsset(_cfg);
            RefreshSnapshotAsync();
            Repaint();
        }

        EditorGUILayout.HelpBox(
            _cfg != null && _cfg.enableGitAwareRetainedLocks
                ? L(
                    "ON: Manual locks may switch to Retained instead of disappearing immediately. This helps keep GitHub branch work safe until the protected branch picks up the commit.",
                    "ON: 手動ロックはすぐに消えず、保持状態に切り替わることがあります。保護ブランチにコミットが入るまで GitHub のブランチ作業を安全寄りに保ちます。")
                : L(
                    "OFF: Unlock behaves in the old simple way and removes your lock immediately.",
                    "OFF: 従来どおり、解除操作ですぐにロックを消します。"),
            MessageType.Info);

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField(L("Doctor", "Doctor"), EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            L("Checks shared JSON creation, read/write access, and the base structure, then attempts auto-repair if needed.",
              "共有 JSON の生成、読み書き、基本構造を確認し、必要なら自動修復を試みます。"),
            MessageType.None);

        DrawDoctorStatusSummary();

        DrawActionButtons(
            new ActionButtonInfo
            {
                label = L("Run Doctor", "Doctor を実行"),
                enabled = !_doctorRunning,
                onClick = StartDoctorRun
            },
            new ActionButtonInfo
            {
                label = L("Copy Result", "結果をコピー"),
                enabled = _doctorReport != null,
                onClick = CopyDoctorResult
            });

        if (_doctorRunning)
            GUILayout.Label(L("Running...", "実行中..."), EditorStyles.miniBoldLabel);
        else if (_doctorReport != null && !string.IsNullOrEmpty(_doctorReport.summary))
            GUILayout.Label(_doctorReport.summary, EditorStyles.miniLabel);

        if (_doctorReport != null)
        {
            if (!string.IsNullOrEmpty(_doctorReport.startedAtLocal))
                EditorGUILayout.LabelField(L("Last Run", "前回実行"), _doctorReport.startedAtLocal);

            if (_doctorReport.issues != null && _doctorReport.issues.Count > 0)
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField(L("Suggested Fixes", "解決方法"), EditorStyles.boldLabel);
                foreach (var issue in _doctorReport.issues)
                {
                    using (new GUILayout.VerticalScope("box"))
                    {
                        EditorGUILayout.LabelField(issue.title ?? "", EditorStyles.boldLabel);
                        if (!string.IsNullOrEmpty(issue.detail))
                            EditorGUILayout.LabelField(issue.detail, EditorStyles.wordWrappedLabel);
                        if (!string.IsNullOrEmpty(issue.fix))
                        {
                            EditorGUILayout.Space(2);
                            EditorGUILayout.LabelField(L("How to fix", "対処方法"), issue.fix, EditorStyles.wordWrappedLabel);
                        }
                    }
                }
            }

            _scrollDoctor = EditorGUILayout.BeginScrollView(_scrollDoctor, GUILayout.MinHeight(180));
            foreach (var line in _doctorReport.lines)
                EditorGUILayout.SelectableLabel(line, EditorStyles.label, GUILayout.Height(EditorGUIUtility.singleLineHeight + 2));
            EditorGUILayout.EndScrollView();
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawBackupSection(bool ok, string resolvedPath)
    {
        EditorGUILayout.LabelField(L("Backup Snapshots", "バックアップスナップショット"), EditorStyles.boldLabel);

        if (!ok)
        {
            EditorGUILayout.HelpBox(
                L("Resolve the shared JSON path first to inspect backups.", "先に共有 JSON パスを解決するとバックアップを確認できます。"),
                MessageType.Info);
            return;
        }

        var backups = GetBackups(resolvedPath, 6);
        var backupDirectory = GetBackupDirectory(resolvedPath);

        DrawActionButtons(
            new ActionButtonInfo
            {
                label = L("Reveal Backups", "バックアップを表示"),
                enabled = !string.IsNullOrEmpty(backupDirectory) && Directory.Exists(backupDirectory),
                onClick = () => EditorUtility.RevealInFinder(backupDirectory)
            },
            new ActionButtonInfo
            {
                label = L("Reveal Latest", "最新を表示"),
                enabled = backups.Count > 0,
                onClick = () => EditorUtility.RevealInFinder(backups[0].fullPath)
            });

        if (backups.Count == 0)
        {
            EditorGUILayout.HelpBox(
                L("No backup snapshot has been created yet. A backup appears after a meaningful lock or memo change.",
                  "まだバックアップは作成されていません。ロックやメモの意味ある変更が発生するとバックアップが作られます。"),
                MessageType.Info);
            return;
        }

        EditorGUILayout.LabelField(L("Stored", "保存数"), backups.Count.ToString());
        EditorGUILayout.LabelField(L("Latest", "最新"), backups[0].timestampLocal);

        foreach (var backup in backups)
        {
            using (new GUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField(backup.fileName, EditorStyles.boldLabel);
                EditorGUILayout.LabelField(L("Created", "作成日時"), backup.timestampLocal);
                EditorGUILayout.LabelField(L("Size", "サイズ"), FormatSize(backup.sizeBytes));
            }
        }
    }

    private void DrawDoctorStatusSummary()
    {
        if (_doctorRunning)
        {
            EditorGUILayout.HelpBox(L("Doctor is running...", "Doctor を実行中です..."), MessageType.Info);
            return;
        }

        if (_doctorReport == null)
        {
            EditorGUILayout.HelpBox(
                L("Doctor has not run yet. Use it to verify the shared file before inviting the team.",
                  "Doctor はまだ未実行です。共有ファイルをチームで使い始める前の確認に使ってください。"),
                MessageType.Info);
            return;
        }

        var issueCount = _doctorReport.issues?.Count ?? 0;
        var detail = _doctorReport.success
            ? L("Shared JSON is readable and the latest snapshot was broadcast successfully.",
                "共有 JSON の読み書きに成功し、最新スナップショットも正常に配信されました。")
            : issueCount > 0
                ? LF("{0} actionable fix(es) are listed below.", "下に解決方法が {0} 件表示されています。", issueCount)
                : L("Doctor failed without a classified issue. Copy the result and inspect the log.",
                    "分類済みの問題が無いまま Doctor が失敗しました。結果をコピーしてログを確認してください。");

        EditorGUILayout.HelpBox($"{_doctorReport.summary}\n{detail}", DoctorMessageType());
    }

    private void DrawMemoAlertBar()
    {
        if (!NotifyOnNewMemo)
            return;

        var alerts = GetPendingMemoAlerts();
        if (alerts.Count == 0)
            return;

        var latest = alerts[0];
        var extraCount = Mathf.Max(0, alerts.Count - 1);
        var title = extraCount > 0
            ? LF("New memo from {0} (+{1})", "{0} から新着メモ（他 {1} 件）", DisplayUser(latest.authorId, latest.author), extraCount)
            : LF("New memo from {0}", "{0} から新着メモ", DisplayUser(latest.authorId, latest.author));

        using (new GUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            if (!string.IsNullOrEmpty(latest.text))
                GUILayout.Label(RenderMemoMarkdown(latest.text), _memoPreviewStyle);

            DrawActionButtons(
                new ActionButtonInfo
                {
                    label = L("Open Memos", "メモを開く"),
                    onClick = () =>
                    {
                        _tab = 2;
                        _memoFilterUnread = true;
                        Repaint();
                    }
                },
                new ActionButtonInfo
                {
                    label = L("Mark as read", "既読にする"),
                    enabled = _backend != null && !string.IsNullOrEmpty(latest.id),
                    onClick = () => MarkMemoAsReadAsync(latest)
                },
                new ActionButtonInfo
                {
                    label = L("Dismiss", "閉じる"),
                    onClick = () => DismissMemoAlert(latest.id)
                });
        }
    }

    private async void StartDoctorRun()
    {
        _doctorRunning = true;
        Repaint();

        try
        {
            _doctorReport = await CollabSyncDoctor.RunAsync();
        }
        finally
        {
            _doctorRunning = false;
            BuildBackendSafe();
            Repaint();
        }
    }

    private async Task CreateMemoAsync(string text, string assetPath)
    {
        if (_backend == null) return;

        var me = CurrentUserName;
        var memo = new MemoItem
        {
            id = Guid.NewGuid().ToString("N"),
            authorId = CurrentUserId,
            text = (text ?? "").Trim(),
            author = me,
            assetPath = string.IsNullOrWhiteSpace(assetPath) ? "" : assetPath,
            createdAt = TimeUtil.NowMs(),
            pinned = false,
            readByUsers = new List<string> { me },
            readByUserIds = new List<string> { CurrentUserId }
        };

        _newMemo = "";
        await _backend.UpsertMemoAsync(memo);
    }

    private async void MarkMemoAsReadAsync(MemoItem memo)
    {
        if (_backend == null || memo == null || string.IsNullOrEmpty(memo.id))
            return;

        DismissMemoAlert(memo.id);
        await _backend.MarkMemoReadAsync(memo.id, CurrentUserId, CurrentUserName);
        RefreshSnapshotAsync();
    }

    private async void AddAdminAsync(string adminUserId, string adminUserName)
    {
        if (_backend == null)
            return;

        var requesterId = CurrentUserId;
        var requesterName = CurrentUserName;
        adminUserId = (adminUserId ?? "").Trim();
        adminUserName = (adminUserName ?? "").Trim();
        if (string.IsNullOrEmpty(requesterId) || string.IsNullOrEmpty(adminUserId))
            return;

        var added = await _backend.AddAdminAsync(requesterId, requesterName, adminUserId, adminUserName);
        if (added)
        {
            _adminUserIdInput = "";
            RefreshSnapshotAsync();
            ShowNotification(new GUIContent(LF("{0} added as admin.", "{0} を管理者に追加しました。", string.IsNullOrEmpty(adminUserName) ? adminUserId : adminUserName)));
        }
        else
        {
            ShowNotification(new GUIContent(L("Admin update was not applied.", "管理者の更新は適用されませんでした。")));
        }
    }

    private async void RemoveAdminAsync(string adminUserId, string adminUserName)
    {
        if (_backend == null)
            return;

        var requesterId = CurrentUserId;
        var requesterName = CurrentUserName;
        adminUserId = (adminUserId ?? "").Trim();
        adminUserName = (adminUserName ?? "").Trim();
        if (string.IsNullOrEmpty(requesterId) || string.IsNullOrEmpty(adminUserId))
            return;

        var label = string.IsNullOrEmpty(adminUserName) ? adminUserId : adminUserName;
        if (!EditorUtility.DisplayDialog(
                "CollabSync",
                LF("Remove admin access from {0}?", "{0} の管理者権限を削除しますか？", label),
                L("Remove", "削除"),
                L("Cancel", "キャンセル")))
        {
            return;
        }

        var removed = await _backend.RemoveAdminAsync(requesterId, requesterName, adminUserId);
        if (removed)
        {
            RefreshSnapshotAsync();
            ShowNotification(new GUIContent(LF("{0} removed from admins.", "{0} を管理者から削除しました。", label)));
        }
        else
        {
            ShowNotification(new GUIContent(L("Admin removal was not applied.", "管理者削除は適用されませんでした。")));
        }
    }

    private async void DeleteUserAsync(string userId, string userName)
    {
        if (_backend == null || !IsCurrentUserRootAdmin())
            return;

        userId = (userId ?? "").Trim();
        userName = (userName ?? "").Trim();
        if (string.IsNullOrEmpty(userId) || IsCurrentUser(userId, userName))
            return;

        var label = string.IsNullOrEmpty(userName) ? userId : userName;
        if (!EditorUtility.DisplayDialog(
                "CollabSync",
                LF("Delete user {0}?\n\nThis removes their current presence and locks, revokes admin access, and blocks this User ID from writing again.",
                   "ユーザー {0} を削除しますか？\n\n現在のプレゼンスとロックを削除し、管理者権限も外し、このユーザーIDからの再書き込みをブロックします。",
                   label),
                L("Delete User", "ユーザーを削除"),
                L("Cancel", "キャンセル")))
        {
            return;
        }

        var deleted = await _backend.DeleteUserAsync(CurrentUserId, CurrentUserName, userId, userName);
        if (deleted)
        {
            RefreshSnapshotAsync();
            ShowNotification(new GUIContent(LF("{0} was deleted.", "{0} を削除しました。", label)));
        }
        else
        {
            ShowNotification(new GUIContent(L("User deletion was not applied.", "ユーザー削除は適用されませんでした。")));
        }
    }

    private async void RestoreUserAsync(string userId, string userName)
    {
        if (_backend == null || !IsCurrentUserRootAdmin())
            return;

        userId = (userId ?? "").Trim();
        userName = (userName ?? "").Trim();
        if (string.IsNullOrEmpty(userId))
            return;

        var label = string.IsNullOrEmpty(userName) ? userId : userName;
        if (!EditorUtility.DisplayDialog(
                "CollabSync",
                LF("Restore user {0}?\n\nThis removes the deleted-user block and allows the same User ID to write again.",
                   "ユーザー {0} を復活しますか？\n\n削除済みブロックを解除し、同じ User ID で再び書き込めるようにします。",
                   label),
                L("Restore User", "ユーザーを復活"),
                L("Cancel", "キャンセル")))
        {
            return;
        }

        var restored = await _backend.RestoreUserAsync(CurrentUserId, CurrentUserName, userId, userName);
        if (restored)
        {
            RefreshSnapshotAsync();
            ShowNotification(new GUIContent(LF("{0} was restored.", "{0} を復活しました。", label)));
        }
        else
        {
            ShowNotification(new GUIContent(L("User restore was not applied.", "ユーザー復活は適用されませんでした。")));
        }
    }

    private async void DeleteMemoAsync(MemoItem memo, bool forceDelete)
    {
        if (_backend == null || memo == null || string.IsNullOrEmpty(memo.id))
            return;

        var requesterId = CurrentUserId;
        var requesterName = CurrentUserName;
        if (string.IsNullOrEmpty(requesterId))
            return;

        var title = forceDelete ? L("Force delete memo?", "メモを強制削除しますか？") : L("Delete memo?", "メモを削除しますか？");
        var body = forceDelete
            ? LF("Delete {0}'s memo?\n\n{1}", "{0} のメモを強制削除しますか？\n\n{1}", memo.author ?? L("(unknown)", "(不明)"), memo.text ?? "")
            : (memo.text ?? "");

        if (!EditorUtility.DisplayDialog(
                title,
                body,
                forceDelete ? L("Force Delete", "強制削除") : L("Delete", "削除"),
                L("Cancel", "キャンセル")))
        {
            return;
        }

        bool success = forceDelete
            ? await _backend.ForceDeleteMemoAsync(memo.id, requesterId, requesterName)
            : await _backend.DeleteMemoAsync(memo.id, requesterId, requesterName);

        if (success)
        {
            RefreshSnapshotAsync();
            ShowNotification(new GUIContent(forceDelete ? L("Memo force deleted.", "メモを強制削除しました。") : L("Memo deleted.", "メモを削除しました。")));
        }
        else
        {
            ShowNotification(new GUIContent(forceDelete ? L("Force delete failed.", "強制削除に失敗しました。") : L("Delete failed.", "削除に失敗しました。")));
        }
    }

    private async void SetWorkHistoryEnabledAsync(bool enabled)
    {
        if (_backend == null || !IsCurrentUserAdmin())
            return;

        var requesterId = CurrentUserId;
        var requesterName = CurrentUserName;
        if (string.IsNullOrEmpty(requesterId))
            return;

        var success = await _backend.SetWorkHistoryEnabledAsync(requesterId, requesterName, enabled);
        if (success)
        {
            RefreshSnapshotAsync();
            ShowNotification(new GUIContent(enabled
                ? L("Global work history enabled.", "全体の作業履歴を有効化しました。")
                : L("Global work history disabled.", "全体の作業履歴を無効化しました。")));
        }
        else
        {
            ShowNotification(new GUIContent(L("Work history setting was not changed.", "作業履歴の設定は変更されませんでした。")));
        }
    }

    private async void RequestUnlockAsync(LockItem lockItem)
    {
        if (_backend == null || lockItem == null)
            return;

        var requesterId = CurrentUserId;
        var requesterName = CurrentUserName;
        if ((string.IsNullOrEmpty(requesterId) && string.IsNullOrEmpty(requesterName)) || IsCurrentUser(lockItem))
            return;

        var now = TimeUtil.NowMs();
        var duplicate = (_doc?.memos ?? new List<MemoItem>()).Any(m =>
            m != null &&
            IsCurrentUser(m) &&
            string.Equals(m.assetPath, lockItem.assetPath, StringComparison.Ordinal) &&
            now - m.createdAt < 5 * 60 * 1000 &&
            (m.text ?? "").IndexOf(DisplayUser(lockItem.ownerId, lockItem.owner) ?? "", StringComparison.OrdinalIgnoreCase) >= 0 &&
            (((m.text ?? "").StartsWith("[Unlock Request]", StringComparison.Ordinal)) ||
             ((m.text ?? "").StartsWith("【解除依頼】", StringComparison.Ordinal))));

        if (duplicate)
        {
            ShowNotification(new GUIContent(L("An unlock request already exists.", "解除依頼はすでにあります。")));
            return;
        }

        var memo = new MemoItem
        {
            id = Guid.NewGuid().ToString("N"),
            text = LF("[Unlock Request] Please release {0}. Requested to {1}.",
                "【解除依頼】{0} の解除をお願いします。依頼先: {1}",
                FormatLockTargetLabel(lockItem.assetPath),
                DisplayUser(lockItem.ownerId, lockItem.owner) ?? L("(unknown)", "(不明)")),
            authorId = requesterId,
            author = requesterName,
            assetPath = lockItem.assetPath ?? "",
            createdAt = now,
            pinned = false,
            readByUsers = new List<string> { requesterName },
            readByUserIds = new List<string> { requesterId }
        };

        await _backend.UpsertMemoAsync(memo);
        ShowNotification(new GUIContent(L("Unlock request sent.", "解除依頼を送信しました。")));
    }

    private async void LockSelectionTargetAsync(SelectionTargetInfo selection)
    {
        if (_backend == null || string.IsNullOrEmpty(selection.preferredLockKey))
            return;

        await _backend.TryAcquireLockAsync(selection.preferredLockKey, CurrentUserId, CurrentUserName, "window-selection", 0, selection.assetPath);
        RefreshSnapshotAsync();
    }

    private async Task<LockReleaseOutcome> ReleaseOwnedLockAsync(string assetPath)
    {
        if (_backend == null || string.IsNullOrEmpty(assetPath))
            return LockReleaseOutcome.None;

        EditingTracker.SuppressAutoLockForKey(assetPath);
        var success = await _backend.ReleaseLockAsync(assetPath, CurrentUserId, CurrentUserName);
        var doc = await _backend.LoadOnceAsync() ?? new CollabStateDocument();
        var mine = (doc.locks ?? new List<LockItem>())
            .FirstOrDefault(l => l != null
                                 && string.Equals(l.assetPath, assetPath, StringComparison.Ordinal)
                                 && IsCurrentUser(l));

        if (mine != null && CollabSyncGitUtility.IsRetainedLock(mine))
            return LockReleaseOutcome.Retained;

        return success ? LockReleaseOutcome.Released : LockReleaseOutcome.None;
    }

    private void NotifyLockReleaseResult(int releasedCount, int retainedCount)
    {
        if (retainedCount > 0)
        {
            ShowNotification(new GUIContent(LF(
                releasedCount > 0
                    ? "{0} released / {1} retained until the protected branch picks up the commit."
                    : "{0} lock(s) were retained until the protected branch picks up the commit.",
                releasedCount > 0
                    ? "{0} 件解除 / {1} 件は保護ブランチにコミットが入るまで保持されました。"
                    : "{0} 件のロックは保護ブランチにコミットが入るまで保持されました。",
                releasedCount > 0 ? releasedCount : retainedCount,
                retainedCount)));
            return;
        }

        if (releasedCount > 0)
        {
            ShowNotification(new GUIContent(LF(
                "{0} lock(s) released.",
                "{0} 件のロックを解除しました。",
                releasedCount)));
            return;
        }

        ShowNotification(new GUIContent(L(
            "No lock was released.",
            "解除できるロックはありませんでした。")));
    }

    private async void UnlockSelectionLocksAsync(List<LockItem> locks)
    {
        if (_backend == null || locks == null || locks.Count == 0)
            return;

        int releasedCount = 0;
        int retainedCount = 0;
        foreach (var assetPath in locks.Select(l => l?.assetPath).Where(x => !string.IsNullOrEmpty(x)).Distinct())
        {
            var outcome = await ReleaseOwnedLockAsync(assetPath);
            if (outcome == LockReleaseOutcome.Released)
                releasedCount++;
            else if (outcome == LockReleaseOutcome.Retained)
                retainedCount++;
        }

        RefreshSnapshotAsync();
        NotifyLockReleaseResult(releasedCount, retainedCount);
    }

    private async void ForceUnlockAsync(LockItem lockItem)
    {
        if (_backend == null || lockItem == null || !IsCurrentUserAdmin())
            return;

        if (!EditorUtility.DisplayDialog(
                "CollabSync",
                LF("Force unlock {0} owned by {1}?", "{1} が所有する {0} を強制解除しますか？",
                    FormatLockTargetLabel(lockItem.assetPath),
                    DisplayUser(lockItem.ownerId, lockItem.owner) ?? L("(unknown)", "(不明)")),
                L("Force Unlock", "強制解除"),
                L("Cancel", "キャンセル")))
        {
            return;
        }

        if (await _backend.ForceReleaseLockAsync(lockItem.assetPath, CurrentUserId, CurrentUserName))
        {
            RefreshSnapshotAsync();
            ShowNotification(new GUIContent(L("Lock force released.", "ロックを強制解除しました。")));
        }
        else
        {
            ShowNotification(new GUIContent(L("Force unlock failed.", "強制解除に失敗しました。")));
        }
    }

    private async void UnlockAllMineAsync(List<LockItem> activeLocks)
    {
        if (_backend == null)
            return;

        var mine = activeLocks
            .Where(IsCurrentUser)
            .Select(l => l.assetPath)
            .Distinct()
            .ToArray();

        int releasedCount = 0;
        int retainedCount = 0;
        foreach (var assetPath in mine)
        {
            var outcome = await ReleaseOwnedLockAsync(assetPath);
            if (outcome == LockReleaseOutcome.Released)
                releasedCount++;
            else if (outcome == LockReleaseOutcome.Retained)
                retainedCount++;
        }

        RefreshSnapshotAsync();
        NotifyLockReleaseResult(releasedCount, retainedCount);
    }

    private ActionButtonInfo[] CreateSelectionActionButtons(SelectionTargetInfo selection, List<LockItem> mineLocks)
    {
        mineLocks ??= new List<LockItem>();

        return new[]
        {
            new ActionButtonInfo
            {
                label = L("Lock Target", "対象をロック"),
                enabled = _backend != null && !string.IsNullOrEmpty(selection?.preferredLockKey),
                onClick = () => LockSelectionTargetAsync(selection)
            },
            new ActionButtonInfo
            {
                label = L("Unlock Mine", "自分のロック解除"),
                enabled = _backend != null && mineLocks.Count > 0,
                onClick = () => UnlockSelectionLocksAsync(mineLocks)
            },
            new ActionButtonInfo
            {
                label = L("Ping Asset", "アセットを表示"),
                enabled = !string.IsNullOrEmpty(selection?.assetPath) && IsProjectRelativePath(selection.assetPath),
                onClick = () => PingAssetPath(selection.assetPath)
            },
            new ActionButtonInfo
            {
                label = L("Open Related Memos", "関連メモを開く"),
                enabled = selection != null && selection.HasTarget,
                onClick = () =>
                {
                    _tab = 2;
                    _memoFilterSelection = true;
                    Repaint();
                }
            }
        };
    }

    private void DrawActionButtons(params ActionButtonInfo[] buttons)
    {
        if (buttons == null || buttons.Length == 0)
            return;

        var compact = IsCompactLayout(620f);
        if (compact)
        {
            foreach (var button in buttons)
            {
                using (new EditorGUI.DisabledScope(button == null || !button.enabled))
                {
                    var content = new GUIContent(button?.label ?? "", button?.tooltip ?? "");
                    if (GUILayout.Button(content, GUILayout.ExpandWidth(true)))
                        button?.onClick?.Invoke();
                }
            }

            return;
        }

        using (new GUILayout.HorizontalScope())
        {
            foreach (var button in buttons)
            {
                using (new EditorGUI.DisabledScope(button == null || !button.enabled))
                {
                    var content = new GUIContent(button?.label ?? "", button?.tooltip ?? "");
                    if (GUILayout.Button(content, GUILayout.MinWidth(96), GUILayout.ExpandWidth(true)))
                        button?.onClick?.Invoke();
                }
            }
        }
    }

    private void DrawAdminSection()
    {
        EditorGUILayout.LabelField(L("Administrators", "管理者"), EditorStyles.boldLabel);

        var admins = GetAdminUsers();
        var rootAdminId = GetRootAdminUserId();
        var rootAdminName = GetRootAdminUserName();
        if (admins.Count == 0)
        {
            EditorGUILayout.HelpBox(
                L("The first user who writes to the shared JSON becomes the initial root admin automatically.",
                  "共有 JSON に最初に書き込んだユーザーが、自動的に最初の Root管理者になります。"),
                MessageType.Info);
        }
        else
        {
            foreach (var admin in admins)
            {
                using (new GUILayout.VerticalScope("box"))
                {
                    var roleLabel = CollabIdentityUtility.Matches(admin.userId, admin.displayName, rootAdminId, rootAdminName)
                        ? L("Root Admin", "Root管理者")
                        : L("Admin", "管理者");
                    var meLabel = IsCurrentUser(admin.userId, admin.displayName)
                        ? LF("{0} ({1})", "{0}（{1}）", admin.displayName, L("You", "自分"))
                        : admin.displayName;
                    var label = LF("{0} [{1}]", "{0} [{1}]", meLabel, roleLabel);
                    EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
                    if (!string.IsNullOrEmpty(admin.userId))
                        EditorGUILayout.SelectableLabel(admin.userId, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight + 4));

                    if (IsCurrentUserRootAdmin() &&
                        !CollabIdentityUtility.Matches(admin.userId, admin.displayName, rootAdminId, rootAdminName) &&
                        _backend != null)
                    {
                        if (GUILayout.Button(L("Remove Admin", "管理者から削除"), GUILayout.Width(IsCompactLayout(620f) ? 140f : 160f)))
                            RemoveAdminAsync(admin.userId, admin.displayName);
                    }
                }
            }
        }

        if (!IsCurrentUserAdmin())
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(L("Global Work History", "全体の作業履歴"), EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(
                IsWorkHistoryEnabled()
                    ? L("Enabled. Admins can change this shared setting.", "有効です。この共有設定は管理者のみ変更できます。")
                    : L("Disabled by admin. New work history will not be recorded.", "管理者により無効です。新しい作業履歴は記録されません。"),
                MessageType.None);

            if (admins.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    L("Only the root admin can add or remove admins from Settings.",
                      "Settings から管理者を追加・削除できるのは Root管理者のみです。"),
                    MessageType.None);
            }
            return;
        }

        EditorGUILayout.Space(4);
        DrawWorkHistorySettingSection();

        if (!IsCurrentUserRootAdmin())
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                L("Only the root admin can add or remove admins.",
                  "管理者の追加・削除ができるのは Root管理者のみです。"),
                MessageType.None);
            return;
        }

        EditorGUILayout.Space(6);
        _adminUserIdInput = EditorGUILayout.TextField(L("Add Admin User ID", "追加する管理者のユーザーID"), _adminUserIdInput);
        var trimmedId = (_adminUserIdInput ?? "").Trim();
        var canAddTyped = _backend != null &&
                          !string.IsNullOrEmpty(trimmedId) &&
                          !admins.Any(admin => string.Equals(admin.userId, trimmedId, StringComparison.Ordinal));

        DrawActionButtons(
            new ActionButtonInfo
            {
                label = L("Add Admin", "管理者に追加"),
                enabled = canAddTyped,
                onClick = () => AddAdminAsync(trimmedId, "")
            });

        var knownCandidates = GetKnownUsers(GetAlivePresences(), GetActiveLocks())
            .Where(user => !string.IsNullOrWhiteSpace(user.userId) &&
                           !admins.Any(admin => string.Equals(admin.userId, user.userId, StringComparison.Ordinal)))
            .Take(6)
            .ToList();
        if (knownCandidates.Count > 0)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(L("Known Users", "既知のユーザー"), EditorStyles.miniBoldLabel);
            foreach (var user in knownCandidates)
            {
                using (new GUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.LabelField(user.displayName, EditorStyles.boldLabel);
                    EditorGUILayout.SelectableLabel(user.userId, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight + 4));
                    using (new EditorGUI.DisabledScope(_backend == null))
                    {
                        if (GUILayout.Button(L("Add", "追加"), GUILayout.Width(IsCompactLayout(620f) ? 72f : 88f)))
                            AddAdminAsync(user.userId, user.displayName);
                    }
                }
            }
        }
    }

    private void DrawWorkHistorySettingSection()
    {
        EditorGUILayout.LabelField(L("Global Work History", "全体の作業履歴"), EditorStyles.miniBoldLabel);

        var currentValue = IsWorkHistoryEnabled();
        var nextValue = EditorGUILayout.ToggleLeft(
            L("Record work history for everyone", "全体の作業履歴を記録する"),
            currentValue);

        EditorGUILayout.HelpBox(
            L("When off, new editing, lock, memo, and admin history entries stop being added for the shared workspace.",
              "オフにすると、この共有ワークスペースでは編集、ロック、メモ、管理設定の新しい履歴が追加されなくなります。"),
            MessageType.None);

        if (nextValue != currentValue)
            SetWorkHistoryEnabledAsync(nextValue);
    }

    private void DrawLanguageSection()
    {
        EditorGUILayout.LabelField(L("Language", "言語"), EditorStyles.boldLabel);

        if (CollabSyncLocalization.IsJapaneseEditorWarningActive)
        {
            EditorGUILayout.HelpBox(
                CollabSyncLocalization.JapaneseEditorWorkaroundMessage,
                MessageType.Warning);
        }

        var currentMode = _cfg != null ? (int)_cfg.languageMode : 0;
        var options = new[]
        {
            L("System Default", "システム設定"),
            L("English", "英語"),
            L("Japanese", "日本語")
        };

        var nextMode = EditorGUILayout.Popup(L("UI Language", "UI 言語"), currentMode, options);
        if (_cfg != null && nextMode != currentMode)
        {
            _cfg.languageMode = (CollabSyncLanguageMode)Mathf.Clamp(nextMode, 0, 2);
            CollabSyncConfig.SaveEditorAsset(_cfg);
            CollabSyncLocalization.InvalidateCaches();
            Repaint();
        }

        EditorGUILayout.HelpBox(
            CollabSyncLocalization.GetLanguageStatusSummary(),
            MessageType.None);

        EditorGUILayout.HelpBox(
            L("System Default follows the OS language settings, without using Unity's Application.systemLanguage.",
              "システム設定は Unity の Application.systemLanguage を使わず、OS の言語設定を優先して判定します。"),
            MessageType.None);
    }

    private string BuildOverviewSummaryText(SelectionTargetInfo selection, int unreadCount, List<LockItem> otherLocks, List<EditingPresence> editors)
    {
        if (_backend == null)
        {
            return L(
                "Shared JSON is not connected. Open Settings and confirm the shared file path.",
                "共有 JSON に接続できていません。Settings で共有ファイルのパスを確認してください。");
        }

        if (otherLocks != null && otherLocks.Count > 0)
        {
            return LF(
                "{0} teammate lock(s) affect the current selection.",
                "現在の選択中ターゲットに他ユーザーのロックが {0} 件あります。",
                otherLocks.Count);
        }

        if (editors != null && editors.Count > 0)
        {
            return LF(
                "{0} teammate(s) are editing the current selection.",
                "現在の選択中ターゲットを {0} 人が編集中です。",
                editors.Count);
        }

        if (!selection.HasTarget)
        {
            return unreadCount > 0
                ? LF("No selection. You have {0} unread memo(s).", "対象未選択です。未読メモが {0} 件あります。", unreadCount)
                : L("Everything looks calm. Select something when you need details.", "大きな問題はありません。必要な時に対象を選択してください。");
        }

        return unreadCount > 0
            ? LF("Current selection looks clear. You have {0} unread memo(s).", "現在の選択中ターゲットは安定しています。未読メモが {0} 件あります。", unreadCount)
            : L("Current selection looks clear.", "現在の選択中ターゲットは安定しています。");
    }

    private MessageType OverviewMessageType(List<LockItem> otherLocks, List<EditingPresence> editors)
    {
        if (_backend == null)
            return MessageType.Error;
        if (otherLocks != null && otherLocks.Count > 0)
            return MessageType.Error;
        if (editors != null && editors.Count > 0)
            return MessageType.Warning;
        return MessageType.Info;
    }

    private void DrawOverviewLine(string label, string value)
    {
        DrawOverviewLine(label, value, "");
    }

    private void DrawOverviewLine(string label, string value, string tooltip)
    {
        var labelContent = string.IsNullOrEmpty(tooltip) ? new GUIContent(label) : new GUIContent(label, tooltip);
        var valueContent = string.IsNullOrEmpty(tooltip) ? new GUIContent(value ?? "") : new GUIContent(value ?? "", tooltip);
        if (IsCompactLayout(540f))
        {
            EditorGUILayout.LabelField(labelContent, EditorStyles.miniBoldLabel);
            EditorGUILayout.LabelField(valueContent, _cardDetailStyle);
            return;
        }

        EditorGUILayout.LabelField(labelContent, valueContent);
    }

    private void DrawDetailLine(string label, string value, string tooltip)
    {
        var labelContent = string.IsNullOrEmpty(tooltip) ? new GUIContent(label) : new GUIContent(label, tooltip);
        var valueContent = string.IsNullOrEmpty(tooltip) ? new GUIContent(value ?? "") : new GUIContent(value ?? "", tooltip);
        EditorGUILayout.LabelField(labelContent, valueContent);
    }

    private void ChooseJsonPath()
    {
        var seedPath = CollabSyncBackendUtility.DefaultLocalJsonPath;
        if (_cfg.TryGetResolvedJsonPath(out var resolvedPath, out _))
            seedPath = resolvedPath;
        else if (!string.IsNullOrWhiteSpace(_cfg.localJsonPath))
            seedPath = _cfg.localJsonPath;

        try
        {
            if (!Path.IsPathRooted(seedPath))
                seedPath = Path.GetFullPath(seedPath);
        }
        catch
        {
            seedPath = Path.Combine(Directory.GetCurrentDirectory(), "CollabSyncState.json");
        }

        var directory = Directory.Exists(seedPath) ? seedPath : Path.GetDirectoryName(seedPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            directory = Directory.GetCurrentDirectory();

        var fileName = Path.GetFileName(seedPath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "CollabSyncState.json";

        var chosenPath = EditorUtility.OpenFilePanelWithFilters(
            L("Choose Shared JSON File", "共有 JSON ファイルを選択"),
            directory,
            new[]
            {
                L("JSON files", "JSON ファイル"),
                "json",
                L("All files", "すべてのファイル"),
                "*"
            });

        if (string.IsNullOrWhiteSpace(chosenPath))
            return;

        ApplyJsonPathSelection(chosenPath);
    }

    private void CreateJsonPath()
    {
        var seedPath = CollabSyncBackendUtility.DefaultLocalJsonPath;
        if (_cfg.TryGetResolvedJsonPath(out var resolvedPath, out _))
            seedPath = resolvedPath;
        else if (!string.IsNullOrWhiteSpace(_cfg.localJsonPath))
            seedPath = _cfg.localJsonPath;

        try
        {
            if (!Path.IsPathRooted(seedPath))
                seedPath = Path.GetFullPath(seedPath);
        }
        catch
        {
            seedPath = Path.Combine(Directory.GetCurrentDirectory(), "CollabSyncState.json");
        }

        var directory = Directory.Exists(seedPath) ? seedPath : Path.GetDirectoryName(seedPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            directory = Directory.GetCurrentDirectory();

        var fileName = Path.GetFileName(seedPath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "CollabSyncState.json";

        var chosenPath = EditorUtility.SaveFilePanel(
            L("Create Shared JSON File", "共有 JSON ファイルを作成"),
            directory,
            Path.GetFileNameWithoutExtension(fileName),
            "json");

        if (string.IsNullOrWhiteSpace(chosenPath))
            return;

        ApplyJsonPathSelection(chosenPath);
    }

    private void ApplyJsonPathSelection(string chosenPath)
    {
        if (_cfg == null)
            return;

        CollabSyncConfig.SetEditorLocalJsonPath(_cfg, chosenPath);
        BuildBackendSafe();
        Repaint();
    }

    private void ApplySharedStateProtectionSelection(bool enabled)
    {
        if (_cfg == null)
            return;
        if (!CanEditSharedStateProtectionSetting())
            return;

        _cfg.protectSharedStateFile = enabled;
        _cfg.sharedStateProtectionInitialized = true;
        CollabSyncConfig.SaveEditorAsset(_cfg);
        BuildBackendSafe();
        RefreshSnapshotAsync();
        Repaint();
        ShowNotification(new GUIContent(
            enabled
                ? L("Shared state file protection enabled.", "共有状態ファイルの暗号化を有効にしました。")
                : L("Shared state file protection disabled.", "共有状態ファイルの暗号化を無効にしました。")));
    }

    private bool CanEditSharedStateProtectionSetting()
    {
        return IsCurrentUserRootAdmin()
            || (string.IsNullOrEmpty(GetRootAdminUserId()) && string.IsNullOrEmpty(GetRootAdminUserName()));
    }

    private void NormalizeStoredJsonPathIfNeeded()
    {
        if (_cfg == null)
            return;

        var normalized = CollabSyncBackendUtility.NormalizeStoredPathInput(_cfg.localJsonPath);
        if (string.IsNullOrEmpty(normalized) || string.Equals(normalized, _cfg.localJsonPath, StringComparison.Ordinal))
            return;

        CollabSyncConfig.SetEditorLocalJsonPath(_cfg, normalized);
    }

    private void CopyDoctorResult()
    {
        if (_doctorReport == null)
            return;

        EditorGUIUtility.systemCopyBuffer = _doctorReport.ToClipboardText();
        ShowNotification(new GUIContent(L("Doctor result copied", "Doctor 結果をコピーしました")));
    }

    private SelectionTargetInfo GetCurrentSelectionInfo()
    {
        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        var activeGameObject = Selection.activeGameObject;
        if (activeGameObject != null)
        {
            if (stage != null && !string.IsNullOrEmpty(stage.assetPath) && activeGameObject.scene == stage.scene)
            {
                var objectKey = GetGameObjectLockKey(activeGameObject);
                return new SelectionTargetInfo
                {
                    displayName = activeGameObject.name,
                    assetPath = stage.assetPath,
                    context = L("Prefab Object", "Prefab オブジェクト"),
                    preferredLockKey = string.IsNullOrEmpty(objectKey) ? stage.assetPath : objectKey,
                    objectLockKey = objectKey
                };
            }

            if (activeGameObject.scene.IsValid() && !string.IsNullOrEmpty(activeGameObject.scene.path))
            {
                var objectKey = GetGameObjectLockKey(activeGameObject);
                return new SelectionTargetInfo
                {
                    displayName = activeGameObject.name,
                    assetPath = activeGameObject.scene.path,
                    context = L("Scene Object", "シーンオブジェクト"),
                    preferredLockKey = string.IsNullOrEmpty(objectKey) ? activeGameObject.scene.path : objectKey,
                    objectLockKey = objectKey
                };
            }
        }

        var activeObject = Selection.activeObject;
        if (activeObject != null)
        {
            var assetPath = AssetDatabase.GetAssetPath(activeObject);
            if (!string.IsNullOrEmpty(assetPath))
            {
                var isFolder = AssetDatabase.IsValidFolder(assetPath);
                return new SelectionTargetInfo
                {
                    displayName = activeObject.name,
                    assetPath = assetPath,
                    context = GetAssetContext(assetPath),
                    preferredLockKey = isFolder ? assetPath.TrimEnd('/') + "/" : assetPath,
                    isFolder = isFolder
                };
            }
        }

        if (stage != null && !string.IsNullOrEmpty(stage.assetPath))
        {
            return new SelectionTargetInfo
            {
                displayName = Path.GetFileNameWithoutExtension(stage.assetPath),
                assetPath = stage.assetPath,
                context = L("Prefab", "Prefab"),
                preferredLockKey = stage.assetPath
            };
        }

        var scene = EditorSceneManager.GetActiveScene();
        if (scene.IsValid() && !string.IsNullOrEmpty(scene.path))
        {
            return new SelectionTargetInfo
            {
                displayName = Path.GetFileNameWithoutExtension(scene.path),
                assetPath = scene.path,
                context = L("Scene", "シーン"),
                preferredLockKey = scene.path
            };
        }

        return new SelectionTargetInfo();
    }

    private CollabStateDocument NormalizeWindowDoc(CollabStateDocument doc)
    {
        doc ??= new CollabStateDocument();
        doc.memos ??= new List<MemoItem>();
        doc.locks ??= new List<LockItem>();
        doc.presences ??= new List<EditingPresence>();
        doc.history ??= new List<WorkHistoryItem>();
        doc.adminUserIds ??= new List<string>();
        doc.adminUsers ??= new List<string>();
        doc.blockedUserIds ??= new List<string>();
        doc.blockedUsers ??= new List<string>();
        doc.rootAdminUserId ??= "";
        doc.rootAdminUser ??= "";
        doc.workHistoryMode = string.Equals(doc.workHistoryMode, "disabled", StringComparison.Ordinal) ? "disabled" : "enabled";

        while (doc.adminUserIds.Count < doc.adminUsers.Count)
            doc.adminUserIds.Add("");
        while (doc.blockedUserIds.Count < doc.blockedUsers.Count)
            doc.blockedUserIds.Add("");

        if (string.IsNullOrWhiteSpace(doc.rootAdminUser) && doc.adminUsers.Count > 0)
            doc.rootAdminUser = doc.adminUsers[0] ?? "";
        if (string.IsNullOrWhiteSpace(doc.rootAdminUserId) && doc.adminUserIds.Count > 0)
            doc.rootAdminUserId = doc.adminUserIds[0] ?? "";

        foreach (var presence in doc.presences)
        {
            if (presence == null) continue;
            presence.userId ??= "";
            presence.user ??= "";
            presence.assetPath ??= "";
            presence.targetKey ??= "";
            presence.targetName ??= "";
            presence.context ??= "";
        }

        foreach (var memo in doc.memos)
        {
            if (memo == null) continue;
            memo.authorId ??= "";
            memo.author ??= "";
            memo.assetPath ??= "";
            memo.text ??= "";
            CollabIdentityUtility.EnsureReadBy(memo);
        }

        foreach (var lockItem in doc.locks)
        {
            if (lockItem == null) continue;
            lockItem.ownerId ??= "";
            lockItem.owner ??= "";
            lockItem.assetPath ??= "";
            lockItem.scopeAssetPath ??= "";
            lockItem.reason ??= "";
        }

        foreach (var item in doc.history)
        {
            if (item == null) continue;
            item.userId ??= "";
            item.user ??= "";
            item.assetPath ??= "";
            item.context ??= "";
            item.detail ??= "";
        }

        return doc;
    }

    private void InvalidateDerivedData()
    {
        _derivedDocUpdatedAt = long.MinValue;
        _derivedTimeBucket = long.MinValue;
        _derivedIdentityKey = "";
    }

    private void EnsureDerivedData()
    {
        var doc = _doc ?? new CollabStateDocument();
        var now = TimeUtil.NowMs();
        var timeBucket = now / DerivedDataRefreshWindowMs;
        var identityKey = CurrentUserId + "\n" + CurrentUserName;
        if (_derivedDocUpdatedAt == doc.updatedAt
            && _derivedTimeBucket == timeBucket
            && string.Equals(_derivedIdentityKey, identityKey, StringComparison.Ordinal))
        {
            return;
        }

        _cachedAlivePresences.Clear();
        _cachedActiveLocks.Clear();
        _cachedKnownUsers.Clear();
        _cachedAdminUsers.Clear();
        _cachedPresenceByUserKey.Clear();
        _cachedLocksByUserKey.Clear();
        _cachedAdminUserKeys.Clear();

        var adminNames = doc.adminUsers ?? new List<string>();
        var adminIds = doc.adminUserIds ?? new List<string>();
        for (int i = 0; i < adminNames.Count; i++)
        {
            var userId = i < adminIds.Count ? adminIds[i] ?? "" : "";
            var displayName = CollabIdentityUtility.DisplayName(userId, adminNames[i]);
            if (string.IsNullOrEmpty(userId) && string.IsNullOrEmpty(displayName))
                continue;

            _cachedAdminUsers.Add(new KnownUserInfo
            {
                userId = userId,
                displayName = displayName
            });
        }

        _cachedAdminUsers.Sort((a, b) => string.Compare(a.displayName, b.displayName, StringComparison.OrdinalIgnoreCase));
        foreach (var admin in _cachedAdminUsers)
            _cachedAdminUserKeys.Add(UserKey(admin.userId, admin.displayName));

        var latestPresences = new Dictionary<string, EditingPresence>(StringComparer.Ordinal);
        foreach (var presence in doc.presences ?? new List<EditingPresence>())
        {
            if (presence == null || now - presence.heartbeat >= PresenceAliveWindowMs)
                continue;

            var key = UserKey(presence.userId, presence.user);
            if (!latestPresences.TryGetValue(key, out var existing) || existing.heartbeat < presence.heartbeat)
                latestPresences[key] = presence;
        }

        if (EditingTracker.TryGetLastPublishedPresence(out var localPresence)
            && localPresence != null
            && now - localPresence.heartbeat < PresenceAliveWindowMs
            && IsCurrentUser(localPresence.userId, localPresence.user))
        {
            var key = UserKey(localPresence.userId, localPresence.user);
            if (!latestPresences.TryGetValue(key, out var existing) || existing.heartbeat < localPresence.heartbeat)
                latestPresences[key] = localPresence;
        }

        foreach (var presence in latestPresences.Values)
        {
            _cachedAlivePresences.Add(presence);
            _cachedPresenceByUserKey[UserKey(presence.userId, presence.user)] = presence;
        }

        _cachedAlivePresences.Sort((a, b) =>
        {
            var byAsset = string.Compare(a?.assetPath ?? "", b?.assetPath ?? "", StringComparison.Ordinal);
            if (byAsset != 0)
                return byAsset;
            return string.Compare(DisplayUser(a?.userId, a?.user) ?? "", DisplayUser(b?.userId, b?.user) ?? "", StringComparison.OrdinalIgnoreCase);
        });

        foreach (var lockItem in doc.locks ?? new List<LockItem>())
        {
            if (lockItem == null)
                continue;
            if (lockItem.ttlMs > 0 && now - lockItem.createdAt > lockItem.ttlMs)
                continue;

            _cachedActiveLocks.Add(lockItem);
            var key = UserKey(lockItem.ownerId, lockItem.owner);
            if (!_cachedLocksByUserKey.TryGetValue(key, out var list))
            {
                list = new List<LockItem>();
                _cachedLocksByUserKey[key] = list;
            }
            list.Add(lockItem);
        }

        _cachedActiveLocks.Sort((a, b) =>
        {
            var byAsset = string.Compare(a?.assetPath ?? "", b?.assetPath ?? "", StringComparison.Ordinal);
            if (byAsset != 0)
                return byAsset;
            return string.Compare(DisplayUser(a?.ownerId, a?.owner) ?? "", DisplayUser(b?.ownerId, b?.owner) ?? "", StringComparison.OrdinalIgnoreCase);
        });

        var knownUsers = new Dictionary<string, KnownUserInfo>(StringComparer.Ordinal);
        void AddKnownUser(string userId, string displayName, bool isOnline = false)
        {
            userId = CollabIdentityUtility.Normalize(userId);
            displayName = CollabIdentityUtility.DisplayName(userId, displayName);
            if (string.IsNullOrEmpty(userId) && string.IsNullOrEmpty(displayName))
                return;

            var key = string.IsNullOrEmpty(userId) ? "legacy:" + displayName : userId;
            if (!string.IsNullOrEmpty(userId) && !knownUsers.ContainsKey(key))
            {
                var legacyKey = "legacy:" + displayName;
                if (knownUsers.TryGetValue(legacyKey, out var legacyExisting) && string.IsNullOrEmpty(legacyExisting.userId))
                {
                    knownUsers.Remove(legacyKey);
                    legacyExisting.userId = userId;
                    knownUsers[key] = legacyExisting;
                }
            }

            if (!knownUsers.TryGetValue(key, out var existing))
            {
                knownUsers[key] = new KnownUserInfo
                {
                    userId = userId,
                    displayName = displayName,
                    isOnline = isOnline
                };
                return;
            }

            if (string.IsNullOrEmpty(existing.userId) && !string.IsNullOrEmpty(userId))
                existing.userId = userId;
            if (!string.IsNullOrEmpty(displayName))
                existing.displayName = displayName;
            existing.isOnline |= isOnline;
        }

        foreach (var presence in _cachedAlivePresences)
            AddKnownUser(presence?.userId, presence?.user, true);
        foreach (var lockItem in _cachedActiveLocks)
            AddKnownUser(lockItem?.ownerId, lockItem?.owner);
        foreach (var memo in doc.memos ?? new List<MemoItem>())
            AddKnownUser(memo?.authorId, memo?.author);
        foreach (var item in doc.history ?? new List<WorkHistoryItem>())
            AddKnownUser(item?.userId, item?.user);
        foreach (var admin in _cachedAdminUsers)
            AddKnownUser(admin.userId, admin.displayName);
        AddKnownUser(doc.rootAdminUserId, doc.rootAdminUser);

        foreach (var user in knownUsers.Values)
        {
            if (!IsBlockedUser(user.userId, user.displayName))
                _cachedKnownUsers.Add(user);
        }

        _cachedKnownUsers.Sort((a, b) =>
        {
            var byOnline = b.isOnline.CompareTo(a.isOnline);
            if (byOnline != 0)
                return byOnline;
            return string.Compare(a.displayName, b.displayName, StringComparison.OrdinalIgnoreCase);
        });

        _derivedDocUpdatedAt = doc.updatedAt;
        _derivedTimeBucket = timeBucket;
        _derivedIdentityKey = identityKey;
    }

    private string[] GetMemoReaderDisplayNames(MemoItem memo)
    {
        if (memo == null)
            return Array.Empty<string>();

        CollabIdentityUtility.EnsureReadBy(memo);
        var names = new List<string>();

        foreach (var userId in memo.readByUserIds ?? new List<string>())
        {
            var match = GetKnownUsers(GetAlivePresences(), GetActiveLocks())
                .FirstOrDefault(user => string.Equals(user.userId, userId, StringComparison.Ordinal));
            var label = match != null ? match.displayName : userId;
            if (!string.IsNullOrEmpty(label))
                names.Add(label);
        }

        foreach (var userName in memo.readByUsers ?? new List<string>())
        {
            var label = CollabIdentityUtility.Normalize(userName);
            if (!string.IsNullOrEmpty(label))
                names.Add(label);
        }

        return names
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private List<EditingPresence> GetAlivePresences()
    {
        EnsureDerivedData();
        return _cachedAlivePresences;
    }

    private List<LockItem> GetActiveLocks()
    {
        EnsureDerivedData();
        return _cachedActiveLocks;
    }

    private List<KnownUserInfo> GetKnownUsers(List<EditingPresence> alive, List<LockItem> activeLocks)
    {
        EnsureDerivedData();
        return _cachedKnownUsers;
    }

    private List<KnownUserInfo> GetDeletedUsers()
    {
        var blockedIds = _doc?.blockedUserIds ?? new List<string>();
        var blockedNames = _doc?.blockedUsers ?? new List<string>();
        var deletedUsers = new List<KnownUserInfo>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < Math.Max(blockedIds.Count, blockedNames.Count); i++)
        {
            var userId = i < blockedIds.Count ? blockedIds[i] ?? "" : "";
            var displayName = CollabIdentityUtility.DisplayName(userId, i < blockedNames.Count ? blockedNames[i] ?? "" : "");
            if (string.IsNullOrEmpty(userId) && string.IsNullOrEmpty(displayName))
                continue;

            var key = UserKey(userId, displayName);
            if (!seenKeys.Add(key))
                continue;

            deletedUsers.Add(new KnownUserInfo
            {
                userId = userId,
                displayName = displayName,
                isOnline = false
            });
        }

        deletedUsers.Sort((a, b) => string.Compare(a.displayName, b.displayName, StringComparison.OrdinalIgnoreCase));
        return deletedUsers;
    }

    private List<KnownUserInfo> GetAdminUsers()
    {
        EnsureDerivedData();
        return _cachedAdminUsers;
    }

    private string GetRootAdminUserId() => (_doc?.rootAdminUserId ?? "").Trim();

    private string GetRootAdminUserName() => CollabIdentityUtility.DisplayName(_doc?.rootAdminUserId ?? "", _doc?.rootAdminUser ?? "");

    private bool IsBlockedUser(string userId, string userName)
    {
        var ids = _doc?.blockedUserIds ?? new List<string>();
        var names = _doc?.blockedUsers ?? new List<string>();
        for (int i = 0; i < Math.Max(ids.Count, names.Count); i++)
        {
            var blockedId = i < ids.Count ? ids[i] ?? "" : "";
            var blockedName = i < names.Count ? names[i] ?? "" : "";
            if (CollabIdentityUtility.Matches(userId, userName, blockedId, blockedName))
                return true;
        }

        return false;
    }

    private bool IsCurrentUserAdmin()
    {
        return GetAdminUsers().Any(admin => IsCurrentUser(admin.userId, admin.displayName));
    }

    private bool IsCurrentUserRootAdmin()
    {
        return IsCurrentUser(GetRootAdminUserId(), GetRootAdminUserName());
    }

    private bool IsWorkHistoryEnabled()
    {
        return !string.Equals(_doc?.workHistoryMode, "disabled", StringComparison.Ordinal);
    }

    private string CurrentUserId => CollabSyncUser.UserId ?? "";
    private string CurrentUserName => CollabSyncUser.UserName ?? "";

    private bool IsCurrentUser(string userId, string userName)
    {
        return CollabIdentityUtility.Matches(CurrentUserId, CurrentUserName, userId, userName);
    }

    private bool IsCurrentUser(EditingPresence presence)
    {
        return presence != null && IsCurrentUser(presence.userId, presence.user);
    }

    private bool IsCurrentUser(LockItem lockItem)
    {
        return lockItem != null && IsCurrentUser(lockItem.ownerId, lockItem.owner);
    }

    private bool IsCurrentUser(MemoItem memo)
    {
        return memo != null && IsCurrentUser(memo.authorId, memo.author);
    }

    private string DisplayUser(string userId, string userName)
    {
        return CollabIdentityUtility.DisplayName(userId, userName);
    }

    private string UserKey(string userId, string userName)
    {
        userId = CollabIdentityUtility.Normalize(userId);
        return string.IsNullOrEmpty(userId)
            ? "legacy:" + DisplayUser(userId, userName)
            : userId;
    }

    private void DrawUserHistoryList(string userId, string userName)
    {
        var histories = (_doc?.history ?? new List<WorkHistoryItem>())
            .Where(h => h != null && CollabIdentityUtility.Matches(userId, userName, h.userId, h.user))
            .OrderByDescending(h => h.createdAt)
            .Take(8)
            .ToList();

        if (histories.Count == 0)
        {
            EditorGUILayout.HelpBox(L("No work history yet.", "まだ作業履歴はありません。"), MessageType.Info);
            return;
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField(L("Recent Work", "最近の作業"), EditorStyles.miniBoldLabel);
        foreach (var item in histories)
        {
            using (new GUILayout.VerticalScope("box"))
            {
                var targetText = FormatHistoryTarget(item);
                EditorGUILayout.LabelField($"{UnixToLocal(item.createdAt)}  {FormatHistoryAction(item)}", EditorStyles.miniBoldLabel);
                if (!string.IsNullOrEmpty(targetText))
                    EditorGUILayout.LabelField(targetText, _cardDetailStyle);
            }
        }
    }

    private List<EditingPresence> GetEditorsForSelection(SelectionTargetInfo selection, List<EditingPresence> alive)
    {
        if (!selection.HasTarget || string.IsNullOrEmpty(selection.assetPath))
            return new List<EditingPresence>();

        if (!string.IsNullOrEmpty(selection.objectLockKey))
        {
            var objectEditors = alive
                .Where(p => string.Equals(p.targetKey ?? "", selection.objectLockKey, StringComparison.Ordinal))
                .ToList();
            if (objectEditors.Count > 0)
                return objectEditors;
        }

        if (selection.isFolder)
        {
            var prefix = selection.assetPath.TrimEnd('/') + "/";
            return alive.Where(p => !string.IsNullOrEmpty(p.assetPath) && p.assetPath.StartsWith(prefix, StringComparison.Ordinal))
                       .ToList();
        }

        var preferredKey = string.IsNullOrEmpty(selection.preferredLockKey)
            ? selection.assetPath
            : selection.preferredLockKey;
        var exactEditors = alive
            .Where(p => string.Equals(p.targetKey ?? "", preferredKey, StringComparison.Ordinal))
            .ToList();
        if (exactEditors.Count > 0)
            return exactEditors;

        return alive.Where(p =>
                string.Equals(p.assetPath, selection.assetPath, StringComparison.Ordinal) &&
                (string.IsNullOrEmpty(p.targetKey) || string.Equals(p.targetKey, p.assetPath, StringComparison.Ordinal)))
            .ToList();
    }

    private SelectionTargetInfo GetOverviewWorkTarget(List<EditingPresence> alive)
    {
        var selection = GetCurrentSelectionInfo();
        if (selection != null && selection.HasTarget)
            return selection;

        var myPresence = (alive ?? new List<EditingPresence>())
            .FirstOrDefault(IsCurrentUser);
        if (myPresence == null || string.IsNullOrEmpty(myPresence.assetPath))
            return new SelectionTargetInfo();

        return new SelectionTargetInfo
        {
            displayName = !string.IsNullOrEmpty(myPresence.targetName)
                ? myPresence.targetName
                : Path.GetFileNameWithoutExtension(myPresence.assetPath),
            assetPath = myPresence.assetPath,
            context = myPresence.context ?? "",
            preferredLockKey = string.IsNullOrEmpty(myPresence.targetKey) ? myPresence.assetPath : myPresence.targetKey,
            objectLockKey = !string.IsNullOrEmpty(myPresence.targetKey) && myPresence.targetKey.StartsWith("obj:", StringComparison.Ordinal)
                ? myPresence.targetKey
                : "",
            isFolder = myPresence.assetPath.EndsWith("/", StringComparison.Ordinal)
        };
    }

    private List<LockItem> GetLocksForSelection(SelectionTargetInfo selection, List<LockItem> activeLocks)
    {
        if (!selection.HasTarget)
            return new List<LockItem>();

        return activeLocks.Where(l => DoesLockAffectSelection(l, selection)).ToList();
    }

    private bool DoesLockAffectSelection(LockItem lockItem, SelectionTargetInfo selection)
    {
        if (lockItem == null || selection == null || !selection.HasTarget)
            return false;

        if (!string.IsNullOrEmpty(selection.objectLockKey) &&
            string.Equals(lockItem.assetPath, selection.objectLockKey, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrEmpty(selection.assetPath) || string.IsNullOrEmpty(lockItem.assetPath))
            return false;

        if (lockItem.assetPath.StartsWith("obj:", StringComparison.Ordinal))
        {
            var scopePath = CollabSyncEditorLockUtility.GetLockScopeAssetPath(lockItem);
            if (selection.isFolder)
            {
                var prefix = selection.assetPath.TrimEnd('/') + "/";
                return string.Equals(scopePath, selection.assetPath, StringComparison.Ordinal)
                    || scopePath.StartsWith(prefix, StringComparison.Ordinal);
            }

            return string.Equals(scopePath, selection.assetPath, StringComparison.Ordinal);
        }

        if (selection.isFolder)
        {
            var folderKey = selection.assetPath.TrimEnd('/') + "/";
            if (lockItem.assetPath.EndsWith("/", StringComparison.Ordinal))
                return folderKey.StartsWith(lockItem.assetPath, StringComparison.Ordinal);
            return false;
        }

        if (lockItem.assetPath.EndsWith("/", StringComparison.Ordinal))
            return selection.assetPath.StartsWith(lockItem.assetPath, StringComparison.Ordinal);

        return string.Equals(lockItem.assetPath, selection.assetPath, StringComparison.Ordinal);
    }

    private bool DoesMemoMatchSelection(MemoItem memo, SelectionTargetInfo selection)
    {
        if (memo == null || selection == null || string.IsNullOrEmpty(selection.assetPath) || string.IsNullOrEmpty(memo.assetPath))
            return false;

        if (selection.isFolder)
        {
            var prefix = selection.assetPath.TrimEnd('/') + "/";
            return string.Equals(memo.assetPath, selection.assetPath, StringComparison.Ordinal)
                || memo.assetPath.StartsWith(prefix, StringComparison.Ordinal);
        }

        return string.Equals(memo.assetPath, selection.assetPath, StringComparison.Ordinal);
    }

    private bool MatchesMemoFilters(MemoItem memo, SelectionTargetInfo selection)
    {
        if (memo == null)
            return false;

        if (_memoFilterUnread && !MemoIsUnread(memo) && !memo.pinned)
            return false;
        if (_memoFilterPinned && !memo.pinned)
            return false;
        if (_memoFilterSelection && !DoesMemoMatchSelection(memo, selection))
            return false;

        if (string.IsNullOrWhiteSpace(_memoSearch))
            return true;

        var query = _memoSearch.Trim();
        return (memo.text ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || DisplayUser(memo.authorId, memo.author).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || (memo.assetPath ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool MemoIsUnread(MemoItem memo)
    {
        if (memo == null)
            return false;
        if (IsCurrentUser(memo))
            return false;
        return !CollabIdentityUtility.HasRead(memo, CurrentUserId, CurrentUserName);
    }

    private bool LockMatchesFilter(LockItem lockItem)
    {
        if (lockItem == null)
            return false;

        if (_lockFilter == 1 && !IsCurrentUser(lockItem))
            return false;
        if (_lockFilter == 2 && IsCurrentUser(lockItem))
            return false;

        if (string.IsNullOrWhiteSpace(_lockSearch))
            return true;

        var query = _lockSearch.Trim();
        return (lockItem.assetPath ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || DisplayUser(lockItem.ownerId, lockItem.owner).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || (lockItem.reason ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || (lockItem.gitBranch ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || (lockItem.gitProtectedBranch ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
            || FormatLockState(lockItem).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private List<BackupSnapshotInfo> GetBackups(string resolvedPath, int maxCount)
    {
        var list = new List<BackupSnapshotInfo>();
        try
        {
            var directory = GetBackupDirectory(resolvedPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return list;

            var prefix = Path.GetFileNameWithoutExtension(resolvedPath) + "-";
            foreach (var file in Directory.GetFiles(directory, prefix + "*.json").OrderByDescending(Path.GetFileName).Take(maxCount))
            {
                var info = new FileInfo(file);
                list.Add(new BackupSnapshotInfo
                {
                    fullPath = file,
                    fileName = info.Name,
                    timestampLocal = info.LastWriteTime.ToString("yyyy/MM/dd HH:mm:ss"),
                    sizeBytes = info.Exists ? info.Length : 0
                });
            }
        }
        catch
        {
        }

        return list;
    }

    private string GetBackupDirectory(string resolvedPath)
    {
        var dir = Path.GetDirectoryName(resolvedPath);
        if (string.IsNullOrEmpty(dir))
            return "";
        return Path.Combine(dir, ".collabsync-backups");
    }

    private bool IsProjectRelativePath(string path)
    {
        return !string.IsNullOrEmpty(path)
            && !path.StartsWith("obj:", StringComparison.Ordinal)
            && (path.StartsWith("Assets/", StringComparison.Ordinal)
                || path.StartsWith("Packages/", StringComparison.Ordinal));
    }

    private void PingAssetPath(string assetPath)
    {
        if (!IsProjectRelativePath(assetPath))
            return;

        var cleanPath = assetPath.EndsWith("/", StringComparison.Ordinal)
            ? assetPath.TrimEnd('/')
            : assetPath;
        var asset = AssetDatabase.LoadMainAssetAtPath(cleanPath);
        if (asset == null)
            return;

        EditorGUIUtility.PingObject(asset);
        Selection.activeObject = asset;
    }

    private bool IsWebUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            return false;

        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private void OpenExternalLink(string value)
    {
        if (!IsWebUrl(value))
            return;

        Application.OpenURL(value.Trim());
    }

    private NewMemoLinkMode[] GetAvailableNewMemoLinkModes(string selectedMemoPath)
    {
        return string.IsNullOrEmpty(selectedMemoPath)
            ? new[] { NewMemoLinkMode.None, NewMemoLinkMode.ManualTarget }
            : new[] { NewMemoLinkMode.None, NewMemoLinkMode.CurrentSelection, NewMemoLinkMode.ManualTarget };
    }

    private string GetNewMemoLinkModeLabel(NewMemoLinkMode mode)
    {
        switch (mode)
        {
            case NewMemoLinkMode.CurrentSelection:
                return L("Current Selection", "現在の選択");
            case NewMemoLinkMode.ManualTarget:
                return L("URL / Asset Path", "URL / アセットパス");
            default:
                return L("None", "なし");
        }
    }

    private string ResolveNewMemoLinkTarget(string selectedMemoPath)
    {
        switch (_newMemoLinkMode)
        {
            case NewMemoLinkMode.CurrentSelection:
                return string.IsNullOrWhiteSpace(selectedMemoPath) ? "" : selectedMemoPath.Trim();
            case NewMemoLinkMode.ManualTarget:
                return string.IsNullOrWhiteSpace(_newMemoLinkTarget) ? "" : _newMemoLinkTarget.Trim();
            default:
                return "";
        }
    }

    private string GetGameObjectLockKey(GameObject go)
    {
        try
        {
            var gid = GlobalObjectId.GetGlobalObjectIdSlow(go);
            return "obj:" + gid;
        }
        catch
        {
            var path = go.scene.path;
            if (string.IsNullOrEmpty(path))
                return null;
            return $"obj:{path}#{go.GetInstanceID()}";
        }
    }

    private string GetAssetContext(string assetPath)
    {
        if (AssetDatabase.IsValidFolder(assetPath))
            return L("Folder", "フォルダ");
        if (assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            return L("Scene", "シーン");
        if (assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            return L("Prefab", "Prefab");
        return L("Asset", "アセット");
    }

    private string FormatLockTargetLabel(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath))
            return L("(unknown target)", "（不明な対象）");

        if (assetPath.StartsWith("obj:", StringComparison.Ordinal))
            return L("Scene/Prefab Object Lock", "シーン/Prefab オブジェクトロック");

        return TruncateMiddle(assetPath, IsCompactLayout(620f) ? 52 : 84);
    }

    private string FormatLockReason(string reason)
    {
        if (string.IsNullOrEmpty(reason))
            return "";

        return reason switch
        {
            "auto-lock" => L("Automatic lock while editing", "編集中の自動ロック"),
            "tools-menu" => L("Locked from Tools menu", "Tools メニューからロック"),
            "window-selection" => L("Locked from window", "ウィンドウからロック"),
            "context-menu" => L("Locked from context menu", "右クリックメニューからロック"),
            "object-lock" => L("Object lock", "オブジェクトロック"),
            "component-script" => L("Component script lock", "コンポーネントスクリプトのロック"),
            _ => reason
        };
    }

    private string FormatLockState(LockItem lockItem)
    {
        if (lockItem == null)
            return L("Unknown", "不明");

        return CollabSyncGitUtility.IsRetainedLock(lockItem)
            ? L("Retained", "保持")
            : L("Active", "有効");
    }

    private bool CanUseLockPrimaryAction(LockItem lockItem, bool isMine)
    {
        if (lockItem == null)
            return false;

        if (!isMine)
            return true;

        if (!CollabSyncGitUtility.IsRetainedLock(lockItem))
            return true;

        return !CollabSyncConfig.IsGitAwareRetainedLocksEnabled()
            || CollabSyncGitUtility.CanReleaseRetainedLock(lockItem);
    }

    private string GetLockPrimaryActionLabel(LockItem lockItem, bool isMine)
    {
        if (!isMine)
            return L("Request Unlock", "解除申請");
        if (!CollabSyncGitUtility.IsRetainedLock(lockItem))
            return L("Unlock", "解除");
        if (!CollabSyncConfig.IsGitAwareRetainedLocksEnabled())
            return L("Unlock", "解除");

        return CollabSyncGitUtility.CanReleaseRetainedLock(lockItem)
            ? L("Release Retained", "保持解除")
            : L("Waiting For Merge", "マージ待ち");
    }

    private string GetLockPrimaryActionTooltip(LockItem lockItem, bool isMine)
    {
        if (lockItem == null)
            return "";

        if (!isMine)
        {
            return CollabSyncGitUtility.IsRetainedLock(lockItem)
                ? L(
                    "This teammate lock is retained until the protected branch contains the recorded commit. Use Force Unlock only if you intentionally want to bypass that safety.",
                    "この他ユーザーのロックは、記録済みコミットが保護ブランチに入るまで保持されます。意図して安全確認を無視する場合だけ Force Unlock を使ってください。")
                : L("Send a memo asking the owner to release the lock.", "所有者にロック解除を依頼するメモを送ります。");
        }

        if (!CollabSyncGitUtility.IsRetainedLock(lockItem))
            return L("Release your current lock.", "現在のロックを解除します。");

        if (!CollabSyncConfig.IsGitAwareRetainedLocksEnabled())
            return L("Git-aware retained locks are off, so this lock can be removed immediately.", "Git連動ロックが無効なので、このロックはすぐに解除できます。");

        return CollabSyncGitUtility.CanReleaseRetainedLock(lockItem)
            ? L(
                "The protected branch already contains the recorded commit, so this retained lock can now be removed.",
                "保護ブランチに記録済みコミットが入ったため、この保持ロックを解除できます。")
            : L(
                "This lock is waiting for the protected branch to contain the recorded commit. It will stay retained until then, or an admin can Force Unlock it.",
                "このロックは保護ブランチに記録済みコミットが入るまで待機中です。それまでは保持され、管理者のみ Force Unlock できます。");
    }

    private string GetLockStateTooltip(LockItem lockItem)
    {
        if (lockItem == null)
            return "";

        return CollabSyncGitUtility.IsRetainedLock(lockItem)
            ? L(
                "Retained means the owner tried to unlock after their branch moved forward, but the protected branch does not yet contain the recorded commit.",
                "保持は、所有者が解除を試みた時点でブランチは進んでいるものの、保護ブランチに記録済みコミットがまだ入っていない状態を意味します。")
            : L(
                "Active means the lock is currently held in the normal state.",
                "有効は、通常状態でロック中であることを示します。");
    }

    private string GetLockGitTooltip(LockItem lockItem)
    {
        if (lockItem == null)
            return "";

        return L(
            "Shows the branch and commit recorded when the lock was taken, plus the protected branch used for retained-lock checks.",
            "ロック取得時に記録したブランチとコミット、および保持判定に使う保護ブランチを表示します。");
    }

    private string FormatLockGitSummary(LockItem lockItem)
    {
        if (lockItem == null)
            return "";

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(lockItem.gitBranch))
            parts.Add(CollabSyncGitUtility.NormalizeBranchLabel(lockItem.gitBranch));
        if (!string.IsNullOrEmpty(lockItem.gitHeadCommit))
            parts.Add(CollabSyncGitUtility.FormatShortCommit(lockItem.gitHeadCommit));
        if (!string.IsNullOrEmpty(lockItem.gitProtectedBranch))
        {
            parts.Add(LF("target {0}", "対象 {0}",
                CollabSyncGitUtility.NormalizeBranchLabel(lockItem.gitProtectedBranch)));
        }

        return string.Join(" / ", parts.Where(x => !string.IsNullOrEmpty(x)));
    }

    private string FormatRetainedLockDetail(LockItem lockItem)
    {
        if (!CollabSyncGitUtility.IsRetainedLock(lockItem))
            return "";

        return LF(
            "This lock stays retained until {0} contains commit {1}.",
            "このロックは {0} にコミット {1} が入るまで保持されます。",
            string.IsNullOrEmpty(lockItem?.gitProtectedBranch)
                ? "main"
                : CollabSyncGitUtility.NormalizeBranchLabel(lockItem.gitProtectedBranch),
            string.IsNullOrEmpty(lockItem?.gitHeadCommit)
                ? "HEAD"
                : CollabSyncGitUtility.FormatShortCommit(lockItem.gitHeadCommit));
    }

    private string FormatHistoryAction(WorkHistoryItem item)
    {
        if (item == null)
            return "";

        return item.action switch
        {
            "editing" => LF("Started editing {0}", "{0} の編集を開始", string.IsNullOrEmpty(item.context) ? L("item", "対象") : item.context),
            "lock" => L("Locked", "ロック"),
            "lock-retained" => L("Retained lock", "ロック保持"),
            "unlock" => L("Unlocked", "ロック解除"),
            "force-unlock" => L("Force unlocked", "強制解除"),
            "memo" => L("Added memo", "メモ追加"),
            "memo-delete" => L("Deleted memo", "メモ削除"),
            "memo-force-delete" => L("Force deleted memo", "メモ強制削除"),
            "unlock-request" => L("Requested unlock", "解除依頼"),
            "admin-grant" => L("Granted admin", "管理者追加"),
            "admin-revoke" => L("Removed admin", "管理者削除"),
            "user-delete" => L("Deleted user", "ユーザー削除"),
            "history-setting" => L("Changed work history setting", "作業履歴設定を変更"),
            _ => item.action ?? ""
        };
    }

    private string FormatHistoryTarget(WorkHistoryItem item)
    {
        if (item == null)
            return "";

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(item.assetPath))
            parts.Add(FormatLockTargetLabel(item.assetPath));

        if (!string.IsNullOrEmpty(item.detail))
        {
            if (item.action == "admin-grant")
                parts.Add(LF("Granted to {0}", "{0} を管理者に追加", item.detail));
            else if (item.action == "admin-revoke")
                parts.Add(LF("Removed {0}", "{0} を管理者から削除", item.detail));
            else if (item.action == "force-unlock")
                parts.Add(LF("Released {0}'s lock", "{0} のロックを解除", item.detail));
            else if (item.action == "memo-force-delete")
                parts.Add(LF("Deleted {0}'s memo", "{0} のメモを削除", item.detail));
            else if (item.action == "history-setting")
                parts.Add(string.Equals(item.detail, "disabled", StringComparison.Ordinal)
                    ? L("Disabled", "無効化")
                    : L("Enabled", "有効化"));
            else if (item.action == "user-delete")
                parts.Add(LF("Deleted {0}", "{0} を削除", item.detail));
            else if (item.action == "lock-retained")
                parts.Add(item.detail);
            else
            {
                var detail = item.action == "lock" || item.action == "unlock"
                ? FormatLockReason(item.detail)
                : TrimForToast(item.detail);
                if (!string.IsNullOrEmpty(detail))
                    parts.Add(detail);
            }
        }

        return string.Join(" / ", parts);
    }

    private string FormatPresenceTarget(EditingPresence presence)
    {
        if (presence == null)
            return "";

        if (string.IsNullOrEmpty(presence.assetPath))
        {
            if (!string.IsNullOrEmpty(presence.context))
                return presence.context;

            return L("Online (no active target)", "オンライン中（作業対象なし）");
        }

        if (!string.IsNullOrEmpty(presence.targetName))
            return $"{presence.context} / {presence.targetName}";

        return $"{presence.context} / {TruncateMiddle(presence.assetPath, IsCompactLayout(620f) ? 40 : 72)}";
    }

    private string FormatLockExpiry(LockItem lockItem)
    {
        if (lockItem == null)
            return L("Unknown", "不明");
        if (CollabSyncGitUtility.IsRetainedLock(lockItem))
        {
            return LF(
                "Until {0} contains {1}",
                "{0} に {1} が入るまで",
                string.IsNullOrEmpty(lockItem.gitProtectedBranch)
                    ? "main"
                    : CollabSyncGitUtility.NormalizeBranchLabel(lockItem.gitProtectedBranch),
                string.IsNullOrEmpty(lockItem.gitHeadCommit)
                    ? "HEAD"
                    : CollabSyncGitUtility.FormatShortCommit(lockItem.gitHeadCommit));
        }
        if (lockItem.ttlMs <= 0)
            return L("No expiry", "無期限");

        var remain = Math.Max(0, (int)((lockItem.ttlMs - (TimeUtil.NowMs() - lockItem.createdAt)) / 1000));
        return LF("{0}s remaining", "残り {0} 秒", remain);
    }

    private static string UnixToLocal(long ms)
    {
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().DateTime;
        return dt.ToString("yyyy/MM/dd HH:mm:ss");
    }

    private string FormatUnixToLocal(long ms)
    {
        return UnixToLocal(ms);
    }

    private string FormatSize(long bytes)
    {
        if (bytes < 1024) return LF("{0} B", "{0} B", bytes);
        if (bytes < 1024 * 1024) return LF("{0:0.0} KB", "{0:0.0} KB", bytes / 1024f);
        return LF("{0:0.0} MB", "{0:0.0} MB", bytes / (1024f * 1024f));
    }

    private string FormatNames(string[] names, int maxCount)
    {
        if (names == null || names.Length == 0)
            return L("Nobody active", "アクティブなし");
        if (names.Length <= maxCount)
            return string.Join(", ", names);
        return string.Join(", ", names.Take(maxCount)) + LF(" +{0}", " +{0}", names.Length - maxCount);
    }

    private string TruncateMiddle(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;
        if (maxLength < 8)
            return value.Substring(0, maxLength);

        int left = maxLength / 2 - 1;
        int right = maxLength - left - 1;
        return value.Substring(0, left) + "…" + value.Substring(value.Length - right);
    }

    private bool IsCompactLayout(float threshold = 560f)
    {
        return position.width < threshold;
    }

    private string ToolbarLabel(string english, string japanese, string compactEnglish, string compactJapanese)
    {
        return IsCompactLayout(540f)
            ? L(compactEnglish, compactJapanese)
            : L(english, japanese);
    }

    private Color ToneColor(MessageType tone)
    {
        switch (tone)
        {
            case MessageType.Info: return new Color(0.23f, 0.54f, 0.87f);
            case MessageType.Warning: return new Color(0.88f, 0.63f, 0.12f);
            case MessageType.Error: return new Color(0.84f, 0.28f, 0.27f);
            default: return new Color(0.45f, 0.45f, 0.45f);
        }
    }

    private MessageType DoctorMessageType()
    {
        if (_doctorRunning)
            return MessageType.Info;
        if (_doctorReport == null)
            return MessageType.Info;
        if (_doctorReport.success)
            return MessageType.Info;
        return (_doctorReport.issues?.Count ?? 0) > 0 ? MessageType.Warning : MessageType.Error;
    }

    private void QueueMemoAlert(MemoItem memo)
    {
        if (memo == null || string.IsNullOrEmpty(memo.id))
            return;

        _dismissedMemoAlertIds.Remove(memo.id);
        _pendingMemoAlertIds.Remove(memo.id);
        _pendingMemoAlertIds.Insert(0, memo.id);

        const int maxAlerts = 8;
        if (_pendingMemoAlertIds.Count > maxAlerts)
            _pendingMemoAlertIds.RemoveRange(maxAlerts, _pendingMemoAlertIds.Count - maxAlerts);
    }

    private List<MemoItem> GetPendingMemoAlerts()
    {
        PruneMemoAlerts();

        var list = new List<MemoItem>();
        foreach (var memoId in _pendingMemoAlertIds)
        {
            var memo = (_doc?.memos ?? new List<MemoItem>()).FirstOrDefault(x => x != null && string.Equals(x.id, memoId, StringComparison.Ordinal));
            if (memo != null)
                list.Add(memo);
        }

        return list;
    }

    private void DismissMemoAlert(string memoId)
    {
        if (string.IsNullOrEmpty(memoId))
            return;

        _pendingMemoAlertIds.Remove(memoId);
        _dismissedMemoAlertIds.Add(memoId);
        Repaint();
    }

    private void PruneMemoAlerts()
    {
        var validMemoIds = new HashSet<string>(
            (_doc?.memos ?? new List<MemoItem>())
                .Where(m => m != null &&
                            !string.IsNullOrEmpty(m.id) &&
                            !IsCurrentUser(m) &&
                            MemoIsUnread(m))
                .Select(m => m.id),
            StringComparer.Ordinal);

        _pendingMemoAlertIds.RemoveAll(id => !validMemoIds.Contains(id));
        _dismissedMemoAlertIds.RemoveWhere(id => !validMemoIds.Contains(id));
    }

    private static string TrimForToast(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = StripMemoMarkdownToPlainText(s);
        s = s.Replace("\r", " ").Replace("\n", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > 60 ? s[..60] + "…" : s;
    }

    private bool NotifyOnNewMemo => _cfg != null && _cfg.notifyOnNewMemo;
    private bool BeepOnNewMemo => _cfg != null && _cfg.beepOnNewMemo;

    private void EnsureStyles()
    {
        _cardValueStyle ??= new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 15
        };
        _cardValueButtonStyle ??= new GUIStyle(GUI.skin.button)
        {
            alignment = TextAnchor.MiddleLeft,
            fontSize = 15,
            fontStyle = FontStyle.Bold,
            padding = new RectOffset(0, 0, 0, 0),
            margin = new RectOffset(0, 0, 0, 0)
        };
        _cardDetailStyle ??= new GUIStyle(EditorStyles.miniLabel)
        {
            wordWrap = true
        };
        _memoMetaStyle ??= new GUIStyle(EditorStyles.miniLabel)
        {
            wordWrap = true,
            richText = false,
            fontSize = 10
        };
        _memoBodyStyle ??= new GUIStyle(EditorStyles.wordWrappedLabel)
        {
            wordWrap = true,
            richText = true,
            fontSize = 13
        };
        _memoPreviewStyle ??= new GUIStyle(EditorStyles.wordWrappedLabel)
        {
            wordWrap = true,
            richText = true,
            fontSize = 12
        };
    }

    private static string RenderMemoMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var protectedTokens = new List<string>();
        normalized = ProtectMarkdownTokens(normalized, MemoMarkdownFenceRegex, protectedTokens, FormatCodeBlockToken);
        normalized = ProtectMarkdownTokens(normalized, MemoMarkdownInlineCodeRegex, protectedTokens, FormatInlineCodeToken);
        normalized = ProtectMarkdownTokens(normalized, MemoMarkdownLinkRegex, protectedTokens, FormatLinkToken);

        normalized = EscapeRichText(normalized);

        var lines = normalized.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            lines[i] = RenderMarkdownLine(lines[i]);

        var rendered = string.Join("\n", lines);
        rendered = MemoMarkdownBoldRegex.Replace(rendered, match => "<b>" + match.Groups[2].Value + "</b>");
        rendered = MemoMarkdownItalicRegex.Replace(rendered, match =>
        {
            var content = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            return "<i>" + content + "</i>";
        });

        for (int i = 0; i < protectedTokens.Count; i++)
            rendered = rendered.Replace(CreateMarkdownToken(i), protectedTokens[i]);

        return rendered;
    }

    private static string RenderMarkdownLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return "";

        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("###### ", StringComparison.Ordinal)
            || trimmed.StartsWith("##### ", StringComparison.Ordinal)
            || trimmed.StartsWith("#### ", StringComparison.Ordinal)
            || trimmed.StartsWith("### ", StringComparison.Ordinal)
            || trimmed.StartsWith("## ", StringComparison.Ordinal)
            || trimmed.StartsWith("# ", StringComparison.Ordinal))
        {
            var headingText = trimmed.TrimStart('#').TrimStart();
            return "<b>" + headingText + "</b>";
        }

        if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            return "<color=#666666><i>| " + trimmed[2..] + "</i></color>";

        var bulletMatch = Regex.Match(trimmed, @"^[-*+]\s+");
        if (bulletMatch.Success)
            return "• " + trimmed[bulletMatch.Length..];

        var orderedMatch = Regex.Match(trimmed, @"^(\d+)\.\s+");
        if (orderedMatch.Success)
            return orderedMatch.Groups[1].Value + ". " + trimmed[orderedMatch.Length..];

        return line;
    }

    private static string ProtectMarkdownTokens(string text, Regex regex, List<string> protectedTokens, Func<Match, string> formatter)
    {
        return regex.Replace(text, match =>
        {
            var token = CreateMarkdownToken(protectedTokens.Count);
            protectedTokens.Add(formatter(match));
            return token;
        });
    }

    private static string CreateMarkdownToken(int index)
    {
        return "\uE000" + index + "\uE001";
    }

    private static string FormatCodeBlockToken(Match match)
    {
        var blockText = EscapeRichText((match.Groups[1].Value ?? "").Trim('\n'));
        return "<color=#7A4B00>" + blockText.Replace("\n", "\n") + "</color>";
    }

    private static string FormatInlineCodeToken(Match match)
    {
        return "<color=#7A4B00><b>" + EscapeRichText(match.Groups[1].Value) + "</b></color>";
    }

    private static string FormatLinkToken(Match match)
    {
        var label = EscapeRichText(match.Groups[1].Value);
        return "<color=#2F6DB5><u>" + label + "</u></color>";
    }

    private static string StripMemoMarkdownToPlainText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = MemoMarkdownFenceRegex.Replace(normalized, match => match.Groups[1].Value.Trim('\n'));
        normalized = MemoMarkdownLinkRegex.Replace(normalized, match => match.Groups[1].Value);
        normalized = MemoMarkdownInlineCodeRegex.Replace(normalized, match => match.Groups[1].Value);
        normalized = Regex.Replace(normalized, @"^\s{0,3}#{1,6}\s*", "", RegexOptions.Multiline);
        normalized = Regex.Replace(normalized, @"^\s{0,3}>\s?", "", RegexOptions.Multiline);
        normalized = Regex.Replace(normalized, @"^\s{0,3}[-*+]\s+", "• ", RegexOptions.Multiline);
        normalized = Regex.Replace(normalized, @"^\s{0,3}(\d+)\.\s+", "$1. ", RegexOptions.Multiline);
        normalized = normalized.Replace("**", "").Replace("__", "").Replace("`", "");
        normalized = Regex.Replace(normalized, @"(?<!\*)\*(?!\*)", "");
        normalized = Regex.Replace(normalized, @"(?<!_)_(?!_)", "");
        return normalized.Trim();
    }

    private static string EscapeRichText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var builder = new StringBuilder(text.Length + 16);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '&':
                    builder.Append("&amp;");
                    break;
                default:
                    builder.Append(ch);
                    break;
            }
        }
        return builder.ToString();
    }

    private static string L(string english, string japanese)
    {
        return CollabSyncLocalization.T(english, japanese);
    }

    private static string LF(string english, string japanese, params object[] args)
    {
        return CollabSyncLocalization.F(english, japanese, args);
    }
}
#endif
