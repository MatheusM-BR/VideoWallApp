using System.Runtime.InteropServices;
using Serilog;

namespace VideoWallRW.Media.Ndi;

/// <summary>
/// Descoberta de fontes NDI na rede (NDIlib_find). Roda uma thread própria fazendo
/// polling — a lib NDI não tem callback nativo, então "esperar novas fontes" é
/// literalmente chamar wait_for_sources num loop. Publica a lista atual via evento
/// pra UI (Sources tab) manter o ListBox ao vivo sem o operador precisar atualizar.
/// </summary>
public sealed class NdiFinder : IDisposable
{
    private IntPtr _find;
    private Thread? _thread;
    private volatile bool _running;
    private readonly object _lock = new();

    public IReadOnlyList<string> CurrentSources { get; private set; } = [];
    public event Action<IReadOnlyList<string>>? SourcesChanged;

    /// <summary>Verdadeiro só se a DLL nativa carregou e NDIlib_initialize() teve
    /// sucesso — se o runtime não estiver instalado/registrado, a aba Sources deve
    /// mostrar "instale o NDI" em vez de travar/crashar a UI inteira.</summary>
    public bool IsAvailable { get; }
    public string? UnavailableReason { get; }

    public unsafe NdiFinder()
    {
        try
        {
            if (!NdiInterop.NDIlib_initialize())
            {
                UnavailableReason = "NDIlib_initialize() falhou (CPU sem suporte a SSE4.1/AVX2?).";
                return;
            }
            IsAvailable = true;
        }
        catch (DllNotFoundException)
        {
            UnavailableReason = "Processing.NDI.Lib.x64.dll não encontrada — instale o NDI Runtime/Tools.";
        }
        catch (Exception ex)
        {
            UnavailableReason = ex.Message;
        }
    }

    public unsafe void Start()
    {
        if (!IsAvailable || _running) return;

        var settings = new NdiInterop.find_create_t { show_local_sources = true };
        _find = NdiInterop.NDIlib_find_create_v2(&settings);
        if (_find == IntPtr.Zero)
        {
            Log.Warning("NDIlib_find_create_v2 retornou nulo — descoberta de fontes NDI desativada.");
            return;
        }

        _running = true;
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "NdiFind" };
        _thread.Start();
    }

    private unsafe void PollLoop()
    {
        while (_running)
        {
            NdiInterop.NDIlib_find_wait_for_sources(_find, 1000);
            if (!_running) break;

            uint count;
            var sources = NdiInterop.NDIlib_find_get_current_sources(_find, &count);
            var names = new List<string>((int)count);
            for (int i = 0; i < count; i++)
            {
                string? name = Marshal.PtrToStringUTF8(sources[i].p_ndi_name);
                if (name is null) continue;
                names.Add(name);
                // Guarda o endereço: o receptor conecta direto nele, sem refazer a descoberta.
                NdiReceiverPool.RememberUrl(name, Marshal.PtrToStringUTF8(sources[i].p_url_address));
            }

            lock (_lock) { CurrentSources = names; }
            SourcesChanged?.Invoke(names);
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(2));
        if (_find != IntPtr.Zero)
        {
            NdiInterop.NDIlib_find_destroy(_find);
            _find = IntPtr.Zero;
        }
        if (IsAvailable)
        {
            NdiReceiverPool.Shutdown(); // receptores precisam fechar antes do NDIlib_destroy
            NdiInterop.NDIlib_destroy();
        }
    }
}
