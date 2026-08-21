namespace FileExplorer.Models;

/// A saved connection profile - deliberately has no password field. Password is prompted once
/// per live session (RemoteSessionManager) and kept in memory only, never persisted to disk.
public sealed record RemoteConnection(string Id, string Name, RemoteProtocol Protocol, string Host, int Port, string Username);
