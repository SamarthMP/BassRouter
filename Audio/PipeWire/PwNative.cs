using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace BassRouter.Audio.PipeWire;

/// <summary>
/// Minimal bindings for libpipewire-0.3.
/// <para>
/// Most of the PipeWire client API (pw_core_sync, pw_registry_bind, pw_node_set_param, ...) is implemented
/// as static inline functions that dispatch through a method table stored in a <c>struct spa_interface</c>.
/// Those can't be P/Invoked, so they're reimplemented here by reading the method table directly.
/// The layouts mirror the PipeWire 1.x headers and are part of PipeWire's stable ABI.
/// </para>
/// </summary>
internal static unsafe partial class PwNative
{
    private const string Library = "libpipewire-0.3.so.0";

    public const uint IdCore = 0;
    public const uint IdAny = 0xffffffff;

    public const string TypeNode = "PipeWire:Interface:Node";
    public const string TypeMetadata = "PipeWire:Interface:Metadata";

    public const uint VersionRegistry = 3;
    public const uint VersionNode = 3;
    public const uint VersionMetadata = 3;

    public const int ENOENT = 2;
    public const int EPIPE = 32;

    #region Exported functions

    [LibraryImport(Library)]
    public static partial void pw_init(nint argc, nint argv);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint pw_thread_loop_new(string? name, nint props);

    [LibraryImport(Library)]
    public static partial int pw_thread_loop_start(nint loop);

    [LibraryImport(Library)]
    public static partial void pw_thread_loop_stop(nint loop);

    [LibraryImport(Library)]
    public static partial void pw_thread_loop_destroy(nint loop);

    [LibraryImport(Library)]
    public static partial void pw_thread_loop_lock(nint loop);

    [LibraryImport(Library)]
    public static partial void pw_thread_loop_unlock(nint loop);

    [LibraryImport(Library)]
    public static partial nint pw_thread_loop_get_loop(nint loop);

    [LibraryImport(Library)]
    public static partial nint pw_context_new(nint mainLoop, nint props, nuint userDataSize);

    [LibraryImport(Library)]
    public static partial void pw_context_destroy(nint context);

    [LibraryImport(Library, SetLastError = true)]
    public static partial nint pw_context_connect(nint context, nint props, nuint userDataSize);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial nint pw_context_load_module(nint context, string name, string? args, nint props);

    [LibraryImport(Library)]
    public static partial void pw_impl_module_destroy(nint module);

    [LibraryImport(Library)]
    public static partial void pw_impl_module_add_listener(nint module, nint listener, nint events, nint data);

    [LibraryImport(Library)]
    public static partial int pw_core_disconnect(nint core);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint pw_properties_new_string(string args);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int pw_properties_set(nint props, string key, string? value);

    [LibraryImport(Library)]
    public static partial void pw_properties_free(nint props);

    [LibraryImport(Library)]
    public static partial void pw_proxy_destroy(nint proxy);

    [LibraryImport(Library)]
    public static partial nint pw_get_library_version();

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint pw_stream_new(nint core, string name, nint props);

    [LibraryImport(Library)]
    public static partial void pw_stream_add_listener(nint stream, nint listener, nint events, nint data);

    [LibraryImport(Library)]
    public static partial int pw_stream_connect(nint stream, PwDirection direction, uint targetId, PwStreamFlags flags, nint* parameters, uint parameterCount);

    [LibraryImport(Library)]
    public static partial void pw_stream_destroy(nint stream);

    [LibraryImport(Library)]
    public static partial nint pw_stream_dequeue_buffer(nint stream);

    [LibraryImport(Library)]
    public static partial int pw_stream_queue_buffer(nint stream, nint buffer);

    #endregion

    #region Streams

    public enum PwDirection
    {
        Input = 0,
        Output = 1,
    }

    [Flags]
    public enum PwStreamFlags : uint
    {
        Autoconnect = 1 << 0,
        MapBuffers = 1 << 2,
        DontReconnect = 1 << 7,
    }

    public enum PwStreamState
    {
        Error = -1,
        Unconnected = 0,
        Connecting = 1,
        Paused = 2,
        Streaming = 3,
    }

    public const uint VersionStreamEvents = 2;

    // struct pw_buffer (only the fields used here)
    [StructLayout(LayoutKind.Sequential)]
    public struct PwBuffer
    {
        public nint Buffer;
        public nint UserData;
        public ulong Size;
        public ulong Requested;
    }

    // struct spa_buffer
    [StructLayout(LayoutKind.Sequential)]
    public struct SpaBuffer
    {
        public uint MetaCount;
        public uint DataCount;
        public nint Metas;
        public nint Datas;
    }

