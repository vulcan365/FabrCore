namespace Fabr.Core
{
    /// <summary>
    /// Utility methods for agent handle normalization.
    /// Handle format: "owner:agentAlias" (e.g., "user123:assistant")
    /// </summary>
    public static class HandleUtilities
    {
        /// <summary>
        /// Normalizes an agent handle by ensuring it has the correct owner prefix.
        /// - If handle already has the exact prefix, returns as-is.
        /// - If handle has a different prefix (colon present), returns as-is (cross-client ref).
        /// - If handle has no prefix, adds the owner prefix.
        /// </summary>
        public static string EnsurePrefix(string handle, string ownerPrefix)
        {
            if (string.IsNullOrEmpty(handle))
                throw new ArgumentException("Handle cannot be null or empty", nameof(handle));
            if (string.IsNullOrEmpty(ownerPrefix))
                throw new ArgumentException("Owner prefix cannot be null or empty", nameof(ownerPrefix));

            if (handle.StartsWith(ownerPrefix, StringComparison.Ordinal))
                return handle;

            if (handle.Contains(':'))
                return handle;

            return $"{ownerPrefix}{handle}";
        }

        /// <summary>
        /// Strips the owner prefix from a handle if present.
        /// </summary>
        public static string StripPrefix(string handle, string ownerPrefix)
        {
            if (string.IsNullOrEmpty(handle))
                throw new ArgumentException("Handle cannot be null or empty", nameof(handle));

            if (!string.IsNullOrEmpty(ownerPrefix) &&
                handle.StartsWith(ownerPrefix, StringComparison.Ordinal))
            {
                return handle.Substring(ownerPrefix.Length);
            }

            return handle;
        }

        /// <summary>
        /// Builds an owner prefix string from an owner/client ID.
        /// </summary>
        public static string BuildPrefix(string ownerId) => $"{ownerId}:";
    }
}
