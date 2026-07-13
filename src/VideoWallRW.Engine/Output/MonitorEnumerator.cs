using Vortice.DXGI;

namespace VideoWallRW.Engine.Output;

public static class MonitorEnumerator
{
    public static IReadOnlyList<MonitorInfo> Enumerate(Graphics.GraphicsDevice gfx)
    {
        var monitors = new List<MonitorInfo>();
        for (uint a = 0; gfx.Factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success; a++)
        {
            using (adapter)
            {
                string adapterDescription = adapter!.Description1.Description;
                for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput? output).Success; o++)
                {
                    using (output)
                    {
                        var desc = output!.Description;
                        if (!desc.AttachedToDesktop)
                            continue;
                        var r = desc.DesktopCoordinates;
                        monitors.Add(new MonitorInfo(desc.DeviceName,
                            r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, adapterDescription));
                    }
                }
            }
        }
        return monitors;
    }
}
