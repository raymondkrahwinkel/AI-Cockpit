namespace Cockpit.Infrastructure.Audio;

internal sealed record AudioDuckingEntry(AudioDuckingSession Session, double[] Applied);
