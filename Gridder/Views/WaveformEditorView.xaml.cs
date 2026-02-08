using Gridder.ViewModels;

namespace Gridder.Views;

public partial class WaveformEditorView : ContentView
{
    private WaveformEditorViewModel? _viewModel;

    public WaveformEditorView()
    {
        InitializeComponent();
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();

        if (BindingContext is WaveformEditorViewModel vm)
        {
            _viewModel = vm;
            WaveformCanvas.ViewModel = vm;

            // Redraw when key properties change
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName is nameof(WaveformEditorViewModel.ScrollPositionSeconds)
                    or nameof(WaveformEditorViewModel.PixelsPerSecond)
                    or nameof(WaveformEditorViewModel.WaveformData)
                    or nameof(WaveformEditorViewModel.BeatGrid)
                    or nameof(WaveformEditorViewModel.SelectedBeatIndex)
                    or nameof(WaveformEditorViewModel.PlaybackPositionSeconds))
                {
                    WaveformCanvas.InvalidateSurface();
                }
            };
        }
    }
}
