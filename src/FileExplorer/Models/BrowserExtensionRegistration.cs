namespace FileExplorer.Models;

/// One Chrome/Edge extension allowed to call the Native Messaging host. Label is purely a
/// user-facing note (the browser never sees it) - extension IDs are opaque 32-letter strings, so
/// with more than one registered there's otherwise no way to tell which row is which.
public sealed record BrowserExtensionRegistration(string Id, string? Label);
