namespace FileExplorer.Models;

/// A named, user-defined collection of real file/folder paths (possibly spanning multiple drives),
/// persisted in virtualfolders.db and shown in the left rail with a folder+glint icon. See
/// [[VirtualFolderService]] for storage and [[VirtualFolderPathService]] for the "virtualfolder://{id}"
/// path shape used to navigate into one.
/// IsPrivate folders are only ever shown in the left rail's PIN-gated "Private Virtual Folders"
/// section (see [[VirtualFolderService]].VerifyPin) - the flag carries no enforcement of its own
/// beyond that section's expand gate (a private folder still navigates/lists exactly like any other
/// once you're in it).
public sealed record VirtualFolder(string Id, string Name, DateTimeOffset Created, bool IsPrivate);
