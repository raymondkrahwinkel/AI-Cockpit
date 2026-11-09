using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Audio;

internal sealed class LinuxAudioDucker(ILogger<LinuxAudioDucker> logger, string journalPath)
    : JournaledAudioDucker(logger, journalPath)
{
    protected override async Task<IReadOnlyList<AudioDuckingSession>> ReadSessionsAsync(CancellationToken cancellationToken)
    {
        string sinks = await _RunAsync(["-f", "json", "list", "sinks"], cancellationToken).ConfigureAwait(false);
        string inputs = await _RunAsync(["-f", "json", "list", "sink-inputs"], cancellationToken).ConfigureAwait(false);
        return ParseSessions(inputs, sinks);
    }

    internal static IReadOnlyList<AudioDuckingSession> ParseSessions(string inputsJson, string sinksJson)
    {
        using JsonDocument sinks = JsonDocument.Parse(sinksJson);
        using JsonDocument inputs = JsonDocument.Parse(inputsJson);
        Dictionary<uint, string> devices = sinks.RootElement.EnumerateArray()
            .ToDictionary(sink => sink.GetProperty("index").GetUInt32(), sink => sink.GetProperty("name").GetString() ?? "");
        var sessions = new List<AudioDuckingSession>();
        foreach (JsonElement input in inputs.RootElement.EnumerateArray())
        {
            JsonElement properties = input.GetProperty("properties");
            if (!properties.TryGetProperty("application.process.id", out JsonElement process)
                || !int.TryParse(process.ToString(), CultureInfo.InvariantCulture, out int processId) || processId <= 0
                || !devices.TryGetValue(input.GetProperty("sink").GetUInt32(), out string? device))
            {
                continue;
            }
            double[] volumes = input.GetProperty("volume").EnumerateObject()
                .Select(channel => channel.Value.GetProperty("value").GetUInt32() / 65536.0).ToArray();
            if (volumes.Length == 0)
            {
                continue;
            }
            string restoreId = properties.TryGetProperty("module-stream-restore.id", out JsonElement restore)
                ? restore.GetString() ?? "" : "";
            sessions.Add(new AudioDuckingSession(device,
                input.GetProperty("index").GetUInt32().ToString(CultureInfo.InvariantCulture),
                restoreId, processId, !input.GetProperty("corked").GetBoolean(), volumes));
        }
        return sessions;
    }

    protected override async Task SetVolumesAsync(AudioDuckingSession session, double[] volumes, CancellationToken cancellationToken)
    {
        string[] arguments = ["set-sink-input-volume", session.Id,
            .. volumes.Select(volume => Math.Round(volume * 65536).ToString(CultureInfo.InvariantCulture))];
        await _RunAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> _RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("pactl")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var process = new Process { StartInfo = start };
        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string result = await output.ConfigureAwait(false);
            string failure = await error.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"pactl exited with {process.ExitCode}: {failure.Trim()}");
            }
            return result;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
