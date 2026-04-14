using Coravel.Invocable;
using Serilog;

namespace AutoDispatchService.Services;

/// <summary>
/// Coravel IInvocable entry point — scheduled by the host.
/// Delegates to ShadowModeProcessor for the actual work.
/// </summary>
public class ShadowModeInvocable : IInvocable
{
    private readonly ShadowModeProcessor _processor;

    public ShadowModeInvocable(ShadowModeProcessor processor)
    {
        _processor = processor;
    }

    public async Task Invoke()
    {
        try
        {
            Log.Debug("Shadow mode tick starting");
            await _processor.ProcessAsync();
            Log.Debug("Shadow mode tick complete");
            File.SetLastWriteTimeUtc("/tmp/healthy", DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unhandled error in shadow mode tick");
        }
    }
}