    // struct spa_data
    [StructLayout(LayoutKind.Sequential)]
    public struct SpaData
    {
        public uint Type;
        public uint Flags;
        public long Fd;
        public uint MapOffset;
        public uint MaxSize;
        public nint Data;
        public nint Chunk;
    }

    // struct spa_chunk
    [StructLayout(LayoutKind.Sequential)]
    public struct SpaChunk
    {
        public uint Offset;
        public uint Size;
        public int Stride;
        public int Flags;
    }

    #endregion

    #region Interface method tables

    // struct spa_interface { const char *type; uint32_t version; struct spa_callbacks { const void *funcs; void *data; } cb; }
    private static readonly int InterfaceCallbacksOffset = AlignUp(nint.Size + sizeof(uint), nint.Size);

    // struct pw_*_methods { uint32_t version; <function pointers>... }
    private static readonly int MethodsFirstOffset = AlignUp(sizeof(uint), nint.Size);

    private static void* GetMethod(nint iface, int index, out nint data)
    {
        if (iface == 0)
            throw new ArgumentNullException(nameof(iface));

        nint funcs = *(nint*)(iface + InterfaceCallbacksOffset);
        data = *(nint*)(iface + InterfaceCallbacksOffset + nint.Size);

        void* method = *(void**)(funcs + MethodsFirstOffset + index * nint.Size);
        if (method == null)
            throw new NotSupportedException("PipeWire interface method is not implemented.");

        return method;
    }

    // pw_core_methods: add_listener, hello, sync, pong, error, get_registry, create_object, destroy

    public static int CoreAddListener(nint core, nint hook, nint events, nint userData)
    {
        var fn = (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, int>)GetMethod(core, 0, out nint data);
        return fn(data, hook, events, userData);
    }

    public static int CoreSync(nint core, uint id, int seq)
    {
        var fn = (delegate* unmanaged[Cdecl]<nint, uint, int, int>)GetMethod(core, 2, out nint data);
        return fn(data, id, seq);
    }

    public static nint CoreGetRegistry(nint core, uint version)
    {
        var fn = (delegate* unmanaged[Cdecl]<nint, uint, nuint, nint>)GetMethod(core, 5, out nint data);
        return fn(data, version, 0);
    }

    public static nint CoreCreateObject(nint core, string factoryName, string type, uint version, nint propsDict)
    {
        var fn = (delegate* unmanaged[Cdecl]<nint, byte*, byte*, uint, nint, nuint, nint>)GetMethod(core, 6, out nint data);

        using var factoryNameUtf8 = new Utf8String(factoryName);
        using var typeUtf8 = new Utf8String(type);
        return fn(data, factoryNameUtf8.Pointer, typeUtf8.Pointer, version, propsDict, 0);
    }

    // pw_registry_methods: add_listener, bind, destroy

    public static int RegistryAddListener(nint registry, nint hook, nint events, nint userData)
    {
        var fn = (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, int>)GetMethod(registry, 0, out nint data);
        return fn(data, hook, events, userData);
    }

    public static nint RegistryBind(nint registry, uint id, string type, uint version)
    {
        var fn = (delegate* unmanaged[Cdecl]<nint, uint, byte*, uint, nuint, nint>)GetMethod(registry, 1, out nint data);

        using var typeUtf8 = new Utf8String(type);
        return fn(data, id, typeUtf8.Pointer, version, 0);
    }

    // pw_node_methods: add_listener, subscribe_params, enum_params, set_param, send_command

    public static int NodeSetParam(nint node, uint id, uint flags, ReadOnlySpan<byte> pod)
    {
        var fn = (delegate* unmanaged[Cdecl]<nint, uint, uint, byte*, int>)GetMethod(node, 3, out nint data);

        fixed (byte* podPointer = pod)
        {
            return fn(data, id, flags, podPointer);
        }
    }

    // pw_metadata_methods: add_listener, set_property, clear

    public static int MetadataAddListener(nint metadata, nint hook, nint events, nint userData)
    {
        var fn = (delegate* unmanaged[Cdecl]<nint, nint, nint, nint, int>)GetMethod(metadata, 0, out nint data);
        return fn(data, hook, events, userData);
    }

    public static int MetadataSetProperty(nint metadata, uint subject, string key, string? type, string? value)
    {
        var fn = (delegate* unmanaged[Cdecl]<nint, uint, byte*, byte*, byte*, int>)GetMethod(metadata, 1, out nint data);

        using var keyUtf8 = new Utf8String(key);
        using var typeUtf8 = new Utf8String(type);
        using var valueUtf8 = new Utf8String(value);
        return fn(data, subject, keyUtf8.Pointer, typeUtf8.Pointer, valueUtf8.Pointer);
    }

    #endregion

    #region Hooks and event tables

    // struct spa_hook { struct spa_list link; struct spa_callbacks cb; void (*removed)(struct spa_hook*); void *priv; }
    public static readonly int HookSize = nint.Size * 6;

