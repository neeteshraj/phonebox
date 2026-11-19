namespace PhoneBox.Agent;

// Data shared by the Windows and the Linux / macOS implementations.

public sealed record MediaSnapshot(string App, string Title, string Artist, string Album, bool Playing,
    long PosMs, long DurMs, int ArtId, bool CanSeek);

public sealed record AlbumArt(byte[] Bytes, string Mime);

/// <summary>One of the players open at once (the phone's carousel); <see cref="Id"/> addresses it in /api/media.</summary>
public sealed record MediaSession(string Id, string App, string Title, string Artist, string Album, bool Playing,
    long PosMs, long DurMs, bool CanSeek, int ArtId);

public sealed record AudioSnapshot(int Volume, bool Muted, bool CanControl = true);
