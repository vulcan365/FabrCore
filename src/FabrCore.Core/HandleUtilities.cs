namespace FabrCore.Core
{
    /// <summary>
    /// Utility methods for agent handle normalization.
    /// <para>
    /// Handle format: <c>"owner:agentAlias"</c> (e.g., <c>"user123:assistant"</c>).
    /// </para>
    /// <para>
    /// <strong>Routing rules:</strong>
    /// <list type="bullet">
    ///   <item>Bare alias (no colon): auto-prefixed with the caller's owner — routes to the caller's own agent.</item>
    ///   <item>Fully-qualified handle (contains colon): used as-is — enables cross-owner routing override.</item>
    /// </list>
    /// To send a message to another owner's agent, supply the full <c>"otherOwner:agent"</c> handle.
    /// The system will not re-prefix it because it already contains a colon.
    /// </para>
    /// </summary>
    public static class HandleUtilities
    {
        /// <summary>
        /// Normalizes an agent handle by ensuring it has the correct owner prefix.
        /// <list type="bullet">
        ///   <item>If handle already has the exact <paramref name="ownerPrefix"/>, returns as-is.</item>
        ///   <item>If handle contains <c>':'</c> with a different prefix, returns as-is (cross-owner routing override).</item>
        ///   <item>If handle has no <c>':'</c>, prepends <paramref name="ownerPrefix"/> to scope it to the owner.</item>
        /// </list>
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

        /// <summary>
        /// Parses a handle into its owner and alias components.
        /// Returns an empty owner if the handle has no colon (bare alias).
        /// </summary>
        public static (string Owner, string Alias) ParseHandle(string handle)
        {
            if (string.IsNullOrEmpty(handle))
                throw new ArgumentException("Handle cannot be null or empty", nameof(handle));

            var colonIndex = handle.IndexOf(':');
            if (colonIndex < 0)
                return (string.Empty, handle);

            return (handle[..colonIndex], handle[(colonIndex + 1)..]);
        }
    }
}