    public static nint AllocHook()
    {
        return (nint)NativeMemory.AllocZeroed((nuint)HookSize);
    }

    /// <summary>
    /// Equivalent of spa_hook_remove(). Must be called with the thread loop locked.
    /// </summary>
    public static void RemoveHook(nint hook)
    {
        if (hook == 0)
            return;

        // struct spa_list { struct spa_list *next; struct spa_list *prev; }
        nint* link = (nint*)hook;
        nint next = link[0];
        nint prev = link[1];
        if (prev != 0 && next != 0)
        {
            ((nint*)prev)[0] = next;
            ((nint*)next)[1] = prev;
        }

        nint removed = *(nint*)(hook + nint.Size * 4);
        if (removed != 0)
            ((delegate* unmanaged[Cdecl]<nint, void>)removed)(hook);

        NativeMemory.Clear((void*)hook, (nuint)HookSize);
    }

    public static void FreeHook(nint hook)
    {
        if (hook != 0)
            NativeMemory.Free((void*)hook);
    }

    // Larger than any pw_*_events struct, so events we don't handle read as NULL instead of past the allocation
    private const int MaxEventSlots = 16;

    /// <summary>
    /// Allocates a zeroed events struct (version + function pointers) and fills in the given entries.
    /// Index 0 is the first function pointer after the version field.
    /// </summary>
    public static nint AllocEvents(uint version, params nint[] functions)
    {
        if (functions.Length > MaxEventSlots)
            throw new ArgumentOutOfRangeException(nameof(functions));

        nint events = (nint)NativeMemory.AllocZeroed((nuint)(MethodsFirstOffset + MaxEventSlots * nint.Size));
        *(uint*)events = version;

        for (int i = 0; i < functions.Length; i++)
            *(nint*)(events + MethodsFirstOffset + i * nint.Size) = functions[i];

        return events;
    }

    #endregion

    #region Dictionaries

    /// <summary>
    /// Copies a <c>struct spa_dict</c> into a managed dictionary.
    /// </summary>
    public static Dictionary<string, string> ReadDict(nint dict)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (dict == 0)
            return result;

        // struct spa_dict { uint32_t flags; uint32_t n_items; const struct spa_dict_item *items; }
        uint count = *(uint*)(dict + sizeof(uint));
        nint items = *(nint*)(dict + AlignUp(sizeof(uint) * 2, nint.Size));

        for (int i = 0; i < count; i++)
        {
            // struct spa_dict_item { const char *key; const char *value; }
            nint item = items + i * nint.Size * 2;
            string? key = Marshal.PtrToStringUTF8(*(nint*)item);
            string? value = Marshal.PtrToStringUTF8(*(nint*)(item + nint.Size));

            if (key != null)
                result[key] = value ?? string.Empty;
        }

        return result;
    }

    /// <summary>
    /// Creates a <c>pw_properties</c> object. Its first member is a <c>struct spa_dict</c>,
    /// so the returned pointer can be passed wherever a dict is expected. Free with <see cref="pw_properties_free"/>.
    /// </summary>
    public static nint CreateProperties(IEnumerable<KeyValuePair<string, string>> values)
    {
        nint props = pw_properties_new_string(string.Empty);
        if (props == 0)
            throw new OutOfMemoryException("Failed to allocate PipeWire properties.");

        foreach (var (key, value) in values)
            pw_properties_set(props, key, value);

        return props;
    }

    #endregion

    public static string GetLibraryVersion()
    {
        return Marshal.PtrToStringUTF8(pw_get_library_version()) ?? "unknown";
    }

    private static int AlignUp(int value, int alignment)
    {
        return (value + alignment - 1) / alignment * alignment;
    }

    /// <summary>
    /// A NUL-terminated UTF-8 copy of a managed string in unmanaged memory.
    /// </summary>
    private readonly ref struct Utf8String
    {
        public readonly byte* Pointer;

        public Utf8String(string? value)
        {
            Pointer = value == null ? null : (byte*)Marshal.StringToCoTaskMemUTF8(value);
        }

        public void Dispose()
        {
            if (Pointer != null)
                Marshal.FreeCoTaskMem((nint)Pointer);
        }
    }
}

/// <summary>
/// Builds the SPA POD used to change filter-chain controls at runtime:
/// <c>Object(Props) { params = Struct( String key, Float value, ... ) }</c>
/// </summary>
internal static class PwPropsParams
{
    private const uint SpaTypeFloat = 6;
    private const uint SpaTypeString = 8;
    private const uint SpaTypeStruct = 14;
    private const uint SpaTypeObject = 15;
    private const uint SpaTypeObjectProps = 0x40002;
    private const uint SpaPropParams = 0x80001;

    public const uint SpaParamProps = 2;

