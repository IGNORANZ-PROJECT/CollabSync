using System;
using System.Threading.Tasks;

namespace Ignoranz.CollabSync
{
    public interface ICollabBackend
    {
        void Subscribe(Action<CollabStateDocument> onUpdate);
        Task<CollabStateDocument> LoadOnceAsync();

        // Presence
        Task PublishPresenceAsync(EditingPresence presence);

        // Memo
        Task UpsertMemoAsync(MemoItem memo);
        Task MarkMemoReadAsync(string memoId, string userId, string userName);
        Task<bool> DeleteMemoAsync(string memoId, string requesterId, string requesterName);
        Task<bool> ForceDeleteMemoAsync(string memoId, string requesterId, string requesterName);

        // Lock
        Task<bool> TryAcquireLockAsync(string assetPath, string ownerId, string ownerName, string reason = "", long ttlMs = 0, string scopeAssetPath = "");
        Task<bool> ReleaseLockAsync(string assetPath, string ownerId, string ownerName);
        Task<bool> ForceReleaseLockAsync(string assetPath, string requesterId, string requesterName);

        // Admin
        Task<bool> AddAdminAsync(string requesterId, string requesterName, string adminUserId, string adminUserName);
        Task<bool> RemoveAdminAsync(string requesterId, string requesterName, string adminUserId);
        Task<bool> DeleteUserAsync(string requesterId, string requesterName, string targetUserId, string targetUserName);
        Task<bool> SetWorkHistoryEnabledAsync(string requesterId, string requesterName, bool enabled);
    }
}
