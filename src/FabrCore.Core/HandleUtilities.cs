namespace FabrCore.Core
{
    /// <summary>
    /// Utility methods for agent handle normalization.
    /// <para>
    /// Handle format: <c>"userHandle:agentHandle"</c> (e.g., <c>"user123:assistant"</c>).
    /// </para>
    /// <para>
    /// <strong>Routing rules:</strong>
    /// <list type="bullet">
    ///   <item>Bare agent handle (no colon): auto-prefixed with the caller's user handle — routes to the caller's own agent.</item>
    ///   <item>Fully-qualified handle (contains colon): used as-is — enables cross-user routing override.</item>
    /// </list>
    /// To send a message to another user's agent, supply the full <c>"otherUserHandle:agentHandle"</c> handle.
    /// The system will not re-prefix it because it already contains a colon.
    /// </para>
    /// </summary>
    public static class HandleUtilities
    {
        /// <summary>
        /// Normalizes an agent handle by ensuring it has the correct user handle prefix.
        /// <list type="bullet">
        ///   <item>If handle already has the exact <paramref name="userHandlePrefix"/>, returns as-is.</item>
        ///   <item>If handle contains <c>':'</c> with a different prefix, returns as-is (cross-user routing override).</item>
        ///   <item>If handle has no <c>':'</c>, prepends <paramref name="userHandlePrefix"/> to scope it to the user handle.</item>
        /// </list>
        /// </summary>
        public static string EnsurePrefix(string handle, string userHandlePrefix)
        {
            if (string.IsNullOrEmpty(handle))
                throw new ArgumentException("Handle cannot be null or empty", nameof(handle));
            if (string.IsNullOrEmpty(userHandlePrefix))
                throw new ArgumentException("User handle prefix cannot be null or empty", nameof(userHandlePrefix));

            if (handle.StartsWith(userHandlePrefix, StringComparison.Ordinal))
                return handle;

            if (handle.Contains(':'))
                return handle;

            return $"{userHandlePrefix}{handle}";
        }

        /// <summary>
        /// Strips the user handle prefix from a handle if present.
        /// </summary>
        public static string StripPrefix(string handle, string userHandlePrefix)
        {
            if (string.IsNullOrEmpty(handle))
                throw new ArgumentException("Handle cannot be null or empty", nameof(handle));

            if (!string.IsNullOrEmpty(userHandlePrefix) &&
                handle.StartsWith(userHandlePrefix, StringComparison.Ordinal))
            {
                return handle.Substring(userHandlePrefix.Length);
            }

            return handle;
        }

        /// <summary>
        /// Builds a user handle prefix string from a user handle.
        /// </summary>
        public static string BuildPrefix(string userHandle) => $"{userHandle}:";

        /// <summary>
        /// Parses a handle into its user handle and agent handle components.
        /// Returns an empty user handle if the handle has no colon (bare agent handle).
        /// </summary>
        public static (string UserHandle, string AgentHandle) ParseHandle(string handle)
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
