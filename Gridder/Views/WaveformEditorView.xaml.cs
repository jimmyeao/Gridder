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

            // Click-to-seek on waveform
            var tap = new TapGestureRecognizer();
            tap.Tapped += OnWaveformTapped;
            WaveformCanvas.GestureRecognizers.Add(tap);
        }
    }

    private void OnWaveformTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel == null) return;

        var position = e.GetPosition(WaveformCanvas);
        if (position == null) return;

        var time = _viewModel.XToTime((float)position.Value.X);
        time = Math.Clamp(time, 0, _viewModel.TotalDurationSeconds);
        _viewModel.RequestSeek(time);
    }
}
