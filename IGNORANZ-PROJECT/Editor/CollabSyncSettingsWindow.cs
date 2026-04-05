#if UNITY_EDITOR
using UnityEditor;

public class CollabSyncSettingsWindow : EditorWindow
{
    public static void Open() => CollabSyncWindow.OpenSettingsTab();

    private void OnEnable()
    {
        EditorApplication.delayCall += RedirectAndClose;
    }

    private void OnDisable()
    {
        EditorApplication.delayCall -= RedirectAndClose;
    }

    private void RedirectAndClose()
    {
        EditorApplication.delayCall -= RedirectAndClose;
        CollabSyncWindow.OpenSettingsTab();
        Close();
    }
}
#endif
