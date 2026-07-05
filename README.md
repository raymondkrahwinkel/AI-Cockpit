# Zyra.Voice

Voice front-end for Zyra: microphone capture and audio playback, built on SoundFlow.

Current scope (F0): a solution skeleton (Core / Infrastructure / App) plus an audio spike
proving 16 kHz mono s16 PCM capture and playback work end-to-end on this machine. No STT/TTS,
brain integration, or VAD yet — those are later phases.

## Running the audio spike

```
dotnet run --project src/Zyra.Voice.App -- --audio-spike
```

Lists the default input/output devices, records ~2s of 16 kHz mono PCM, prints the byte count,
duration and RMS level, then plays a 440 Hz test tone followed by the recorded buffer.

## Running the app

```
dotnet run --project src/Zyra.Voice.App
```

Opens the Avalonia window with Record/Play buttons and a status label.