    public static byte[] Build(IEnumerable<KeyValuePair<string, float>> controls)
    {
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var (key, value) in controls)
            {
                byte[] keyBytes = Encoding.UTF8.GetBytes(key);
                writer.Write((uint)keyBytes.Length + 1);
                writer.Write(SpaTypeString);
                writer.Write(keyBytes);
                writer.Write((byte)0);
                Pad(writer);

                writer.Write(sizeof(float));
                writer.Write(SpaTypeFloat);
                writer.Write(value);
                Pad(writer);
            }
        }

        byte[] structBody = body.ToArray();

        using var pod = new MemoryStream();
        using (var writer = new BinaryWriter(pod, Encoding.UTF8, leaveOpen: true))
        {
            // Object header + body (object type, param id)
            writer.Write((uint)(8 + 8 + 8 + structBody.Length));
            writer.Write(SpaTypeObject);
            writer.Write(SpaTypeObjectProps);
            writer.Write(SpaParamProps);

            // Property (key, flags) followed by the Struct value
            writer.Write(SpaPropParams);
            writer.Write(0u);
            writer.Write((uint)structBody.Length);
            writer.Write(SpaTypeStruct);
            writer.Write(structBody);
        }

        return pod.ToArray();
    }

    private static void Pad(BinaryWriter writer)
    {
        while (writer.BaseStream.Position % 8 != 0)
            writer.Write((byte)0);
    }
}

/// <summary>
/// Builds the SPA POD describing the 32-bit float audio format a stream uses:
/// <c>Object(Format, EnumFormat) { mediaType = audio, mediaSubtype = raw, format = F32, rate, channels, position }</c>
/// </summary>
internal static class PwAudioFormatParams
{
    private const uint SpaTypeId = 3;
    private const uint SpaTypeInt = 4;
    private const uint SpaTypeArray = 13;
    private const uint SpaTypeObject = 15;
    private const uint SpaTypeObjectFormat = 0x40003;
    private const uint SpaParamEnumFormat = 3;

    private const uint SpaFormatMediaType = 1;
    private const uint SpaFormatMediaSubtype = 2;
    private const uint SpaFormatAudioFormat = 0x10001;
    private const uint SpaFormatAudioRate = 0x10003;
    private const uint SpaFormatAudioChannels = 0x10004;
    private const uint SpaFormatAudioPosition = 0x10005;

    private const uint SpaMediaTypeAudio = 1;
    private const uint SpaMediaSubtypeRaw = 1;
    private const uint SpaAudioFormatF32LE = 0x11b;

    public const uint SpaAudioChannelMono = 2;
    public const uint SpaAudioChannelFL = 3;
    public const uint SpaAudioChannelFR = 4;

    public static byte[] Build(int sampleRate, uint[] positions)
    {
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            WriteProperty(writer, SpaFormatMediaType, SpaTypeId, SpaMediaTypeAudio);
            WriteProperty(writer, SpaFormatMediaSubtype, SpaTypeId, SpaMediaSubtypeRaw);
            WriteProperty(writer, SpaFormatAudioFormat, SpaTypeId, SpaAudioFormatF32LE);
            WriteProperty(writer, SpaFormatAudioRate, SpaTypeInt, (uint)sampleRate);
            WriteProperty(writer, SpaFormatAudioChannels, SpaTypeInt, (uint)positions.Length);

            // Array of Ids: the child POD header followed by the packed values
            writer.Write(SpaFormatAudioPosition);
            writer.Write(0u);
            writer.Write((uint)(8 + positions.Length * sizeof(uint)));
            writer.Write(SpaTypeArray);
            writer.Write((uint)sizeof(uint));
            writer.Write(SpaTypeId);
            foreach (uint position in positions)
                writer.Write(position);
            Pad(writer);
        }

        byte[] properties = body.ToArray();

        using var pod = new MemoryStream();
        using (var writer = new BinaryWriter(pod, Encoding.UTF8, leaveOpen: true))
        {
            // Object header + body (object type, param id)
            writer.Write((uint)(8 + properties.Length));
            writer.Write(SpaTypeObject);
            writer.Write(SpaTypeObjectFormat);
            writer.Write(SpaParamEnumFormat);
            writer.Write(properties);
        }

        return pod.ToArray();
    }

    /// <summary>
    /// A property (key, flags) whose value is a single Id or Int.
    /// </summary>
    private static void WriteProperty(BinaryWriter writer, uint key, uint type, uint value)
    {
        writer.Write(key);
        writer.Write(0u);
        writer.Write((uint)sizeof(uint));
        writer.Write(type);
        writer.Write(value);
        Pad(writer);
    }

    private static void Pad(BinaryWriter writer)
    {
        while (writer.BaseStream.Position % 8 != 0)
            writer.Write((byte)0);
    }
}
