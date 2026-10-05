using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static BassRouter.Audio.PipeWire.PwNative;

namespace BassRouter.Audio.PipeWire;

/// <summary>
/// A 32-bit float audio stream to or from a node, with interleaved channels.
/// <para>
/// The process callback runs on the PipeWire thread loop (not the realtime thread), so it can run managed code.
/// For a capture stream it gets the recorded samples, for a playback stream it fills the samples to play.
/// The stream must be created and disposed with the thread loop locked.
/// </para>
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class PwAudioStream : IDisposable
{
    public delegate void ProcessHandler(Span<float> samples);

    private readonly nint Stream;
    private readonly nint Events;
    private readonly nint Hook;
    private readonly ProcessHandler Process;
    private readonly int Channels;
    private GCHandle SelfHandle;
    private volatile PwStreamState _State = PwStreamState.Unconnected;
    private volatile string? _Error;
    private bool Disposed;

    public PwDirection Direction { get; }

    public PwStreamState State => _State;

    public string? Error => _Error;

    /// <param name="core">The connection to create the stream on.</param>
    /// <param name="positions">Channel positions, which also sets the channel count.</param>
    /// <param name="properties">Stream properties, such as <c>target.object</c>.</param>
    public PwAudioStream(
        nint core,
        string name,
        PwDirection direction,
        int sampleRate,
        uint[] positions,
        IEnumerable<KeyValuePair<string, string>> properties,
        ProcessHandler process)
    {
        Process = process;
        Direction = direction;
        Channels = positions.Length;

        // pw_stream_new takes ownership of the properties
        Stream = pw_stream_new(core, name, CreateProperties(properties));
        if (Stream == 0)
            throw new InvalidOperationException($"Failed to create the PipeWire stream \"{name}\".");

        SelfHandle = GCHandle.Alloc(this);
        Hook = AllocHook();
        Events = AllocEvents(VersionStreamEvents,
            0,                                                                                  // destroy
            (nint)(delegate* unmanaged[Cdecl]<nint, PwStreamState, PwStreamState, nint, void>)&OnStateChanged,
            0,                                                                                  // control_info
            0,                                                                                  // io_changed
            0,                                                                                  // param_changed
            0,                                                                                  // add_buffer
            0,                                                                                  // remove_buffer
            (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnProcess);                         // process

        pw_stream_add_listener(Stream, Hook, Events, GCHandle.ToIntPtr(SelfHandle));

        // The format is copied by pw_stream_connect
        byte[] format = PwAudioFormatParams.Build(sampleRate, positions);
        int result;
        fixed (byte* formatPointer = format)
        {
            nint parameter = (nint)formatPointer;
            result = pw_stream_connect(
                Stream,
                direction,
                IdAny,
                PwStreamFlags.Autoconnect | PwStreamFlags.MapBuffers | PwStreamFlags.DontReconnect,
                &parameter,
                1);
        }

        if (result < 0)
        {
            Dispose();
            throw new InvalidOperationException($"Failed to connect the PipeWire stream \"{name}\": {Marshal.GetPInvokeErrorMessage(-result)}");
        }
    }

    public void Dispose()
    {
        if (Disposed)
            return;

        Disposed = true;
        RemoveHook(Hook);
        pw_stream_destroy(Stream);
        FreeHook(Hook);
        NativeMemory.Free((void*)Events);
        SelfHandle.Free();
    }

    private void HandleProcess()
    {
        nint buffer = pw_stream_dequeue_buffer(Stream);
        if (buffer == 0)
            return;

        try
        {
            var pwBuffer = (PwBuffer*)buffer;
            var spaBuffer = (SpaBuffer*)pwBuffer->Buffer;
            if (spaBuffer->DataCount < 1)
                return;

            var data = (SpaData*)spaBuffer->Datas;
            var chunk = (SpaChunk*)data->Chunk;
            if (data->Data == 0 || chunk == null)
                return;

            if (Direction == PwDirection.Output)
            {
                int stride = sizeof(float) * Channels;
                int frames = (int)(data->MaxSize / stride);
                if (pwBuffer->Requested > 0)
                    frames = (int)Math.Min((ulong)frames, pwBuffer->Requested);

                Process(new Span<float>((void*)data->Data, frames * Channels));

                chunk->Offset = 0;
                chunk->Stride = stride;
                chunk->Size = (uint)(frames * stride);
                pwBuffer->Size = (ulong)frames;
            }
            else
            {
                uint offset = Math.Min(chunk->Offset, data->MaxSize);
                uint size = Math.Min(chunk->Size, data->MaxSize - offset);
                Process(new Span<float>((void*)(data->Data + offset), (int)(size / sizeof(float))));
            }
        }
        finally
        {
            pw_stream_queue_buffer(Stream, buffer);
        }
    }

    private static PwAudioStream? FromUserData(nint data)
    {
        return data == 0 ? null : GCHandle.FromIntPtr(data).Target as PwAudioStream;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnStateChanged(nint data, PwStreamState old, PwStreamState state, nint error)
    {
        try
        {
            PwAudioStream? stream = FromUserData(data);
            if (stream == null)
                return;

            stream._Error = Marshal.PtrToStringUTF8(error);
            stream._State = state;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"BassRouter: stream state callback failed: {ex}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnProcess(nint data)
    {
        try
        {
            FromUserData(data)?.HandleProcess();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"BassRouter: stream process callback failed: {ex}");
        }
    }
}
