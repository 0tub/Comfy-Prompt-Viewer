using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ComfyPromptViewer;

// The folder header. Its values and requests come from HeaderViewModel; this owns what needs controls: focus
// and caret in the search box, the help popup toggle, and the pulse when the count changes.
public partial class HeaderView : UserControl
{
    public HeaderView()
    {
        InitializeComponent();
    }

    internal HeaderViewModel ViewModel => (HeaderViewModel)DataContext!;

    internal void Initialize(HeaderViewModel viewModel)
    {
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    // Adds a term to the query, widening the scope to all fields first: settings are only searched there, so
    // a narrower scope would find nothing for a clicked model, sampler or seed.
    internal void AppendSearchTerm(string query)
    {
        ViewModel.SearchScopeIndex = (int)SearchScope.All;
        ViewModel.SearchText = SearchEngine.AppendQueryTerm(ViewModel.SearchText, query);
        SearchTextBox.CaretIndex = ViewModel.SearchText.Length;
    }

    // Dip and recover is the "this value changed" pulse, and the count carries a 100 ms opacity transition.
    // Running it unconditionally fired on every selection change and several times per folder load, which
    // reads as the label flickering rather than as an update.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(HeaderViewModel.CountText))
        {
            return;
        }

        CountBlock.Opacity = 0.2;
        Dispatcher.UIThread.Post(() => CountBlock.Opacity = 1.0, DispatcherPriority.Render);
    }

    private void ClearSearchButton_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.SearchText = string.Empty;
        SearchTextBox.Focus();
    }

    private void SearchHelpButton_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.IsSearchHelpOpen = !ViewModel.IsSearchHelpOpen;
        e.Handled = true;
    }
}
