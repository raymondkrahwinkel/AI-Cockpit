namespace Cockpit.Infrastructure.Voice;

/// <summary>On-disk paths of a downloaded-and-extracted Piper voice, resolved by <see cref="PiperVoiceCache"/>.</summary>
internal sealed record PiperVoicePaths(string ModelPath, string TokensPath, string DataDirectoryPath);
