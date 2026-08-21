namespace FileExplorer.Models;

/// Best-effort drive hardware details from WMI - any field can be null if the query fails or the
/// underlying provider doesn't report it for this drive type (common for virtual/some USB drives).
public sealed record DriveHardwareInfo(string? Manufacturer, string? Model, long CapacityBytes, string? FileSystem, string? InterfaceType, string? InterfaceSpeed);
