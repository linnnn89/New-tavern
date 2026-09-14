using NAudio.Wave;
using TavernDesk.Core.Abstractions;

namespace TavernDesk.App.Services;

public sealed class WindowsSpeechAudioOutput : ISpeechAudioOutput
{
    public async Task PlayAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> audio, CancellationToken cancellationToken)
    {
        var buffer = new BufferedWaveProvider(new WaveFormat(44100, 16, 1), TimeSpan.FromSeconds(4))
        { DiscardOnBufferOverflow = false };
        await using var player = new WasapiPlayerBuilder().WithLatency(50).Build();
        player.Init(buffer);
        Exception? playbackError = null;
        player.PlaybackStopped += (_, args) => playbackError = args.Exception;
        using var cancelPlayback = cancellationToken.Register(player.Stop);
        var started = false;
        byte? remainder = null;
        try
        {
            await foreach (var chunk in audio.WithCancellation(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = new byte[chunk.Length + (remainder.HasValue ? 1 : 0)];
                if (remainder is { } tail) { bytes[0] = tail; chunk.CopyTo(bytes.AsMemory(1)); }
                else chunk.CopyTo(bytes);
                var count = bytes.Length & ~1;
                remainder = count < bytes.Length ? bytes[^1] : null;
                // Limit buffering without dropping valid small chunks or the final sample.
                for (var offset = 0; offset < count; offset += 8192)
                {
                    while (buffer.BufferedDuration.TotalSeconds > 2)
                    {
                        if (playbackError is not null) throw playbackError;
                        await Task.Delay(20, cancellationToken);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    buffer.AddSamples(bytes, offset, Math.Min(8192, count - offset));
                    if (!started) { player.Play(); started = true; }
                }
            }
            if (!started || remainder.HasValue) throw new SpeechException("InvalidAudio");
            while (buffer.BufferedBytes > 0)
            {
                if (playbackError is not null) throw playbackError;
                await Task.Delay(20, cancellationToken);
            }
            await Task.Delay(100, cancellationToken);
        }
        finally { player.Stop(); buffer.ClearBuffer(); }
    }
}
