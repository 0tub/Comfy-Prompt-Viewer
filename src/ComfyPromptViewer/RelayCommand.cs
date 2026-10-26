using System;
using System.Windows.Input;

namespace ComfyPromptViewer;

internal sealed class RelayCommand(Action execute) : ICommand
{
    // Always enabled, so there is nothing to raise.
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}
