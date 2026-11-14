using System.Runtime.InteropServices;

namespace VideoWallRW.Media.Ndi;

/// <summary>
/// P/Invoke cru pra Processing.NDI.Lib.x64.dll (API C estável desde a NDI SDK 3, sem
/// mudanças de assinatura até a 6). Não usar direto fora deste namespace — NdiFinder e
/// NdiVideoSource são as fachadas gerenciadas.
/// </summary>
internal static unsafe class NdiInterop
{
    private const string Lib = "Processing.NDI.Lib.x64.dll";

    /// <summary>
    /// Resolve a DLL nativa do NDI em ordem de preferência:
    ///  1. AO LADO DO EXE (a cópia embutida no app — third_party/ndi via .csproj): é a
    ///     que faz o NDI funcionar SEM instalar o NDI Tools/Runtime na máquina;
    ///  2. NDI_RUNTIME_DIR_V6/V5 (se o operador tiver o NDI Tools/Runtime instalado —
    ///     usa a versão dele, que pode ser mais nova);
    ///  3. resolvedor padrão do .NET (PATH/System32, caso o NDI Runtime "de sistema"
    ///     esteja registrado).
    /// </summary>
    static NdiInterop()
    {
        NativeLibrary.SetDllImportResolver(typeof(NdiInterop).Assembly, (name, assembly, searchPath) =>
        {
            if (name != Lib) return IntPtr.Zero;

            // 1) cópia embutida ao lado do exe — sem depender de instalação
            string bundled = Path.Combine(AppContext.BaseDirectory, Lib);
            if (File.Exists(bundled) && NativeLibrary.TryLoad(bundled, out var bundledHandle))
                return bundledHandle;

            // 2) NDI Tools/Runtime instalado (variável de ambiente de máquina)
            string? runtimeDir = Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V6")
                ?? Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V5");
            if (runtimeDir is not null)
            {
                string candidate = Path.Combine(runtimeDir, Lib);
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                    return handle;
            }
            return IntPtr.Zero; // 3) deixa o resolvedor padrão tentar (PATH, dir do app)
        });
    }

    /// <summary>Força a inicialização do resolver estático acima antes do primeiro DllImport.</summary>
    public static void EnsureResolverRegistered() { }

    [StructLayout(LayoutKind.Sequential)]
    public struct source_t
    {
        public IntPtr p_ndi_name;   // char* (UTF-8)
        public IntPtr p_url_address; // char* (UTF-8) — união com p_ip_address na lib original
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct find_create_t
    {
        [MarshalAs(UnmanagedType.U1)] public bool show_local_sources;
        public IntPtr p_groups;
        public IntPtr p_extra_ips;
    }

    public enum recv_bandwidth_e
    {
        metadata_only = -10,
        audio_only = 10,
        lowest = 0,
        highest = 100,
    }

    public enum recv_color_format_e
    {
        BGRX_BGRA = 0,
        UYVY_BGRA = 1,
        RGBX_RGBA = 2,
        UYVY_RGBA = 3,
        fastest = 100,
        best = 101,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct recv_create_v3_t
    {
        public source_t source_to_connect_to;
        public recv_color_format_e color_format;
        public recv_bandwidth_e bandwidth;
        [MarshalAs(UnmanagedType.U1)] public bool allow_video_fields;
        public IntPtr p_ndi_recv_name;
    }

    public enum frame_type_e
    {
        none = 0,
        video = 1,
        audio = 2,
        metadata = 3,
        error = 4,
        status_change = 100,
    }

    public enum frame_format_type_e
    {
        progressive = 1,
        interleaved = 0,
        field_0 = 2,
        field_1 = 3,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct video_frame_v2_t
    {
        public int xres;
        public int yres;
        public uint FourCC; // NDIlib_FourCC_video_type_e — comparamos via FourCcBgra/FourCcRgba
        public int frame_rate_N;
        public int frame_rate_D;
        public float picture_aspect_ratio;
        public frame_format_type_e frame_format_type;
        public long timecode;
        public IntPtr p_data;
        public int line_stride_in_bytes; // união com data_size_in_bytes (não usado aqui)
        public IntPtr p_metadata;
        public long timestamp;
    }

    // 'B'|'G'|'R'|'A' e 'R'|'G'|'B'|'A' como FourCC little-endian (byte0 no bit menos significativo).
    public const uint FourCcBgra = (byte)'B' | (byte)'G' << 8 | (byte)'R' << 16 | (byte)'A' << 24;
    public const uint FourCcBgrx = (byte)'B' | (byte)'G' << 8 | (byte)'R' << 16 | (byte)'X' << 24;
    public const uint FourCcRgba = (byte)'R' | (byte)'G' << 8 | (byte)'B' << 16 | (byte)'A' << 24;
    public const uint FourCcRgbx = (byte)'R' | (byte)'G' << 8 | (byte)'B' << 16 | (byte)'X' << 24;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool NDIlib_initialize();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_destroy();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr NDIlib_find_create_v2(find_create_t* p_create_settings);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_find_destroy(IntPtr p_instance);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool NDIlib_find_wait_for_sources(IntPtr p_instance, uint timeout_in_ms);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern source_t* NDIlib_find_get_current_sources(IntPtr p_instance, uint* p_no_sources);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr NDIlib_recv_create_v3(recv_create_v3_t* p_create_settings);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_recv_destroy(IntPtr p_instance);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern frame_type_e NDIlib_recv_capture_v3(IntPtr p_instance,
        video_frame_v2_t* p_video_data, IntPtr p_audio_data, IntPtr p_metadata, uint timeout_in_ms);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_recv_free_video_v2(IntPtr p_instance, video_frame_v2_t* p_video_data);

    [StructLayout(LayoutKind.Sequential)]
    public struct tally_t
    {
        [MarshalAs(UnmanagedType.U1)] public bool on_program;
        [MarshalAs(UnmanagedType.U1)] public bool on_preview;
    }

    /// <summary>
    /// Muitos encoders NDI em hardware (câmeras PTZ, HX) só mandam o vídeo em taxa
    /// cheia pra receptores marcados como "on program" — sem isso, ficam num modo de
    /// economia de banda (~1 fps) até detectar alguém realmente exibindo o sinal. Um
    /// software de vídeo wall É sempre "on program" (está indo pro telão), então
    /// chamamos isso logo após conectar.
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_recv_set_tally(IntPtr p_instance, tally_t* p_tally);
}
