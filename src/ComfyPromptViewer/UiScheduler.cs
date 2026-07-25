using System;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace ComfyPromptViewer;

internal interface IUiScheduler
{
    void Post(Action action);
    Task InvokeAsync(Action action);
    Task InvokeBackgroundAsync(Action action);
}

internal sealed class AvaloniaUiScheduler : IUiScheduler
{
    public void Post(Action action)
    {
        Dispatcher.UIThread.Post(action);
    }

    public async Task InvokeAsync(Action action)
    {
        await Dispatcher.UIThread.InvokeAsync(action);
    }

    public async Task InvokeBackgroundAsync(Action action)
    {
        await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Background);
    }
}
