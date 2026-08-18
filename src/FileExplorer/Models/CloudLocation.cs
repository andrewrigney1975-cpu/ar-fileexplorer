namespace FileExplorer.Models;

/// A detected local sync folder for a cloud storage provider (OneDrive, Google Drive, Dropbox, Box).
public sealed record CloudLocation(string Name, string Path, string Provider)
{
    public string Glyph => Provider switch
    {
        "OneDrive" => "",
        "GoogleDrive" => "",
        "Dropbox" => "",
        "Box" => "",
        _ => "",
    };
}
